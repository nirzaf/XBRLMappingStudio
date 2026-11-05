using XBRLMappingStudio.Engine;
using System.Globalization;
using System.Text;

namespace XBRLMappingStudio.Application;

/// <summary>Single-pass adapter reader with a bounded probe prefix and fixed-size batch reads.</summary>
internal sealed class BufferedSourceReader : IAsyncDisposable
{
    private readonly IAsyncEnumerator<DataRecord> _enumerator;
    private bool _ended;
    private bool _disposed;
    private bool _enumeratorDisposed;
    private int _bufferOffset;
    private DataRecord? _pending;

    public List<DataRecord> Buffer { get; } = new();
    public bool IsEnded => _ended;

    public BufferedSourceReader(IAsyncEnumerable<DataRecord> source, CancellationToken cancellationToken)
        => _enumerator = source.GetAsyncEnumerator(cancellationToken);

    public async ValueTask<bool> BufferNextAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ended) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            Buffer.Add(_enumerator.Current);
            return true;
        }
        _ended = true;
        await DisposeEnumeratorAsync().ConfigureAwait(false);
        return false;
    }

    public async ValueTask<IReadOnlyList<DataRecord>> ReadBatchAsync(int maximumRows,
        CancellationToken cancellationToken)
        => await ReadBatchAsync(maximumRows, long.MaxValue, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<DataRecord>> ReadBatchAsync(int maximumRows, long maximumBytes,
        Func<DataRecord, int>? measureBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumRows < 1) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var rows = new List<DataRecord>(maximumRows);
        long batchBytes = 0;
        while (rows.Count < maximumRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DataRecord? next;
            if (_pending is not null)
            {
                next = _pending;
                _pending = null;
            }
            else if (_bufferOffset < Buffer.Count)
            {
                next = Buffer[_bufferOffset++];
            }
            else if (_ended)
            {
                break;
            }
            else if (await _enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                next = _enumerator.Current;
            }
            else
            {
                _ended = true;
                await DisposeEnumeratorAsync().ConfigureAwait(false);
                break;
            }

            var bytes = measureBytes?.Invoke(next) ?? 0;
            if (bytes > 0 && rows.Count > 0 && bytes > maximumBytes - batchBytes)
            {
                _pending = next;
                break;
            }
            rows.Add(next);
            batchBytes = checked(batchBytes + bytes);
        }
        if (_bufferOffset == Buffer.Count)
        {
            Buffer.Clear();
            _bufferOffset = 0;
        }
        else if (_bufferOffset >= maximumRows * 4)
        {
            Buffer.RemoveRange(0, _bufferOffset);
            _bufferOffset = 0;
        }
        return rows;
    }

    public static int EstimateRecordBytes(DataRecord record)
    {
        long bytes = Utf8Bytes(record.RowId) + 64;
        foreach (var (name, value) in record.Fields)
        {
            bytes += Utf8Bytes(name) + Utf8Bytes(value.LexicalSource ?? "") + 48;
            bytes += value.Raw switch
            {
                null => 0,
                string text => Utf8Bytes(text),
                DateOnly date => Utf8Bytes(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                DateTimeOffset instant => Utf8Bytes(instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                IFormattable formattable => Utf8Bytes(formattable.ToString(null, CultureInfo.InvariantCulture) ?? ""),
                var raw => Utf8Bytes(raw.ToString() ?? "")
            };
            if (bytes >= int.MaxValue) return int.MaxValue;
        }
        if (record.CellSources is not null)
        {
            foreach (var (name, source) in record.CellSources)
            {
                bytes += Utf8Bytes(name) + Utf8Bytes(source.SourceId) + Utf8Bytes(source.Locator)
                    + Utf8Bytes(source.DisplayName ?? "") + 48;
                if (bytes >= int.MaxValue) return int.MaxValue;
            }
        }
        return (int)bytes;
    }

    private static int Utf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    public IReadOnlyList<DataRecord> TakeBufferedRecords()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ended || _bufferOffset != 0)
            throw new InvalidOperationException("Only a fully buffered, unread source can be used by the small-run path.");
        return Buffer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeEnumeratorAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposeEnumeratorAsync()
    {
        if (_enumeratorDisposed) return;
        _enumeratorDisposed = true;
        _ended = true;
        await _enumerator.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class BufferedSourceReaderSet : IAsyncDisposable
{
    private readonly List<BufferedSourceReader> _readers = new();
    public Dictionary<string, BufferedSourceReader> Readers { get; } = new(StringComparer.Ordinal);

    public BufferedSourceReader Add(string bindingId, IAsyncEnumerable<DataRecord> source,
        CancellationToken cancellationToken)
    {
        var reader = new BufferedSourceReader(source, cancellationToken);
        Readers.Add(bindingId, reader);
        _readers.Add(reader);
        return reader;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var reader in _readers)
            await reader.DisposeAsync().ConfigureAwait(false);
    }
}
