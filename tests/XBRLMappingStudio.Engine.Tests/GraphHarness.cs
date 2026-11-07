using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine.Tests;

/// <summary>
/// Compact builders for Group 2 acceptance tests (MAP05 collections/joins, MAP07
/// functions, MAP08 preview). Graphs are executed in-process against hand-authored
/// <see cref="DataRecord"/> sources so no Excel or file I/O is required.
/// </summary>
internal static class GraphHarness
{
    public static MappingNode Node(
        string type,
        string displayName,
        Dictionary<string, object?>? parameters = null,
        PortDefinition[]? inputs = null,
        PortDefinition[]? outputs = null)
        => new()
        {
            NodeId = Id.New(),
            NodeType = type,
            DisplayName = displayName,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            InputPorts = inputs?.ToList() ?? new List<PortDefinition>(),
            OutputPorts = outputs?.ToList() ?? new List<PortDefinition>()
        };

    public static MappingEdge Link(MappingNode src, string srcPort, MappingNode tgt, string tgtPort)
        => new()
        {
            EdgeId = Id.New(),
            SourceNodeId = src.NodeId,
            SourcePortName = srcPort,
            TargetNodeId = tgt.NodeId,
            TargetPortName = tgtPort
        };

    public static PortDefinition P(
        string name, PortShape shape,
        PortCardinality card = PortCardinality.Single,
        bool required = true)
        => new(name, shape, card, required);

    public static MappingProject Project(IEnumerable<MappingNode> nodes, IEnumerable<MappingEdge> edges,
        IEnumerable<SourceBinding>? bindings = null, IEnumerable<FunctionDefinition>? functions = null)
        => new()
        {
            ProjectId = Id.New(),
            Name = "harness",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest,
            FunctionCatalogVersion = FunctionCatalog.Default.Version,
            Nodes = nodes.ToList(),
            Edges = edges.ToList(),
            Bindings = (bindings ?? Array.Empty<SourceBinding>()).ToList(),
            Functions = (functions ?? Array.Empty<FunctionDefinition>()).ToList()
        };

    public static SourceBinding Binding(string id) => new()
    {
        BindingId = id,
        Kind = "excel",
        DisplayName = "harness-" + id,
        Configuration = new Dictionary<string, object?>()
    };

    public static DataRecord Row(params (string Field, DataValue Value)[] fields)
        => new()
        {
            RowId = Id.New(),
            Fields = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal)
        };

    public static ExecutionContext Ctx(
        (string Binding, IReadOnlyList<DataRecord> Rows)[] sources,
        Dictionary<string, string>? parameters = null,
        bool preview = false,
        int previewLimit = 10,
        int maxJoinRows = 200_000,
        int maxCollectionItems = 100_000,
        int maxGroupCount = 100_000,
        long maxGroupWorkingBytes = 64L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var ctx = new ExecutionContext
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Manifest = new RunManifest
            {
                RunId = "test",
                OperationId = "test",
                ProjectId = "test",
                ProfileDigest = SyntheticProfile.Build().Digest,
                EngineVersion = "test"
            },
            Parameters = parameters ?? new Dictionary<string, string>(),
            IsPreview = preview,
            PreviewRowLimit = previewLimit,
            MaxJoinExpansionRows = maxJoinRows,
            MaxCollectionItems = maxCollectionItems,
            MaxGroupCount = maxGroupCount,
            MaxGroupWorkingBytes = maxGroupWorkingBytes,
            CancellationToken = cancellationToken
        };
        foreach (var s in sources) ctx.LoadedSources[s.Binding] = s.Rows;
        return ctx;
    }

    /// <summary>
    /// Assemble an Assets (instant) fact sink plus the entity/instant constants it needs.
    /// <paramref name="valuePort"/> is the name of the fact's value input port
    /// ("value" for scalar producers, "values" for a collection producer).
    /// </summary>
    public static List<MappingNode> FactSink(
        out List<MappingEdge> edges,
        MappingNode valueProducer,
        string valuePort,
        PortCardinality valueCard)
    {
        edges = new List<MappingEdge>();
        var entity = Node(NodeTypeConstants.Constant, "Entity",
            new() { ["literal"] = "ACME", ["shape"] = "String" },
            outputs: new[] { P("value", PortShape.String) });
        var date = Node(NodeTypeConstants.Constant, "Instant",
            new() { ["literal"] = "2025-12-31", ["shape"] = "String" },
            outputs: new[] { P("value", PortShape.String) });
        var inst = Node(NodeTypeConstants.BuildInstantDate, "AsOf",
            inputs: new[] { P("dateText", PortShape.String) },
            outputs: new[] { P("value", PortShape.Instant) });
        var fact = Node(NodeTypeConstants.FactConstruct, "Asset Fact",
            new()
            {
                ["concept"] = SyntheticProfile.Qualified("Assets"),
                ["unit"] = "USD",
                ["periodType"] = "instant"
            },
            inputs: new[]
            {
                P(valuePort, PortShape.Decimal, valueCard, required: false),
                P("entity", PortShape.String),
                P("instantDate", PortShape.Instant, required: false)
            },
            outputs: new[] { P("fact", PortShape.Fact) });

        edges.Add(Link(valueProducer, valueProducer.OutputPorts[0].Name, fact, valuePort));
        edges.Add(Link(entity, "value", fact, "entity"));
        edges.Add(Link(date, "value", inst, "dateText"));
        edges.Add(Link(inst, "value", fact, "instantDate"));

        return new List<MappingNode> { entity, date, inst, fact };
    }

    public static ExecutionResult? Run(MappingProject project, ExecutionContext context,
        out CompilationResult compile)
    {
        var compiler = new PlanCompiler(FunctionCatalog.Default);
        var graph = new MappingGraph(project);
        compile = compiler.Compile(graph, SyntheticProfile.Build());
        return compile.Success && compile.Plan is not null
            ? compile.Plan.ExecuteAsync(context).GetAwaiter().GetResult()
            : null;
    }
}
