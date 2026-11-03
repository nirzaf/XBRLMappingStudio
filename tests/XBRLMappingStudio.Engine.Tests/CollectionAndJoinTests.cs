using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine.Tests;

/// <summary>
/// MAP05 (collections and group aggregation) and MAP06 (joins and lookup cardinality)
/// acceptance over hand-authored sources executed through the two-phase plan.
/// </summary>
public class CollectionAndJoinTests
{
    private const string Binding = "b1";

    private static DataValue D(decimal v) => DataValue.DecimalValue(v);
    private static DataValue S(string v) => DataValue.StringValue(v);

    // ---- MAP05: scalar collection reduce -> single aggregate fact -------------------

    [Fact]
    public async Task CollectThenSum_EmitsOneAggregateFact()
    {
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

        var project = GraphHarness.Project(
            new List<MappingNode> { excel, collect, sum }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var ctx = GraphHarness.Ctx(new[] { (Binding, new[] {
            GraphHarness.Row(("amount", D(100m))),
            GraphHarness.Row(("amount", D(200m))),
            GraphHarness.Row(("amount", D(300m))),
        }.ToList() as IReadOnlyList<DataRecord>) });

        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.NotNull(result);
        Assert.Single(result!.Facts);
        Assert.Equal(600m, result.Facts[0]!.Value);
        await Task.CompletedTask;
    }

    // ---- MAP05: group aggregation buckets independently ----------------------------

    [Fact]
    public void GroupBy_AggregatesEachEntityIndependently()
    {
        var rows = new[]
        {
            GraphHarness.Row(("entity", S("X")), ("amount", D(10m))),
            GraphHarness.Row(("entity", S("X")), ("amount", D(5m))),
            GraphHarness.Row(("entity", S("Y")), ("amount", D(20m))),
        };

        var collectRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Rows",
            new() { ["binding"] = Binding },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var group = GraphHarness.Node(NodeTypeConstants.GroupBy, "By entity",
            new() { ["keys"] = "entity", ["sumField"] = "amount" },
            inputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) },
            outputs: new[]
            {
                GraphHarness.P("values", PortShape.Decimal, PortCardinality.Collection),
                GraphHarness.P("keys", PortShape.String, PortCardinality.Collection),
            });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(collectRows, "items", group, "items"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, group, "values", PortCardinality.Collection);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { collectRows, group }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var ctx = GraphHarness.Ctx(new[] { (Binding, rows as IReadOnlyList<DataRecord>) });
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.NotNull(result);

