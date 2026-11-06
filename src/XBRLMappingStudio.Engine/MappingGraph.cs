using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Runtime view of a mapping graph. Loads nodes and edges from a <see cref="MappingProject"/>
/// and provides indexed access for validation and plan compilation. Layout is excluded
/// per MAP01: two visually different layouts produce the same graph.
/// </summary>
public sealed class MappingGraph
{
    public IReadOnlyDictionary<string, MappingNode> Nodes { get; }
    public IReadOnlyDictionary<string, MappingEdge> Edges { get; }
    public IReadOnlyDictionary<string, SourceBinding> Bindings { get; }

    /// <summary>Originating project, retained so function inlining (MAP07) can reach
    /// the <see cref="MappingProject.Functions"/> definitions during compilation.</summary>
    public MappingProject Source { get; }

    /// <summary>Edge lookup by target port: (targetNodeId, targetPortName) -> edge.</summary>
    private readonly Dictionary<(string, string), MappingEdge> _incomingByPort = new();

    /// <summary>Edges by source: (sourceNodeId, sourcePortName) -> edges.</summary>
    private readonly Dictionary<(string, string), List<MappingEdge>> _outgoingByPort = new();

    /// <summary>Ports that receive input from a specific node output.</summary>
    public IEnumerable<MappingEdge> OutgoingEdges(string nodeId, string portName)
        => _outgoingByPort.TryGetValue((nodeId, portName), out var list) ? list : Enumerable.Empty<MappingEdge>();

    public MappingEdge? IncomingEdge(string nodeId, string portName)
        => _incomingByPort.TryGetValue((nodeId, portName), out var e) ? e : null;

    public MappingNode GetNode(string nodeId) => Nodes[nodeId];

    /// <summary>Nodes with no input edges (root sources). Includes constants and bindings.</summary>
    public IEnumerable<MappingNode> Roots => Nodes.Values.Where(n =>
        n.InputPorts.Count == 0
        || n.InputPorts.All(p => !HasInput(p.Name, n.NodeId)));

    /// <summary>Nodes with no outgoing edges (sinks, typically FactConstruct).</summary>
    public IEnumerable<MappingNode> Sinks => Nodes.Values.Where(n =>
        n.OutputPorts.Count == 0
        || n.OutputPorts.All(p => !OutgoingEdges(n.NodeId, p.Name).Any()));

    private bool HasInput(string portName, string nodeId)
        => IncomingEdge(nodeId, portName) is not null;

    public string ComputeSemanticDigest()
        => _semanticDigest;

    private readonly string _semanticDigest;

    public MappingGraph(MappingProject project)
    {
        var nodeMap = new Dictionary<string, MappingNode>(StringComparer.Ordinal);
        foreach (var n in project.Nodes)
        {
            if (nodeMap.ContainsKey(n.NodeId))
                throw new InvalidOperationException($"Duplicate NodeId {n.NodeId}.");
            nodeMap[n.NodeId] = n;
        }

        var edgeMap = new Dictionary<string, MappingEdge>(StringComparer.Ordinal);
        foreach (var e in project.Edges)
        {
            if (!nodeMap.ContainsKey(e.SourceNodeId))
                throw new InvalidOperationException($"Edge {e.EdgeId} references unknown source {e.SourceNodeId}.");
            if (!nodeMap.ContainsKey(e.TargetNodeId))
                throw new InvalidOperationException($"Edge {e.EdgeId} references unknown target {e.TargetNodeId}.");
            edgeMap[e.EdgeId] = e;

            var srcKey = (e.SourceNodeId, e.SourcePortName);
            if (!_outgoingByPort.TryGetValue(srcKey, out var list))
            {
                list = new List<MappingEdge>();
                _outgoingByPort[srcKey] = list;
            }
            list.Add(e);

            var tgtKey = (e.TargetNodeId, e.TargetPortName);
            if (_incomingByPort.ContainsKey(tgtKey))
                throw new InvalidOperationException($"Port {tgtKey} has multiple incoming edges.");
            _incomingByPort[tgtKey] = e;
        }

        Nodes = nodeMap;
        Edges = edgeMap;
        Bindings = project.Bindings.ToDictionary(b => b.BindingId, StringComparer.Ordinal);
        Source = project;
        _semanticDigest = project.ComputeSemanticDigest();
    }
}

/// <summary>
/// Signature of a node type as declared by the catalog.
/// </summary>
public sealed record FunctionSignature(
    string NodeType,
    string DisplayName,
    IReadOnlyList<PortDefinition> Inputs,
    IReadOnlyList<PortDefinition> Outputs,
    IReadOnlyDictionary<string, ParameterRequirement> Parameters)
{
    public int MaxInstances { get; init; } = int.MaxValue;
    public bool IsDeterministic { get; init; } = true;
}

