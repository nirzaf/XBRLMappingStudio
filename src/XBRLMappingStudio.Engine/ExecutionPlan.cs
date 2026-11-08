using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine;

/// <summary>Row-phase plan step. Runs once per row for scalar transforms.</summary>
public sealed record PlanStep(
    string NodeId,
    string NodeType,
    Action<int, EvaluationFrame, ExecutionContext, DiagnosticBag> Evaluator);

/// <summary>
/// Set-phase step (MAP05/06). Runs once after the row loop; receives a snapshot of
/// every per-row frame and produces set-level outputs written into a shared
/// <see cref="SetFrame"/> consumed by downstream aggregate-driven sinks.
/// </summary>
public sealed record SetStep(
    string NodeId,
    string NodeType,
    Func<RowSnapshot, Dictionary<string, DataValue>, ExecutionContext, DiagnosticBag, IReadOnlyDictionary<string, DataValue>> Evaluator);

/// <summary>Read-only view of every per-row frame captured during the row loop.</summary>
public sealed class RowSnapshot
{
    private readonly IReadOnlyList<EvaluationFrame> _frames;
    public RowSnapshot(IReadOnlyList<EvaluationFrame> frames) => _frames = frames;
    public int Count => _frames.Count;
    public DataValue Get(int row, string nodeId, string port) => _frames[row].Get(nodeId, port);
    public EvaluationFrame Frame(int row) => _frames[row];
}

/// <summary>Per-row slot storage during execution.</summary>
public sealed class EvaluationFrame
{
    public required int RowIndex { get; init; }
    private readonly Dictionary<ExecutionOutputKey, DataValue> _outputValues = new();

    public void Set(string nodeId, string port, DataValue v)
        => _outputValues[new ExecutionOutputKey(nodeId, port)] = v;

    public DataValue Get(string nodeId, string port)
        => _outputValues.TryGetValue(new ExecutionOutputKey(nodeId, port), out var v)
            ? v
            : DataValue.Missing();

    public bool TryGet(string nodeId, string port, out DataValue v)
        => _outputValues.TryGetValue(new ExecutionOutputKey(nodeId, port), out v!);

    public IReadOnlyDictionary<ExecutionOutputKey, DataValue> OutputValues => _outputValues;
}

/// <summary>Compiled deterministic execution plan with optional set-phase.</summary>
public sealed class ExecutionPlan
{
    public IReadOnlyList<PlanStep> Steps { get; }
    public IReadOnlyList<SetStep> SetSteps { get; }
    public IReadOnlyList<string> SinkNodeIds { get; }
    public IReadOnlyList<string> IterationSourceNodeIds { get; }
    public PartitionedGroupPlan? PartitionedGroup { get; }

    /// <summary>For each sink, the input port names whose upstream source is a
    /// set-phase producer. Sinks with a non-empty list fire once after the row loop
    /// (scalar aggregate mode), iterating the collection when the set output is
    /// multi-item (GroupBy mode).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SinkSetInputPorts { get; }

    /// <summary>Reverse lookup: for each sink input port name, the (sourceNodeId,
    /// sourcePortName) it reads from in the set frame.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, (string NodeId, string Port)>>
        SinkSetInputSources { get; }

    internal ExecutionPlan(
        IReadOnlyList<PlanStep> steps,
        IReadOnlyList<SetStep> setSteps,
        IReadOnlyList<string> sinks,
        IReadOnlyList<string> iterationSources,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sinkSetInputPorts = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, (string, string)>>? sinkSetInputSources = null,
        PartitionedGroupPlan? partitionedGroup = null)
    {
        PartitionedGroup = partitionedGroup;
        Steps = steps;
        SetSteps = setSteps;
        SinkNodeIds = sinks;
        IterationSourceNodeIds = iterationSources;
        SinkSetInputPorts = sinkSetInputPorts ?? new Dictionary<string, IReadOnlyList<string>>();
        SinkSetInputSources = sinkSetInputSources ?? new Dictionary<string, IReadOnlyDictionary<string, (string, string)>>();
    }

