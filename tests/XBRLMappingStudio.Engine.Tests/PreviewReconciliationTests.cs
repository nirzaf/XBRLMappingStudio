using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine.Tests;

/// <summary>
/// MAP08: preview runs are visibly qualified (partial) and reconcile against an
/// independently-computed full-run total. Sampled aggregates must never be mistaken
/// for a complete figure.
/// </summary>
public class PreviewReconciliationTests
{
    private const string Binding = "b1";
    private static DataValue D(decimal v) => DataValue.DecimalValue(v);

    private static MappingProject SumProject(out IReadOnlyList<DataRecord> rows)
    {
        rows = new[]
        {
            GraphHarness.Row(("amount", D(100m))),
            GraphHarness.Row(("amount", D(200m))),
            GraphHarness.Row(("amount", D(300m))),
        };

        var excel = GraphHarness.Node(NodeTypeConstants.ExcelField, "Amounts",
            new() { ["bindingId"] = Binding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var collect = GraphHarness.Node(NodeTypeConstants.Collect, "Collect",
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal) },
            outputs: new[] { GraphHarness.P("items", PortShape.Decimal, PortCardinality.Collection) });
        var sum = GraphHarness.Node(NodeTypeConstants.Sum, "Sum",
            inputs: new[] { GraphHarness.P("items", PortShape.Decimal, PortCardinality.Collection) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(excel, "value", collect, "input"),
            GraphHarness.Link(collect, "items", sum, "items"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, sum, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        return GraphHarness.Project(
            new List<MappingNode> { excel, collect, sum }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });
    }

    [Fact]
    public void FullRun_MatchesIndependentlyComputedOracle()
    {
        var project = SumProject(out _);
        var rows = BuildSourcedRows(50);
        // Independent oracle: summed outside the graph.
        decimal oracle = rows.Sum(r => r.Fields["amount"].AsDecimal());

        var ctx = GraphHarness.Ctx(new[] { (Binding, (IReadOnlyList<DataRecord>)rows) });
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));

        Assert.Single(result!.Facts);
        Assert.Equal(oracle, result.Facts[0]!.Value);
        // A full run records no partial sampled totals.
        Assert.Empty(ctx.SampledTotals);

        var sourceNodeId = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField).NodeId;
        var source = Assert.Single(result.NodeOutputPreviews, output =>
            output.NodeId == sourceNodeId && output.PortName == "value");
        Assert.Equal(rows.Length, source.ValueCount);
        Assert.Equal(rows.Length, source.PresentCount);
        Assert.False(source.IsPartial);
        Assert.Equal(NodeOutputPreviewTracker.MaximumSamplesPerPort, source.Samples.Count);
        Assert.Equal(new[] { "1", "2", "3" }, source.Samples.Select(sample => sample.Value));
        Assert.Equal(new[] { "row=1,amount", "row=2,amount", "row=3,amount" },
            source.Samples.Select(sample => Assert.Single(sample.Contributors).Source.Locator));

        var totalNodeId = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Sum).NodeId;
        var total = Assert.Single(result.NodeOutputPreviews, output =>
            output.NodeId == totalNodeId && output.PortName == "value");
        Assert.Equal(oracle.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(total.Samples).Value);
        Assert.False(total.IsPartial);
        Assert.Equal(1, total.ValueCount);
    }

    [Fact]
    public void PreviewRun_IsVisiblyQualifiedAsPartial()
    {
        var project = SumProject(out _);
        var rows = BuildSourcedRows(5);
        var ctx = GraphHarness.Ctx(new[] { (Binding, (IReadOnlyList<DataRecord>)rows) },
            preview: true, previewLimit: 2);
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success);

        // Sampled total over the first two rows only.
        Assert.Single(result!.Facts);
        Assert.Equal(3m, result.Facts[0]!.Value);

        var sampled = Assert.Single(ctx.SampledTotals);
        Assert.True(sampled.IsPartial);
        Assert.Equal(3m, sampled.Value);
        Assert.NotEqual(rows.Sum(r => r.Fields["amount"].AsDecimal()), sampled.Value);

        var sourceNodeId = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField).NodeId;
        var source = Assert.Single(result.NodeOutputPreviews, output =>
            output.NodeId == sourceNodeId && output.PortName == "value");
        Assert.Equal(2, source.ValueCount);
        Assert.True(source.IsPartial);
        Assert.Equal(new[] { "1", "2" }, source.Samples.Select(item => item.Value));
        Assert.Equal(new[] { "row=1,amount", "row=2,amount" }, source.Samples
            .Select(item => Assert.Single(item.Contributors).Source.Locator));

        var sumNodeId = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Sum).NodeId;
        var intermediateTotal = Assert.Single(result.NodeOutputPreviews, output =>
            output.NodeId == sumNodeId && output.PortName == "value");
        Assert.True(intermediateTotal.IsPartial);
        Assert.Equal("3", Assert.Single(intermediateTotal.Samples).Value);
        Assert.Equal(2, Assert.Single(intermediateTotal.Samples).Contributors.Count);
    }

    [Fact]
    public void CancelledExecution_DoesNotReturnPartialNodeOutputs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var project = SumProject(out _);
        var context = GraphHarness.Ctx(new[] { (Binding, (IReadOnlyList<DataRecord>)BuildSourcedRows(20)) },
            cancellationToken: cancellation.Token);

        var result = GraphHarness.Run(project, context, out var compile);

        Assert.True(compile.Success);
        Assert.NotNull(result);
        Assert.Equal(XBRLMappingStudio.Diagnostics.RunOutcome.Cancelled, result!.Outcome);
        Assert.Empty(result.Facts);
        Assert.Empty(result.NodeOutputPreviews);
    }

    private static DataRecord[] BuildSourcedRows(int count)
        => Enumerable.Range(1, count).Select(index => GraphHarness.Row(("amount",
            DataValue.DecimalValue(index, new XBRLMappingStudio.Diagnostics.SourceReference(
                Binding, $"row={index},amount"))))).ToArray();
}