/// <summary>
/// Declaration of a node-level parameter.
/// </summary>
public sealed record ParameterRequirement(
    string Name,
    PortShape Shape,
    bool IsRequired,
    string? DefaultValue = null,
    string? Description = null);

/// <summary>
/// Registry of node types available for execution. The catalog is versioned independently
/// so that a project pinned to an old catalog still replays correctly (per MAP07).
/// </summary>
public sealed class FunctionCatalog
{
    public string Version { get; }
    private readonly Dictionary<string, FunctionSignature> _signatures;

    public FunctionCatalog(string version, IEnumerable<FunctionSignature> signatures)
    {
        Version = version;
        _signatures = signatures.ToDictionary(s => s.NodeType, StringComparer.Ordinal);
    }

    public FunctionSignature? Find(string nodeType)
        => _signatures.TryGetValue(nodeType, out var s) ? s : null;

    public IEnumerable<FunctionSignature> All => _signatures.Values;

    /// <summary>
    /// Catalog for G1: source bindings, transform (scale, parse, constant), conditional
    /// (coalesce), and fact construction. Aggregation, joins, group are deferred (MAP05/06).
    /// </summary>
    public static FunctionCatalog Default { get; } = new("1.0", new[]
    {
        // Source binding: emits one field per row from an Excel/DB source.
        new FunctionSignature(
            NodeTypeConstants.ExcelField,
            "Excel Field",
            Array.Empty<PortDefinition>(),
            new[]
            {
                new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true),
                new PortDefinition("textValue", PortShape.String, PortCardinality.Single, false)
            },
            new Dictionary<string, ParameterRequirement>
            {
                ["bindingId"] = new("bindingId", PortShape.Identifier, true, Description: "SourceBinding id"),
                ["column"] = new("column", PortShape.String, true),
                ["headerRow"] = new("headerRow", PortShape.Integer, false, "1"),
                ["rowFilterColumn"] = new("rowFilterColumn", PortShape.String, false, Description: "Optional: only accept rows where this column matches rowFilterValue."),
                ["rowFilterValue"] = new("rowFilterValue", PortShape.String, false)
            }),

        new FunctionSignature(
            NodeTypeConstants.Constant,
            "Constant",
            Array.Empty<PortDefinition>(),
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["literal"] = new("literal", PortShape.String, true),
                ["shape"] = new("shape", PortShape.String, false, "Decimal")
            }),

        new FunctionSignature(
            NodeTypeConstants.RunParameter,
            "Run Parameter",
            Array.Empty<PortDefinition>(),
            new[] { new PortDefinition("value", PortShape.String, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["name"] = new("name", PortShape.String, true),
                ["shape"] = new("shape", PortShape.String, false, "String")
            }),

        // Transform: parse a text value into a decimal using declared culture.
        new FunctionSignature(
            NodeTypeConstants.ParseDecimal,
            "Parse Decimal",
            new[] { new PortDefinition("input", PortShape.String, PortCardinality.Single, true) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["culture"] = new("culture", PortShape.String, true, Description: "BCP-47 tag or 'invariant'"),
                ["decimalSeparator"] = new("decimalSeparator", PortShape.String, false, "."),
                ["groupSeparator"] = new("groupSeparator", PortShape.String, false, ",")
            }),

        // Transform: multiply by scale factor (thousands, millions).
        new FunctionSignature(
            NodeTypeConstants.Scale,
            "Scale (multiply)",
            new[] { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single, true) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["factor"] = new("factor", PortShape.Decimal, true, "1")
            }),

        // Transform: change sign.
        new FunctionSignature(
            NodeTypeConstants.ChangeSign,
            "Change Sign",
            new[] { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single, true) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>()),

        // Transform: coalesce (first non-nil/non-missing).
        new FunctionSignature(
            NodeTypeConstants.Coalesce,
            "Coalesce",
            new[]
            {
                new PortDefinition("a", PortShape.Decimal, PortCardinality.Single, true),
                new PortDefinition("b", PortShape.Decimal, PortCardinality.Single, false)
            },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>()),

