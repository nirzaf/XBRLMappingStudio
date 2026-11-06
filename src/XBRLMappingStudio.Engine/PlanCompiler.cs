using System.Globalization;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Compiles a <see cref="MappingGraph"/> into a deterministic <see cref="ExecutionPlan"/>.
/// Nodes run in topological order; ties broken by NodeId (section 3, MAP03).
/// </summary>
public sealed class PlanCompiler
{
    private readonly FunctionCatalog _catalog;

    public PlanCompiler(FunctionCatalog catalog)
    {
        _catalog = catalog;
    }

    public CompilationResult Compile(MappingGraph graph, FilingProfile profile)
    {
        var diag = new DiagnosticBag();

        // MAP07: inline named visual functions against their pinned revision so that
        // validation and compilation operate on the expanded graph.
        graph = FunctionExpander.Expand(graph, _catalog.Version, diag);
        if (diag.HasBlockingErrors)
            return CompilationResult.Failed(diag);

        // Validate first (MAP02 acceptance: cycle/dangling/type errors surface here).
        var validator = new GraphValidator(_catalog);
        var validationDiags = validator.Validate(graph);
        foreach (var d in validationDiags.Items) diag.Add(d);
        if (diag.HasBlockingErrors)
        {
            return CompilationResult.Failed(diag);
        }

        // Topological order with deterministic tie-breaking by NodeId.
        var order = TopoSort(graph);

        // Discover the iteration source. ExcelField (row-phase) or CollectRows (set-phase)
        // nodes each require a loaded binding and therefore drive row iteration.
        var iterationSources = order
            .Select(id => graph.Nodes[id])
            .Where(n => n.NodeType == NodeTypeConstants.ExcelField
                        || n.NodeType == NodeTypeConstants.CollectRows)
            .Select(n => n.NodeId)
            .ToList();

        if (iterationSources.Count == 0)
        {
            diag.Error(
                DiagnosticCodes.MapUnboundRequiredInput,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Compilation,
                "No iteration source (ExcelField or collectRows node) is present in the graph.");
            return CompilationResult.Failed(diag);
        }

        // Sinks are fact-producing nodes.
        var sinks = order
            .Select(id => graph.Nodes[id])
            .Where(n => n.NodeType == NodeTypeConstants.FactConstruct)
            .Select(n => n.NodeId)
            .ToList();

        // Set-phase producers: Collect/Sum/Count/First/Lookup run once after the row loop.
        var setProducers = new HashSet<string>(StringComparer.Ordinal);
        var rowSteps = new List<PlanStep>();
        var setSteps = new List<SetStep>();
        foreach (var nodeId in order)
        {
            var node = graph.Nodes[nodeId];
            if (IsSetPhase(node.NodeType))
            {
                setSteps.Add(new SetStep(nodeId, node.NodeType, BuildSetEvaluator(graph, nodeId, profile)));
                setProducers.Add(nodeId);
            }
            else
            {
                rowSteps.Add(new PlanStep(nodeId, node.NodeType,
                    BuildEvaluator(graph, nodeId, profile, diag)));
            }
        }

        // Detect which sink input ports come from set-phase producers.
        var sinkSetInputPorts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var sinkSetInputSources = new Dictionary<string, IReadOnlyDictionary<string, (string, string)>>(StringComparer.Ordinal);
        foreach (var sinkId in sinks)
        {
            var sinkNode = graph.Nodes[sinkId];
            var sources = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var port in sinkNode.InputPorts)
            {
                var edge = graph.IncomingEdge(sinkId, port.Name);
                if (edge is null) continue;
                if (setProducers.Contains(edge.SourceNodeId))
                    sources[port.Name] = (edge.SourceNodeId, edge.SourcePortName);
            }
            if (sources.Count > 0)
            {
                sinkSetInputPorts[sinkId] = sources.Keys.ToList();
                sinkSetInputSources[sinkId] = sources;
            }
        }

