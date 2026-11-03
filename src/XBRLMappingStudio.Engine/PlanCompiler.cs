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

        // Discover the iteration source. For G1, this is any ExcelField node.
        var iterationSources = order
            .Select(id => graph.Nodes[id])
            .Where(n => n.NodeType == NodeTypeConstants.ExcelField)
            .Select(n => n.NodeId)
            .ToList();

        if (iterationSources.Count == 0)
        {
            diag.Error(
                DiagnosticCodes.MapUnboundRequiredInput,
                DiagnosticSubsystem.Mapping,
                DiagnosticStage.Compilation,
                "No iteration source (ExcelField node) is present in the graph.");
            return CompilationResult.Failed(diag);
        }

        // Sinks are fact-producing nodes.
        var sinks = order
            .Select(id => graph.Nodes[id])
            .Where(n => n.NodeType == NodeTypeConstants.FactConstruct)
            .Select(n => n.NodeId)
            .ToList();

        // Row count determined lazily at execution time from the binding; assume
        // a compile-time hint of 0 and let ExecutionPlan read it from context.
        var steps = order
            .Select(nodeId => new PlanStep(nodeId, graph.Nodes[nodeId].NodeType,
                BuildEvaluator(graph, nodeId, profile, diag)))
            .ToList();

        return CompilationResult.Ok(new ExecutionPlan(steps, sinks, iterationSources), graph);
    }

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
    private Action<int, EvaluationFrame, ExecutionContext, DiagnosticBag> BuildEvaluator(
        MappingGraph graph, string nodeId, FilingProfile profile, DiagnosticBag compileDiag)
    {
        var node = graph.Nodes[nodeId];
        var sig = _catalog.Find(node.NodeType)!;

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
            }

            var outputs = Dispatch(node, sig, inputs, row, ctx, diag, profile);
            foreach (var (name, value) in outputs)
            {
                frame.Set(nodeId, name, value);
            }
        };
    }

    private static Dictionary<string, DataValue> Dispatch(
        MappingNode node,
        FunctionSignature sig,
        Dictionary<string, DataValue> inputs,
        int row,
        ExecutionContext ctx,
        DiagnosticBag diag,
        FilingProfile profile)
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
                var cell = record.Fields.TryGetValue(column ?? "", out var v) ? v : DataValue.Missing();
                // Emit at row granularity: for collection ports the frame stores per-row indexed values.
                outputs["value"] = cell;
                if (cell.Source is not null)
                {
                    var slotKey = ProvenanceTracker.SlotKey(node.NodeId, "value", row);
                    ctx.Provenance.RecordSource(slotKey, cell.Source);
                }
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
                    PortShape.Decimal => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
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
                    outputs["value"] = DataValue.Missing(PortShape.Decimal);
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
                // while producing only the intended facts.
                var valueProbe = inputs.GetValueOrDefault("value", DataValue.Missing(PortShape.Decimal));
                if (valueProbe.IsMissing)
                {
                    outputs["fact"] = DataValue.Missing(PortShape.Fact);
                    break;
                }

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
                        nodeId: node.NodeId);
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
                        nodeId: node.NodeId);
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
                            nodeId: node.NodeId);
                        outputs["fact"] = DataValue.Missing(PortShape.Fact);
                        break;
                    }
                    unit = new XbrlUnit(unitStr);
                }

                var entity = inputs.GetValueOrDefault("entity", DataValue.Missing(PortShape.String)).AsString();
                var valueIn = inputs.GetValueOrDefault("value", DataValue.Missing(PortShape.Decimal));
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

                var decimalsStr = node.Parameters.TryGetValue("decimals", out var dv) ? dv?.ToString() : null;
                int? decimals = null;
                if (!string.IsNullOrEmpty(decimalsStr))
                {
                    if (int.TryParse(decimalsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        // Bounded exact-decimal range (section 3): reject just-outside limits.
                        if (parsed < -30 || parsed > 30)
                        {
                            diag.Error(
                                DiagnosticCodes.XbrDecimalsOutOfRange,
                                DiagnosticSubsystem.XBRL,
                                DiagnosticStage.Execution,
                                $"Decimals value {parsed} is outside the supported range [-30, 30].",
                                nodeId: node.NodeId);
                        }
                        else decimals = parsed;
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
                    Value = valueIn.Presence == ValuePresence.Present ? valueIn.AsDecimal() : null,
                    IsNil = valueIn.IsNil
                };

                // Period-vs-concept type check (acceptance: wrong period type produces useful error).
                var probe = new XbrlFact
                {
                    ConceptName = concept,
                    EntityScheme = pf.EntityScheme,
                    EntityIdentifier = pf.EntityIdentifier,
                    Period = period ?? XbrlPeriod.OfInstant(default),
                    Unit = unit,
                    Value = pf.Value,
                    Decimals = pf.Decimals,
                    Language = pf.Language,
                    IsNil = pf.IsNil
                };
                if (!probe.ValidateAgainst(conceptMeta, out var reason))
                {
                    diag.Error(
                        DiagnosticCodes.XbrPeriodTypeMismatch,
                        DiagnosticSubsystem.XBRL,
                        DiagnosticStage.Execution,
                        reason,
                        nodeId: node.NodeId);
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
                        ProvenanceTracker.SlotKey(node.NodeId, "instantDate", row)
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

    private static bool TryParseDecimalInvariant(string text, ExecutionContext ctx, DiagnosticBag diag,
        MappingNode node, out decimal value)
    {
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
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

        // Preserve the original lexical form; reject ambiguity.
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
