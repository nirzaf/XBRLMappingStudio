using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed partial class LargeRunExecutionTests
{
    public static IEnumerable<object[]> JoinParityCases()
    {
        foreach (var kind in new[] { "sqlite", "csv" })
        foreach (var cardinality in new[] { "Inner", "Left", "Right", "Full" })
        foreach (var duplicate in new[] { "First", "All" })
        foreach (var conflict in new[] { "Left", "Right" })
            yield return new object[] { kind, cardinality, duplicate, conflict };
        yield return new object[] { "csv-left", "Full", "All", "Right" };
        yield return new object[] { "csv-right", "Full", "All", "Right" };
    }

    [Theory]
    [MemberData(nameof(JoinParityCases))]
    public async Task PartitionedJoin_ReportsFactsDigestsAndPathsMatchSmallExecution(
        string kind, string cardinality, string duplicate, string conflict)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root, kind);
            var project = fixture.Project;
            var join = project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Join);
            join.Parameters["cardinality"] = cardinality;
            join.Parameters["duplicateKey"] = duplicate;
            join.Parameters["fieldConflict"] = conflict;
            using var small = await RunJoin(fixture, new(InMemorySourceRecordLimit: 100));
            var stages = new List<string>();
            using var large = await RunJoin(fixture, new(InMemorySourceRecordLimit: 1, RowBatchSize: 2),
                progress: new SynchronousGroupProgress(p => stages.Add(p.Stage)));
            Assert.True(small.IsPublishable, Describe(small));
            Assert.True(large.IsPublishable, Describe(large));
            var artifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
            Assert.Equal(small.XbrlJson, await File.ReadAllTextAsync(artifact.ReportPath));
            Assert.Equal(JsonSerializer.Serialize(small.Facts), JsonSerializer.Serialize(artifact.InInputOrder().ToArray()));
            Assert.Equal(2, large.Manifest.Inputs.Count);
            Assert.Equal(small.Manifest.Inputs.Select(i => i.Digest), large.Manifest.Inputs.Select(i => i.Digest));
            Assert.Contains("Partitioning source joins on disk", stages);
            Assert.Contains("Executing bounded disk-partitioned join group", stages);
            var facts = artifact.InInputOrder().ToArray();
            Assert.Equal(cardinality switch
            {
                "Inner" => new[] { "B", "A" }, "Left" => new[] { "B", "LeftOnly", "A" },
                "Right" => new[] { "B", "A", "RightOnly" }, _ => new[] { "B", "LeftOnly", "A", "RightOnly" }
            }, facts.Select(f => f.EntityIdentifier));
            Assert.All(facts, fact => Assert.All(fact.SourceTransformationPaths, path =>
            {
                Assert.Equal(4, path.TransformationIds.Count);
                Assert.Equal(join.NodeId, path.TransformationIds[1]);
                Assert.Equal(project.Nodes.Single(n => n.NodeType == NodeTypeConstants.GroupBy).NodeId, path.TransformationIds[2]);
            }));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(0, "Inner")]
    [InlineData(0, "Left")]
    [InlineData(0, "Right")]
    [InlineData(0, "Full")]
    [InlineData(1, "Inner")]
    [InlineData(1, "Left")]
    [InlineData(1, "Right")]
    [InlineData(1, "Full")]
    public async Task PartitionedJoin_EmptySourcePreservesOutcomeFactsAndBothDigests(int emptySide, string cardinality)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            fixture.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Join).Parameters["cardinality"] = cardinality;
            await using (var db = new SqliteConnection($"Data Source={fixture.Project.Bindings[emptySide].Locator};Pooling=False"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = emptySide == 0 ? "DELETE FROM financials;" : "DELETE FROM rightrows;";
                await command.ExecuteNonQueryAsync();
            }
            var smallWorker = new DigestOnlyArtifactWorker(SyntheticProfile.Build());
            var largeWorker = new DigestOnlyArtifactWorker(SyntheticProfile.Build());
            using var small = await RunJoin(fixture, new(InMemorySourceRecordLimit: 100), smallWorker);
            using var large = await RunJoin(fixture, new(InMemorySourceRecordLimit: 1, RowBatchSize: 1), largeWorker);
            Assert.Equal(small.IsPublishable, large.IsPublishable);
            Assert.Equal(small.Manifest.Outcome, large.Manifest.Outcome);
            if (small.IsPublishable)
            {
                var artifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
                Assert.Equal(small.XbrlJson, await File.ReadAllTextAsync(artifact.ReportPath));
                Assert.Equal(JsonSerializer.Serialize(small.Facts), JsonSerializer.Serialize(artifact.InInputOrder().ToArray()));
                Assert.Equal(2, small.Manifest.Inputs.Count);
                Assert.Equal(2, large.Manifest.Inputs.Count);
                Assert.Equal(small.Manifest.Inputs.Select(i => i.Digest), large.Manifest.Inputs.Select(i => i.Digest));
                Assert.NotEqual(large.Manifest.Inputs[0].Digest, large.Manifest.Inputs[1].Digest);
                foreach (var binding in fixture.Project.Bindings)
                    Assert.Equal(small.Manifest.Inputs.Single(i => i.SourceId == binding.BindingId).Digest,
                        large.Manifest.Inputs.Single(i => i.SourceId == binding.BindingId).Digest);
                var emptyTable = emptySide == 0 ? "financials" : "rightrows";
                Assert.Equal(RunManifest.ComputeSha256($"{emptyTable.Length}:{emptyTable}0:"),
                    large.Manifest.Inputs.Single(i => i.SourceId == fixture.Project.Bindings[emptySide].BindingId).Digest);
            }
            else
            {
                Assert.Null(small.LargeArtifact);
                Assert.Null(large.LargeArtifact);
                Assert.Equal(0, smallWorker.ValidationRequests);
                Assert.Equal(0, largeWorker.ValidationRequests);
            }
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("duplicateKey", "Fail", DiagnosticCodes.MapDuplicateJoinKey)]
    [InlineData("manyToMany", "Fail", DiagnosticCodes.MapManyToManyJoinBlocked)]
    [InlineData("fieldConflict", "Fail", DiagnosticCodes.MapJoinFieldConflict)]
    public async Task PartitionedJoin_PolicyFailuresDiscardOutput(string parameter, string value, string code)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            fixture.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Join).Parameters[parameter] = value;
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            var before = LargeRunDirectories();
            using var small = await RunJoin(fixture, new(InMemorySourceRecordLimit: 100));
            using var large = await RunJoin(fixture, new(InMemorySourceRecordLimit: 1), worker);
            Assert.Contains(small.Manifest.Diagnostics, d => d.Code == code && d.IsBlocking);
            Assert.Contains(large.Manifest.Diagnostics, d => d.Code == code && d.IsBlocking);
            Assert.False(large.IsPublishable);
            Assert.Empty(large.Facts);
            Assert.Null(large.LargeArtifact);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Equal(before, LargeRunDirectories());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("expansion")]
    [InlineData("groups")]
    [InlineData("collection")]
    [InlineData("working")]
    [InlineData("batch")]
    [InlineData("spool")]
    public async Task PartitionedJoin_GlobalAndPerKeyBudgetsBlockPublication(string limit)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            fixture.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Join).Parameters["cardinality"] = "Full";
            if (limit == "batch")
            {
                await using var db = new SqliteConnection($"Data Source={fixture.Project.Bindings[0].Locator};Pooling=False");
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                // Every row is small, but the complete B partition must exceed
                // the aggregate 4096-byte budget independently of GUID lengths.
                command.CommandText = "WITH RECURSIVE ids(i) AS (SELECT 5 UNION ALL SELECT i+1 FROM ids WHERE i<16) INSERT INTO financials SELECT i,'B','10','0' FROM ids;";
                await command.ExecuteNonQueryAsync();
            }
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 1);
            budget = limit switch
            {
                "expansion" => budget with { MaxJoinExpansionRows = 4 }, // B alone expands to four, later A crosses the global limit.
                "groups" => budget with { MaxGroupCount = 2 },
                "collection" => budget with { MaxCollectionItems = 2 },
                "working" => budget with { MaxGroupWorkingBytes = 1 },
                "batch" => budget with { MaxRowBytes = 4096, MaxBatchBytes = 4096 },
                _ => budget with { MaxSpoolBytes = 1 }
            };
            var worker = new DigestOnlyArtifactWorker(SyntheticProfile.Build());
            var before = LargeRunDirectories();
            using var result = await RunJoin(fixture, budget, worker);
            Assert.False(result.IsPublishable, Describe(result));
            Assert.Null(result.LargeArtifact);
            Assert.Empty(result.Facts);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Contains(result.Manifest.Diagnostics, d => d.IsBlocking && d.Code == (limit switch
            {
                "expansion" => DiagnosticCodes.MapJoinExpansionExceeded,
                "spool" => DiagnosticCodes.IoNumberOutOfBudget,
                _ => DiagnosticCodes.MapCollectionBudgetExceeded
            }));
            Assert.Equal(before, LargeRunDirectories());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("Partitioning source joins on disk")]
    [InlineData("Executing bounded disk-partitioned join group")]
    public async Task PartitionedJoin_CancellationCleansSpoolAndRestarts(string stage)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            using var cancellation = new CancellationTokenSource();
            var before = LargeRunDirectories();
            var worker = new DigestOnlyArtifactWorker(SyntheticProfile.Build());
            using var result = await RunJoin(fixture, new(InMemorySourceRecordLimit: 1, RowBatchSize: 1), worker,
                new SynchronousGroupProgress(p => { if (p.Stage == stage) cancellation.Cancel(); }), cancellation.Token);
            Assert.Equal(RunOutcome.Cancelled, result.Manifest.Outcome);
            Assert.Null(result.LargeArtifact);
            Assert.Empty(result.Facts);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Equal(before, LargeRunDirectories());
            using var restart = await RunJoin(fixture, new(InMemorySourceRecordLimit: 1));
            Assert.True(restart.IsPublishable, Describe(restart));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("different-key")]
    [InlineData("different-binding")]
    [InlineData("row-node")]
    public async Task PartitionedJoin_UnsupportedShapeRefusesLargeExecution(string shape)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            var project = fixture.Project;
            if (shape == "different-key") project.Nodes.Single(n => n.NodeType == NodeTypeConstants.GroupBy).Parameters["keys"] = "Current";
            else if (shape == "different-binding") project.Nodes.Where(n => n.NodeType == NodeTypeConstants.CollectRows).Last().Parameters["binding"] = project.Bindings[0].BindingId;
            else project.Nodes.Add(new MappingNode { NodeId = Id.New(), NodeType = NodeTypeConstants.ExcelField, DisplayName = "Row dependent",
                Parameters = new() { ["bindingId"] = project.Bindings[0].BindingId, ["column"] = "Current" },
                OutputPorts = new() { new("value", PortShape.Decimal, PortCardinality.Single) } });
            using var result = await RunJoin(fixture, new(InMemorySourceRecordLimit: 1));
            Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics, d => d.Code == DiagnosticCodes.IoUnsupportedOperation);
            Assert.Null(result.LargeArtifact);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PartitionedJoin_PublicationRechecksEachIndependentSource(int side)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root, "csv");
            var profile = SyntheticProfile.Build();
            var service = new MappingJobService(profile, new DigestOnlyArtifactWorker(profile),
                executionBudget: new(InMemorySourceRecordLimit: 1));
            using var result = await service.RunAsync(fixture.Project, fixture.Connections, new Dictionary<string, string>(), false, default);
            Assert.True(result.IsPublishable, Describe(result));
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            await File.AppendAllTextAsync(fixture.Project.Bindings[side].Locator!, "New,99\n");
            var error = await Assert.ThrowsAsync<PublicationException>(() => service.ValidateStagedArtifactAsync(
                artifact.ReportPath, Path.Combine(root, "published.json"), result, default));
            Assert.Equal(DiagnosticCodes.PubDigestMismatch, error.Code);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartitionedJoin_PublicationRequiresExactStagedValidation(bool reject)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            var project = fixture.Project;
            var profile = SyntheticProfile.Build();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "partitioned join test", ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest, Authority = "test-fixture"
            });
            var worker = new DigestOnlyArtifactWorker(profile);
            var service = new MappingJobService(profile, worker, executionBudget: new(InMemorySourceRecordLimit: 1));
            using var result = await service.RunAsync(project, fixture.Connections, new Dictionary<string, string>(), false, default);
            Assert.True(result.IsPublishable, Describe(result));
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            var destination = Path.Combine(root, "published.json");
            await File.WriteAllTextAsync(destination, "prior accepted report");
            if (reject) worker.ValidationStatus = XBRLMappingStudio.Worker.WorkerStatus.DomainFailure;
            var publication = new PublicationService(new RunJournal(root));
            var publish = publication.PublishAuthorizedLargeArtifactAsync(destination, artifact, root,
                project, profile, true, result,
                (path, token) => service.ValidateStagedArtifactAsync(path, destination, result, token), default);
            if (reject)
            {
                await Assert.ThrowsAsync<PublicationException>(() => publish);
                Assert.Equal("prior accepted report", await File.ReadAllTextAsync(destination));
            }
            else
            {
                var receipt = await publish;
                Assert.Equal(artifact.Sha256, receipt.ValidatedDigest);
                Assert.Equal(await File.ReadAllTextAsync(artifact.ReportPath), await File.ReadAllTextAsync(destination));
            }
            Assert.Equal(2, worker.ValidationRequests);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task PartitionedJoin_XmlFactsMatchAndOnlyValidatedArtifactIsPublished()
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            var project = fixture.Project;
            var profile = SyntheticProfile.Build();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "partitioned join XML test", ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest, Authority = "test-fixture"
            });
            using var small = await RunJoin(fixture, new(InMemorySourceRecordLimit: 100));
            await using var worker = new XBRLMappingStudio.Worker.LocalSyntheticWorker(profile);
            var service = new MappingJobService(profile, worker,
                executionBudget: new(InMemorySourceRecordLimit: 1, RowBatchSize: 1));
            using var large = await service.RunAsync(project, fixture.Connections,
                new Dictionary<string, string>(), preview: false, CancellationToken.None,
                outputKind: "xbrl-xml");
            Assert.True(small.IsPublishable, Describe(small));
            Assert.True(large.IsPublishable, Describe(large));
            var artifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
            Assert.Equal("xbrl-xml", artifact.OutputKind);
            Assert.EndsWith(".xml", artifact.ReportPath, StringComparison.OrdinalIgnoreCase);
            var identity = Assert.Single(large.Manifest.Outputs);
            Assert.Equal("xbrl-xml", identity.Kind);
            Assert.Equal(artifact.Sha256, identity.Digest);
            var roundTripped = XmlSerializer.ReadFacts(await File.ReadAllTextAsync(artifact.ReportPath), profile);
            Assert.Equal(small.Facts.Select(f => f.SemanticKey()).Order(StringComparer.Ordinal),
                roundTripped.Select(f => f.SemanticKey()).Order(StringComparer.Ordinal));
            Assert.Equal(small.Manifest.Inputs.Select(i => i.Digest), large.Manifest.Inputs.Select(i => i.Digest));
            var destination = Path.Combine(root, "published-report.xml");
            var publisher = new PublicationService(new RunJournal(root));
            var receipt = await publisher.PublishAuthorizedLargeArtifactAsync(
                destination, artifact, root, project, profile, allowSyntheticProfile: true, result: large,
                validateStagedArtifactAsync: (path, token) => service.ValidateStagedArtifactAsync(
                    path, destination, large, token, "xbrl-xml"), outputKind: "xbrl-xml");
            Assert.Equal(artifact.Sha256, receipt.FinalDigest);
            Assert.Equal(await File.ReadAllBytesAsync(artifact.ReportPath), await File.ReadAllBytesAsync(destination));
            Assert.Equal(destination, Assert.Single(large.Manifest.Outputs).Path);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    [Trait("Category", "ProcessMemory")]
    public async Task JoinHundredThousandRows_PartitionsWithBoundedMemoryAndExactTotals()
    {
        var root = NewDirectory();
        try
        {
            const int perSide = 50_000;
            const int groups = 10_000;
            var fixture = await CreateJoinFixture(root, "csv", perSide, groups);
            var samples = new List<long>();
            using var result = await RunJoin(fixture,
                new(InMemorySourceRecordLimit: 128, RowBatchSize: 256, SqliteCacheKiB: 256,
                    MaxJoinExpansionRows: 300_000, MaxSpoolBytes: 512L * 1024 * 1024),
                progress: new SynchronousGroupProgress(p =>
                {
                    if ((p.Stage == "Partitioning source joins on disk" && p.Current % 5120 == 0)
                        || (p.Stage == "Executing bounded disk-partitioned join group" && p.Current % 500 == 0))
                        samples.Add(GC.GetTotalMemory(forceFullCollection: true));
                }));
            Assert.True(result.IsPublishable, Describe(result));
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            Assert.Equal(groups, artifact.FactCount);
            var join = fixture.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Join).NodeId;
            var group = fixture.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.GroupBy).NodeId;
            var sink = fixture.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct).NodeId;
            var collects = fixture.Project.Nodes.Where(n => n.NodeType == NodeTypeConstants.CollectRows).ToArray();
            var index = 0;
            foreach (var fact in artifact.InInputOrder())
            {
                Assert.Equal($"K{index}", fact.EntityIdentifier);
                Assert.Equal(25m * (index % 7 + 1), fact.Value);
                // Five original rows per side. Each right value and both join keys
                // contribute once despite the 25-row Cartesian product.
                Assert.Equal(15, fact.SourceTransformationPaths.Count);
                Assert.Equal(5, fact.SourceTransformationPaths.Count(p => p.Source.SourceId == fixture.Project.Bindings[0].BindingId));
                Assert.Equal(10, fact.SourceTransformationPaths.Count(p => p.Source.SourceId == fixture.Project.Bindings[1].BindingId));
                var expectedSources = Enumerable.Range(0, 5).SelectMany(offset => new[]
                {
                    new SourceReference(fixture.Project.Bindings[0].BindingId, $"row={index + offset * groups + 2},col=1"),
                    new SourceReference(fixture.Project.Bindings[1].BindingId, $"row={index + offset * groups + 2},col=1"),
                    new SourceReference(fixture.Project.Bindings[1].BindingId, $"row={index + offset * groups + 2},col=2")
                }).ToHashSet();
                Assert.True(expectedSources.SetEquals(fact.SourceTransformationPaths.Select(p => p.Source)));
                Assert.All(fact.SourceTransformationPaths, path =>
                {
                    var collect = collects.Single(n => (string)n.Parameters["binding"]! == path.Source.SourceId);
                    Assert.Equal(new[] { collect.NodeId, join, group, sink }, path.TransformationIds);
                });
                index++;
            }
            Assert.Equal(groups, index);
            Assert.True(samples.Count >= 10, $"Only {samples.Count} heap samples were collected.");
            Assert.True(samples.Max() - samples.Min() < 64L * 1024 * 1024,
                $"Join retained managed heap exceeded 64 MiB range: {string.Join(", ", samples)}");
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartitionedJoin_UnusedRetainedNumericFieldsStillValidated(bool projectUnused)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            var left = fixture.Project.Nodes.First(n => n.NodeType == NodeTypeConstants.CollectRows);
            if (!projectUnused) left.Parameters.Remove("fields");
            await using (var db = new SqliteConnection($"Data Source={fixture.Project.Bindings[0].Locator};Pooling=False"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "UPDATE financials SET Prior='12345';";
                await command.ExecuteNonQueryAsync();
            }
            using var small = await RunJoin(fixture, new(InMemorySourceRecordLimit: 100, MaxSignificantDigits: 3));
            using var large = await RunJoin(fixture, new(InMemorySourceRecordLimit: 1, MaxSignificantDigits: 3));
            Assert.Equal(small.IsPublishable, large.IsPublishable);
            Assert.Equal(projectUnused, large.IsPublishable);
            if (!projectUnused) Assert.Contains(large.Manifest.Diagnostics, d => d.Code == DiagnosticCodes.MapNumericOverflow);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("Inner")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Full")]
    public async Task PartitionedJoin_SpoolPreservesMissingNilBlankAndUnmatchedOrdering(string cardinality)
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateJoinFixture(root);
            var project = fixture.Project;
            project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Join).Parameters["cardinality"] = cardinality;
            project.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct).Parameters.Remove("decimals");
            var plan = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), SyntheticProfile.Build()).Plan!;
            var keys = new[] { DataValue.StringValue("Matched"), DataValue.Missing(PortShape.String),
                DataValue.ExplicitNil(PortShape.String), DataValue.StringValue(""), DataValue.StringValue("  "),
                DataValue.StringValue("RightOnly") };
            DataRecord Row(int side, int index, DataValue key, DataValue? amount = null) => new()
            {
                RowId = $"{side}:{index}", HasStableRowIdentity = true,
                Fields = new Dictionary<string, DataValue> { ["Line"] = key, ["Current"] = amount ?? DataValue.DecimalValue(index + 1) },
                CellSources = new Dictionary<string, SourceReference>
                {
                    ["Line"] = new(project.Bindings[side].BindingId, $"{index}.Line"),
                    ["Current"] = new(project.Bindings[side].BindingId, $"{index}.Current")
                }
            };
            var left = keys.Take(5).Select((key, i) => Row(0, i, key)).ToArray();
            // Put invalid keys before valid unmatched keys, and a valid match last:
            // ordinary Join emits valid unmatched rights before invalid rights.
            var right = new[] { 1, 2, 3, 4, 5, 0 }.Select(i => Row(1, i, keys[i])).ToArray();
            async Task<ExecutionResult> Execute(IReadOnlyList<DataRecord> l, IReadOnlyList<DataRecord> r)
            {
                var context = new XBRLMappingStudio.Engine.ExecutionContext
                {
                    OperationId = "join-test", Manifest = new() { RunId = "join-test", OperationId = "join-test",
                        ProjectId = project.ProjectId, ProfileDigest = project.ProfileDigest, EngineVersion = "test" },
                    Parameters = new Dictionary<string, string>()
                };
                context.LoadedSources.Add(project.Bindings[0].BindingId, l);
                context.LoadedSources.Add(project.Bindings[1].BindingId, r);
                return await plan.ExecuteAsync(context);
            }
            // The right-side value wins; test aggregation after joining rather than
            // simply comparing serialization of the key's presence metadata.
            var aggregateCases = new[]
            {
                ("NilValue", DataValue.ExplicitNil(PortShape.Decimal)),
                ("MissingValue", DataValue.Missing(PortShape.Decimal)),
                ("ZeroValue", DataValue.DecimalValue(0)),
                ("MixedValue", DataValue.DecimalValue(5)),
                ("MixedValue", DataValue.ExplicitNil(PortShape.Decimal))
            };
            left = left.Concat(aggregateCases.Select((item, i) => Row(0, 10 + i,
                DataValue.StringValue(item.Item1), DataValue.DecimalValue(99)))).ToArray();
            right = right.Concat(aggregateCases.Select((item, i) => Row(1, 10 + i,
                DataValue.StringValue(item.Item1), item.Item2))).ToArray();
            var expected = await Execute(left, right);
            using var spool = new LargeRunFactStore(Path.Combine(root, "join.db"), 256, 8 * 1024 * 1024);
            foreach (var row in left) spool.AddJoinRecord(row, true, plan.PartitionedJoin!, new(), default);
            foreach (var row in right) spool.AddJoinRecord(row, false, plan.PartitionedJoin!, new(), default);
            spool.PrepareJoinPartitions(plan.PartitionedJoin!, new(), default);
            var actual = new List<PendingFact>();
            foreach (var partition in spool.ReadJoinPartitions(default))
                actual.AddRange((await Execute(partition.Left, partition.Right)).Facts);
            Assert.Equal(JsonSerializer.Serialize(expected.Facts), JsonSerializer.Serialize(actual));
            Assert.Contains(actual, f => f.EntityIdentifier == "Matched" && Equals(f.Value, 1m));
            Assert.Contains(actual, f => f.EntityIdentifier == "NilValue" && f.IsNil);
            Assert.DoesNotContain(actual, f => f.EntityIdentifier == "MissingValue");
            Assert.Contains(actual, f => f.EntityIdentifier == "ZeroValue" && Equals(f.Value, 0m));
            Assert.Contains(actual, f => f.EntityIdentifier == "MixedValue" && Equals(f.Value, 10m));
            if (cardinality is "Right" or "Full")
                Assert.Contains(actual, f => f.EntityIdentifier == "RightOnly" && Equals(f.Value, 6m));
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed record JoinFixture(MappingProject Project, IReadOnlyDictionary<string, SourceConnection> Connections);

    private static async Task<JobResult> RunJoin(JoinFixture fixture, ExecutionBudget budget,
        DigestOnlyArtifactWorker? worker = null, IProgress<MappingJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profile = SyntheticProfile.Build();
        return await new MappingJobService(profile, worker ?? new DigestOnlyArtifactWorker(profile), executionBudget: budget)
            .RunAsync(fixture.Project, fixture.Connections, new Dictionary<string, string>(), false, cancellationToken, progress);
    }

    private static async Task<JoinFixture> CreateJoinFixture(string root, string kind = "sqlite", int rowsPerSide = 0, int groups = 0)
    {
        var database = await CreateSourceDatabase(root, 4, variedValues: true);
        await using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM financials; INSERT INTO financials VALUES (1,'B','10','0'),(2,'LeftOnly','20','0'),(3,'A','30','0'),(4,'B','40','0'); CREATE TABLE rightrows(Id INTEGER PRIMARY KEY, Line TEXT, Current TEXT); INSERT INTO rightrows VALUES(1,'RightOnly','3'),(2,'A','5'),(3,'B','7'),(4,'B','11');";
            await command.ExecuteNonQueryAsync();
        }
        var project = BuildPartitionedGroupProject(database);
        var left = project.Nodes.Single(n => n.NodeType == NodeTypeConstants.CollectRows);
        left.Parameters["fields"] = "Line,Current";
        var second = project.Bindings[0] with { BindingId = Id.New(), DisplayName = "Right source",
            Configuration = new() { ["table"] = "rightrows", ["decimalColumns"] = "Current" } };
        project.Bindings.Add(second);
        var right = new MappingNode { NodeId = Id.New(), NodeType = NodeTypeConstants.CollectRows, DisplayName = "Collect right",
            Parameters = new() { ["binding"] = second.BindingId, ["fields"] = "Line,Current" },
            OutputPorts = new() { new("items", PortShape.Record, PortCardinality.Collection) } };
        var join = new MappingNode { NodeId = Id.New(), NodeType = NodeTypeConstants.Join, DisplayName = "Join sources",
            Parameters = new() { ["leftKey"] = "Line", ["rightKey"] = "Line", ["cardinality"] = "Inner",
                ["duplicateKey"] = "All", ["manyToMany"] = "Allow", ["fieldConflict"] = "Right" },
            InputPorts = new() { new("left", PortShape.Record, PortCardinality.Collection), new("right", PortShape.Record, PortCardinality.Collection) },
            OutputPorts = new() { new("rows", PortShape.Record, PortCardinality.Collection) } };
        project.Nodes.AddRange(new[] { right, join });
        var edgeIndex = project.Edges.FindIndex(e => e.SourceNodeId == left.NodeId);
        project.Edges[edgeIndex] = project.Edges[edgeIndex] with { SourceNodeId = join.NodeId, SourcePortName = "rows" };
        foreach (var pair in new[] { (left.NodeId, "left"), (right.NodeId, "right") })
            project.Edges.Add(new MappingEdge { EdgeId = Id.New(), SourceNodeId = pair.Item1, SourcePortName = "items", TargetNodeId = join.NodeId, TargetPortName = pair.Item2 });
        var connections = new Dictionary<string, SourceConnection>();
        for (var side = 0; side < 2; side++)
        {
            var binding = project.Bindings[side];
            var table = side == 0 ? "financials" : "rightrows";
            if (kind == "csv" || (kind == "csv-left" && side == 0) || (kind == "csv-right" && side == 1))
            {
                var path = Path.Combine(root, $"side{side}.csv");
                using (var writer = new StreamWriter(path))
                {
                    await writer.WriteLineAsync("Line,Current");
                    if (rowsPerSide > 0)
                        for (var i = 0; i < rowsPerSide; i++) await writer.WriteLineAsync($"K{i % groups},{i % groups % 7 + 1}");
                    else await writer.WriteAsync(side == 0 ? "B,10\nLeftOnly,20\nA,30\nB,40\n" : "RightOnly,3\nA,5\nB,7\nB,11\n");
                }
                project.Bindings[side] = binding with { Kind = "csv", Locator = path, Configuration = new() };
                connections[binding.BindingId] = new(binding.BindingId, "csv", path);
            }
            else connections[binding.BindingId] = new(binding.BindingId, "sqlite", database,
                new Dictionary<string, string> { ["table"] = table, ["decimalColumns"] = side == 0 ? "Current,Prior" : "Current" });
        }
        return new(project, connections);
    }
}
