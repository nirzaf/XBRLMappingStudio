using System.Text.Json;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.CLI;

/// <summary>
/// Explicit operator entry point for adjudicating interrupted atomic report writes.
/// It only finalizes durable publication evidence; it never retries or publishes.
/// </summary>
public static class FilePublicationRecoveryCommand
{
    public static int Execute(RunJournal journal, TextWriter standardOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(standardOutput);

        var actions = journal.RecoverUnfinalized(cancellationToken);
        var finalizedCount = actions.Count(action => action.Decision == RecoveryDecision.Finalized);
        var retryNeededCount = actions.Count(action => action.Decision == RecoveryDecision.RetryNeeded);
        var abortedCount = actions.Count(action => action.Decision == RecoveryDecision.Aborted);
        var response = new
        {
            checkedCount = actions.Count,
            finalizedCount,
            abortedCount,
            retryNeededCount,
            actions = actions.Select(action => new
            {
                runId = action.RunId,
                decision = action.Decision.ToString(),
                detail = action.Detail
            }).ToArray()
        };
        standardOutput.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        return retryNeededCount == 0 ? ExitCodes.Success : ExitCodes.OperationalFailure;
    }
}
