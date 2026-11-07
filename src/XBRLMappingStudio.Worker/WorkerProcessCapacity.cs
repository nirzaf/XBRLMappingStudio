using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Worker;

/// <summary>
/// Application-process-wide capacity for external worker processes. Persistent
/// Arelle clients and one-shot supervised workers share this limit.
/// </summary>
public static class WorkerProcessCapacity
{
    public const int MaximumConcurrentProcesses = 2;

    private static readonly SemaphoreSlim Slots = new(
        MaximumConcurrentProcesses, MaximumConcurrentProcesses);

    public static async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessSlot();
    }

    private sealed class ProcessSlot : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Slots.Release();
        }
    }
}

/// <summary>
/// Lazily starts a worker client while holding a shared process slot. The lease is
/// retained until the client is disposed, so concurrent clients cannot exceed the
/// process-wide worker budget.
/// </summary>
public sealed class BoundedWorkerClient : IWorkerClient
{
    private readonly Func<IWorkerClient> _factory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private IWorkerClient? _inner;
    private IDisposable? _processSlot;
    private int _disposeStarted;

    public BoundedWorkerClient(Func<IWorkerClient> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
    {
        var inner = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await inner.HandshakeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkerResponse> RequestAsync(
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inner = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await inner.RequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IWorkerClient> GetClientAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCancellation.Token);
        await _initializationLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            if (_inner is not null) return _inner;

            IDisposable? slot = await WorkerProcessCapacity.AcquireAsync(linked.Token)
                .ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
                IWorkerClient inner;
                try
                {
                    inner = _factory()
                        ?? throw new InvalidOperationException("The worker factory returned no client.");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                    or IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    throw WorkerUnavailable(ex);
                }
                _inner = inner;
                _processSlot = slot;
                slot = null;
                return inner;
            }
            finally
            {
                slot?.Dispose();
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposeCancellation.Cancel();
        await _initializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var inner = _inner;
            _inner = null;
            var slot = _processSlot;
            _processSlot = null;
            try
            {
                if (inner is not null)
                    await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                slot?.Dispose();
            }
        }
        finally
        {
            _initializationLock.Release();
            _disposeCancellation.Dispose();
        }
    }

    private static WorkerStartupException WorkerUnavailable(Exception exception)
    {
        const string messagePrefix = "Could not start the selected profile worker: ";
        var message = messagePrefix + exception.Message;
        var diagnostic = new Diagnostic
        {
            Code = DiagnosticCodes.XbrWorkerUnavailable,
            Subsystem = DiagnosticSubsystem.Worker,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Worker,
            IsBlocking = true,
            Message = message
        };
        return new WorkerStartupException(WorkerStatus.IncompleteValidation,
            message, new[] { diagnostic });
    }
}
