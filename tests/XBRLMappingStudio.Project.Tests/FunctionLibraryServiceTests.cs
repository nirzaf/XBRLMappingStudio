using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Project.Tests;

public sealed class FunctionLibraryServiceTests
{
    private const string CatalogVersion = "1.0";

    private static PortDefinition DecimalPort(string name)
        => new(name, PortShape.Decimal, PortCardinality.Single);

    private static MappingNode Node(
        string id,
        string type,
        string displayName,
        Dictionary<string, object?>? parameters = null,
        IEnumerable<PortDefinition>? inputs = null,
        IEnumerable<PortDefinition>? outputs = null)
        => new()
        {
            NodeId = id,
            NodeType = type,
            DisplayName = displayName,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            InputPorts = inputs?.ToList() ?? new List<PortDefinition>(),
            OutputPorts = outputs?.ToList() ?? new List<PortDefinition>()
        };

    private static MappingEdge Edge(string id, MappingNode source, string sourcePort, MappingNode target, string targetPort)
        => new()
        {
            EdgeId = id,
            SourceNodeId = source.NodeId,
            SourcePortName = sourcePort,
            TargetNodeId = target.NodeId,
            TargetPortName = targetPort
        };

    private static FunctionDefinition Scale(string id, int revision, string factor)
    {
        var input = Node($"{id}-in-{revision}", "functionInput", "Input", outputs: new[] { DecimalPort("value") });
        var scale = Node($"{id}-scale-{revision}", "scale", "Scale",
            new() { ["factor"] = factor },
            new[] { DecimalPort("input") }, new[] { DecimalPort("value") });
        var output = Node($"{id}-out-{revision}", "functionOutput", "Output", inputs: new[] { DecimalPort("value") });
        return new FunctionDefinition
        {
            FunctionId = id,
            Name = id,
            Revision = revision,
            CatalogVersion = CatalogVersion,
            Inputs = new() { DecimalPort("input") },
            Outputs = new() { DecimalPort("value") },
            Nodes = new() { input, scale, output },
            Edges = new()
            {
                Edge($"{id}-e1-{revision}", input, "value", scale, "input"),
                Edge($"{id}-e2-{revision}", scale, "value", output, "value")
            }
        };
    }

