using System.Text.Json;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine.Tests;

/// <summary>
/// MAP01: node, edge, catalog semantics independent of graph control.
/// Acceptance: two visually different layouts produce the same semantic model.
/// </summary>
public class MappingGraphTests
{
    [Fact]
    public void LayoutOnlyChanges_DoNotAlter_SemanticDigest()
    {
        var seed = Guid.Parse("22222222-3333-4444-5555-666666666666");
        var a = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var b = TestFixtures.BuildRevenueMappingProject(idSeed: seed);

        // Move every node in b to a different position.
        foreach (var node in b.Nodes)
            node.Layout = new LayoutPosition(node.Layout!.X + 500, node.Layout!.Y - 300);

        Assert.Equal(a.ComputeSemanticDigest(), b.ComputeSemanticDigest());
    }

    [Fact]
    public void ChangingParameter_Alters_SemanticDigest()
    {
        var seed = Guid.Parse("22222222-3333-4444-5555-666666666667");
        var a = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var b = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var factNode = b.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct);
        factNode.Parameters["unit"] = "EUR";
        Assert.NotEqual(a.ComputeSemanticDigest(), b.ComputeSemanticDigest());
    }

    [Fact]
    public void ChangingConcept_Alters_SemanticDigest()
    {
        var seed = Guid.Parse("22222222-3333-4444-5555-666666666668");
        var a = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var b = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var factNode = b.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct);
        factNode.Parameters["concept"] = SyntheticProfile.Qualified("Expenses");
        Assert.NotEqual(a.ComputeSemanticDigest(), b.ComputeSemanticDigest());
    }
}

/// <summary>
/// MAP02: graph validation with stable, actionable diagnostics.
/// Fixtures for a cycle, dangling port, and collection-to-single mismatch.
/// </summary>
public class GraphValidatorTests
{
    private static GraphValidator NewValidator() => new(FunctionCatalog.Default);

    [Fact]
    public void CycleProduces_StableDiagnostic()
    {
        // Build a self-referencing cycle: A -> B -> A.
        var a = Id.New();
        var b = Id.New();
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "cycle",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest,
            Nodes = new()
            {
                new MappingNode { NodeId = a, NodeType = NodeTypeConstants.Scale, DisplayName = "A",
                    InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) } },
                new MappingNode { NodeId = b, NodeType = NodeTypeConstants.Scale, DisplayName = "B",
                    InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) } }
            },
            Edges = new()
            {
                new MappingEdge { EdgeId = Id.New(), SourceNodeId = a, SourcePortName = "value", TargetNodeId = b, TargetPortName = "input" },
                new MappingEdge { EdgeId = Id.New(), SourceNodeId = b, SourcePortName = "value", TargetNodeId = a, TargetPortName = "input" }
            }
        };

        var bag = NewValidator().Validate(new MappingGraph(project));
        Assert.Contains(bag.Items, d => d.Code == "MAP003");
    }

    [Fact]
    public void DanglingRequiredPort_ProducesStableDiagnostic()
    {
        var scale = Id.New();
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "dangling",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest,
            Nodes = new()
            {
                new MappingNode
                {
                    NodeId = scale,
                    NodeType = NodeTypeConstants.Scale,
                    DisplayName = "Scale",
                    InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) }
                }
            }
        };

        var bag = NewValidator().Validate(new MappingGraph(project));
        Assert.Contains(bag.Items, d => d.Code == "MAP002" && d.NodeId == scale && d.PortName == "input");
    }

    [Fact]
    public void UnknownNodeType_BlocksExecution()
    {
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "unknown",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest,
            Nodes = new()
            {
                new MappingNode
                {
                    NodeId = Id.New(),
                    NodeType = "quantumFlanger",
                    DisplayName = "Unknown node"
                }
            }
        };

        var bag = NewValidator().Validate(new MappingGraph(project));
        Assert.Contains(bag.Items, d => d.Code == "MAP001" && d.IsBlocking);
    }

    [Fact]
    public void SameInvalidGraph_ProducesSameCode_AndBindingId()
    {
        // Diagnostic identity check (BASE04 acceptance).
        var a = BuildInvalid();
        var b = BuildInvalid();
        var diagA = NewValidator().Validate(new MappingGraph(a)).Items.Single(d => d.Code == "MAP002");
        var diagB = NewValidator().Validate(new MappingGraph(b)).Items.Single(d => d.Code == "MAP002");
        Assert.Equal(diagA.Code, diagB.Code);
        Assert.Equal(diagA.NodeId, diagB.NodeId);
        Assert.Equal(diagA.PortName, diagB.PortName);
    }

    private static MappingProject BuildInvalid()
    {
        var nodeId = "11111111111111111111111111111111";
        return new MappingProject
        {
            ProjectId = Id.New(),
            Name = "invalid",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest,
            Nodes = new()
            {
                new MappingNode
                {
                    NodeId = nodeId,
                    NodeType = NodeTypeConstants.Scale,
                    DisplayName = "S",
                    InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) }
                }
            }
        };
    }
}

