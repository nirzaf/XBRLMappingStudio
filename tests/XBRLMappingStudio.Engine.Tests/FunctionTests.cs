using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine.Tests;

/// <summary>
/// MAP04 completion (tie rounding, ratio divide-by-zero policy, comparison operators)
/// and MAP07 named visual functions (compile-time inlining pinned by revision).
/// </summary>
public class FunctionTests
{
    private const string Binding = "b1";
    private static DataValue D(decimal v) => DataValue.DecimalValue(v);

    private static MappingNode Driver(out List<MappingEdge> seedEdges, decimal amount)
    {
        // A single-row source drives one iteration and supplies the transform input.
        var excel = GraphHarness.Node(NodeTypeConstants.ExcelField, "In",
            new() { ["bindingId"] = Binding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        seedEdges = new List<MappingEdge>();
        return excel;
    }

    private static ExecutionContext OneRow(decimal amount)
        => GraphHarness.Ctx(new[] { (Binding, new[] { GraphHarness.Row(("amount", D(amount))) } as IReadOnlyList<DataRecord>) });

    // ---- MAP04: rounding modes ------------------------------------------------------

    [Theory]
    [InlineData("HalfEven", 2.5, 2)]
    [InlineData("HalfEven", 3.5, 4)]
    [InlineData("HalfUp", 2.5, 3)]
    [InlineData("Truncate", 2.9, 2)]
    [InlineData("Truncate", -2.9, -2)]
    public void Round_ModesProduceDeclaredTieBehaviour(string mode, decimal input, decimal expected)
    {
        var excel = Driver(out var edges, input);
        var round = GraphHarness.Node(NodeTypeConstants.Round, "Round",
            new() { ["mode"] = mode, ["digits"] = "0" },
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        edges.Add(GraphHarness.Link(excel, "value", round, "input"));
        var sink = GraphHarness.FactSink(out var factEdges, round, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { excel, round }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var result = GraphHarness.Run(project, OneRow(input), out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Single(result!.Facts);
        Assert.Equal(expected, result.Facts[0]!.Value);
    }

    // ---- MAP04: divide-by-zero policies --------------------------------------------

    [Fact]
    public void Ratio_DiagnosticPolicy_BlocksOnZero()
    {
        var (project, ctx) = RatioGraph("Diagnostic", 10m, 0m);
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success);
        Assert.Contains(result!.Diagnostics, d => d.Code == "MAP009");
        Assert.Empty(result.Facts);
    }

    [Fact]
    public void Ratio_MissingPolicy_SilentlyDropsFact()
    {
        var (project, ctx) = RatioGraph("Missing", 10m, 0m);
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success);
        Assert.DoesNotContain(result!.Diagnostics, d => d.Code == "MAP009");
        Assert.Empty(result.Facts);
    }

    [Fact]
    public void RatioFact_RecordsBothSourceNodesAndFactLevelTransformationIds()
    {
        const string numeratorBinding = "ratio-numerator";
        const string denominatorBinding = "ratio-denominator";
        var numerator = GraphHarness.Node(NodeTypeConstants.ExcelField, "Numerator",
            new() { ["bindingId"] = numeratorBinding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var denominator = GraphHarness.Node(NodeTypeConstants.ExcelField, "Denominator",
            new() { ["bindingId"] = denominatorBinding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var ratio = GraphHarness.Node(NodeTypeConstants.Ratio, "Ratio",
            new() { ["divideByZero"] = "Diagnostic" },
            inputs: new[]
            {
                GraphHarness.P("numerator", PortShape.Decimal),
                GraphHarness.P("denominator", PortShape.Decimal)
            },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(numerator, "value", ratio, "numerator"),
            GraphHarness.Link(denominator, "value", ratio, "denominator")
        };
        var sink = GraphHarness.FactSink(out var sinkEdges, ratio, "value", PortCardinality.Single);
        edges.AddRange(sinkEdges);
        var project = GraphHarness.Project(
            new[] { numerator, denominator, ratio }.Concat(sink), edges,
            bindings: new[]
            {
                GraphHarness.Binding(numeratorBinding),
                GraphHarness.Binding(denominatorBinding)
            });
        var numeratorSource = new SourceReference(numeratorBinding, "financials.Current", "pk:revenue");
        var denominatorSource = new SourceReference(denominatorBinding, "comparatives.Prior", "pk:revenue");
        var context = GraphHarness.Ctx(new[]
        {
            (numeratorBinding, new[]
            {
                GraphHarness.Row(("amount", DataValue.DecimalValue(10m, numeratorSource)))
            } as IReadOnlyList<DataRecord>),
            (denominatorBinding, new[]
            {
                GraphHarness.Row(("amount", DataValue.DecimalValue(2m, denominatorSource)))
            } as IReadOnlyList<DataRecord>)
        });

        var execution = GraphHarness.Run(project, context, out var compile);

        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
        var fact = Assert.Single(execution!.Facts);
        Assert.Equal(5m, fact.Value);
        Assert.Contains(numeratorSource, fact.Sources);
        Assert.Contains(denominatorSource, fact.Sources);
        Assert.Contains(numerator.NodeId, fact.TransformationIds);
        Assert.Contains(denominator.NodeId, fact.TransformationIds);
        Assert.Contains(ratio.NodeId, fact.TransformationIds);
        Assert.Contains(sink.Single(node => node.NodeType == NodeTypeConstants.FactConstruct).NodeId,
            fact.TransformationIds);

        var factConstruct = sink.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        var numeratorPath = Assert.Single(fact.SourceTransformationPaths,
            path => path.Source == numeratorSource);
        Assert.Equal(new[] { numerator.NodeId, ratio.NodeId, factConstruct.NodeId }, numeratorPath.TransformationIds);
        var denominatorPath = Assert.Single(fact.SourceTransformationPaths,
            path => path.Source == denominatorSource);
        Assert.Equal(new[] { denominator.NodeId, ratio.NodeId, factConstruct.NodeId }, denominatorPath.TransformationIds);
    }

    [Fact]
    public void RatioFactConstructRejection_ReportsBothSourcePathsAndKeepsPrimarySource()
    {
        const string numeratorBinding = "ratio-reject-numerator";
        const string denominatorBinding = "ratio-reject-denominator";
        var numerator = GraphHarness.Node(NodeTypeConstants.ExcelField, "Numerator",
            new() { ["bindingId"] = numeratorBinding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var denominator = GraphHarness.Node(NodeTypeConstants.ExcelField, "Denominator",
            new() { ["bindingId"] = denominatorBinding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var ratio = GraphHarness.Node(NodeTypeConstants.Ratio, "Ratio",
            new() { ["divideByZero"] = "Diagnostic" },
            inputs: new[]
            {
                GraphHarness.P("numerator", PortShape.Decimal),
                GraphHarness.P("denominator", PortShape.Decimal)
            },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(numerator, "value", ratio, "numerator"),
            GraphHarness.Link(denominator, "value", ratio, "denominator")
        };
        var sink = GraphHarness.FactSink(out var sinkEdges, ratio, "value", PortCardinality.Single);
        edges.AddRange(sinkEdges);
        var factConstruct = sink.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        factConstruct.Parameters["unit"] = "unsupported-unit";
        var project = GraphHarness.Project(
            new[] { numerator, denominator, ratio }.Concat(sink), edges,
            bindings: new[]
            {
                GraphHarness.Binding(numeratorBinding),
                GraphHarness.Binding(denominatorBinding)
            });
        var numeratorSource = new SourceReference(numeratorBinding, "financials.Current", "pk:revenue");
        var denominatorSource = new SourceReference(denominatorBinding, "comparatives.Prior", "pk:revenue");
        var context = GraphHarness.Ctx(new[]
        {
            (numeratorBinding, new[]
            {
                GraphHarness.Row(("amount", DataValue.DecimalValue(10m, numeratorSource)))
            } as IReadOnlyList<DataRecord>),
            (denominatorBinding, new[]
            {
                GraphHarness.Row(("amount", DataValue.DecimalValue(2m, denominatorSource)))
            } as IReadOnlyList<DataRecord>)
        });

        var execution = GraphHarness.Run(project, context, out var compile);

        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
        Assert.Empty(execution!.Facts);
        var diagnostic = Assert.Single(execution.Diagnostics, item => item.Code == DiagnosticCodes.XbrUnitMissing);
        Assert.Equal(numeratorSource, diagnostic.Source);
        Assert.Equal(2, diagnostic.SourceTransformationPaths?.Count);
        AssertPath(numeratorSource, numerator.NodeId);
        AssertPath(denominatorSource, denominator.NodeId);

        var diagnosticJson = JsonSerializer.Serialize(diagnostic);
        Assert.Equal(diagnosticJson,
            JsonSerializer.Serialize(diagnostic, DiagnosticsJsonContext.Default.Diagnostic));
        using var document = JsonDocument.Parse(diagnosticJson);
        var serializedPaths = document.RootElement.GetProperty(nameof(Diagnostic.SourceTransformationPaths))
            .EnumerateArray()
            .ToDictionary(
                path => path.GetProperty("Source").GetProperty("Locator").GetString()!,
                path => path.GetProperty("TransformationIds").EnumerateArray()
                    .Select(node => node.GetString()!).ToArray(),
                StringComparer.Ordinal);
        Assert.Equal(new[] { numerator.NodeId, ratio.NodeId, factConstruct.NodeId },
            serializedPaths[numeratorSource.Locator]);
        Assert.Equal(new[] { denominator.NodeId, ratio.NodeId, factConstruct.NodeId },
            serializedPaths[denominatorSource.Locator]);

        void AssertPath(SourceReference source, string fieldNodeId)
        {
            var path = Assert.Single(diagnostic.SourceTransformationPaths!, item => item.Source == source);
            Assert.Equal(new[] { fieldNodeId, ratio.NodeId, factConstruct.NodeId }, path.TransformationIds);
        }
    }

    [Fact]
    public void DiagnosticWithoutTransformationPaths_PreservesLegacyJsonBytes()
    {
        var diagnostic = new Diagnostic
        {
            Code = "XBR099",
            Subsystem = DiagnosticSubsystem.XBRL,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Execution,
            IsBlocking = true,
            Message = "invalid"
        };

        var json = JsonSerializer.Serialize(diagnostic);
        Assert.Equal(json, JsonSerializer.Serialize(diagnostic, DiagnosticsJsonContext.Default.Diagnostic));
        var emptyPathsJson = JsonSerializer.Serialize(diagnostic with
        {
            SourceTransformationPaths = Array.Empty<SourceTransformationPath>()
        });

        Assert.Equal("{\"Code\":\"XBR099\",\"Subsystem\":\"XBRL\",\"Severity\":2,\"Stage\":\"Execution\",\"IsBlocking\":true,\"Message\":\"invalid\",\"Source\":null,\"NodeId\":null,\"PortName\":null,\"EdgeId\":null,\"OperationId\":null,\"RuleIdentifier\":null,\"Details\":null}", json);
        Assert.Equal(json, emptyPathsJson);
    }

    private static (MappingProject, ExecutionContext) RatioGraph(string policy, decimal num, decimal den)
    {
        var n = GraphHarness.Node(NodeTypeConstants.ExcelField, "Num",
            new() { ["bindingId"] = Binding, ["column"] = "num" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var d = GraphHarness.Node(NodeTypeConstants.RunParameter, "Den",
            new() { ["name"] = "den", ["shape"] = "Decimal" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var ratio = GraphHarness.Node(NodeTypeConstants.Ratio, "Ratio",
            new() { ["divideByZero"] = policy },
            inputs: new[]
            {
                GraphHarness.P("numerator", PortShape.Decimal),
                GraphHarness.P("denominator", PortShape.Decimal),
            },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(n, "value", ratio, "numerator"),
            GraphHarness.Link(d, "value", ratio, "denominator"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, ratio, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { n, d, ratio }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });
        var ctx = GraphHarness.Ctx(
            new[] { (Binding, new[] { GraphHarness.Row(("num", D(num))) } as IReadOnlyList<DataRecord>) },
            parameters: new Dictionary<string, string> { ["den"] = den.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return (project, ctx);
    }

    // ---- MAP04: comparison operators ------------------------------------------------

    [Fact]
    public void Compare_ValidOp_GatesFilter()
    {
        var a = GraphHarness.Node(NodeTypeConstants.ExcelField, "A",
            new() { ["bindingId"] = Binding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var b = GraphHarness.Node(NodeTypeConstants.Constant, "B",
            new() { ["literal"] = "5", ["shape"] = "Decimal" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var cmp = GraphHarness.Node(NodeTypeConstants.Compare, "Cmp",
            new() { ["op"] = "Eq" },
            inputs: new[]
            {
                GraphHarness.P("a", PortShape.Decimal),
                GraphHarness.P("b", PortShape.Decimal),
            },
            outputs: new[] { GraphHarness.P("value", PortShape.Boolean) });
        var filter = GraphHarness.Node(NodeTypeConstants.Filter, "Filter",
            inputs: new[]
            {
                GraphHarness.P("input", PortShape.Decimal),
                GraphHarness.P("predicate", PortShape.Boolean),
            },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(a, "value", cmp, "a"),
            GraphHarness.Link(b, "value", cmp, "b"),
            GraphHarness.Link(a, "value", filter, "input"),
            GraphHarness.Link(cmp, "value", filter, "predicate"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, filter, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { a, b, cmp, filter }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var result = GraphHarness.Run(project, OneRow(5m), out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Single(result!.Facts);
        Assert.Equal(5m, result.Facts[0]!.Value);
    }

    [Fact]
    public void Compare_InvalidOp_ProducesStableDiagnostic()
    {
        var a = GraphHarness.Node(NodeTypeConstants.ExcelField, "A",
            new() { ["bindingId"] = Binding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var b = GraphHarness.Node(NodeTypeConstants.Constant, "B",
            new() { ["literal"] = "5", ["shape"] = "Decimal" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var cmp = GraphHarness.Node(NodeTypeConstants.Compare, "Cmp",
            new() { ["op"] = "Between" },
            inputs: new[]
            {
                GraphHarness.P("a", PortShape.Decimal),
                GraphHarness.P("b", PortShape.Decimal),
            },
            outputs: new[] { GraphHarness.P("value", PortShape.Boolean) });
        var filter = GraphHarness.Node(NodeTypeConstants.Filter, "Filter",
            inputs: new[]
            {
                GraphHarness.P("input", PortShape.Decimal),
                GraphHarness.P("predicate", PortShape.Boolean),
            },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(a, "value", cmp, "a"),
            GraphHarness.Link(b, "value", cmp, "b"),
            GraphHarness.Link(a, "value", filter, "input"),
            GraphHarness.Link(cmp, "value", filter, "predicate"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, filter, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { a, b, cmp, filter }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var result = GraphHarness.Run(project, OneRow(5m), out var compile);
        Assert.True(compile.Success);
        Assert.Contains(result!.Diagnostics, d => d.Code == "MAP019");
        Assert.Empty(result.Facts);
    }

    // ---- MAP07: named visual functions, pinned by revision --------------------------

    private static FunctionDefinition ScaleFunction(string fnId, int revision, string factor)
    {
        var input = GraphHarness.Node("functionInput", "in",
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var scale = GraphHarness.Node(NodeTypeConstants.Scale, "scale",
            new() { ["factor"] = factor },
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var output = GraphHarness.Node("functionOutput", "out",
            inputs: new[] { GraphHarness.P("value", PortShape.Decimal) });

        return new FunctionDefinition
        {
            FunctionId = fnId,
            Name = "Scale x" + factor,
            Revision = revision,
            CatalogVersion = FunctionCatalog.Default.Version,
            Inputs = new() { GraphHarness.P("input", PortShape.Decimal) },
            Outputs = new() { GraphHarness.P("value", PortShape.Decimal) },
            Nodes = new() { input, scale, output },
            Edges = new()
            {
                GraphHarness.Link(input, "value", scale, "input"),
                GraphHarness.Link(scale, "value", output, "value"),
            }
        };
    }

    [Fact]
    public void FunctionCall_InlinesSubgraph_AtPinnedRevision()
    {
        var fn = ScaleFunction("scale10", 1, "10");
        var excel = Driver(out var edges, 5m);
        var call = GraphHarness.Node(NodeTypeConstants.FunctionCall, "Call",
            new() { ["functionId"] = "scale10", ["revision"] = "1" },
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal, required: false) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        edges.Add(GraphHarness.Link(excel, "value", call, "input"));
        var sink = GraphHarness.FactSink(out var factEdges, call, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { excel, call }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) },
            functions: new[] { fn });

        var result = GraphHarness.Run(project, OneRow(5m), out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Single(result!.Facts);
        Assert.Equal(50m, result.Facts[0]!.Value); // 5 inlined through scale x10
    }

    [Fact]
    public void FunctionCall_PinnedRevision_SelectsMatchingBody()
    {
        var rev1 = ScaleFunction("scaleX", 1, "10");
        var rev2 = ScaleFunction("scaleX", 2, "100");

        var excel = Driver(out var edges, 5m);
        var callA = GraphHarness.Node(NodeTypeConstants.FunctionCall, "A@1",
            new() { ["functionId"] = "scaleX", ["revision"] = "1" },
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal, required: false) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var callB = GraphHarness.Node(NodeTypeConstants.FunctionCall, "B@2",
            new() { ["functionId"] = "scaleX", ["revision"] = "2" },
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal, required: false) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        edges.Add(GraphHarness.Link(excel, "value", callA, "input"));
        edges.Add(GraphHarness.Link(excel, "value", callB, "input"));

        var sinkA = GraphHarness.FactSink(out var factEdgesA, callA, "value", PortCardinality.Single);
        edges.AddRange(factEdgesA);
        var sinkB = GraphHarness.FactSink(out var factEdgesB, callB, "value", PortCardinality.Single);
        edges.AddRange(factEdgesB);

        var project = GraphHarness.Project(
            new List<MappingNode> { excel, callA, callB }.Concat(sinkA).Concat(sinkB), edges,
            bindings: new[] { GraphHarness.Binding(Binding) },
            functions: new[] { rev1, rev2 });

        var result = GraphHarness.Run(project, OneRow(5m), out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        var values = result!.Facts.Select(f => f.Value!.Value).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 50m, 500m }, values);
    }

    [Fact]
    public void FunctionCall_UnknownRevision_BlocksCompile()
    {
        var fn = ScaleFunction("scale10", 1, "10");
        var excel = Driver(out var edges, 5m);
        var call = GraphHarness.Node(NodeTypeConstants.FunctionCall, "Call@9",
            new() { ["functionId"] = "scale10", ["revision"] = "9" },
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal, required: false) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        edges.Add(GraphHarness.Link(excel, "value", call, "input"));
        var sink = GraphHarness.FactSink(out var factEdges, call, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { excel, call }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) },
            functions: new[] { fn });

        GraphHarness.Run(project, OneRow(5m), out var compile);
        Assert.False(compile.Success);
        Assert.Contains(compile.Diagnostics.Items, d => d.Code == "MAP020");
    }

    [Fact]
    public void EditingFunction_AltersSemanticDigest()
    {
        var fnA = ScaleFunction("scale10", 1, "10");
        var fnB = ScaleFunction("scale10", 1, "100"); // same id@revision, different body
        var excel = Driver(out var e1, 5m);
        var p1 = GraphHarness.Project(new[] { excel }, e1, new[] { GraphHarness.Binding(Binding) }, new[] { fnA });
        var p2 = GraphHarness.Project(new[] { excel }, e1, new[] { GraphHarness.Binding(Binding) }, new[] { fnB });
        Assert.NotEqual(p1.ComputeSemanticDigest(), p2.ComputeSemanticDigest());
    }

    [Fact]
    public void SerializingProject_PreservesFunctionsRoundTrip()
    {
        var fn = ScaleFunction("scale10", 3, "10");
        var excel = Driver(out var edges, 5m);
        var project = GraphHarness.Project(new[] { excel }, edges,
            new[] { GraphHarness.Binding(Binding) }, new[] { fn });

        var json = ProjectSerializer.Serialize(project);
        var restored = ProjectSerializer.Deserialize(json);

        Assert.Single(restored.Functions);
        Assert.Equal("scale10", restored.Functions[0].FunctionId);
        Assert.Equal(3, restored.Functions[0].Revision);
        Assert.Equal(3, restored.Functions[0].Nodes.Count);
        Assert.Equal(project.ComputeSemanticDigest(), restored.ComputeSemanticDigest());
    }
}