    private static FunctionDefinition Call(string id, int revision, string targetId, int targetRevision)
    {
        var input = Node($"{id}-in-{revision}", "functionInput", "Input", outputs: new[] { DecimalPort("value") });
        var call = Node($"{id}-call-{revision}", "functionCall", "Nested call",
            new() { ["functionId"] = targetId, ["revision"] = targetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            new[] { DecimalPort("input") }, new[] { DecimalPort("value") });
        var output = Node($"{id}-out-{revision}", "functionOutput", "Output", inputs: new[] { DecimalPort("value") });
        return new FunctionDefinition
        {
            FunctionId = id,
            Name = id,
            Revision = revision,
            CatalogVersion = CatalogVersion,
            Inputs = new() { DecimalPort("input") },
            Outputs = new() { DecimalPort("value") },
            Nodes = new() { input, call, output },
            Edges = new()
            {
                Edge($"{id}-e1-{revision}", input, "value", call, "input"),
                Edge($"{id}-e2-{revision}", call, "value", output, "value")
            }
        };
    }

    private static MappingProject Project(params FunctionDefinition[] functions)
        => ProjectWithCatalog(CatalogVersion, functions);

    private static MappingProject ProjectWithCatalog(string catalogVersion, params FunctionDefinition[] functions)
        => new()
        {
            ProjectId = "project",
            Name = "Function library test",
            ProfileId = "profile",
            ProfileDigest = "sha256:test",
            FunctionCatalogVersion = catalogVersion,
            Functions = functions.ToList()
        };

    [Fact]
    public void CreateFunction_AppendsFirstRevisionWithoutMutatingSourceProject()
    {
        var project = Project();
        var function = Scale("scale", 1, "10");

        var updated = FunctionLibraryService.CreateFunction(project, function);

        Assert.Empty(project.Functions);
        Assert.Single(updated.Functions);
        Assert.Equal("scale", updated.Functions[0].FunctionId);
        Assert.Equal(1, updated.Functions[0].Revision);
        Assert.NotSame(function.Nodes[0], updated.Functions[0].Nodes[0]);
    }

    [Fact]
    public void CreateFunction_RejectsDuplicateIdentityAndIncompatibleCatalog()
    {
        var existing = Scale("scale", 1, "10");
        var duplicateError = Assert.Throws<InvalidOperationException>(() =>
            FunctionLibraryService.CreateFunction(Project(existing), existing));
        Assert.Contains("already exists", duplicateError.Message);

        var incompatible = existing with { FunctionId = "other", CatalogVersion = "future" };
        var catalogError = Assert.Throws<InvalidOperationException>(() =>
            FunctionLibraryService.CreateFunction(Project(existing), incompatible));
        Assert.Contains("catalog", catalogError.Message);
    }

    [Fact]
    public void FunctionPackageSerializer_RoundTripsPinnedRootAndDependencies()
    {
        var package = new FunctionPackageDocument
        {
            RootFunctionId = "wrapper",
            RootRevision = 1,
            Functions = new List<FunctionDefinition>
            {
                Scale("scale", 1, "10"),
                Call("wrapper", 1, "scale", 1)
            }
        };

        var roundTrip = FunctionPackageSerializer.Deserialize(FunctionPackageSerializer.Serialize(package));

        Assert.Equal("wrapper", roundTrip.RootFunctionId);
        Assert.Equal(1, roundTrip.RootRevision);
        Assert.Equal(new[] { "scale", "wrapper" }, roundTrip.Functions
            .Select(function => function.FunctionId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void FunctionPackageSerializer_RejectsMissingRootAndUnsupportedSchema()
    {
        var missingRoot = new FunctionPackageDocument
        {
            RootFunctionId = "missing",
            RootRevision = 1,
            Functions = new List<FunctionDefinition> { Scale("scale", 1, "10") }
        };
        Assert.Throws<InvalidOperationException>(() => FunctionPackageSerializer.Serialize(missingRoot));

        var unsupported = missingRoot with { RootFunctionId = "scale", SchemaVersion = 2 };
        Assert.Throws<InvalidOperationException>(() => FunctionPackageSerializer.Serialize(unsupported));

        var invalidFunction = Scale("scale", 1, "10") with { Nodes = null! };
        var malformedShape = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            rootFunctionId = "scale",
            rootRevision = 1,
            functions = new[] { invalidFunction }
        });
        Assert.Throws<InvalidOperationException>(() => FunctionPackageSerializer.Deserialize(malformedShape));
    }

    [Fact]
    public void CreateRevision_AppendsNewBodyWithoutChangingPinnedOldRevision()
    {
        var first = Scale("scale", 1, "10");
        var project = Project(first);
        var editedNodes = first.Nodes.Select(node => node.NodeType == "scale"
            ? node with { Parameters = new() { ["factor"] = "100" } }
            : node).ToList();

        var revised = FunctionLibraryService.CreateRevision(project, "scale", expectedRevision: 1,
            editedNodes, first.Edges, name: "Scale x100");

        Assert.Equal(new[] { 1, 2 }, revised.Functions.Select(function => function.Revision));
        Assert.Equal("10", (string)project.Functions[0].Nodes.Single(node => node.NodeType == "scale").Parameters["factor"]!);
        Assert.Equal("100", (string)revised.Functions[1].Nodes.Single(node => node.NodeType == "scale").Parameters["factor"]!);
        Assert.NotEqual(project.ComputeSemanticDigest(), revised.ComputeSemanticDigest());
    }

    [Fact]
    public void CreateRevision_RejectsStaleEditorRevision()
    {
        var project = Project(Scale("scale", 1, "10"), Scale("scale", 2, "100"));

        var error = Assert.Throws<ProjectSaveException>(() => FunctionLibraryService.CreateRevision(
            project, "scale", expectedRevision: 1, project.Functions[1].Nodes, project.Functions[1].Edges));

        Assert.Equal(DiagnosticCodes.PrjRevisionConflict, error.Code);
        Assert.Equal(2, project.Functions.Count);
    }

    [Fact]
    public void CreateRevision_AcceptsLegacyProjectCatalogDefault()
    {
        var project = ProjectWithCatalog("1", Scale("scale", 1, "10"));

        var revised = FunctionLibraryService.CreateRevision(project, "scale", expectedRevision: 1,
            project.Functions[0].Nodes, project.Functions[0].Edges);

        Assert.Equal(2, revised.Functions[^1].Revision);
        Assert.Equal("1", revised.FunctionCatalogVersion);
    }

    [Fact]
    public void ImportAs_NamespacesDependenciesAndRewritesNestedCalls()
    {
        var package = new[] { Scale("scale", 1, "10"), Call("wrapper", 1, "scale", 1) };

        var imported = FunctionLibraryService.ImportAs(Project(), package,
            sourceFunctionId: "wrapper", newFunctionId: "revenue", newName: "Revenue transform");

        Assert.Equal(new[] { "revenue__scale", "revenue" },
            imported.Functions.Select(function => function.FunctionId));
        var wrapper = imported.Functions.Single(function => function.FunctionId == "revenue");
        Assert.Equal("Revenue transform", wrapper.Name);
        var nestedCall = wrapper.Nodes.Single(node => node.NodeType == "functionCall");
        Assert.Equal("revenue__scale", nestedCall.Parameters["functionId"]);
        Assert.Equal("1", nestedCall.Parameters["revision"]);
    }

    [Fact]
    public void ImportAs_RejectsDependenciesMissingFromPackageAndDestination()
    {
        var package = new[] { Call("wrapper", 1, "missing", 2) };

        var error = Assert.Throws<InvalidOperationException>(() => FunctionLibraryService.ImportAs(
            Project(), package, sourceFunctionId: "wrapper", newFunctionId: "revenue"));

        Assert.Contains("dependency 'missing@2'", error.Message);
    }
}