    public int DetermineRowCount(ExecutionContext context)
    {
        var rowCount = 0;
        foreach (var rows in context.LoadedSources.Values)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            rowCount = Math.Max(rowCount, rows.Count);
        }
        return rowCount;
    }

    public async Task<ExecutionResult> ExecuteAsync(ExecutionContext context)
    {
        var facts = new List<PendingFact>();
        var frames = new List<EvaluationFrame>();

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            int rowCount = DetermineRowCount(context);
            if (context.IsPreview && rowCount > context.PreviewRowLimit)
                rowCount = context.PreviewRowLimit;

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

                // Row-scoped sinks emit here. Set-driven sinks are deferred.
                foreach (var sinkId in SinkNodeIds)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (SinkSetInputPorts.ContainsKey(sinkId)) continue;
                    if (frame.TryGet(sinkId, "fact", out var factSlot)
                        && factSlot.Raw is PendingFact pf)
                    {
                        var slotKey = ProvenanceTracker.SlotKey(sinkId, "fact", row);
                        pf.Sources.AddRange(context.Provenance.GetContributors(slotKey));
                        pf.TransformationIds.AddRange(context.Provenance.GetTransformationIds(slotKey));
                        pf.SourceTransformationPaths.AddRange(context.Provenance.GetSourceTransformationPaths(slotKey));
                        facts.Add(pf);
                    }
                }

                context.NodeOutputs.RecordRow(frame, context.Provenance);
                frames.Add(frame);
            }

            // Set phase.
            if (SetSteps.Count > 0)
            {
                var snapshot = new RowSnapshot(frames);
                var setSlots = new Dictionary<string, DataValue>(StringComparer.Ordinal);
                foreach (var step in SetSteps)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var outputs = step.Evaluator(snapshot, setSlots, context, context.Diagnostics);
                    foreach (var (port, value) in outputs)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        DecimalDomain.ValidateValue(value, context.MaxSignificantDigits, step.NodeId,
                            context.CancellationToken);
                        setSlots[ProvenanceTracker.SlotKey(step.NodeId, port)] = value;
                        context.NodeOutputs.RecordSetOutput(step.NodeId, port, value, context.Provenance);
                    }
                }

                foreach (var sinkId in SinkNodeIds)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (!SinkSetInputPorts.TryGetValue(sinkId, out var ports) || ports.Count == 0)
                        continue;
                    EmitSetSink(sinkId, frames, setSlots, context, facts);
                }
            }

            return new ExecutionResult(
                RunOutcome.Success,
                facts,
                context.Diagnostics.Items,
                context.Provenance.Snapshot(),
                context.NodeOutputs.Snapshot(context.IsPreview));
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
                context.Provenance.Snapshot(),
                Array.Empty<NodeOutputPreview>());
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
                context.Provenance.Snapshot(),
                Array.Empty<NodeOutputPreview>());
        }
    }

    private void EmitSetSink(
        string sinkId,
        IReadOnlyList<EvaluationFrame> frames,
        Dictionary<string, DataValue> setSlots,
        ExecutionContext context,
        List<PendingFact> facts)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var sources = SinkSetInputSources.TryGetValue(sinkId, out var s) ? s : null;
        if (sources is null) return;

        // Determine iteration count from any collection-shaped set output.
        int iterationCount = 1;
        foreach (var (portName, (srcNode, srcPort)) in sources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var key = ProvenanceTracker.SlotKey(srcNode, srcPort);
            if (setSlots.TryGetValue(key, out var v) && v.Raw is IReadOnlyList<DataValue> list)
            {
                if (list.Count > iterationCount) iterationCount = list.Count;
            }
        }

        // Find the sink step.
        var sinkStep = Steps.FirstOrDefault(st => st.NodeId == sinkId);
        if (sinkStep is null) return;

        // Scalar template from the first frame if present, else blank.
        var template = frames.Count > 0 ? frames[0] : new EvaluationFrame { RowIndex = 0 };

        for (int idx = 0; idx < iterationCount; idx++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = new EvaluationFrame { RowIndex = 10_000 + idx };
            // Copy template scalar slots.
            foreach (var (slot, value) in template.OutputValues)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                frame.Set(slot.NodeId, slot.PortName, value);
            }
            // Overlay set-port values (scalar or collection slice at idx) onto the frame
            // under the SOURCE (node, port) so the row-phase sink evaluator, which reads
            // inputs by upstream edge, observes them.
            foreach (var (portName, (srcNode, srcPort)) in sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var key = ProvenanceTracker.SlotKey(srcNode, srcPort);
                if (!setSlots.TryGetValue(key, out var v)) continue;
                DataValue emitValue = v;
                string? setProvenanceKey = null;
                if (v.Raw is IReadOnlyList<DataValue> list)
                {
                    emitValue = idx < list.Count ? list[idx] : DataValue.Missing();
                    if (idx < list.Count)
                        setProvenanceKey = ProvenanceTracker.SetSlotKey(srcNode, srcPort, idx);
                }
                else
                {
                    setProvenanceKey = ProvenanceTracker.SetSlotKey(srcNode, srcPort);
                }
                frame.Set(srcNode, srcPort, emitValue);
                if (setProvenanceKey is not null)
                {
                    // Copy only this selected collection item's evidence onto the
                    // synthetic sink row. The row-phase sink then consumes it through
                    // the ordinary upstream-slot provenance path.
                    var rowSourceKey = ProvenanceTracker.SlotKey(srcNode, srcPort, 10_000 + idx);
                    context.Provenance.MergeSources(rowSourceKey, new[] { setProvenanceKey });
                    context.Provenance.MergeTransformations(rowSourceKey, new[] { setProvenanceKey });
                    context.Provenance.MergeSourceTransformationPaths(rowSourceKey, new[] { setProvenanceKey });
                }
            }
            sinkStep.Evaluator(10_000 + idx, frame, context, context.Diagnostics);

            if (frame.TryGet(sinkId, "fact", out var factSlot) && factSlot.Raw is PendingFact pf)
            {
                var slotKey = ProvenanceTracker.SlotKey(sinkId, "fact", 10_000 + idx);
                pf.Sources.AddRange(context.Provenance.GetContributors(slotKey));
                pf.TransformationIds.AddRange(context.Provenance.GetTransformationIds(slotKey));
                pf.SourceTransformationPaths.AddRange(context.Provenance.GetSourceTransformationPaths(slotKey));
                facts.Add(pf);
            }
        }
    }
}

public sealed record ExecutionResult(
    RunOutcome Outcome,
    IReadOnlyList<PendingFact> Facts,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, IReadOnlyList<SourceReference>> Provenance,
    IReadOnlyList<NodeOutputPreview> NodeOutputPreviews);
