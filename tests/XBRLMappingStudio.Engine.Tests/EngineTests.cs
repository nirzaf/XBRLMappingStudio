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

    private static FilingProfile AddNumericCompoundUnitConcept(FilingProfile source)
        => new()
        {
            ProfileId = source.ProfileId,
            Digest = source.Digest,
            Concepts = source.Concepts.Append(new ConceptMetadata(
                "synthetic:UnitsPerShare", SyntheticProfile.Namespace, "UnitsPerShare",
                "Units per share", "decimal", "duration", IsAbstract: false)).ToArray(),
            SupportedUnits = source.SupportedUnits,
            TaxonomyVersion = source.TaxonomyVersion,
            DimensionDomains = source.DimensionDomains,
            PresentationLinks = source.PresentationLinks,
            CalculationLinks = source.CalculationLinks,
            DefinitionLinks = source.DefinitionLinks,
            OutputFormat = source.OutputFormat,
            NamespaceUris = source.NamespaceUris,
            TaxonomyUrls = source.TaxonomyUrls,
            PackageHashes = source.PackageHashes,
            RequiredPlugins = source.RequiredPlugins,
            RequiredChecks = source.RequiredChecks
        };

    private static MappingProject ConfigureTextFactProject(MappingProject project, string language)
    {
        var sourceNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField);
        var scaleNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        sourceNode.Parameters["column"] = "Line";
        sourceNode.OutputPorts.Add(new PortDefinition("textValue", PortShape.String, PortCardinality.Single, false));
        project.Nodes.Remove(scaleNode);
        project.Edges.RemoveAll(edge => edge.SourceNodeId == scaleNode.NodeId || edge.TargetNodeId == scaleNode.NodeId
            || (edge.TargetNodeId == factNode.NodeId && edge.TargetPortName == "value"));
        factNode.Parameters["concept"] = SyntheticProfile.Qualified("EntityRegistrantName");
        factNode.Parameters.Remove("unit");
        factNode.Parameters.Remove("decimals");
        factNode.Parameters["language"] = language;
        var numericPortIndex = factNode.InputPorts.FindIndex(port => port.Name == "value");
        factNode.InputPorts[numericPortIndex] = new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, false);
        factNode.InputPorts.Add(new PortDefinition("textValue", PortShape.String, PortCardinality.Single, false));
        project.Edges.Add(new MappingEdge
        {
            EdgeId = Id.New(),
            SourceNodeId = sourceNode.NodeId,
            SourcePortName = "textValue",
            TargetNodeId = factNode.NodeId,
            TargetPortName = "textValue"
        });
        return project;
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
    public async Task FactConstructCarriesOnlyProfileDeclaredDimensions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-dimensions-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, synthetic) = Prepare(path);
            var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            factNode.Parameters["dimensions"] = "ex:RegionAxis=ex:Domestic";
            var profile = new FilingProfile
            {
                ProfileId = synthetic.ProfileId,
                Digest = synthetic.Digest,
                Concepts = synthetic.Concepts,
                SupportedUnits = synthetic.SupportedUnits,
                DimensionDomains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["ex:RegionAxis"] = new[] { "ex:Domestic" }
                },
                TaxonomyVersion = synthetic.TaxonomyVersion,
                OutputFormat = synthetic.OutputFormat,
                NamespaceUris = synthetic.NamespaceUris,
                TaxonomyUrls = synthetic.TaxonomyUrls,
                PackageHashes = synthetic.PackageHashes,
                RequiredChecks = synthetic.RequiredChecks
            };
            var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));

            var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

            Assert.NotEmpty(result.Facts);
            Assert.Equal("ex:RegionAxis", Assert.Single(result.Facts[0]!.Dimensions).Name);
            Assert.Equal("ex:Domestic", result.Facts[0]!.Dimensions[0].MemberName);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FactConstructSupportsLanguageTaggedTextWithSourceProvenance()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-text-fact-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            ConfigureTextFactProject(project, "fr");
            var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));

            var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

            var fact = Assert.Single(result.Facts);
            Assert.Null(fact.Value);
            Assert.Equal("Revenue", fact.TextValue);
            Assert.Equal("fr", fact.Language);
            Assert.Null(fact.Unit);
            Assert.Null(fact.Decimals);
            Assert.NotEmpty(fact.Sources);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FactConstructCarriesProfileValidatedCompoundUnit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-compound-unit-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, sourceProfile) = Prepare(path);
            var profile = AddNumericCompoundUnitConcept(sourceProfile);
            var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            factNode.Parameters["concept"] = "synthetic:UnitsPerShare";
            factNode.Parameters["unit"] = "USD/shares";

            var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));

            var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

            var fact = Assert.Single(result.Facts);
            Assert.Null(fact.Unit);
            Assert.Equal("USD/shares", fact.CompoundUnit?.CanonicalKey);
            Assert.NotEmpty(fact.Sources);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FactConstructRejectsUnsupportedTextLanguageWithSource()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-text-language-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            ConfigureTextFactProject(project, "de");
            var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));

            var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

            Assert.Empty(result.Facts);
            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == Diagnostics.DiagnosticCodes.XbrUnsupportedConstruct);
            Assert.Equal("textValue", diagnostic.PortName);
            Assert.NotNull(diagnostic.Source);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FactConstructRejectsDuplicateMalformedAndOverLimitDimensionsBeforeSerialization()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-invalid-dimensions-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, synthetic) = Prepare(path);
            var profile = new FilingProfile
            {
                ProfileId = synthetic.ProfileId,
                Digest = synthetic.Digest,
                Concepts = synthetic.Concepts,
                SupportedUnits = synthetic.SupportedUnits,
                DimensionDomains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["synthetic:RegionAxis"] = new[] { "synthetic:Domestic", "synthetic:International" }
                },
                TaxonomyVersion = synthetic.TaxonomyVersion,
                OutputFormat = synthetic.OutputFormat,
                NamespaceUris = synthetic.NamespaceUris,
                TaxonomyUrls = synthetic.TaxonomyUrls,
                PackageHashes = synthetic.PackageHashes,
                RequiredPlugins = synthetic.RequiredPlugins,
                RequiredChecks = synthetic.RequiredChecks
            };
            var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var invalidDimensions = new[]
            {
                "synthetic:RegionAxis=synthetic:Domestic\nsynthetic:RegionAxis=synthetic:International",
                "synthetic:RegionAxis=",
                string.Join("\n", Enumerable.Repeat("synthetic:RegionAxis=synthetic:Domestic", FactDimensionParser.MaximumDimensions + 1))
            };

            foreach (var dimensions in invalidDimensions)
            {
                factNode.Parameters["dimensions"] = dimensions;
                var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
                Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));
                var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

                Assert.Empty(result.Facts);
                var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == Diagnostics.DiagnosticCodes.XbrDimensionInvalid);
                Assert.Equal(factNode.NodeId, diagnostic.NodeId);
                Assert.NotNull(diagnostic.Source);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-integer")]
    [InlineData("31")]
    public async Task FactConstructRejectsPresentInvalidDecimalsAndLinksSource(string decimals)
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-decimals-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            factNode.Parameters["decimals"] = decimals;
            var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));

            var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

            Assert.Empty(result.Facts);
            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == Diagnostics.DiagnosticCodes.XbrDecimalsOutOfRange);
            Assert.Equal(factNode.NodeId, diagnostic.NodeId);
            Assert.Equal("value", diagnostic.PortName);
            Assert.NotNull(diagnostic.Source);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FactConstructAcceptsAbsentAndExplicitInfDecimalsAsInfiniteAccuracy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-decimals-inf-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var compiler = new PlanCompiler(FunctionCatalog.Default);

            factNode.Parameters["decimals"] = "INF";
            var explicitInf = compiler.Compile(new MappingGraph(project), profile);
            Assert.True(explicitInf.Success, string.Join(" | ", explicitInf.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));
            var explicitResult = await explicitInf.Plan!.ExecuteAsync(MakeContext(project, path));
            Assert.NotEmpty(explicitResult.Facts);
            Assert.All(explicitResult.Facts, fact => Assert.Null(fact.Decimals));

            factNode.Parameters.Remove("decimals");
            var absent = compiler.Compile(new MappingGraph(project), profile);
            Assert.True(absent.Success, string.Join(" | ", absent.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));
            var absentResult = await absent.Plan!.ExecuteAsync(MakeContext(project, path));
            Assert.NotEmpty(absentResult.Facts);
            Assert.All(absentResult.Facts, fact => Assert.Null(fact.Decimals));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FactConstructPeriodTypeRejectionIncludesSourceAndSpecificDiagnostic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-period-aspect-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            factNode.Parameters["concept"] = SyntheticProfile.Qualified("Assets");
            factNode.Parameters["unit"] = "USD";
            factNode.Parameters["periodType"] = "instant";
            var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));

            var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

            Assert.Empty(result.Facts);
            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == Diagnostics.DiagnosticCodes.XbrPeriodTypeMismatch);
            Assert.Equal(factNode.NodeId, diagnostic.NodeId);
            Assert.NotNull(diagnostic.Source);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("absent", "instantDate")]
    [InlineData("malformed", "period")]
    public async Task FactConstructRejectsAbsentOrMalformedPeriodWithoutPlaceholder(string periodIssue, string expectedPort)
    {
        var dir = Path.Combine(Path.GetTempPath(), "xbrl-period-invalid-" + Guid.NewGuid().ToString("N"));
        var path = TestFixtures.MaterializeWorkbook(dir);
        try
        {
            var (project, profile) = Prepare(path);
            var factNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            if (periodIssue == "absent")
            {
                project.Edges.RemoveAll(edge => edge.TargetNodeId == factNode.NodeId && edge.TargetPortName == "period");
            }
            else
            {
                var endDate = project.Nodes.Single(node => node.DisplayName == "Period end (2026-01-01)");
                endDate.Parameters["literal"] = "2025-13-40";
            }

            var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(diagnostic => diagnostic.Message)));
            var result = await compile.Plan!.ExecuteAsync(MakeContext(project, path));

            Assert.Empty(result.Facts);
            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == Diagnostics.DiagnosticCodes.XbrPeriodInvalid);
            Assert.Equal(factNode.NodeId, diagnostic.NodeId);
            Assert.Equal(expectedPort, diagnostic.PortName);
            Assert.NotNull(diagnostic.Source);
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

    [Fact]
    public async Task Cancellation_IsReportedForAnEmptyInput()
    {
        var (project, profile) = Prepare();
        var compile = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), profile);
        Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = GraphHarness.Ctx(
            Array.Empty<(string Binding, IReadOnlyList<DataRecord> Rows)>(),
            cancellationToken: cancellation.Token);

        var result = await compile.Plan!.ExecuteAsync(context);

        Assert.Equal(Diagnostics.RunOutcome.Cancelled, result.Outcome);
        Assert.Empty(result.Facts);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == Diagnostics.DiagnosticCodes.MapExecutionCancelled);
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
