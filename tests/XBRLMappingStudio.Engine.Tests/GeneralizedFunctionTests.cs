using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine.Tests;

public sealed class GeneralizedFunctionTests
{
    private static PortDefinition P(string name, PortShape shape, PortCardinality cardinality = PortCardinality.Single)
        => new(name, shape, cardinality);

    private static FunctionDefinition Pair(string id = "pair", string? nested = null,
        PortShape secondShape = PortShape.Boolean, PortCardinality cardinality = PortCardinality.Single)
    {
        var inputs = new[] { P("text", PortShape.String), P("other", secondShape, cardinality) };
        var outputs = new[] { P("label", PortShape.String), P("result", secondShape, cardinality) };
        var input = GraphHarness.Node("functionInput", "Input", outputs: inputs);
        var output = GraphHarness.Node("functionOutput", "Output", inputs: outputs);
        var nodes = new List<MappingNode> { input, output };
        var edges = new List<MappingEdge>();
        if (nested is null)
        {
            edges.Add(GraphHarness.Link(input, "text", output, "label"));
            edges.Add(GraphHarness.Link(input, "other", output, "result"));
        }
        else
        {
            var call = GraphHarness.Node(NodeTypeConstants.FunctionCall, "Nested",
                new() { ["functionId"] = nested, ["revision"] = "1" }, inputs, outputs);
            nodes.Add(call);
            edges.Add(GraphHarness.Link(input, "text", call, "text"));
            edges.Add(GraphHarness.Link(input, "other", call, "other"));
            edges.Add(GraphHarness.Link(call, "label", output, "label"));
            edges.Add(GraphHarness.Link(call, "result", output, "result"));
        }
        return new FunctionDefinition
        {
            FunctionId = id, Name = id, CatalogVersion = FunctionCatalog.Default.Version,
            Inputs = inputs.ToList(), Outputs = outputs.ToList(), Nodes = nodes, Edges = edges
        };
    }

    private static MappingProject Calling(FunctionDefinition definition, params FunctionDefinition[] dependencies)
    {
        var text = GraphHarness.Node(NodeTypeConstants.ExcelField, "Text",
            new() { ["bindingId"] = "b", ["column"] = "text" }, outputs: new[] { P("value", PortShape.String) });
        var otherPort = definition.Inputs[1];
        var other = GraphHarness.Node(NodeTypeConstants.Constant, "Other",
            new() { ["literal"] = "true", ["shape"] = otherPort.Shape.ToString() },
            outputs: new[] { otherPort with { Name = "value" } });
        var call = GraphHarness.Node(NodeTypeConstants.FunctionCall, "Call",
            new() { ["functionId"] = definition.FunctionId, ["revision"] = "1" },
            definition.Inputs.ToArray(), definition.Outputs.ToArray());
        // A second invocation verifies reusable cached expansion and independent namespacing.
        var second = call with { NodeId = Id.New() };
        return GraphHarness.Project(new[] { text, other, call, second }, new[]
        {
            GraphHarness.Link(text, "value", call, "text"),
            GraphHarness.Link(other, "value", call, "other"),
            GraphHarness.Link(call, "label", second, "text"),
            GraphHarness.Link(call, "result", second, "other")
        }, bindings: new[] { GraphHarness.Binding("b") }, functions: dependencies.Append(definition));
    }

