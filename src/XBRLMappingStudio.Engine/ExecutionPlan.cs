using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// One step in a compiled plan. Steps run in topological order; each step processes
/// a single row for the current node. Independent operations use deterministic
/// tie-breaking by NodeId (per section 3).
/// </summary>
public sealed record PlanStep(
    string NodeId,
    string NodeType,
    Action<int, EvaluationFrame, ExecutionContext, DiagnosticBag> Evaluator);

/// <summary>
/// Per-row slot storage during execution. Key format: nodeId.portName.
/// </summary>
public sealed class EvaluationFrame
{
    public required int RowIndex { get; init; }
    private readonly Dictionary<string, DataValue> _slots = new(StringComparer.Ordinal);

    public void Set(string nodeId, string port, DataValue v)
        => _slots[ProvenanceTracker.SlotKey(nodeId, port)] = v;

    public DataValue Get(string nodeId, string port)
        => _slots.TryGetValue(ProvenanceTracker.SlotKey(nodeId, port), out var v)
            ? v
            : DataValue.Missing();

    public bool TryGet(string nodeId, string port, out DataValue v)
        => _slots.TryGetValue(ProvenanceTracker.SlotKey(nodeId, port), out v!);

    public IReadOnlyDictionary<string, DataValue> Slots => _slots;
}

/// <summary>
/// Compiled deterministic execution plan (MAP03). Runs each step per row in a
/// topological order. Cancellation between steps leaves no side effect: the
/// result is discarded rather than partially published.
/// </summary>
public sealed class ExecutionPlan
{
    public IReadOnlyList<PlanStep> Steps { get; }
    public IReadOnlyList<string> SinkNodeIds { get; }
    public IReadOnlyList<string> IterationSourceNodeIds { get; }

    internal ExecutionPlan(
        IReadOnlyList<PlanStep> steps,
        IReadOnlyList<string> sinks,
        IReadOnlyList<string> iterationSources)
    {
        Steps = steps;
        SinkNodeIds = sinks;
        IterationSourceNodeIds = iterationSources;
    }

    /// <summary>Number of rows this plan iterates over, derived at execution time.</summary>
    public int DetermineRowCount(ExecutionContext context)
        => context.LoadedSources.Values.Select(r => r.Count).DefaultIfEmpty(0).Max();

    public async Task<ExecutionResult> ExecuteAsync(ExecutionContext context)
    {
        var facts = new List<PendingFact>();
        var frames = new List<EvaluationFrame>();

        int rowCount = DetermineRowCount(context);
        if (context.IsPreview && rowCount > context.PreviewRowLimit)
            rowCount = context.PreviewRowLimit;

        try
        {
            for (int row = 0; row < rowCount; row++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();

                var frame = new EvaluationFrame { RowIndex = row };
                foreach (var step in Steps)
                {
                    step.Evaluator(row, frame, context, context.Diagnostics);
                    if (context.CancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException();
                }

                // Emit facts from sinks.
                foreach (var sinkId in SinkNodeIds)
                {
                    if (frame.TryGet(sinkId, "fact", out var factSlot)
                        && factSlot.Raw is PendingFact pf)
                    {
                        var slotKey = ProvenanceTracker.SlotKey(sinkId, "fact", row);
                        pf.Sources.AddRange(context.Provenance.GetContributors(slotKey));
                        facts.Add(pf);
                    }
                }

                frames.Add(frame);
            }

            return new ExecutionResult(
                RunOutcome.Success,
                facts,
                context.Diagnostics.Items,
                context.Provenance.Snapshot());
        }
        catch (OperationCanceledException)
        {
            context.Diagnostics.Warn(
                DiagnosticCodes.MapExecutionCancelled,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Execution,
                "Execution cancelled. Partial output discarded.");
            return new ExecutionResult(
                RunOutcome.Cancelled,
                Array.Empty<PendingFact>(),
                context.Diagnostics.Items,
                context.Provenance.Snapshot());
        }
        catch (NumericOverflowException ex)
        {
            context.Diagnostics.Error(
                DiagnosticCodes.MapNumericOverflow,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Execution,
                ex.Message);
            return new ExecutionResult(
                RunOutcome.InvalidMapping,
                Array.Empty<PendingFact>(),
                context.Diagnostics.Items,
                context.Provenance.Snapshot());
        }
    }
}

public sealed record ExecutionResult(
    RunOutcome Outcome,
    IReadOnlyList<PendingFact> Facts,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, IReadOnlyList<SourceReference>> Provenance);