        var values = result!.Facts.Select(f => f.Value!.Value).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 15m, 20m }, values);
    }

    // ---- MAP06: equi-join feeds an independent group aggregation --------------------

    [Fact]
    public void JoinThenGroup_CorrectlyCorrelatesTwoTables()
    {
        const string Left = "L";
        const string Right = "R";

        var left = new[]
        {
            GraphHarness.Row(("k", S("1")), ("a", D(10m))),
            GraphHarness.Row(("k", S("1")), ("a", D(5m))),
            GraphHarness.Row(("k", S("2")), ("a", D(20m))),
        };
        var right = new[]
        {
            GraphHarness.Row(("k", S("1")), ("name", S("alpha"))),
            GraphHarness.Row(("k", S("2")), ("name", S("beta"))),
        };

        var lRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Left",
            new() { ["binding"] = Left },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var rRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Right",
            new() { ["binding"] = Right },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var join = GraphHarness.Node(NodeTypeConstants.Join, "Join",
            new() { ["leftKey"] = "k", ["rightKey"] = "k", ["cardinality"] = "Inner", ["duplicateKey"] = "All" },
            inputs: new[]
            {
                GraphHarness.P("left", PortShape.Record, PortCardinality.Collection),
                GraphHarness.P("right", PortShape.Record, PortCardinality.Collection),
            },
            outputs: new[] { GraphHarness.P("rows", PortShape.Record, PortCardinality.Collection) });
        var group = GraphHarness.Node(NodeTypeConstants.GroupBy, "By name",
            new() { ["keys"] = "name", ["sumField"] = "a" },
            inputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) },
            outputs: new[]
            {
                GraphHarness.P("values", PortShape.Decimal, PortCardinality.Collection),
                GraphHarness.P("keys", PortShape.String, PortCardinality.Collection),
            });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(lRows, "items", join, "left"),
            GraphHarness.Link(rRows, "items", join, "right"),
            GraphHarness.Link(join, "rows", group, "items"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, group, "values", PortCardinality.Collection);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { lRows, rRows, join, group }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Left), GraphHarness.Binding(Right) });

        var ctx = GraphHarness.Ctx(new[]
        {
            (Left, left as IReadOnlyList<DataRecord>),
            (Right, right as IReadOnlyList<DataRecord>),
        });
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));

        var values = result!.Facts.Select(f => f.Value!.Value).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 15m, 20m }, values);
    }

    [Fact]
    public void JoinDuplicateKeyFail_BlocksDiagnostic()
    {
        const string Left = "L";
        const string Right = "R";
        var left = new[] { GraphHarness.Row(("k", S("1")), ("a", D(1m))) };
        var right = new[]
        {
            GraphHarness.Row(("k", S("1")), ("name", S("alpha"))),
            GraphHarness.Row(("k", S("1")), ("name", S("alpha2"))),
        };

        var lRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Left",
            new() { ["binding"] = Left },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var rRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Right",
            new() { ["binding"] = Right },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var join = GraphHarness.Node(NodeTypeConstants.Join, "Join",
            new() { ["leftKey"] = "k", ["rightKey"] = "k", ["cardinality"] = "Inner", ["duplicateKey"] = "Fail" },
            inputs: new[]
            {
                GraphHarness.P("left", PortShape.Record, PortCardinality.Collection),
                GraphHarness.P("right", PortShape.Record, PortCardinality.Collection),
            },
            outputs: new[] { GraphHarness.P("rows", PortShape.Record, PortCardinality.Collection) });
        var group = GraphHarness.Node(NodeTypeConstants.GroupBy, "g",
            new() { ["keys"] = "name", ["sumField"] = "a" },
            inputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) },
            outputs: new[]
            {
                GraphHarness.P("values", PortShape.Decimal, PortCardinality.Collection),
                GraphHarness.P("keys", PortShape.String, PortCardinality.Collection),
            });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(lRows, "items", join, "left"),
            GraphHarness.Link(rRows, "items", join, "right"),
            GraphHarness.Link(join, "rows", group, "items"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, group, "values", PortCardinality.Collection);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { lRows, rRows, join, group }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Left), GraphHarness.Binding(Right) });

        var ctx = GraphHarness.Ctx(new[]
        {
            (Left, left as IReadOnlyList<DataRecord>),
            (Right, right as IReadOnlyList<DataRecord>),
        });
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.NotNull(result);
        Assert.Contains(result!.Diagnostics, d => d.Code == "MAP017");
        Assert.Empty(result.Facts);
    }

    [Fact]
    public void JoinExpansionBudget_BlocksBeforePublication()
    {
        const string Left = "L";
        const string Right = "R";
        var left = new[]
        {
            GraphHarness.Row(("k", S("1")), ("a", D(1m))),
            GraphHarness.Row(("k", S("1")), ("a", D(2m))),
        };
        var right = new[]
        {
            GraphHarness.Row(("k", S("1")), ("name", S("x"))),
            GraphHarness.Row(("k", S("1")), ("name", S("y"))),
            GraphHarness.Row(("k", S("1")), ("name", S("z"))),
        };

        var lRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Left",
            new() { ["binding"] = Left },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var rRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Right",
            new() { ["binding"] = Right },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var join = GraphHarness.Node(NodeTypeConstants.Join, "Join",
            new() { ["leftKey"] = "k", ["rightKey"] = "k", ["cardinality"] = "Inner", ["duplicateKey"] = "All" },
            inputs: new[]
            {
                GraphHarness.P("left", PortShape.Record, PortCardinality.Collection),
                GraphHarness.P("right", PortShape.Record, PortCardinality.Collection),
            },
            outputs: new[] { GraphHarness.P("rows", PortShape.Record, PortCardinality.Collection) });
        var group = GraphHarness.Node(NodeTypeConstants.GroupBy, "g",
            new() { ["keys"] = "name", ["sumField"] = "a" },
            inputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) },
            outputs: new[]
            {
                GraphHarness.P("values", PortShape.Decimal, PortCardinality.Collection),
                GraphHarness.P("keys", PortShape.String, PortCardinality.Collection),
            });

        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(lRows, "items", join, "left"),
            GraphHarness.Link(rRows, "items", join, "right"),
            GraphHarness.Link(join, "rows", group, "items"),
        };
        var sink = GraphHarness.FactSink(out var factEdges, group, "values", PortCardinality.Collection);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { lRows, rRows, join, group }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Left), GraphHarness.Binding(Right) });

        var ctx = GraphHarness.Ctx(
            new[] { (Left, left as IReadOnlyList<DataRecord>), (Right, right as IReadOnlyList<DataRecord>) },
            maxJoinRows: 2);
        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        // 2 left x 3 right = 6 join rows for key "1", budget 2 -> expansion exceeded.
        Assert.Contains(result!.Diagnostics, d => d.Code == "MAP015");
    }

    // ---- MAP06: lookup over a project-pinned dictionary -----------------------------

    [Fact]
    public void Lookup_FetchesValueByKeyFromSnapshot()
    {
        var excel = GraphHarness.Node(NodeTypeConstants.ExcelField, "Code",
            new() { ["bindingId"] = Binding, ["column"] = "code" },
            outputs: new[] { GraphHarness.P("value", PortShape.String) });
        var lookup = GraphHarness.Node(NodeTypeConstants.Lookup, "Fx",
            new() { ["table"] = "fxrates", ["missingKey"] = "Missing" },
            inputs: new[] { GraphHarness.P("key", PortShape.String) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });

        var edges = new List<MappingEdge> { GraphHarness.Link(excel, "value", lookup, "key") };
        var sink = GraphHarness.FactSink(out var factEdges, lookup, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { excel, lookup }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var ctx = GraphHarness.Ctx(
            new[] { (Binding, new[] { GraphHarness.Row(("code", S("EUR"))) } as IReadOnlyList<DataRecord>) },
            parameters: new Dictionary<string, string> { ["lookup:fxrates"] = "{\"EUR\":4.5}" });

        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Single(result!.Facts);
        Assert.Equal(4.5m, result.Facts[0]!.Value);
    }

    [Fact]
    public void Lookup_MissingKeyDiagnosticPolicy_BlocksFact()
    {
        var excel = GraphHarness.Node(NodeTypeConstants.ExcelField, "Code",
            new() { ["bindingId"] = Binding, ["column"] = "code" },
            outputs: new[] { GraphHarness.P("value", PortShape.String) });
        var lookup = GraphHarness.Node(NodeTypeConstants.Lookup, "Fx",
            new() { ["table"] = "fxrates", ["missingKey"] = "Diagnostic" },
            inputs: new[] { GraphHarness.P("key", PortShape.String) },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });

        var edges = new List<MappingEdge> { GraphHarness.Link(excel, "value", lookup, "key") };
        var sink = GraphHarness.FactSink(out var factEdges, lookup, "value", PortCardinality.Single);
        edges.AddRange(factEdges);

        var project = GraphHarness.Project(
            new List<MappingNode> { excel, lookup }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var ctx = GraphHarness.Ctx(
            new[] { (Binding, new[] { GraphHarness.Row(("code", S("GBP"))) } as IReadOnlyList<DataRecord>) },
            parameters: new Dictionary<string, string> { ["lookup:fxrates"] = "{\"EUR\":4.5}" });

        var result = GraphHarness.Run(project, ctx, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(d => d.Message)));
        Assert.Contains(result!.Diagnostics, d => d.Code == "MAP016");
        Assert.Empty(result.Facts);
    }
}