        return CompilationResult.Ok(
            new ExecutionPlan(rowSteps, setSteps, sinks, iterationSources,
                sinkSetInputPorts, sinkSetInputSources),
            graph);
    }

    /// <summary>Node types that produce set-level outputs after the row loop.</summary>
    private static bool IsSetPhase(string nodeType) => nodeType switch
    {
        NodeTypeConstants.Collect => true,
        NodeTypeConstants.Sum => true,
        NodeTypeConstants.Count => true,
        NodeTypeConstants.First => true,
        NodeTypeConstants.CollectRows => true,
        NodeTypeConstants.GroupBy => true,
        NodeTypeConstants.Join => true,
        _ => false
    };

    private static List<string> TopoSort(MappingGraph graph)
    {
        var indegree = graph.Nodes.ToDictionary(k => k.Key, _ => 0);
        foreach (var e in graph.Edges.Values)
        {
            indegree[e.TargetNodeId]++;
        }

        // Deterministic: sort ready set by NodeId ordinal.
        var ready = new SortedSet<string>(
            indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var order = new List<string>();

        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            order.Add(current);

            var node = graph.Nodes[current];
            foreach (var p in node.OutputPorts)
            {
                foreach (var e in graph.OutgoingEdges(node.NodeId, p.Name))
                {
                    if (--indegree[e.TargetNodeId] == 0) ready.Add(e.TargetNodeId);
                }
            }
        }

        return order;
    }

    /// <summary>
    /// Build the per-node evaluator. Reads inputs from the frame and writes outputs
    /// back to the frame. Records provenance for any input that carries a source.
    /// </summary>
    /// <summary>
    /// Set-phase evaluator factory (MAP05). Aggregate steps read a specific
    /// upstream (node, port) across every row captured in the row loop.
    /// </summary>
    private Func<RowSnapshot, Dictionary<string, DataValue>, ExecutionContext, DiagnosticBag, IReadOnlyDictionary<string, DataValue>>
        BuildSetEvaluator(MappingGraph graph, string nodeId, FilingProfile profile)
    {
        var node = graph.Nodes[nodeId];
        return (snapshot, setSlots, ctx, diag) =>
        {
            var outputs = new Dictionary<string, DataValue>(StringComparer.Ordinal);

            // Resolve a set-phase input port as a flat list: an upstream set producer's
            // collection output (via setSlots), a single scalar set output treated as a
            // one-item stream, or (for row-shaped inputs) the value across the snapshot.
            IReadOnlyList<DataValue> ReadSetInput(string port)
            {
                var e = graph.IncomingEdge(nodeId, port);
                if (e is null) return Array.Empty<DataValue>();
                var key = ProvenanceTracker.SlotKey(e.SourceNodeId, e.SourcePortName);
                if (setSlots.TryGetValue(key, out var v))
                {
                    if (v.Raw is IReadOnlyList<DataValue> list) return list;
                    return new[] { v };
                }
                return ResolveCollection(snapshot, e);
            }

            IReadOnlyList<IReadOnlyList<SourceTransformationPath>> ReadSetInputPaths(string port)
            {
                var e = graph.IncomingEdge(nodeId, port);
                if (e is null) return Array.Empty<IReadOnlyList<SourceTransformationPath>>();
                var key = ProvenanceTracker.SlotKey(e.SourceNodeId, e.SourcePortName);
                if (setSlots.TryGetValue(key, out var value))
                {
                    if (value.Raw is IReadOnlyList<DataValue> list)
                        return Enumerable.Range(0, list.Count)
                            .Select(index => (IReadOnlyList<SourceTransformationPath>)ctx.Provenance
                                .GetSourceTransformationPaths(ProvenanceTracker.SetSlotKey(
                                    e.SourceNodeId, e.SourcePortName, index)))
                            .ToArray();
                    return new[] { ctx.Provenance.GetSourceTransformationPaths(
                        ProvenanceTracker.SetSlotKey(e.SourceNodeId, e.SourcePortName)) };
                }

                return Enumerable.Range(0, snapshot.Count)
                    .Select(index => (IReadOnlyList<SourceTransformationPath>)ctx.Provenance
                        .GetSourceTransformationPaths(ProvenanceTracker.SlotKey(
                            e.SourceNodeId, e.SourcePortName, index)))
                    .ToArray();
            }

            IReadOnlyList<SourceTransformationPath> ReadSetFieldPaths(
                MappingEdge? inputEdge, int itemIndex, string fieldName)
            {
                if (inputEdge is null) return Array.Empty<SourceTransformationPath>();
                var inputSlotKey = ProvenanceTracker.SlotKey(
                    inputEdge.SourceNodeId, inputEdge.SourcePortName);
                if (setSlots.TryGetValue(inputSlotKey, out var setValue))
                {
                    if (setValue.Raw is not IReadOnlyList<DataValue> items || itemIndex >= items.Count)
                        return Array.Empty<SourceTransformationPath>();
                    return ctx.Provenance.GetSourceTransformationPaths(ProvenanceTracker.SetSlotKey(
                        inputEdge.SourceNodeId, inputEdge.SourcePortName, itemIndex, fieldName));
                }

                return ctx.Provenance.GetSourceTransformationPaths(ProvenanceTracker.SlotKey(
                    inputEdge.SourceNodeId, inputEdge.SourcePortName, itemIndex));
            }

            void StoreSetPaths(string port, int? itemIndex, IEnumerable<SourceTransformationPath> paths,
                string? fieldName = null)
            {
                var key = ProvenanceTracker.SetSlotKey(nodeId, port, itemIndex, fieldName);
                foreach (var path in paths)
                {
                    ctx.Provenance.RecordSourceTransformationPath(key, path);
                    ctx.Provenance.RecordSource(key, path.Source);
                    foreach (var transformationId in path.TransformationIds)
                        ctx.Provenance.RecordTransformation(key, transformationId);
                }
            }

            IReadOnlyList<SourceTransformationPath> ExtendPaths(
                IEnumerable<SourceTransformationPath> paths, string transformationId)
            {
                var extended = new List<SourceTransformationPath>();
                foreach (var path in paths)
                {
                    var ids = path.TransformationIds.Contains(transformationId, StringComparer.Ordinal)
                        ? path.TransformationIds
                        : path.TransformationIds.Append(transformationId).ToArray();
                    if (!extended.Any(existing => existing.Source == path.Source
                        && existing.TransformationIds.SequenceEqual(ids, StringComparer.Ordinal)))
                        extended.Add(new SourceTransformationPath(path.Source, ids));
                }
                return extended;
            }

            void AddDistinctPaths(List<SourceTransformationPath> target,
                IEnumerable<SourceTransformationPath> candidates)
            {
                foreach (var candidate in candidates)
                    if (!target.Any(existing => existing.Source == candidate.Source
                        && existing.TransformationIds.SequenceEqual(candidate.TransformationIds, StringComparer.Ordinal)))
                        target.Add(candidate);
            }

            switch (node.NodeType)
            {
                case NodeTypeConstants.Collect:
                {
                    var edge = graph.IncomingEdge(nodeId, "input");
                    var list = new List<DataValue>();
                    var inputPaths = ReadSetInputPaths("input");
                    if (edge is not null)
                    {
                        for (int r = 0; r < snapshot.Count; r++)
                        {
                            var v = snapshot.Get(r, edge.SourceNodeId, edge.SourcePortName);
                            if (v.IsMissing) continue;
                            list.Add(v);
                            var paths = r < inputPaths.Count
                                ? inputPaths[r]
                                : Array.Empty<SourceTransformationPath>();
                            StoreSetPaths("items", list.Count - 1, ExtendPaths(paths, nodeId));
                            if (list.Count > ctx.MaxCollectionItems)
                            {
                                diag.Error(DiagnosticCodes.MapCollectionBudgetExceeded,
                                    DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                    $"Collect exceeded MaxCollectionItems={ctx.MaxCollectionItems}.",
                                    nodeId: nodeId);
                                break;
                            }
                        }
                    }
                    outputs["items"] = DataValue.Collection(list);
                    break;
                }

                case NodeTypeConstants.Sum:
                {
                    var items = ReadSetInput("items");
                    var itemPaths = ReadSetInputPaths("items");
                    decimal total = 0m;
                    var contributingPaths = new List<SourceTransformationPath>();
                    for (var index = 0; index < items.Count; index++)
                    {
                        var it = items[index];
                        if (it.IsMissing || it.IsNil) continue;
                        try { total += it.AsDecimal(); }
                        catch (OverflowException)
                        { throw new NumericOverflowException($"Sum overflow on node '{node.DisplayName}'."); }
                        if (index < itemPaths.Count) AddDistinctPaths(contributingPaths, itemPaths[index]);
                    }
                    outputs["value"] = DataValue.DecimalValue(total);
                    StoreSetPaths("value", null, ExtendPaths(contributingPaths, nodeId));
                    if (ctx.IsPreview)
                        ctx.SampledTotals.Add(new SampledTotal(nodeId, "sum", total, IsPartial: true));
                    break;
                }

                case NodeTypeConstants.Count:
                {
                    var items = ReadSetInput("items");
                    var itemPaths = ReadSetInputPaths("items");
                    long c = 0;
                    var contributingPaths = new List<SourceTransformationPath>();
                    for (var index = 0; index < items.Count; index++)
                    {
                        var it = items[index];
                        if (it.IsMissing || it.IsNil) continue;
                        c++;
                        if (index < itemPaths.Count) AddDistinctPaths(contributingPaths, itemPaths[index]);
                    }
                    outputs["value"] = DataValue.IntegerValue(c);
                    StoreSetPaths("value", null, ExtendPaths(contributingPaths, nodeId));
                    break;
                }

                case NodeTypeConstants.First:
                {
                    var items = ReadSetInput("items");
                    var itemPaths = ReadSetInputPaths("items");
                    var firstIndex = -1;
                    for (var index = 0; index < items.Count; index++)
                    {
                        if (items[index].IsMissing || items[index].IsNil) continue;
                        firstIndex = index;
                        break;
                    }
                    outputs["value"] = firstIndex >= 0
                        ? items[firstIndex]
                        : DataValue.Missing(PortShape.Decimal);
                    StoreSetPaths("value", null, firstIndex >= 0 && firstIndex < itemPaths.Count
                        ? ExtendPaths(itemPaths[firstIndex], nodeId)
                        : Array.Empty<SourceTransformationPath>());
                    break;
                }

                case NodeTypeConstants.CollectRows:
                {
                    var bindingId = GetString(node, "binding");
                    var fieldsCsv = GetString(node, "fields");
                    var retain = string.IsNullOrEmpty(fieldsCsv)
                        ? null
                        : fieldsCsv!.Split(',', StringSplitOptions.TrimEntries);
                    var rows = new List<DataValue>();
                    if (bindingId is null || !ctx.LoadedSources.TryGetValue(bindingId, out var records))
                    {
                        diag.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Adapter,
                            DiagnosticStage.Execution,
                            $"collectRows binding '{bindingId}' is not loaded in execution context.",
                            nodeId: nodeId);
                    }
                    else
                    {
                        int limit = ctx.IsPreview ? Math.Min(records.Count, ctx.PreviewRowLimit) : records.Count;
                        for (int r = 0; r < limit; r++)
                        {
                            var dict = new Dictionary<string, DataValue>(StringComparer.Ordinal);
                            foreach (var f in records[r].Fields)
                            {
                                if (retain is null || retain.Contains(f.Key, StringComparer.Ordinal))
                                {
                                    dict[f.Key] = f.Value;
                                    var source = f.Value.Source;
                                    var cellSources = records[r].CellSources;
                                    if (source is null && cellSources is not null)
                                        cellSources.TryGetValue(f.Key, out source);
                                    if (source is not null)
                                    {
                                        var path = new SourceTransformationPath(source, new[] { nodeId });
                                        StoreSetPaths("items", rows.Count,
                                            new[] { path });
                                        StoreSetPaths("items", rows.Count, new[] { path }, f.Key);
                                    }
                                }
                            }
                            rows.Add(DataValue.Record(dict));
                            if (rows.Count > ctx.MaxCollectionItems)
                            {
                                diag.Error(DiagnosticCodes.MapCollectionBudgetExceeded,
                                    DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                    $"collectRows exceeded MaxCollectionItems={ctx.MaxCollectionItems}.",
                                    nodeId: nodeId);
                                rows.RemoveAt(rows.Count - 1);
                                break;
                            }
                        }
                    }
                    outputs["items"] = DataValue.Collection(rows);
                    break;
                }

                case NodeTypeConstants.GroupBy:
                {
                    var items = ReadSetInput("items");
                    var inputEdge = graph.IncomingEdge(nodeId, "items");
                    // CollectRows and record-preserving set operators such as Join
                    // publish field-specific paths. Other derived record shapes remain
                    // untraced unless they explicitly preserve this metadata.
                    var keyFields = (GetString(node, "keys") ?? "")
                        .Split(',', StringSplitOptions.TrimEntries);
                    var sumField = GetString(node, "sumField") ?? "";
                    var order = new List<string>();
                    var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    var groupKeyPaths = new Dictionary<string, List<SourceTransformationPath>>(StringComparer.Ordinal);
                    var groupValuePaths = new Dictionary<string, List<SourceTransformationPath>>(StringComparer.Ordinal);
                    long groupWorkingBytes = 0;

                    static long EstimateStringStorageBytes(string? value)
                        => value is null ? 0 : 24L + (long)value.Length * sizeof(char);

                    static long EstimateGroupPathBytes(SourceTransformationPath path)
                    {
                        var oneCopy = 96L
                            + EstimateStringStorageBytes(path.Source.SourceId)
                            + EstimateStringStorageBytes(path.Source.Locator)
                            + EstimateStringStorageBytes(path.Source.DisplayName)
                            + 24L + (long)path.TransformationIds.Count * 16L;
                        foreach (var transformationId in path.TransformationIds)
                            oneCopy = checked(oneCopy + EstimateStringStorageBytes(transformationId));
                        // Include the retained source path plus headroom for the set-output
                        // and fact provenance copies produced after aggregation.
                        return checked(oneCopy * 3);
                    }

                    bool TryReserveGroupMemory(long bytes)
                    {
                        if (bytes < 0 || bytes > ctx.MaxGroupWorkingBytes
                            || groupWorkingBytes > ctx.MaxGroupWorkingBytes - bytes)
                            return false;
                        groupWorkingBytes += bytes;
                        return true;
                    }

                    bool TryAddGroupPaths(List<SourceTransformationPath> target,
                        IEnumerable<SourceTransformationPath> candidates)
                    {
                        foreach (var candidate in candidates)
                        {
                            if (target.Any(existing => existing.Source == candidate.Source
                                && existing.TransformationIds.SequenceEqual(candidate.TransformationIds,
                                    StringComparer.Ordinal)))
                                continue;
                            if (!TryReserveGroupMemory(EstimateGroupPathBytes(candidate)))
                                return false;
                            target.Add(candidate);
                        }
                        return true;
                    }

                    bool exceededWorkingMemory = false;
                    foreach (var (rec, itemIndex) in items.Select((item, index) => (item, index)))
                    {
                        if (!TryRecordFields(rec, out var fields)) continue;
                        var bk = string.Join("|", keyFields.Select(kf => GetField(fields, kf).AsString()));
                        if (!totals.ContainsKey(bk))
                        {
                            if (order.Count >= ctx.MaxGroupCount)
                            {
                                diag.Error(DiagnosticCodes.MapCollectionBudgetExceeded,
                                    DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                    $"GroupBy exceeded MaxGroupCount={ctx.MaxGroupCount}; no additional groups were added.",
                                    nodeId: nodeId);
                                break;
                            }
                            if (order.Count >= ctx.MaxCollectionItems)
                            {
                                diag.Error(DiagnosticCodes.MapCollectionBudgetExceeded,
                                    DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                    $"GroupBy output exceeded MaxCollectionItems={ctx.MaxCollectionItems}; no additional groups were added.",
                                    nodeId: nodeId);
                                break;
                            }
                            var bucketBytes = 512L + EstimateStringStorageBytes(bk);
                            if (!TryReserveGroupMemory(bucketBytes))
                            {
                                diag.Error(DiagnosticCodes.MapCollectionBudgetExceeded,
                                    DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                    $"GroupBy exceeded MaxGroupWorkingBytes={ctx.MaxGroupWorkingBytes}; no additional groups were added.",
                                    nodeId: nodeId);
                                exceededWorkingMemory = true;
                                break;
                            }
                            order.Add(bk);
                            totals[bk] = 0m;
                            groupKeyPaths[bk] = new List<SourceTransformationPath>();
                            groupValuePaths[bk] = new List<SourceTransformationPath>();
                        }
                        if (inputEdge is not null)
                        {
                            foreach (var keyField in keyFields)
                            {
                                var paths = ReadSetFieldPaths(inputEdge, itemIndex, keyField);
                                if (!TryAddGroupPaths(groupKeyPaths[bk], paths)
                                    || !TryAddGroupPaths(groupValuePaths[bk], paths))
                                {
                                    diag.Error(DiagnosticCodes.MapCollectionBudgetExceeded,
                                        DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                        $"GroupBy exceeded MaxGroupWorkingBytes={ctx.MaxGroupWorkingBytes}; no additional groups were added.",
                                        nodeId: nodeId);
                                    exceededWorkingMemory = true;
                                    break;
                                }
                            }
                            if (exceededWorkingMemory) break;
                        }
                        var v = GetField(fields, sumField);
                        if (v.IsMissing || v.IsNil) continue;
                        try { totals[bk] += v.AsDecimal(); }
                        catch (OverflowException)
                        { throw new NumericOverflowException($"GroupBy sum overflow on node '{node.DisplayName}'."); }
                        if (inputEdge is not null)
                        {
                            if (!TryAddGroupPaths(groupValuePaths[bk],
                                ReadSetFieldPaths(inputEdge, itemIndex, sumField)))
                            {
                                diag.Error(DiagnosticCodes.MapCollectionBudgetExceeded,
                                    DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                    $"GroupBy exceeded MaxGroupWorkingBytes={ctx.MaxGroupWorkingBytes}; no additional groups were added.",
                                    nodeId: nodeId);
                                exceededWorkingMemory = true;
                                break;
                            }
                        }
                    }
                    var valueList = new List<DataValue>();
                    var keyList = new List<DataValue>();
                    foreach (var bk in order)
                    {
                        var outputIndex = valueList.Count;
                        valueList.Add(DataValue.DecimalValue(totals[bk]));
                        keyList.Add(DataValue.StringValue(bk));
                        StoreSetPaths("values", outputIndex, ExtendPaths(groupValuePaths[bk], nodeId));
                        StoreSetPaths("keys", outputIndex, ExtendPaths(groupKeyPaths[bk], nodeId));
                        if (ctx.IsPreview)
                            ctx.SampledTotals.Add(new SampledTotal(nodeId, $"group:{bk}", totals[bk], IsPartial: true));
                    }
                    outputs["values"] = DataValue.Collection(valueList);
                    outputs["keys"] = DataValue.Collection(keyList);
                    break;
                }

                case NodeTypeConstants.Join:
                {
                    var left = ReadSetInput("left");
                    var right = ReadSetInput("right");
                    var leftEdge = graph.IncomingEdge(nodeId, "left");
                    var rightEdge = graph.IncomingEdge(nodeId, "right");
                    var leftKey = GetString(node, "leftKey") ?? "";
                    var rightKey = GetString(node, "rightKey") ?? "";
                    var cardinality = GetString(node, "cardinality") ?? "Inner";
                    var dupPolicy = GetString(node, "duplicateKey") ?? "All";
                    var fieldConflict = GetString(node, "fieldConflict") ?? "Right";
                    if (cardinality is not ("Inner" or "Left" or "Right" or "Full"))
                    {
                        diag.Error(DiagnosticCodes.MapCardinalityMismatch, DiagnosticSubsystem.Mapping,
                            DiagnosticStage.Execution,
                            $"Unknown join cardinality '{cardinality}'.", nodeId: nodeId);
                        cardinality = "Inner";
                    }
                    if (fieldConflict is not ("Left" or "Right" or "Fail"))
                    {
                        diag.Error(DiagnosticCodes.MapInvalidJoinFieldConflictPolicy,
                            DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                            $"Unknown join fieldConflict '{fieldConflict}'. Expected Left, Right or Fail.",
                            nodeId: nodeId);
                        outputs["rows"] = DataValue.Collection(Array.Empty<DataValue>());
                        return outputs;
                    }

                    // Build right-hand index keyed by rightKey, honouring the duplicate policy.
                    var index = new Dictionary<string, List<(Dictionary<string, DataValue> Fields, int ItemIndex)>>(StringComparer.Ordinal);
                    var rightOrder = new List<string>();
                    foreach (var (rec, itemIndex) in right.Select((record, index) => (record, index)))
                    {
                        if (!TryRecordFields(rec, out var fields)) continue;
                        var rk = GetField(fields, rightKey).AsString();
                        if (!index.TryGetValue(rk, out var bucket))
                        {
                            bucket = new List<(Dictionary<string, DataValue> Fields, int ItemIndex)>();
                            index[rk] = bucket;
                            rightOrder.Add(rk);
                        }
                        if (bucket.Count > 0 && dupPolicy is "Fail" or "First")
                        {
                            if (dupPolicy == "Fail")
                            {
                                diag.Error(DiagnosticCodes.MapDuplicateJoinKey, DiagnosticSubsystem.Mapping,
                                    DiagnosticStage.Execution,
                                    $"Duplicate right key '{rk}' with duplicateKey=Fail.", nodeId: nodeId);
                                outputs["rows"] = DataValue.Collection(Array.Empty<DataValue>());
                                return outputs;
                            }
                            continue; // First: keep only the first per key
                        }
                        bucket.Add((fields, itemIndex));
                    }

                    var joined = new List<DataValue>();
                    var matchedRights = new HashSet<string>(StringComparer.Ordinal);
                    bool AddRow(
                        Dictionary<string, DataValue>? l, int? leftItemIndex,
                        Dictionary<string, DataValue>? r, int? rightItemIndex)
                    {
                        if (joined.Count >= ctx.MaxJoinExpansionRows)
                        {
                            diag.Error(DiagnosticCodes.MapJoinExpansionExceeded, DiagnosticSubsystem.Mapping,
                                DiagnosticStage.Execution,
                                $"Join expansion exceeded MaxJoinExpansionRows={ctx.MaxJoinExpansionRows}. "
                                + "Publication is blocked before output.", nodeId: nodeId);
                            return false;
                        }

                        var merged = new Dictionary<string, DataValue>(StringComparer.Ordinal);
                        if (l is not null) foreach (var kv in l) merged[kv.Key] = kv.Value;
                        if (r is not null)
                        {
                            foreach (var kv in r)
                            {
                                if (!merged.TryGetValue(kv.Key, out var leftValue))
                                {
                                    merged[kv.Key] = kv.Value;
                                    continue;
                                }

                                if (leftValue.Equals(kv.Value))
                                {
                                    if (fieldConflict == "Right") merged[kv.Key] = kv.Value;
                                    continue;
                                }

                                switch (fieldConflict)
                                {
                                    case "Left":
                                        break;
                                    case "Right":
                                        merged[kv.Key] = kv.Value;
                                        break;
                                    case "Fail":
                                        diag.Error(DiagnosticCodes.MapJoinFieldConflict,
                                            DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                                            $"Join field '{kv.Key}' has different values on the left and right inputs; "
                                            + "fieldConflict=Fail.", nodeId: nodeId);
                                        return false;
                                }
                            }
                        }
                        var outputIndex = joined.Count;
                        joined.Add(DataValue.Record(merged));

                        var leftJoinKeyPaths = leftItemIndex is { } leftIndex
                            ? ReadSetFieldPaths(leftEdge, leftIndex, leftKey)
                            : Array.Empty<SourceTransformationPath>();
                        var rightJoinKeyPaths = rightItemIndex is { } rightIndex
                            ? ReadSetFieldPaths(rightEdge, rightIndex, rightKey)
                            : Array.Empty<SourceTransformationPath>();
                        foreach (var fieldName in merged.Keys)
                        {
                            var fieldPaths = new List<SourceTransformationPath>();
                            var hasLeftField = l is not null && l.ContainsKey(fieldName);
                            var hasRightField = r is not null && r.ContainsKey(fieldName);
                            var equalBothSides = hasLeftField && hasRightField && fieldConflict == "Fail";
                            var comesFromRight = hasRightField && (!hasLeftField || fieldConflict == "Right");
                            if (equalBothSides && leftItemIndex is { } equalLeftFieldIndex)
                            {
                                AddDistinctPaths(fieldPaths,
                                    ReadSetFieldPaths(leftEdge, equalLeftFieldIndex, fieldName));
                                if (rightItemIndex is { } equalRightFieldIndex)
                                    AddDistinctPaths(fieldPaths,
                                        ReadSetFieldPaths(rightEdge, equalRightFieldIndex, fieldName));
                            }
                            else if (comesFromRight && rightItemIndex is { } rightFieldIndex)
                                AddDistinctPaths(fieldPaths,
                                    ReadSetFieldPaths(rightEdge, rightFieldIndex, fieldName));
                            else if (hasLeftField && leftItemIndex is { } leftFieldIndex)
                                AddDistinctPaths(fieldPaths,
                                    ReadSetFieldPaths(leftEdge, leftFieldIndex, fieldName));
                            AddDistinctPaths(fieldPaths, leftJoinKeyPaths);
                            AddDistinctPaths(fieldPaths, rightJoinKeyPaths);
                            var extendedPaths = ExtendPaths(fieldPaths, nodeId);
                            StoreSetPaths("rows", outputIndex, extendedPaths);
                            StoreSetPaths("rows", outputIndex, extendedPaths, fieldName);
                        }
                        return true;
                    }

                    foreach (var (rec, itemIndex) in left.Select((record, index) => (record, index)))
                    {
                        if (!TryRecordFields(rec, out var fields)) continue;
                        var lk = GetField(fields, leftKey).AsString();
                        var hasMatch = index.TryGetValue(lk, out var bucket) && bucket.Count > 0;
                        if (hasMatch)
                        {
                            foreach (var rrow in bucket!)
                            {
                                if (!AddRow(fields, itemIndex, rrow.Fields, rrow.ItemIndex))
                                {
                                    outputs["rows"] = DataValue.Collection(joined);
                                    return outputs;
                                }
                                matchedRights.Add(lk);
                            }
                        }
                        else if (cardinality is "Left" or "Full")
                        {
                            if (!AddRow(fields, itemIndex, null, null))
                            {
                                outputs["rows"] = DataValue.Collection(joined);
                                return outputs;
                            }
                        }
                    }

                    if (cardinality is "Right" or "Full")
                    {
                        foreach (var rk in rightOrder)
                        {
                            var bucket = index[rk];
                            if (matchedRights.Contains(rk)) continue;
                            foreach (var rrow in bucket)
                            {
                                if (!AddRow(null, null, rrow.Fields, rrow.ItemIndex))
                                {
                                    outputs["rows"] = DataValue.Collection(joined);
                                    return outputs;
                                }
                            }
                        }
                    }

                    outputs["rows"] = DataValue.Collection(joined);
                    break;
                }

                default:
                    diag.Error(DiagnosticCodes.MapUnknownNodeType, DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Execution,
                        $"Unknown set-phase node type '{node.NodeType}'.", nodeId: nodeId);
                    outputs["value"] = DataValue.Missing();
                    break;
            }
            return outputs;
        };
    }

    /// <summary>Unwrap a Record-shaped DataValue into its field dictionary.</summary>
    private static bool TryRecordFields(DataValue rec, out Dictionary<string, DataValue> fields)
    {
        if (rec.Raw is IReadOnlyDictionary<string, DataValue> dict)
        {
            fields = dict.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return true;
        }
        fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        return false;
    }

    private static DataValue GetField(IReadOnlyDictionary<string, DataValue> fields, string name)
        => fields.TryGetValue(name, out var v) ? v : DataValue.Missing(PortShape.String);

    private static IReadOnlyList<DataValue> ResolveCollection(
        RowSnapshot snapshot, MappingEdge? edge)
    {
        if (edge is null) return Array.Empty<DataValue>();
        var list = new List<DataValue>(snapshot.Count);
        for (int r = 0; r < snapshot.Count; r++)
            list.Add(snapshot.Get(r, edge.SourceNodeId, edge.SourcePortName));
        return list;
    }

    /// <summary>
    /// Build the per-node evaluator. Reads inputs from the frame and writes outputs
    /// back to the frame. Records provenance for any input that carries a source.
    /// </summary>
    private Action<int, EvaluationFrame, ExecutionContext, DiagnosticBag> BuildEvaluator(
        MappingGraph graph, string nodeId, FilingProfile profile, DiagnosticBag compileDiag)
    {
        var node = graph.Nodes[nodeId];
        var sig = _catalog.Find(node.NodeType)!;
        var periodInputConnected = graph.IncomingEdge(nodeId, "period") is not null;

        return (row, frame, ctx, diag) =>
        {
            // Read input ports into named slots.
            var inputs = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            foreach (var port in node.InputPorts)
            {
                var edge = graph.IncomingEdge(nodeId, port.Name);
                if (edge is null)
                {
                    inputs[port.Name] = DataValue.Missing(port.Shape);
                    continue;
                }
                var upstream = frame.Get(edge.SourceNodeId, edge.SourcePortName);
                inputs[port.Name] = upstream;

                // Record provenance link: this slot inherits upstream contributors.
                var upstreamKey = ProvenanceTracker.SlotKey(edge.SourceNodeId, edge.SourcePortName, row);
                var thisKey = ProvenanceTracker.SlotKey(nodeId, port.Name, row);
                ctx.Provenance.MergeSources(thisKey, new[] { upstreamKey });
                ctx.Provenance.MergeTransformations(thisKey, new[] { upstreamKey });
                ctx.Provenance.MergeSourceTransformationPaths(thisKey, new[] { upstreamKey });
            }

            var factConstructSourcePaths = node.NodeType == NodeTypeConstants.FactConstruct
                ? CollectFactConstructSourcePaths(node, row, ctx)
                : null;
            var outputs = Dispatch(node, sig, inputs, row, ctx, diag, profile,
                periodInputConnected, factConstructSourcePaths);
            foreach (var (name, value) in outputs)
            {
                DecimalDomain.ValidateValue(value, ctx.MaxSignificantDigits, nodeId);
                frame.Set(nodeId, name, value);

                // Propagate all upstream contributors across transforms and joins,
                // rather than relying only on the first DataValue.Source reference.
                // Operations such as ratios can combine several source cells even
                // though their result value has no single direct source property.
                var outputKey = ProvenanceTracker.SlotKey(nodeId, name, row);
                var inputKeys = node.InputPorts
                    .Select(port => ProvenanceTracker.SlotKey(nodeId, port.Name, row))
                    .ToArray();
                ctx.Provenance.MergeSources(outputKey, inputKeys);
                ctx.Provenance.MergeTransformations(outputKey, inputKeys);
                ctx.Provenance.MergeSourceTransformationPaths(outputKey, inputKeys);
                // Source adapters such as ExcelField are direct pass-throughs: their
                // evaluator still identifies the mapping step that bound the cell.
                ctx.Provenance.RecordTransformation(outputKey, nodeId);
                ctx.Provenance.RecordTransformationOnSourcePaths(outputKey, nodeId);
                if (value.Source is not null)
                {
                    ctx.Provenance.RecordSource(outputKey, value.Source);
                    ctx.Provenance.RecordSourceTransformation(outputKey, value.Source, nodeId);
                }
            }
        };
    }

    private static IReadOnlyList<SourceTransformationPath> CollectFactConstructSourcePaths(
        MappingNode node, int row, ExecutionContext context)
    {
        var paths = new List<SourceTransformationPath>();
        foreach (var port in node.InputPorts)
        {
            var slotKey = ProvenanceTracker.SlotKey(node.NodeId, port.Name, row);
            foreach (var path in context.Provenance.GetSourceTransformationPaths(slotKey))
            {
                var transformationIds = path.TransformationIds.Contains(node.NodeId, StringComparer.Ordinal)
                    ? path.TransformationIds
                    : path.TransformationIds.Append(node.NodeId).ToArray();
                if (paths.Any(existing => existing.Source == path.Source
                    && existing.TransformationIds.SequenceEqual(transformationIds, StringComparer.Ordinal)))
                    continue;
                paths.Add(new SourceTransformationPath(path.Source, transformationIds));
            }
        }
        return paths;
    }

    private static Dictionary<string, DataValue> Dispatch(
        MappingNode node,
        FunctionSignature sig,
        Dictionary<string, DataValue> inputs,
        int row,
        ExecutionContext ctx,
        DiagnosticBag diag,
        FilingProfile profile,
        bool periodInputConnected,
        IReadOnlyList<SourceTransformationPath>? factConstructSourcePaths)
    {
        var outputs = new Dictionary<string, DataValue>(StringComparer.Ordinal);

        switch (node.NodeType)
        {
            case NodeTypeConstants.ExcelField:
            {
                var bindingId = GetString(node, "bindingId");
                var column = GetString(node, "column");
                if (bindingId is null || !ctx.LoadedSources.TryGetValue(bindingId, out var records))
                {
                    diag.Error(
                        DiagnosticCodes.IoSchemaMismatch,
                        DiagnosticSubsystem.Adapter,
                        DiagnosticStage.Execution,
                        $"Binding '{bindingId}' is not loaded in execution context.",
                        nodeId: node.NodeId);
                    outputs["value"] = DataValue.Missing(PortShape.String);
                    break;
                }
                if (row >= records.Count)
                {
                    outputs["value"] = DataValue.Missing(PortShape.String);
                    break;
                }
                var record = records[row];
                // Optional row filter: emit Missing for rows that don't match.
                var filterCol = GetString(node, "rowFilterColumn");
                var filterVal = GetString(node, "rowFilterValue");
                if (!string.IsNullOrEmpty(filterCol) && !string.IsNullOrEmpty(filterVal))
                {
                    if (!record.Fields.TryGetValue(filterCol, out var fc))
                    {
                        outputs["value"] = DataValue.Missing(PortShape.String);
                        break;
                    }
                    var fcs = fc.AsString();
                    if (!string.Equals(fcs, filterVal, StringComparison.Ordinal))
                    {
                        outputs["value"] = DataValue.Missing(PortShape.String);
                        break;
                    }
                }
                var fieldName = column ?? "";
                var cell = record.Fields.TryGetValue(fieldName, out var v) ? v : DataValue.Missing();
                if (cell.Source is null
                    && record.CellSources is not null
                    && record.CellSources.TryGetValue(fieldName, out var cellSource))
                    cell = cell.WithSourceIfMissing(cellSource);
                var textCell = cell.Shape == PortShape.String ? cell : DataValue.Missing(PortShape.String);
                // Emit at row granularity: for collection ports the frame stores per-row indexed values.
                outputs["value"] = cell;
                outputs["textValue"] = textCell;
                if (cell.Source is not null)
                {
                    var slotKey = ProvenanceTracker.SlotKey(node.NodeId, "value", row);
                    ctx.Provenance.RecordSource(slotKey, cell.Source);
                }
                if (textCell.Source is not null)
                    ctx.Provenance.RecordSource(ProvenanceTracker.SlotKey(node.NodeId, "textValue", row), textCell.Source);
                break;
            }

            case NodeTypeConstants.Constant:
            {
                var literal = GetString(node, "literal") ?? "";
                var shapeStr = GetString(node, "shape") ?? "Decimal";
                var shape = Enum.TryParse<PortShape>(shapeStr, true, out var s) ? s : PortShape.Decimal;
                outputs["value"] = shape switch
                {
                    PortShape.Decimal => TryParseDecimalInvariant(literal, ctx, diag, node, out var d)
                        ? DataValue.DecimalValue(d) : DataValue.Missing(PortShape.Decimal),
                    PortShape.Integer => long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                        ? DataValue.IntegerValue(l) : DataValue.Missing(PortShape.Integer),
                    _ => DataValue.StringValue(literal)
                };
                break;
            }

            case NodeTypeConstants.RunParameter:
            {
                var name = GetString(node, "name") ?? "";
                var shapeStr = GetString(node, "shape") ?? "String";
                var shape = Enum.TryParse<PortShape>(shapeStr, true, out var s) ? s : PortShape.String;
                if (!ctx.Parameters.TryGetValue(name, out var raw))
                {
                    diag.Error(
                        DiagnosticCodes.MapUnboundRequiredInput,
                        DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Execution,
                        $"Run parameter '{name}' is not bound in the execution context.",
                        nodeId: node.NodeId);
                    outputs["value"] = DataValue.Missing(shape);
                    break;
                }
                outputs["value"] = shape switch
                {
                    PortShape.Decimal => TryParseDecimalInvariant(raw, ctx, diag, node, out var d)
                        ? DataValue.DecimalValue(d) : DataValue.Missing(shape),
                    PortShape.Integer => long.TryParse(raw, out var l) ? DataValue.IntegerValue(l) : DataValue.Missing(shape),
                    _ => DataValue.StringValue(raw)
                };
                break;
            }

            case NodeTypeConstants.ParseDecimal:
            {
                var input = inputs.GetValueOrDefault("input", DataValue.Missing(PortShape.String));
                if (input.IsMissing || input.IsNil)
                {
                    outputs["value"] = input.IsNil
                        ? DataValue.ExplicitNil(PortShape.Decimal)
                        : DataValue.Missing(PortShape.Decimal);
                    break;
                }
                var text = input.AsString();
                var cultureTag = GetString(node, "culture") ?? "invariant";
                if (!TryParseDecimalWithCulture(text, cultureTag, ctx, diag, node, input.Source, out var parsed))
                {
                    outputs["value"] = DataValue.Missing(PortShape.Decimal);
                    break;
                }
                outputs["value"] = DataValue.DecimalValue(parsed, input.Source, text);
                break;
            }

            case NodeTypeConstants.Scale:
            {
                var input = inputs.GetValueOrDefault("input", DataValue.Missing(PortShape.Decimal));
                var factor = decimal.Parse(GetString(node, "factor") ?? "1", CultureInfo.InvariantCulture);
                if (input.IsMissing || input.IsNil)
                {
                    outputs["value"] = input.Presence == ValuePresence.ExplicitNil
                        ? DataValue.ExplicitNil(PortShape.Decimal)
                        : DataValue.Missing(PortShape.Decimal);
                    break;
                }
                try
                {
                    var v = input.AsDecimal() * factor;
                    outputs["value"] = DataValue.DecimalValue(v, input.Source);
                }
                catch (OverflowException)
                {
                    throw new NumericOverflowException(
                        $"Scale multiplication overflowed decimal range on node '{node.DisplayName}'.");
                }
                break;
            }

            case NodeTypeConstants.ChangeSign:
            {
                var input = inputs.GetValueOrDefault("input", DataValue.Missing(PortShape.Decimal));
                if (input.IsMissing || input.IsNil)
                {
                    outputs["value"] = DataValue.Missing(PortShape.Decimal);
                    break;
                }
                outputs["value"] = DataValue.DecimalValue(-input.AsDecimal(), input.Source);
                break;
            }

            case NodeTypeConstants.Coalesce:
            {
                var a = inputs.GetValueOrDefault("a", DataValue.Missing(PortShape.Decimal));
                var b = inputs.GetValueOrDefault("b", DataValue.Missing(PortShape.Decimal));
                outputs["value"] = a.Presence is ValuePresence.Present ? a : b;
                break;
            }

            case NodeTypeConstants.Round:
            {
                var input = inputs.GetValueOrDefault("input", DataValue.Missing(PortShape.Decimal));
                if (input.IsMissing || input.IsNil)
                {
                    outputs["value"] = input.IsNil
                        ? DataValue.ExplicitNil(PortShape.Decimal)
                        : DataValue.Missing(PortShape.Decimal);
                    break;
                }
                var digitsStr = GetString(node, "digits") ?? "0";
                if (!int.TryParse(digitsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var digits)
                    || digits < 0 || digits > 30)
                {
                    diag.Error(DiagnosticCodes.MapInvalidRoundingMode, DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Execution,
                        $"Round digits '{digitsStr}' is outside the supported range 0..30.",
                        nodeId: node.NodeId, source: input.Source);
                    outputs["value"] = DataValue.Missing(PortShape.Decimal);
                    break;
                }
                var modeStr = GetString(node, "mode") ?? "HalfEven";
                decimal rounded;
                try
                {
                    rounded = modeStr switch
                    {
                        "HalfUp" => RoundHalfUp(input.AsDecimal(), digits),
                        "HalfEven" => decimal.Round(input.AsDecimal(), digits, MidpointRounding.ToEven),
                        "Truncate" => TruncateToDigits(input.AsDecimal(), digits),
                        _ => decimal.MinValue // sentinel -> invalid mode
                    };
                    if (rounded == decimal.MinValue)
                    {
                        diag.Error(DiagnosticCodes.MapInvalidRoundingMode, DiagnosticSubsystem.Mapping,
                            DiagnosticStage.Execution,
                            $"Unknown rounding mode '{modeStr}'.",
                            nodeId: node.NodeId, source: input.Source);
                        outputs["value"] = DataValue.Missing(PortShape.Decimal);
                        break;
                    }
                }
                catch (OverflowException)
                {
                    throw new NumericOverflowException(
                        $"Round overflowed decimal range on node '{node.DisplayName}'.");
                }
                outputs["value"] = DataValue.DecimalValue(rounded, input.Source);
                break;
            }

            case NodeTypeConstants.Ratio:
            {
                var n = inputs.GetValueOrDefault("numerator", DataValue.Missing(PortShape.Decimal));
                var d = inputs.GetValueOrDefault("denominator", DataValue.Missing(PortShape.Decimal));
                if (n.IsMissing || d.IsMissing || n.IsNil || d.IsNil)
                {
                    outputs["value"] = DataValue.Missing(PortShape.Decimal);
                    break;
                }
                var dv = d.AsDecimal();
                if (dv == 0m)
                {
                    var policy = GetString(node, "divideByZero") ?? "Diagnostic";
                    switch (policy)
                    {
                        case "Missing":
                            outputs["value"] = DataValue.Missing(PortShape.Decimal);
                            break;
                        case "NaN":
                            // CLR decimal cannot represent NaN; we emit a Missing sentinel and
                            // record an informational diagnostic rather than a numeric NaN.
                            diag.Warn(DiagnosticCodes.MapDivideByZero, DiagnosticSubsystem.Mapping,
                                DiagnosticStage.Execution,
                                "Divide-by-zero resolved as Missing (CLR decimal has no NaN).",
                                nodeId: node.NodeId, source: d.Source);
                            outputs["value"] = DataValue.Missing(PortShape.Decimal);
                            break;
                        default:
                            diag.Error(DiagnosticCodes.MapDivideByZero, DiagnosticSubsystem.Mapping,
                                DiagnosticStage.Execution,
                                "Ratio denominator is zero.",
                                nodeId: node.NodeId, source: d.Source);
                            outputs["value"] = DataValue.Missing(PortShape.Decimal);
                            break;
                    }
                    break;
                }
                try
                {
                    var result = n.AsDecimal() / dv;
                    outputs["value"] = DataValue.DecimalValue(result, n.Source ?? d.Source);
                }
                catch (OverflowException)
                {
                    throw new NumericOverflowException(
                        $"Ratio division overflowed decimal range on node '{node.DisplayName}'.");
                }
                break;
            }

            case NodeTypeConstants.Filter:
            {
                var input = inputs.GetValueOrDefault("input", DataValue.Missing(PortShape.Decimal));
                var pred = inputs.GetValueOrDefault("predicate", DataValue.Missing(PortShape.Boolean));
                if (pred.IsMissing || pred.IsNil || !pred.AsBoolean())
                {
                    outputs["value"] = DataValue.Missing(PortShape.Decimal);
                    break;
                }
                outputs["value"] = input;
                break;
            }

            case NodeTypeConstants.Compare:
            {
                var a = inputs.GetValueOrDefault("a", DataValue.Missing(PortShape.Decimal));
                var b = inputs.GetValueOrDefault("b", DataValue.Missing(PortShape.Decimal));
                if (a.IsMissing || b.IsMissing || a.IsNil || b.IsNil)
                {
                    outputs["value"] = DataValue.Missing(PortShape.Boolean);
                    break;
                }
                var op = GetString(node, "op") ?? "Eq";
                var av = a.AsDecimal();
                var bv = b.AsDecimal();
                bool? cmp = op switch
                {
                    "Eq" => av == bv,
                    "Ne" => av != bv,
                    "Lt" => av < bv,
                    "Le" => av <= bv,
                    "Gt" => av > bv,
                    "Ge" => av >= bv,
                    _ => (bool?)null
                };
                if (cmp is null)
                {
                    diag.Error(DiagnosticCodes.MapInvalidComparisonOp, DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Execution,
                        $"Invalid comparison operator '{op}'.", nodeId: node.NodeId);
                    outputs["value"] = DataValue.Missing(PortShape.Boolean);
                    break;
                }
                outputs["value"] = DataValue.BoolValue(cmp.Value);
                break;
            }

            case NodeTypeConstants.Lookup:
            {
                var key = inputs.GetValueOrDefault("key", DataValue.Missing(PortShape.String));
                var table = GetString(node, "table");
                var policy = GetString(node, "missingKey") ?? "Diagnostic";
                var keyStr = key.AsString();
                outputs["value"] = DataValue.Missing(PortShape.Decimal);
                bool found = false;
                decimal foundVal = 0m;
                if (table is not null
                    && ctx.Parameters.TryGetValue("lookup:" + table, out var rawTable))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(rawTable);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                            && doc.RootElement.TryGetProperty(keyStr, out var el)
                            && el.ValueKind == System.Text.Json.JsonValueKind.Number
                            && el.TryGetDecimal(out var dv))
                        {
                            found = true;
                            foundVal = dv;
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        diag.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Adapter,
                            DiagnosticStage.Execution,
                            $"Lookup table '{table}' is not a valid JSON object.",
                            nodeId: node.NodeId, source: key.Source);
                    }
                }
                if (found)
                {
                    outputs["value"] = DataValue.DecimalValue(foundVal, key.Source);
                    break;
                }
                switch (policy)
                {
                    case "Missing":
                        outputs["value"] = DataValue.Missing(PortShape.Decimal);
                        break;
                    case "Default":
                        var defStr = GetString(node, "defaultValue");
                        if (decimal.TryParse(defStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var def))
                            outputs["value"] = DataValue.DecimalValue(def);
                        break;
                    default:
                        diag.Error(DiagnosticCodes.MapLookupKeyMissing, DiagnosticSubsystem.Mapping,
                            DiagnosticStage.Execution,
                            $"Lookup key '{keyStr}' was not found in table '{table}'.",
                            nodeId: node.NodeId, source: key.Source);
                        break;
                }
                break;
            }

            case NodeTypeConstants.BuildInstantDate:
            {
                var input = inputs.GetValueOrDefault("dateText", DataValue.Missing(PortShape.String));
                if (input.IsMissing || input.IsNil)
                {
                    outputs["value"] = DataValue.Missing(PortShape.Instant);
                    break;
                }
                if (!DateOnly.TryParseExact(input.AsString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                {
                    diag.Error(
                        DiagnosticCodes.MapInvalidConstant,
                        DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Execution,
                        $"'{input.AsString()}' is not a valid ISO yyyy-MM-dd date.",
                        nodeId: node.NodeId,
                        source: input.Source);
                    outputs["value"] = DataValue.Missing(PortShape.Instant);
                    break;
                }
                outputs["value"] = DataValue.Opaque(PortShape.Instant,
                    new PeriodPayload(Instant: date, Start: null, End: null), src: input.Source);
                break;
            }

            case NodeTypeConstants.BuildDurationDate:
            {
                var s = inputs.GetValueOrDefault("startText", DataValue.Missing(PortShape.String));
                var e = inputs.GetValueOrDefault("endText", DataValue.Missing(PortShape.String));
                if (s.IsMissing) diag.Error(DiagnosticCodes.MapMissingPeriodStart, DiagnosticSubsystem.Mapping,
                    DiagnosticStage.Execution, "Duration period requires a start date.", nodeId: node.NodeId);
                if (e.IsMissing) diag.Error(DiagnosticCodes.MapMissingPeriodEnd, DiagnosticSubsystem.Mapping,
                    DiagnosticStage.Execution, "Duration period requires an end date.", nodeId: node.NodeId);
                if (s.IsMissing || e.IsMissing
                    || !DateOnly.TryParseExact(s.AsString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var sd)
                    || !DateOnly.TryParseExact(e.AsString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var ed))
                {
                    outputs["value"] = DataValue.Missing(PortShape.Instant);
                    break;
                }
                outputs["value"] = DataValue.Opaque(PortShape.Instant,
                    new PeriodPayload(Instant: null, Start: sd, End: ed), src: s.Source ?? e.Source);
                break;
            }

            case NodeTypeConstants.FactConstruct:
            {
                // Silent skip: when the value input is Missing (e.g., blank cell or a row
                // excluded by a filter upstream), we do not emit a fact and we do not
                // raise a diagnostic. This allows one mapping to iterate a mixed table
                // while producing only the intended facts. A set-driven fact (e.g. fed by
                // GroupBy) carries its per-item value on the collection-shaped `values` port.
                var valueIn = inputs.GetValueOrDefault("value", DataValue.Missing(PortShape.Decimal));
                if (valueIn.IsMissing)
                    valueIn = inputs.GetValueOrDefault("values", DataValue.Missing(PortShape.Decimal));
                var textIn = inputs.GetValueOrDefault("textValue", DataValue.Missing(PortShape.String));
                var hasNumericInput = !valueIn.IsMissing;
                var hasTextInput = textIn.Presence is ValuePresence.Present or ValuePresence.ExplicitNil;
                if (!hasNumericInput && !hasTextInput)
                {
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }
                if (hasNumericInput && hasTextInput)
                {
                    diag.Error(
                        DiagnosticCodes.XbrUnsupportedConstruct,
                        DiagnosticSubsystem.XBRL,
                        DiagnosticStage.Execution,
                        "FactConstruct cannot receive both numeric and text values.",
                        nodeId: node.NodeId,
                        portName: "textValue",
                        source: textIn.Source ?? valueIn.Source,
                        sourceTransformationPaths: factConstructSourcePaths);
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }
                var factValueSource = hasTextInput ? textIn.Source : valueIn.Source;
                var factValuePortName = hasTextInput ? "textValue" : "value";

                var concept = GetString(node, "concept")
                    ?? throw new InvalidOperationException($"FactConstruct node '{node.DisplayName}' has no concept parameter.");
                var conceptMeta = profile.FindConcept(concept);
                if (conceptMeta is null)
                {
                    diag.Error(
                        DiagnosticCodes.XbrConceptUnknown,
                        DiagnosticSubsystem.XBRL,
                        DiagnosticStage.Execution,
                        $"Concept '{concept}' is not defined in profile '{profile.ProfileId}'.",
                        nodeId: node.NodeId,
                        portName: factValuePortName,
                        source: factValueSource,
                        sourceTransformationPaths: factConstructSourcePaths);
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }
                if (conceptMeta.IsAbstract)
                {
                    diag.Error(
                        DiagnosticCodes.XbrConceptUnknown,
                        DiagnosticSubsystem.XBRL,
                        DiagnosticStage.Execution,
                        $"Concept '{concept}' is abstract and cannot carry fact values.",
                        nodeId: node.NodeId,
                        portName: factValuePortName,
                        source: factValueSource,
                        sourceTransformationPaths: factConstructSourcePaths);
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }

                var unitStr = GetString(node, "unit");
                XbrlUnit? unit = null;
                if (!string.IsNullOrEmpty(unitStr))
                {
                    if (!profile.SupportedUnits.Contains(unitStr))
                    {
                        diag.Error(
                            DiagnosticCodes.XbrUnitMissing,
                            DiagnosticSubsystem.XBRL,
                            DiagnosticStage.Execution,
                            $"Unit '{unitStr}' is not supported by profile '{profile.ProfileId}'.",
                            nodeId: node.NodeId,
                            portName: factValuePortName,
                            source: factValueSource,
                            sourceTransformationPaths: factConstructSourcePaths);
                        outputs["fact"] = DataValue.Missing(PortShape.Fact);
                        break;
                    }
                    unit = new XbrlUnit(unitStr);
                }

                if (!FactDimensionParser.TryParse(
                        GetString(node, "dimensions"),
                        profile.DimensionDomains,
                        out var dimensions,
                        out var dimensionError))
                {
                    diag.Error(
                        DiagnosticCodes.XbrDimensionInvalid,
                        DiagnosticSubsystem.XBRL,
                        DiagnosticStage.Execution,
                        dimensionError,
                        nodeId: node.NodeId,
                        portName: factValuePortName,
                        source: factValueSource,
                        sourceTransformationPaths: factConstructSourcePaths);
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }

                var entityIn = inputs.GetValueOrDefault("entity", DataValue.Missing(PortShape.String));
                var entity = entityIn.AsString();
                var periodIn = inputs.GetValueOrDefault("period", DataValue.Missing(PortShape.Instant));
                var instantIn = inputs.GetValueOrDefault("instantDate", DataValue.Missing(PortShape.Instant));

                var periodPayload = (periodIn.Raw as PeriodPayload ?? instantIn.Raw as PeriodPayload);
                XbrlPeriod? period = periodPayload switch
                {
                    { Instant: { } inst } => XbrlPeriod.OfInstant(inst),
                    { Start: var s, End: var e } when s.HasValue && e.HasValue
                        => XbrlPeriod.OfDuration(s.Value, e.Value),
                    _ => null
                };

                if (period is null)
                {
                    diag.Error(
                        DiagnosticCodes.XbrPeriodInvalid,
                        DiagnosticSubsystem.XBRL,
                        DiagnosticStage.Execution,
                        "Fact requires a valid instant or duration period.",
                        nodeId: node.NodeId,
                        portName: periodInputConnected ? "period" : "instantDate",
                        source: periodIn.Source ?? instantIn.Source ?? factValueSource,
                        sourceTransformationPaths: factConstructSourcePaths);
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }

                int? decimals = null;
                if (node.Parameters.TryGetValue("decimals", out var dv))
                {
                    var decimalsStr = dv?.ToString();
                    if (string.Equals(decimalsStr, "INF", StringComparison.OrdinalIgnoreCase))
                    {
                        decimals = null;
                    }
                    else if (!int.TryParse(decimalsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        diag.Error(
                            DiagnosticCodes.XbrDecimalsOutOfRange,
                            DiagnosticSubsystem.XBRL,
                            DiagnosticStage.Execution,
                            "Decimals must be an integer in the supported range [-30, 30] or INF.",
                            nodeId: node.NodeId,
                            portName: factValuePortName,
                            source: factValueSource,
                            sourceTransformationPaths: factConstructSourcePaths);
                        outputs["fact"] = DataValue.Missing(PortShape.Fact);
                        break;
                    }
                    else
                    {
                        // Bounded exact-decimal range (section 3): reject just-outside limits.
                        if (parsed < -30 || parsed > 30)
                        {
                            diag.Error(
                                DiagnosticCodes.XbrDecimalsOutOfRange,
                                DiagnosticSubsystem.XBRL,
                                DiagnosticStage.Execution,
                                $"Decimals value {parsed} is outside the supported range [-30, 30].",
                                nodeId: node.NodeId,
                                portName: factValuePortName,
                                source: factValueSource,
                                sourceTransformationPaths: factConstructSourcePaths);
                            outputs["fact"] = DataValue.Missing(PortShape.Fact);
                            break;
                        }
                        decimals = parsed;
                    }
                }

                var pf = new PendingFact
                {
                    ConceptName = concept,
                    EntityScheme = "http://xbrl.org/entity/",
                    EntityIdentifier = entity,
                    Period = period,
                    Unit = unit,
                    Decimals = decimals,
                    Language = GetString(node, "language"),
                    Dimensions = dimensions,
                    Value = hasNumericInput && valueIn.Presence == ValuePresence.Present ? valueIn.AsDecimal() : null,
                    TextValue = textIn.Presence == ValuePresence.Present ? textIn.AsString() : null,
                    IsNil = valueIn.IsNil || textIn.IsNil
                };

                // Period-vs-concept type check (acceptance: wrong period type produces useful error).
                var probe = new XbrlFact
                {
                    ConceptName = concept,
                    EntityScheme = pf.EntityScheme,
                    EntityIdentifier = pf.EntityIdentifier,
                    Period = period,
                    Unit = unit,
                    Value = pf.Value,
                    TextValue = pf.TextValue,
                    Decimals = pf.Decimals,
                    Language = pf.Language,
                    Dimensions = pf.Dimensions,
                    IsNil = pf.IsNil
                };
                if (!probe.ValidateAgainst(conceptMeta, out var reason, out var issue))
                {
                    var (diagnosticCode, portName, source) = issue switch
                    {
                        FactValidationIssue.InvalidEntity =>
                            (DiagnosticCodes.XbrEntityMissing, "entity", entityIn.Source ?? factValueSource),
                        FactValidationIssue.InvalidPeriod =>
                            (DiagnosticCodes.XbrPeriodInvalid, "period", periodIn.Source ?? instantIn.Source ?? factValueSource),
                        FactValidationIssue.PeriodTypeMismatch =>
                            (DiagnosticCodes.XbrPeriodTypeMismatch, "period", periodIn.Source ?? instantIn.Source ?? factValueSource),
                        FactValidationIssue.InvalidUnit =>
                            (DiagnosticCodes.XbrUnitMissing, factValuePortName, factValueSource ?? entityIn.Source),
                        FactValidationIssue.InvalidDecimals =>
                            (DiagnosticCodes.XbrDecimalsOutOfRange, factValuePortName, factValueSource),
                        FactValidationIssue.InvalidDimensions =>
                            (DiagnosticCodes.XbrDimensionInvalid, factValuePortName, factValueSource),
                        FactValidationIssue.InvalidConcept =>
                            (DiagnosticCodes.XbrConceptUnknown, factValuePortName, factValueSource),
                        FactValidationIssue.InvalidLanguage =>
                            (DiagnosticCodes.XbrUnsupportedConstruct, factValuePortName, factValueSource ?? entityIn.Source),
                        FactValidationIssue.InvalidValue =>
                            (DiagnosticCodes.XbrUnsupportedConstruct, factValuePortName, factValueSource ?? entityIn.Source),
                        _ => (DiagnosticCodes.XbrUnsupportedConstruct, factValuePortName, factValueSource ?? entityIn.Source)
                    };
                    diag.Error(
                        diagnosticCode,
                        DiagnosticSubsystem.XBRL,
                        DiagnosticStage.Execution,
                        reason,
                        nodeId: node.NodeId,
                        portName: portName,
                        source: source,
                        sourceTransformationPaths: factConstructSourcePaths);
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }

                // Record provenance: value cell, entity cell, and period cells feed the fact.
                var slotKey = ProvenanceTracker.SlotKey(node.NodeId, "value", /*row=*/row);
                ctx.Provenance.MergeSources(
                    ProvenanceTracker.SlotKey(node.NodeId, "fact", row),
                    new[]
                    {
                        slotKey,
                        ProvenanceTracker.SlotKey(node.NodeId, "entity", row),
                        ProvenanceTracker.SlotKey(node.NodeId, "period", row),
                        ProvenanceTracker.SlotKey(node.NodeId, "instantDate", row),
                        ProvenanceTracker.SlotKey(node.NodeId, "textValue", row)
                    });

                outputs["fact"] = DataValue.Opaque(PortShape.Fact, pf);
                break;
            }

            default:
                diag.Error(
                    DiagnosticCodes.MapUnknownNodeType,
                    DiagnosticSubsystem.Mapping,
                    DiagnosticStage.Execution,
                    $"No evaluator registered for node type '{node.NodeType}'.",
                    nodeId: node.NodeId);
                outputs["value"] = DataValue.Missing();
                break;
        }

        return outputs;
    }

    private static string? GetString(MappingNode node, string key)
    {
        if (!node.Parameters.TryGetValue(key, out var v) || v is null) return null;
        return v is System.Text.Json.JsonElement je ? je.GetString() : v.ToString();
    }

    /// <summary>Half-up rounding at the given digit count (0.5 magnitude away from zero).</summary>
    private static decimal RoundHalfUp(decimal value, int digits)
    {
        var factor = Pow10(digits);
        var scaled = value * factor;
        // Decimal.Round with AwayFromZero is the standard banker-inverted mode.
        return decimal.Round(scaled, 0, MidpointRounding.AwayFromZero) / factor;
    }

    private static decimal TruncateToDigits(decimal value, int digits)
    {
        var factor = Pow10(digits);
        return decimal.Truncate(value * factor) / factor;
    }

    private static decimal Pow10(int n)
    {
        decimal r = 1m;
        for (int i = 0; i < n; i++) r *= 10m;
        return r;
    }

    private static bool TryParseDecimalInvariant(string text, ExecutionContext ctx, DiagnosticBag diag,
        MappingNode node, out decimal value)
    {
        if (!DecimalDomain.TryValidateLexical(text, CultureInfo.InvariantCulture.NumberFormat,
                ctx.MaxSignificantDigits, out var domainFailure))
        {
            value = 0m;
            diag.Error(
                DiagnosticCodes.MapInvalidConstant,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Execution,
                $"Constant '{text}' is outside the supported decimal domain: {domainFailure}",
                nodeId: node.NodeId);
            return false;
        }
        if (decimal.TryParse(text, DecimalDomain.InvariantNumberStyles,
                CultureInfo.InvariantCulture, out value))
            return true;
        diag.Error(
            DiagnosticCodes.MapInvalidConstant,
            DiagnosticSubsystem.Mapping,
            DiagnosticStage.Execution,
            $"Constant '{text}' is not a valid decimal under invariant culture.",
            nodeId: node.NodeId);
        return false;
    }

    private static bool TryParseDecimalWithCulture(string text, string cultureTag,
        ExecutionContext ctx, DiagnosticBag diag, MappingNode node,
        SourceReference? src, out decimal value)
    {
        CultureInfo culture;
        try
        {
            culture = cultureTag.Equals("invariant", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(cultureTag);
        }
        catch (CultureNotFoundException)
        {
            diag.Error(
                DiagnosticCodes.MapCultureMissing,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Execution,
                $"Culture '{cultureTag}' is not available on this host.",
                nodeId: node.NodeId,
                source: src);
            value = 0m;
            return false;
        }

        // Preserve the original lexical form; reject excess precision before CLR
        // decimal parsing can round a too-long financial lexeme.
        if (!DecimalDomain.TryValidateLexical(text, culture.NumberFormat,
                ctx.MaxSignificantDigits, out var domainFailure))
        {
            diag.Error(
                DiagnosticCodes.MapInvalidConstant,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Execution,
                $"'{text}' is outside the supported decimal domain: {domainFailure}",
                nodeId: node.NodeId,
                source: src);
            value = 0m;
            return false;
        }
        if (!decimal.TryParse(text, NumberStyles.Number, culture, out value))
        {
            diag.Error(
                DiagnosticCodes.MapInvalidConstant,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Execution,
                $"'{text}' is not a valid decimal under culture '{culture.Name}'.",
                nodeId: node.NodeId,
                source: src);
            return false;
        }
        return true;
    }
}

/// <summary>
/// Payload for period-shaped values: either an instant or a duration.
/// </summary>
internal sealed record PeriodPayload(DateOnly? Instant, DateOnly? Start, DateOnly? End);

public sealed class CompilationResult
{
    public ExecutionPlan? Plan { get; }
    public DiagnosticBag Diagnostics { get; }
    public bool Success => Plan is not null && !Diagnostics.HasBlockingErrors;

    private CompilationResult(ExecutionPlan? plan, DiagnosticBag diag)
    {
        Plan = plan;
        Diagnostics = diag;
    }

    internal static CompilationResult Ok(ExecutionPlan plan, MappingGraph graph)
        => new(plan, new DiagnosticBag());

    internal static CompilationResult Failed(DiagnosticBag diag)
        => new(null, diag);
}