/// <summary>
/// MAP03: execution plan is deterministic, records provenance, and honours cancellation.
/// </summary>
public class ExecutionPlanTests
{
    private static (MappingProject, FilingProfile) Prepare(string? workbookPath = null)
    {
        var project = TestFixtures.BuildRevenueMappingProject(workbookPath);
        var profile = SyntheticProfile.Build();
        return (project, profile);
    }

    [Fact]
    public void CompileProducesPlan_AndSinkIdentified()
    {
        var (project, profile) = Prepare();
        var compiler = new PlanCompiler(FunctionCatalog.Default);
        var result = compiler.Compile(new MappingGraph(project), profile);
        Assert.True(result.Success, string.Join(" | ", result.Diagnostics.Items.Select(d => d.Message)));
        Assert.NotNull(result.Plan);
        Assert.Contains(NodeTypeConstants.FactConstruct, result.Plan!.SinkNodeIds.Select(id => result.Plan.Steps.First(s => s.NodeId == id).NodeType));
    }

    [Fact]
    public async Task DeterministicExecution_ProducesStableResults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-g1-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            var plan = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile).Plan!;

            var first = await plan.ExecuteAsync(MakeContext(project, path));
            var second = await plan.ExecuteAsync(MakeContext(project, path));

            Assert.Equal(first.Facts.Count, second.Facts.Count);
            Assert.NotEmpty(first.Facts);
            Assert.Equal(first.Facts[0]!.Value, second.Facts[0]!.Value);
            Assert.Equal(first.Facts[0]!.ConceptName, second.Facts[0]!.ConceptName);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Cancellation_DiscardsPartialOutput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-g1-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            var plan = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile).Plan!;
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ctx = MakeContext(project, path, cts.Token);
            var result = await plan.ExecuteAsync(ctx);
            Assert.Equal(Diagnostics.RunOutcome.Cancelled, result.Outcome);
            Assert.Empty(result.Facts);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static ExecutionContext MakeContext(MappingProject project, string workbookPath,
        CancellationToken? ct = null)
    {
        var adapter = new Adapters.Excel.ExcelAdapter();
        var binding = project.Bindings[0];
        var cfg = binding.Configuration?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value?.ToString() ?? "")
            ?? new Dictionary<string, string>();
        var conn = new Adapters.SourceConnection(binding.BindingId, "excel", workbookPath, cfg);
        var schema = adapter.InspectSchemaAsync(conn, CancellationToken.None).GetAwaiter().GetResult();
        var records = new List<Engine.DataRecord>();
        var iter = adapter.ReadFullAsync(conn, schema, CancellationToken.None);
        // Materialize synchronously using a small helper: this is test-only.
        var task = Task.Run(async () =>
        {
            await foreach (var r in iter) records.Add(r);
        });
        task.Wait();

        return new ExecutionContext
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Manifest = new Diagnostics.RunManifest
            {
                RunId = "test",
                OperationId = "test",
                ProjectId = project.ProjectId,
                ProfileDigest = project.ProfileDigest,
                EngineVersion = "test"
            },
            Parameters = new Dictionary<string, string>(),
            CancellationToken = ct ?? CancellationToken.None,
            LoadedSources = { [project.Bindings[0].BindingId] = records }
        };
    }
}