    private static CompilationResult Compile(MappingProject project)
        => new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), SyntheticProfile.Build());

    [Theory]
    [InlineData(PortShape.Boolean, PortCardinality.Single)]
    [InlineData(PortShape.Integer, PortCardinality.Single)]
    [InlineData(PortShape.Identifier, PortCardinality.Single)]
    [InlineData(PortShape.Fact, PortCardinality.Single)]
    [InlineData(PortShape.String, PortCardinality.Single)]
    [InlineData(PortShape.Date, PortCardinality.Single)]
    [InlineData(PortShape.Instant, PortCardinality.Single)]
    [InlineData(PortShape.QualifiedName, PortCardinality.Single)]
    [InlineData(PortShape.Record, PortCardinality.Collection)]
    [InlineData(PortShape.Decimal, PortCardinality.Collection)]
    public void TypedMultiPortPassThroughAndNestedReuseCompile(PortShape shape, PortCardinality cardinality)
    {
        var inner = Pair(secondShape: shape, cardinality: cardinality);
        var wrapper = Pair("wrapper", "pair", shape, cardinality);
        var project = Calling(wrapper, inner);
        var compile = Compile(project);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Equal(project.ComputeSemanticDigest(), ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).ComputeSemanticDigest());
    }

    [Theory]
    [InlineData("duplicateInput")]
    [InlineData("missingOutput")]
    [InlineData("markerType")]
    [InlineData("unknownPort")]
    [InlineData("duplicateOutputEdge")]
    [InlineData("invalidShape")]
    [InlineData("invalidCardinality")]
    public void MalformedDefinitionFailsClosed(string corruption)
    {
        var definition = Pair();
        switch (corruption)
        {
            case "duplicateInput": definition.Inputs.Add(definition.Inputs[0]); break;
            case "missingOutput": definition.Edges.RemoveAt(1); break;
            case "markerType": definition.Nodes[0].OutputPorts[0] = P("text", PortShape.Decimal); break;
            case "unknownPort": definition.Edges[0] = definition.Edges[0] with { SourcePortName = "missing" }; break;
            case "duplicateOutputEdge": definition.Edges.Add(definition.Edges[0] with { EdgeId = Id.New() }); break;
            case "invalidShape": definition.Inputs[0] = P("text", (PortShape)999); break;
            case "invalidCardinality": definition.Inputs[0] = P("text", PortShape.String, (PortCardinality)999); break;
        }
        var compile = Compile(Calling(definition));
        Assert.False(compile.Success);
        Assert.Contains(compile.Diagnostics.Items, d => d.Code == DiagnosticCodes.MapFunctionDefinitionInvalid);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("name")]
    [InlineData("cardinality")]
    [InlineData("missing")]
    [InlineData("required")]
    public void CallCannotWeakenPinnedSignature(string corruption)
    {
        var project = Calling(Pair());
        var index = project.Nodes.FindIndex(n => n.NodeType == NodeTypeConstants.FunctionCall);
        var call = project.Nodes[index].ShallowCopy();
        switch (corruption)
        {
            case "type": call.InputPorts[0] = P("text", PortShape.Decimal); break;
            case "name": call.InputPorts[0] = P("renamed", PortShape.String); break;
            case "cardinality": call.InputPorts[0] = P("text", PortShape.String, PortCardinality.Collection); break;
            case "missing": call.OutputPorts.RemoveAt(1); break;
            case "required":
                call.InputPorts[0] = call.InputPorts[0] with { IsRequired = false };
                project.Edges.RemoveAll(e => e.TargetNodeId == call.NodeId && e.TargetPortName == "text");
                break;
        }
        project.Nodes[index] = call;
        var compile = Compile(project);
        Assert.False(compile.Success);
        Assert.Contains(compile.Diagnostics.Items, d => d.Code == DiagnosticCodes.MapFunctionDefinitionInvalid);
    }

    [Fact]
    public void RecursiveMultiPortFunctionsFailClosed()
    {
        var compile = Compile(Calling(Pair("first", "second"), Pair("second", "first")));
        Assert.False(compile.Success);
        Assert.Contains(compile.Diagnostics.Items, d => d.Code == DiagnosticCodes.MapFunctionCycleDetected);
    }

    [Fact]
    public void PortListOrderDoesNotChangeNamedBinding()
    {
        var definition = Pair();
        definition.Inputs.Reverse();
        definition.Outputs.Reverse();
        // Build with the original public signature then replace the pinned definition.
        var project = Calling(Pair());
        project.Functions[0] = definition;
        var compile = Compile(project);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
    }

    [Fact]
    public void PassThroughCallsCannotEraseGraphCycles()
    {
        var project = Calling(Pair());
        var calls = project.Nodes.Where(n => n.NodeType == NodeTypeConstants.FunctionCall).ToArray();
        project.Edges.RemoveAll(e => e.TargetNodeId == calls[0].NodeId && e.TargetPortName == "text");
        project.Edges.Add(GraphHarness.Link(calls[1], "label", calls[0], "text"));
        var compile = Compile(project);
        Assert.False(compile.Success);
        Assert.Contains(compile.Diagnostics.Items, d => d.Code == DiagnosticCodes.MapFunctionDefinitionInvalid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundaryTypesAreValidatedBeforePassThroughErasesThem(bool inputBoundary)
    {
        var project = Calling(Pair());
        var first = project.Nodes.FindIndex(n => n.NodeType == NodeTypeConstants.FunctionCall);
        if (inputBoundary)
            project.Nodes[0].OutputPorts[0] = P("value", PortShape.Decimal);
        else
        {
            // The second call's declared input disagrees with its pinned signature;
            // whichever call expands first, the invalid edge must remain blocking.
            project.Nodes[first + 1].InputPorts[0] = P("text", PortShape.Decimal);
        }
        var compile = Compile(project);
        Assert.False(compile.Success);
        Assert.Contains(compile.Diagnostics.Items, d => d.Code == DiagnosticCodes.MapFunctionDefinitionInvalid);
    }

    [Fact]
    public void ZeroInputFunctionProducesTypedConstant()
    {
        var input = GraphHarness.Node("functionInput", "Input");
        var constant = GraphHarness.Node(NodeTypeConstants.Constant, "Constant",
            new() { ["literal"] = "7", ["shape"] = "Decimal" }, outputs: new[] { P("value", PortShape.Decimal) });
        var output = GraphHarness.Node("functionOutput", "Output", inputs: new[] { P("number", PortShape.Decimal) });
        var definition = new FunctionDefinition
        {
            FunctionId = "constant", Name = "Constant", CatalogVersion = FunctionCatalog.Default.Version,
            Outputs = new() { P("number", PortShape.Decimal) }, Nodes = new() { input, constant, output },
            Edges = new() { GraphHarness.Link(constant, "value", output, "number") }
        };
        var source = GraphHarness.Node(NodeTypeConstants.ExcelField, "Driver",
            new() { ["bindingId"] = "b", ["column"] = "value" }, outputs: new[] { P("value", PortShape.Decimal) });
        var call = GraphHarness.Node(NodeTypeConstants.FunctionCall, "Call",
            new() { ["functionId"] = "constant", ["revision"] = "1" }, outputs: definition.Outputs.ToArray());
        var sinks = GraphHarness.FactSink(out var edges, call, "value", PortCardinality.Single);
        var project = GraphHarness.Project(new[] { source, call }.Concat(sinks), edges,
            new[] { GraphHarness.Binding("b") }, new[] { definition });
        var context = GraphHarness.Ctx(new[] { ("b", (IReadOnlyList<DataRecord>)new[] { GraphHarness.Row(("value", DataValue.DecimalValue(0))) }) });
        var result = GraphHarness.Run(project, context, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Equal(7m, Assert.Single(result!.Facts).Value);
    }

    [Fact]
    public void MixedMultiPortBodyExecutesAndRoutesIndependentOutputs()
    {
        var input = GraphHarness.Node("functionInput", "Input", outputs: new[] { P("text", PortShape.String), P("entity", PortShape.String) });
        var parse = GraphHarness.Node(NodeTypeConstants.ParseDecimal, "Parse", new() { ["culture"] = "invariant" },
            new[] { P("input", PortShape.String) }, new[] { P("value", PortShape.Decimal) });
        var output = GraphHarness.Node("functionOutput", "Output", inputs: new[] { P("amount", PortShape.Decimal), P("entityName", PortShape.String) });
        var definition = new FunctionDefinition
        {
            FunctionId = "parse", Name = "Parse", CatalogVersion = FunctionCatalog.Default.Version,
            Inputs = new() { P("text", PortShape.String), P("entity", PortShape.String) },
            Outputs = new() { P("amount", PortShape.Decimal), P("entityName", PortShape.String) },
            Nodes = new() { input, parse, output },
            Edges = new() { GraphHarness.Link(input, "text", parse, "input"), GraphHarness.Link(parse, "value", output, "amount"), GraphHarness.Link(input, "entity", output, "entityName") }
        };
        var source = GraphHarness.Node(NodeTypeConstants.ExcelField, "Text source",
            new() { ["bindingId"] = "b", ["column"] = "text" }, outputs: new[] { P("value", PortShape.String) });
        var call = GraphHarness.Node(NodeTypeConstants.FunctionCall, "Parse call",
            new() { ["functionId"] = "parse", ["revision"] = "1" }, definition.Inputs.ToArray(), definition.Outputs.ToArray());
        var sinks = GraphHarness.FactSink(out var edges, call, "value", PortCardinality.Single);
        edges.Add(GraphHarness.Link(source, "value", call, "text"));
        var entity = sinks.Single(n => n.DisplayName == "Entity");
        var fact = sinks.Single(n => n.NodeType == NodeTypeConstants.FactConstruct);
        edges.RemoveAll(e => e.TargetNodeId == fact.NodeId && e.TargetPortName == "entity");
        edges.Add(GraphHarness.Link(entity, "value", call, "entity"));
        edges.Add(GraphHarness.Link(call, "entityName", fact, "entity"));
        var project = GraphHarness.Project(new[] { source, call }.Concat(sinks), edges,
            new[] { GraphHarness.Binding("b") }, new[] { definition });
        var context = GraphHarness.Ctx(new[] { ("b", (IReadOnlyList<DataRecord>)new[] { GraphHarness.Row(("text", DataValue.StringValue("12.5"))) }) });
        var result = GraphHarness.Run(project, context, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Equal(12.5m, Assert.Single(result!.Facts).Value);
    }
}
