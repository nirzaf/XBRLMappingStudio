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
        var project = SumProject(out var rows);
        // Independent oracle: summed outside the graph.
        decimal oracle = rows.Sum(r => r.Fields["amount"].AsDecimal());

        var ctx = GraphHarness.Ctx(new[] { (Binding, rows) });
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));

        Assert.Single(result!.Facts);
        Assert.Equal(oracle, result.Facts[0]!.Value);
        // A full run records no partial sampled totals.
        Assert.Empty(ctx.SampledTotals);
    }

    [Fact]
    public void PreviewRun_IsVisiblyQualifiedAsPartial()
    {
        var project = SumProject(out var rows);
        var ctx = GraphHarness.Ctx(new[] { (Binding, rows) }, preview: true, previewLimit: 2);
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success);

        // Sampled total over the first two rows only.
        Assert.Single(result!.Facts);
        Assert.Equal(300m, result.Facts[0]!.Value);

        var sampled = Assert.Single(ctx.SampledTotals);
        Assert.True(sampled.IsPartial);
        Assert.Equal(300m, sampled.Value);
        Assert.NotEqual(rows.Sum(r => r.Fields["amount"].AsDecimal()), sampled.Value);
    }
}
