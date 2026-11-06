using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

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

    [Fact]
    public async Task CollectThenSum_PreservesEachRowSourcePathThroughAggregateEmission()
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
            GraphHarness.Link(collect, "items", sum, "items")
        };
        var sink = GraphHarness.FactSink(out var sinkEdges, sum, "value", PortCardinality.Single);
        edges.AddRange(sinkEdges);
        var factConstruct = sink.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        var sources = Enumerable.Range(1, 3)
            .Select(index => new SourceReference(Binding, "financials.Current", $"row:{index}"))
            .ToArray();
        var rows = sources.Select((source, index) => GraphHarness.Row(
            ("amount", DataValue.DecimalValue((index + 1) * 10m, source)))).ToList();
        var project = GraphHarness.Project(new[] { excel, collect, sum }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var result = GraphHarness.Run(project,
            GraphHarness.Ctx(new[] { (Binding, rows as IReadOnlyList<DataRecord>) }), out var compile);

        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
        var fact = Assert.Single(result!.Facts);
        Assert.Equal(60m, fact.Value);
        Assert.Equal(3, fact.SourceTransformationPaths.Count);
        foreach (var source in sources)
        {
            var path = Assert.Single(fact.SourceTransformationPaths, item => item.Source == source);
            Assert.Equal(new[] { excel.NodeId, collect.NodeId, sum.NodeId, factConstruct.NodeId },
                path.TransformationIds);
        }
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("sum", PortShape.Decimal, 60, 3)]
    [InlineData("count", PortShape.Integer, 3, 3)]
    [InlineData("first", PortShape.Decimal, 10, 1)]
    public void CollectAggregates_PreserveOnlyContributingSourcePaths(
        string aggregateType, PortShape outputShape, int expected, int expectedSources)
    {
        var excel = GraphHarness.Node(NodeTypeConstants.ExcelField, "Amounts",
            new() { ["bindingId"] = Binding, ["column"] = "amount" },
            outputs: new[] { GraphHarness.P("value", PortShape.Decimal) });
        var collect = GraphHarness.Node(NodeTypeConstants.Collect, "Collect",
            inputs: new[] { GraphHarness.P("input", PortShape.Decimal) },
            outputs: new[] { GraphHarness.P("items", PortShape.Decimal, PortCardinality.Collection) });
        var aggregate = GraphHarness.Node(aggregateType, aggregateType,
            inputs: new[] { GraphHarness.P("items", PortShape.Decimal, PortCardinality.Collection) },
            outputs: new[] { GraphHarness.P("value", outputShape) });
        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(excel, "value", collect, "input"),
            GraphHarness.Link(collect, "items", aggregate, "items")
        };
        var sink = GraphHarness.FactSink(out var sinkEdges, aggregate, "value", PortCardinality.Single);
        edges.AddRange(sinkEdges);
        var factConstruct = sink.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        var sources = new[]
        {
            new SourceReference(Binding, "financials.Current", "row:1"),
            new SourceReference(Binding, "financials.Current", "row:2"),
            new SourceReference(Binding, "financials.Current", "row:3")
        };
        var values = new[] { 10m, 20m, 30m };
        var rows = values.Select((value, index) => GraphHarness.Row(
            ("amount", DataValue.DecimalValue(value, sources[index])))).ToList();
        var project = GraphHarness.Project(new[] { excel, collect, aggregate }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var result = GraphHarness.Run(project,
            GraphHarness.Ctx(new[] { (Binding, rows as IReadOnlyList<DataRecord>) }), out var compile);

        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
        var fact = Assert.Single(result!.Facts);
        Assert.Equal((decimal)expected, fact.Value);
        Assert.Equal(expectedSources, fact.SourceTransformationPaths.Count);
        var expectedSourceSet = aggregateType == NodeTypeConstants.First
            ? new[] { sources[0] }
            : sources;
        Assert.Equal(expectedSourceSet.OrderBy(source => source.Locator),
            fact.SourceTransformationPaths.Select(path => path.Source).OrderBy(source => source.Locator));
        foreach (var path in fact.SourceTransformationPaths)
            Assert.Equal(new[] { excel.NodeId, collect.NodeId, aggregate.NodeId, factConstruct.NodeId },
                path.TransformationIds);
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

    [Fact]
    public void GroupBySetEmission_PreservesOnlyMemberRowsAndFieldSources()
    {
        var collectRows = GraphHarness.Node(NodeTypeConstants.CollectRows, "Rows",
            new() { ["binding"] = Binding },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var group = GraphHarness.Node(NodeTypeConstants.GroupBy, "By entity",
            new() { ["keys"] = "entity", ["sumField"] = "amount" },
            inputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) },
            outputs: new[]
            {
                GraphHarness.P("values", PortShape.Decimal, PortCardinality.Collection),
                GraphHarness.P("keys", PortShape.String, PortCardinality.Collection)
            });
        var edges = new List<MappingEdge> { GraphHarness.Link(collectRows, "items", group, "items") };
        var sink = GraphHarness.FactSink(out var sinkEdges, group, "values", PortCardinality.Collection);
        edges.AddRange(sinkEdges);
        var factConstruct = sink.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        var rowSources = new[]
        {
            (new SourceReference(Binding, "financials.Current", "row:1:entity"),
                new SourceReference(Binding, "financials.Current", "row:1:amount")),
            (new SourceReference(Binding, "financials.Current", "row:2:entity"),
                new SourceReference(Binding, "financials.Current", "row:2:amount")),
            (new SourceReference(Binding, "financials.Current", "row:3:entity"),
                new SourceReference(Binding, "financials.Current", "row:3:amount"))
        };
        var rows = new[]
        {
            GraphHarness.Row(
                ("entity", DataValue.StringValue("A", rowSources[0].Item1)),
                ("amount", DataValue.DecimalValue(10m, rowSources[0].Item2))),
            GraphHarness.Row(
                ("entity", DataValue.StringValue("A", rowSources[1].Item1)),
                ("amount", DataValue.DecimalValue(5m, rowSources[1].Item2))),
            GraphHarness.Row(
                ("entity", DataValue.StringValue("B", rowSources[2].Item1)),
                ("amount", DataValue.DecimalValue(20m, rowSources[2].Item2)))
        };
        var project = GraphHarness.Project(new[] { collectRows, group }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Binding) });

        var result = GraphHarness.Run(project,
            GraphHarness.Ctx(new[] { (Binding, rows as IReadOnlyList<DataRecord>) }), out var compile);

        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
        Assert.Equal(new[] { 15m, 20m }, result!.Facts.Select(fact => fact.Value!.Value).ToArray());
        var firstGroup = result.Facts.Single(fact => fact.Value == 15m);
        var secondGroup = result.Facts.Single(fact => fact.Value == 20m);
        var firstSources = new[] { rowSources[0].Item1, rowSources[0].Item2,
            rowSources[1].Item1, rowSources[1].Item2 };
        var secondSources = new[] { rowSources[2].Item1, rowSources[2].Item2 };
        Assert.Equal(firstSources.OrderBy(source => source.Locator),
            firstGroup.SourceTransformationPaths.Select(path => path.Source).OrderBy(source => source.Locator));
        Assert.Equal(secondSources.OrderBy(source => source.Locator),
            secondGroup.SourceTransformationPaths.Select(path => path.Source).OrderBy(source => source.Locator));
        foreach (var fact in result.Facts)
        {
            Assert.All(fact.SourceTransformationPaths, path =>
                Assert.Equal(new[] { collectRows.NodeId, group.NodeId, factConstruct.NodeId },
                    path.TransformationIds));
        }
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
    public void JoinThenGroup_PreservesJoinedKeyAndValueFieldPathsPerMemberRow()
    {
        const string Left = "L";
        const string Right = "R";

        var leftRows = new[]
        {
            GraphHarness.Row(
                ("k", DataValue.StringValue("1", new SourceReference(Left, "left:1:key"))),
                ("a", DataValue.DecimalValue(10m, new SourceReference(Left, "left:1:amount")))),
            GraphHarness.Row(
                ("k", DataValue.StringValue("1", new SourceReference(Left, "left:2:key"))),
                ("a", DataValue.DecimalValue(5m, new SourceReference(Left, "left:2:amount")))),
            GraphHarness.Row(
                ("k", DataValue.StringValue("2", new SourceReference(Left, "left:3:key"))),
                ("a", DataValue.DecimalValue(20m, new SourceReference(Left, "left:3:amount"))))
        };
        var rightRows = new[]
        {
            GraphHarness.Row(
                ("k", DataValue.StringValue("1", new SourceReference(Right, "right:1:key"))),
                ("name", DataValue.StringValue("alpha", new SourceReference(Right, "right:1:name")))),
            GraphHarness.Row(
                ("k", DataValue.StringValue("2", new SourceReference(Right, "right:2:key"))),
                ("name", DataValue.StringValue("beta", new SourceReference(Right, "right:2:name"))))
        };

        var leftCollect = GraphHarness.Node(NodeTypeConstants.CollectRows, "Left",
            new() { ["binding"] = Left },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var rightCollect = GraphHarness.Node(NodeTypeConstants.CollectRows, "Right",
            new() { ["binding"] = Right },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var join = GraphHarness.Node(NodeTypeConstants.Join, "Join",
            new() { ["leftKey"] = "k", ["rightKey"] = "k", ["cardinality"] = "Inner", ["duplicateKey"] = "All" },
            inputs: new[]
            {
                GraphHarness.P("left", PortShape.Record, PortCardinality.Collection),
                GraphHarness.P("right", PortShape.Record, PortCardinality.Collection)
            },
            outputs: new[] { GraphHarness.P("rows", PortShape.Record, PortCardinality.Collection) });
        var group = GraphHarness.Node(NodeTypeConstants.GroupBy, "By name",
            new() { ["keys"] = "name", ["sumField"] = "a" },
            inputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) },
            outputs: new[]
            {
                GraphHarness.P("values", PortShape.Decimal, PortCardinality.Collection),
                GraphHarness.P("keys", PortShape.String, PortCardinality.Collection)
            });
        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(leftCollect, "items", join, "left"),
            GraphHarness.Link(rightCollect, "items", join, "right"),
            GraphHarness.Link(join, "rows", group, "items")
        };
        var sink = GraphHarness.FactSink(out var sinkEdges, group, "values", PortCardinality.Collection);
        edges.AddRange(sinkEdges);
        var factConstruct = sink.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        var project = GraphHarness.Project(new[] { leftCollect, rightCollect, join, group }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(Left), GraphHarness.Binding(Right) });

        var result = GraphHarness.Run(project, GraphHarness.Ctx(new[]
        {
            (Left, leftRows as IReadOnlyList<DataRecord>),
            (Right, rightRows as IReadOnlyList<DataRecord>)
        }), out var compile);

        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
        var alpha = Assert.Single(result!.Facts, fact => fact.Value == 15m);
        var beta = Assert.Single(result.Facts, fact => fact.Value == 20m);
        AssertPaths(alpha, new[]
        {
            (new SourceReference(Left, "left:1:key"), leftCollect.NodeId),
            (new SourceReference(Left, "left:1:amount"), leftCollect.NodeId),
            (new SourceReference(Left, "left:2:key"), leftCollect.NodeId),
            (new SourceReference(Left, "left:2:amount"), leftCollect.NodeId),
            (new SourceReference(Right, "right:1:key"), rightCollect.NodeId),
            (new SourceReference(Right, "right:1:name"), rightCollect.NodeId)
        });
        AssertPaths(beta, new[]
        {
            (new SourceReference(Left, "left:3:key"), leftCollect.NodeId),
            (new SourceReference(Left, "left:3:amount"), leftCollect.NodeId),
            (new SourceReference(Right, "right:2:key"), rightCollect.NodeId),
            (new SourceReference(Right, "right:2:name"), rightCollect.NodeId)
        });

        void AssertPaths(PendingFact fact, (SourceReference Source, string CollectNodeId)[] expected)
        {
            Assert.Equal(expected.Select(item => item.Source).OrderBy(source => source.Locator),
                fact.SourceTransformationPaths.Select(path => path.Source).OrderBy(source => source.Locator));
            foreach (var item in expected)
            {
                var path = Assert.Single(fact.SourceTransformationPaths, candidate => candidate.Source == item.Source);
                Assert.Equal(new[] { item.CollectNodeId, join.NodeId, group.NodeId, factConstruct.NodeId },
                    path.TransformationIds);
            }
        }
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
    public void JoinCardinalityAndDuplicatePolicies_MatchIndependentOracle()
    {
        var leftRows = new[]
        {
            GraphHarness.Row(("k", S("1")), ("a", D(10m))),
            GraphHarness.Row(("k", S("2")), ("a", D(20m)))
        };
        var rightRows = new[]
        {
            GraphHarness.Row(("k", S("1")), ("name", S("first"))),
            GraphHarness.Row(("k", S("1")), ("name", S("duplicate"))),
            GraphHarness.Row(("k", S("3")), ("name", S("right-only")))
        };

        decimal[] Values(string cardinality, string duplicateKey, int maxJoinRows = 200_000)
            => RunJoinToGroupedFacts(leftRows, rightRows, cardinality, duplicateKey, maxJoinRows)
                .Facts.Select(fact => fact.Value!.Value).OrderBy(value => value).ToArray();

        Assert.Equal(new[] { 10m }, Values("Inner", "First"));
        Assert.Equal(new[] { 10m, 20m }, Values("Left", "First"));
        Assert.Equal(new[] { 0m, 10m }, Values("Right", "First"));
        Assert.Equal(new[] { 0m, 10m, 20m }, Values("Full", "First"));
        Assert.Equal(new[] { 20m }, Values("Inner", "All"));

        var duplicateFailure = RunJoinToGroupedFacts(leftRows, rightRows, "Inner", "Fail");
        Assert.Contains(duplicateFailure.Diagnostics, diagnostic => diagnostic.Code == "MAP017");
        Assert.Empty(duplicateFailure.Facts);

        var expansionFailure = RunJoinToGroupedFacts(leftRows, rightRows, "Inner", "All", maxJoinRows: 1);
        Assert.Contains(expansionFailure.Diagnostics, diagnostic => diagnostic.Code == "MAP015");
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

    private static ExecutionResult RunJoinToGroupedFacts(
        IReadOnlyList<DataRecord> leftRows,
        IReadOnlyList<DataRecord> rightRows,
        string cardinality,
        string duplicateKey,
        int maxJoinRows = 200_000)
    {
        const string leftBinding = "join-left";
        const string rightBinding = "join-right";
        var left = GraphHarness.Node(NodeTypeConstants.CollectRows, "Left",
            new() { ["binding"] = leftBinding },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var right = GraphHarness.Node(NodeTypeConstants.CollectRows, "Right",
            new() { ["binding"] = rightBinding },
            outputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) });
        var join = GraphHarness.Node(NodeTypeConstants.Join, "Join",
            new() { ["leftKey"] = "k", ["rightKey"] = "k", ["cardinality"] = cardinality,
                ["duplicateKey"] = duplicateKey },
            inputs: new[]
            {
                GraphHarness.P("left", PortShape.Record, PortCardinality.Collection),
                GraphHarness.P("right", PortShape.Record, PortCardinality.Collection)
            },
            outputs: new[] { GraphHarness.P("rows", PortShape.Record, PortCardinality.Collection) });
        var group = GraphHarness.Node(NodeTypeConstants.GroupBy, "By key",
            new() { ["keys"] = "k", ["sumField"] = "a" },
            inputs: new[] { GraphHarness.P("items", PortShape.Record, PortCardinality.Collection) },
            outputs: new[]
            {
                GraphHarness.P("values", PortShape.Decimal, PortCardinality.Collection),
                GraphHarness.P("keys", PortShape.String, PortCardinality.Collection)
            });
        var edges = new List<MappingEdge>
        {
            GraphHarness.Link(left, "items", join, "left"),
            GraphHarness.Link(right, "items", join, "right"),
            GraphHarness.Link(join, "rows", group, "items")
        };
        var sink = GraphHarness.FactSink(out var sinkEdges, group, "values", PortCardinality.Collection);
        edges.AddRange(sinkEdges);
        var project = GraphHarness.Project(new[] { left, right, join, group }.Concat(sink), edges,
            bindings: new[] { GraphHarness.Binding(leftBinding), GraphHarness.Binding(rightBinding) });
        var context = GraphHarness.Ctx(new[]
        {
            (leftBinding, leftRows),
            (rightBinding, rightRows)
        }, maxJoinRows: maxJoinRows);

        var result = GraphHarness.Run(project, context, out var compile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
        return Assert.IsType<ExecutionResult>(result);
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
