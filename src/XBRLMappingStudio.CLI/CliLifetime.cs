using System.Runtime.InteropServices;

namespace XBRLMappingStudio.CLI;

/// <summary>One cooperative cancellation scope and last-resort exit boundary per CLI invocation.</summary>
public static class CliLifetime
{
    public static async Task<int> RunAsync(Func<CancellationToken, Task<int>> command)
    {
        using var cancellation = new CancellationTokenSource();
        void RequestCancellation()
        {
            if (cancellation.IsCancellationRequested) return;
            Console.Error.WriteLine("CLI_STOP_REQUESTED: Waiting for cooperative shutdown; inspect publication receipts before retrying.");
            cancellation.Cancel();
        }
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; RequestCancellation(); };
        Console.CancelKeyPress += onCancel;
        using var termination = OperatingSystem.IsWindows() ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                RequestCancellation();
            });
        try
        {
            // Preserve the actual outcome: a stop arriving after commit does not undo publication.
            return await command(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("CLI_CANCELLED: Operation cancelled; inspect publication receipts before retrying.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine($"CLI_INVALID_ARGUMENT: {ex.Message}");
            return ExitCodes.IncompleteProfile;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"CLI_ACCESS_DENIED: {ex.Message}");
            return ExitCodes.AuthorizationFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CLI_OPERATIONAL_FAILURE: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.OperationalFailure;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