        // Transform: round with declared mode (MAP04 acceptance — tie rounding must be explicit).
        new FunctionSignature(
            NodeTypeConstants.Round,
            "Round",
            new[] { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single, true) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["digits"] = new("digits", PortShape.Integer, false, "0", Description: "Decimal digits to retain."),
                ["mode"] = new("mode", PortShape.String, true, Description: "HalfUp | HalfEven | Truncate")
            }),

        // Transform: ratio of two decimals with divide-by-zero policy (MAP04).
        new FunctionSignature(
            NodeTypeConstants.Ratio,
            "Ratio",
            new[]
            {
                new PortDefinition("numerator", PortShape.Decimal, PortCardinality.Single, true),
                new PortDefinition("denominator", PortShape.Decimal, PortCardinality.Single, true)
            },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["divideByZero"] = new("divideByZero", PortShape.String, false, "Diagnostic",
                    Description: "Missing | NaN | Diagnostic")
            }),

        // Conditional: boolean gate that passes a value only when the condition holds.
        new FunctionSignature(
            NodeTypeConstants.Filter,
            "Filter",
            new[]
            {
                new PortDefinition("input", PortShape.Decimal, PortCardinality.Single, true),
                new PortDefinition("predicate", PortShape.Boolean, PortCardinality.Single, true)
            },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>()),

        // Conditional: compare two decimals and emit a boolean.
        new FunctionSignature(
            NodeTypeConstants.Compare,
            "Compare",
            new[]
            {
                new PortDefinition("a", PortShape.Decimal, PortCardinality.Single, true),
                new PortDefinition("b", PortShape.Decimal, PortCardinality.Single, true)
            },
            new[] { new PortDefinition("value", PortShape.Boolean, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["op"] = new("op", PortShape.String, true, Description: "Eq | Ne | Lt | Le | Gt | Ge")
            }),

        // Transform: build an instant date from a DateOnly.
        new FunctionSignature(
            NodeTypeConstants.BuildInstantDate,
            "Instant Date",
            new[] { new PortDefinition("dateText", PortShape.String, PortCardinality.Single, true) },
            new[] { new PortDefinition("value", PortShape.Instant, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["calendar"] = new("calendar", PortShape.String, false, "Gregorian")
            }),

        // Transform: build a duration period.
        new FunctionSignature(
            NodeTypeConstants.BuildDurationDate,
            "Duration Period",
            new[]
            {
                new PortDefinition("startText", PortShape.String, PortCardinality.Single, true),
                new PortDefinition("endText", PortShape.String, PortCardinality.Single, true)
            },
            new[] { new PortDefinition("value", PortShape.Instant, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>()),

        // Sink: fact construction. Emits one XBRL fact per input iteration. Accepts a
        // scalar numeric `value`, a `values` Collection (set-driven), or a scalar
        // `textValue` for language-tagged string concepts.
        new FunctionSignature(
            NodeTypeConstants.FactConstruct,
            "XBRL Fact",
            new[]
            {
                new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, false),
                new PortDefinition("values", PortShape.Decimal, PortCardinality.Collection, false),
                new PortDefinition("textValue", PortShape.String, PortCardinality.Single, false),
                new PortDefinition("entity", PortShape.String, PortCardinality.Single, true),
                new PortDefinition("period", PortShape.Instant, PortCardinality.Single, false),
                new PortDefinition("instantDate", PortShape.Instant, PortCardinality.Single, false)
            },
            new[] { new PortDefinition("fact", PortShape.Fact, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["concept"] = new("concept", PortShape.QualifiedName, true),
                ["unit"] = new("unit", PortShape.String, false),
                ["periodType"] = new("periodType", PortShape.String, true, Description: "'instant'|'duration'"),
                ["decimals"] = new("decimals", PortShape.Integer, false, Description: "e.g. -3 for thousands"),
                ["language"] = new("language", PortShape.String, false, Description: "BCP-47 tag")
            }),

        // MAP05 collection operators -------------------------------------------------

        // Collect: reads a scalar upstream port across every row and emits a Collection.
        new FunctionSignature(
            NodeTypeConstants.Collect,
            "Collect (rows -> collection)",
            new[] { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single, true) },
            new[] { new PortDefinition("items", PortShape.Decimal, PortCardinality.Collection, true) },
            new Dictionary<string, ParameterRequirement>()),

        // Sum: reduce a Collection<Decimal> to a single Decimal.
        new FunctionSignature(
            NodeTypeConstants.Sum,
            "Sum",
            new[] { new PortDefinition("items", PortShape.Decimal, PortCardinality.Collection, true) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>()),

        // Count: reduce a Collection to a single Integer.
        new FunctionSignature(
            NodeTypeConstants.Count,
            "Count",
            new[] { new PortDefinition("items", PortShape.Decimal, PortCardinality.Collection, true) },
            new[] { new PortDefinition("value", PortShape.Integer, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>()),

        // First: return the first Present item, or Missing.
        new FunctionSignature(
            NodeTypeConstants.First,
            "First",
            new[] { new PortDefinition("items", PortShape.Decimal, PortCardinality.Collection, true) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>()),

        // MAP06 lookup ---------------------------------------------------------------

        // Lookup: fetch a value by key from a project-pinned source snapshot.
        new FunctionSignature(
            NodeTypeConstants.Lookup,
            "Lookup",
            new[] { new PortDefinition("key", PortShape.String, PortCardinality.Single, true) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["table"] = new("table", PortShape.String, true, Description: "Named dictionary supplied via ExecutionContext.Parameters prefixed 'lookup:."),
                ["missingKey"] = new("missingKey", PortShape.String, false, "Diagnostic", Description: "Missing | Diagnostic | Default"),
                ["defaultValue"] = new("defaultValue", PortShape.Decimal, false, Description: "Value used when missingKey=Default.")
            }),

        // MAP05 record collection: reads a source binding into a Collection<Record>.
        new FunctionSignature(
            NodeTypeConstants.CollectRows,
            "Collect Rows (binding -> records)",
            Array.Empty<PortDefinition>(),
            new[] { new PortDefinition("items", PortShape.Record, PortCardinality.Collection, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["binding"] = new("binding", PortShape.Identifier, true, Description: "SourceBinding id whose loaded rows become records."),
                ["fields"] = new("fields", PortShape.String, false, Description: "Optional comma list of columns to retain.")
            }),

        // MAP05 group aggregation: buckets a Collection<Record> by key fields and sums a value field.
        new FunctionSignature(
            NodeTypeConstants.GroupBy,
            "Group By",
            new[] { new PortDefinition("items", PortShape.Record, PortCardinality.Collection, true) },
            new[]
            {
                new PortDefinition("values", PortShape.Decimal, PortCardinality.Collection, true),
                new PortDefinition("keys", PortShape.String, PortCardinality.Collection, true)
            },
            new Dictionary<string, ParameterRequirement>
            {
                ["keys"] = new("keys", PortShape.String, true, Description: "Comma list of record fields to group by."),
                ["sumField"] = new("sumField", PortShape.String, true, Description: "Record field summed within each bucket.")
            }),

        // MAP06 join: equi-join two Collection<Record> inputs on declared key fields.
        new FunctionSignature(
            NodeTypeConstants.Join,
            "Join",
            new[]
            {
                new PortDefinition("left", PortShape.Record, PortCardinality.Collection, true),
                new PortDefinition("right", PortShape.Record, PortCardinality.Collection, true)
            },
            new[] { new PortDefinition("rows", PortShape.Record, PortCardinality.Collection, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["leftKey"] = new("leftKey", PortShape.String, true),
                ["rightKey"] = new("rightKey", PortShape.String, true),
                ["cardinality"] = new("cardinality", PortShape.String, false, "Inner", Description: "Inner | Left | Right | Full"),
                ["duplicateKey"] = new("duplicateKey", PortShape.String, false, "All", Description: "Fail | First | All")
            }),

        // MAP07 named visual function call. Inlined at compile time against a pinned revision.
        new FunctionSignature(
            NodeTypeConstants.FunctionCall,
            "Function Call",
            new[] { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single, false) },
            new[] { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, true) },
            new Dictionary<string, ParameterRequirement>
            {
                ["functionId"] = new("functionId", PortShape.Identifier, true),
                ["revision"] = new("revision", PortShape.Integer, true, "1", Description: "Pinned FunctionDefinition revision.")
            })
    });
}

public static class NodeTypeConstants
{
    public const string ExcelField = "excelField";
    public const string Constant = "constant";
    public const string RunParameter = "runParameter";
    public const string ParseDecimal = "parseDecimal";
    public const string Scale = "scale";
    public const string ChangeSign = "changeSign";
    public const string Coalesce = "coalesce";
    public const string BuildInstantDate = "buildInstantDate";
    public const string BuildDurationDate = "buildDurationDate";
    public const string FactConstruct = "factConstruct";
    public const string Round = "round";
    public const string Ratio = "ratio";
    public const string Filter = "filter";
    public const string Compare = "compare";
    public const string Collect = "collect";
    public const string Sum = "sum";
    public const string Count = "count";
    public const string First = "first";
    public const string Lookup = "lookup";
    public const string CollectRows = "collectRows";
    public const string GroupBy = "groupBy";
    public const string Join = "join";
    public const string FunctionCall = "functionCall";

    public static IEnumerable<string> All => new[]
    {
        ExcelField, Constant, RunParameter, ParseDecimal, Scale, ChangeSign, Coalesce,
        BuildInstantDate, BuildDurationDate, FactConstruct,
        Round, Ratio, Filter, Compare,
        Collect, Sum, Count, First, Lookup,
        CollectRows, GroupBy, Join, FunctionCall
    };
}
