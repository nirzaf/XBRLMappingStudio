using System.Text;
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
    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("csv", false)]
    [InlineData("csv", true)]
    public async Task PartitionedGroup_ExactReportOrderDigestAndSourcePathsMatchInMemory(string kind, bool collectionPort)
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 12, variedValues: true);
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var update = connection.CreateCommand();
                // Four interleaved groups, each with different exact totals. No
                // sorted-input assumption may merge or reorder their evidence.
                update.CommandText = "UPDATE financials SET Line = 'Entity-' || ((Id-1)%4);";
                await update.ExecuteNonQueryAsync();
            }
            var project = BuildPartitionedGroupProject(database);
            if (collectionPort)
            {
                var sinkNode = project.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct);
                var port = sinkNode.InputPorts.FindIndex(p => p.Name == "value");
                sinkNode.InputPorts[port] = sinkNode.InputPorts[port] with { Name = "values" };
                var edge = project.Edges.FindIndex(e => e.TargetNodeId == sinkNode.NodeId && e.TargetPortName == "value");
                project.Edges[edge] = project.Edges[edge] with { TargetPortName = "values" };
            }
            var connections = Connections(project, database);
            if (kind == "csv")
            {
                var csv = Path.Combine(root, "source.csv");
                await File.WriteAllTextAsync(csv, "Line,Current\n" + string.Concat(
                    Enumerable.Range(0, 12).Select(i => $"Entity-{i % 4},{1250 + i % 3}\n")));
                project.Bindings[0] = project.Bindings[0] with { Kind = "csv", Locator = csv, Configuration = new() };
                connections = new Dictionary<string, SourceConnection>
                {
                    [project.Bindings[0].BindingId] = new(project.Bindings[0].BindingId, "csv", csv)
                };
            }
            var profile = SyntheticProfile.Build();
            async Task<JobResult> Execute(int threshold) => await new MappingJobService(profile,
                new DigestOnlyArtifactWorker(profile), executionBudget: new ExecutionBudget(
                    InMemorySourceRecordLimit: threshold, RowBatchSize: 2))
                .RunAsync(project, connections, new Dictionary<string, string>(), false, default);
            using var small = await Execute(20);
            using var large = await Execute(1);
            Assert.True(small.IsPublishable, Describe(small));
            Assert.True(large.IsPublishable, Describe(large));
            var artifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
            Assert.Equal(small.XbrlJson, await File.ReadAllTextAsync(artifact.ReportPath));
            Assert.Equal(small.Manifest.Inputs.Single().Digest, large.Manifest.Inputs.Single().Digest);
            var facts = artifact.InInputOrder().ToArray();
            Assert.Equal(new[] { "Entity-0", "Entity-1", "Entity-2", "Entity-3" }, facts.Select(f => f.EntityIdentifier));
            Assert.All(facts, f => Assert.Equal(3753m, f.Value));
            Assert.Equal(JsonSerializer.Serialize(small.Facts), JsonSerializer.Serialize(facts));
            var collect = project.Nodes.Single(n => n.NodeType == NodeTypeConstants.CollectRows).NodeId;
            var group = project.Nodes.Single(n => n.NodeType == NodeTypeConstants.GroupBy).NodeId;
            var sink = project.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct).NodeId;
            Assert.All(facts, fact =>
            {
                Assert.Equal(6, fact.SourceTransformationPaths.Count);
                Assert.All(fact.SourceTransformationPaths, path => Assert.Equal(new[] { collect, group, sink }, path.TransformationIds));
            });
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("group-count")]
    [InlineData("member-count")]
    [InlineData("member-bytes")]
    [InlineData("provenance")]
    [InlineData("fact-bytes")]
    [InlineData("spool-bytes")]
    public async Task PartitionedGroup_BudgetsDiscardAllOutputBeforeValidation(string limit)
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 9, variedValues: true);
            var project = BuildPartitionedGroupProject(database, limit == "group-count" ? "Id" : "Line");
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 2);
            budget = limit switch
            {
                "group-count" => budget with { MaxGroupCount = 2 },
                "member-count" => budget with { MaxCollectionItems = 2 },
                "member-bytes" => budget with { MaxRowBytes = 4096, MaxBatchBytes = 4096 },
                "provenance" => budget with { MaxGroupWorkingBytes = 1 },
                "fact-bytes" => budget with { MaxFactBytes = 1 },
                "spool-bytes" => budget with { MaxSpoolBytes = 1 },
                _ => throw new InvalidOperationException()
            };
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            var priorDirectories = LargeRunDirectories();
            using var result = await Run(project, database, profile, budget, worker);
            Assert.Equal(priorDirectories, LargeRunDirectories());
            Assert.False(result.IsPublishable);
            Assert.Empty(result.Facts);
            Assert.Null(result.LargeArtifact);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Contains(result.Manifest.Diagnostics, d => d.IsBlocking && d.Code ==
                (limit is "fact-bytes" or "spool-bytes" ? DiagnosticCodes.IoNumberOutOfBudget : DiagnosticCodes.MapCollectionBudgetExceeded));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task PartitionedGroup_CancellationDiscardsSpoolAndAllowsCleanRestart()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 12, variedValues: true);
            var project = BuildPartitionedGroupProject(database, "Id");
            var profile = SyntheticProfile.Build();
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 2);
            using var cancellation = new CancellationTokenSource();
            var worker = new DigestOnlyArtifactWorker(profile);
            var priorDirectories = LargeRunDirectories();
            using var cancelled = await Run(project, database, profile, budget, worker,
                new SynchronousGroupProgress(p =>
                {
                    if (p.Stage == "Executing bounded disk-partitioned group") cancellation.Cancel();
                }), cancellation.Token);
            Assert.Equal(RunOutcome.Cancelled, cancelled.Manifest.Outcome);
            Assert.Equal(priorDirectories, LargeRunDirectories());
            Assert.Empty(cancelled.Facts);
            Assert.Null(cancelled.LargeArtifact);
            Assert.Equal(0, worker.ValidationRequests);
            using var restarted = await Run(project, database, profile, budget);
            Assert.True(restarted.IsPublishable, Describe(restarted));
            Assert.Equal(12, restarted.Facts.Count);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task PartitionedGroup_UnsupportedRowDependentPlanStillRefusesLargeExecution()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 12, variedValues: true);
            var project = BuildPartitionedGroupProject(database);
            project.Nodes.Add(new MappingNode
            {
                NodeId = Id.New(), NodeType = NodeTypeConstants.ExcelField, DisplayName = "Row-dependent input",
                Parameters = new() { ["bindingId"] = project.Bindings[0].BindingId, ["column"] = "Current" },
                OutputPorts = new() { new("value", PortShape.Decimal, PortCardinality.Single) }
            });
            var profile = SyntheticProfile.Build();
            using var result = await Run(project, database, profile, new(InMemorySourceRecordLimit: 1));
            Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics, d => d.Code == DiagnosticCodes.IoUnsupportedOperation);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void PartitionedGroup_SpoolPreservesTypedValuesPresenceProjectionAndOrdinalKeys()
    {
        var root = NewDirectory();
        try
        {
            using var spool = new LargeRunFactStore(Path.Combine(root, "groups.db"), 256, 8 * 1024 * 1024);
            var plan = new PartitionedGroupPlan("b", "g", new[] { "K1", "K2" },
                new HashSet<string>(new[] { "K1", "K2", "Decimal", "Nil", "Missing", "Blank", "Date", "Instant", "Bool", "Integer" }));
            plan = plan with { RetainedFields = plan.RequiredFields };
            var source = new SourceReference("b", "row#1.Current");
            var fields = new Dictionary<string, DataValue>
            {
                ["K1"] = DataValue.StringValue("a|b"), ["K2"] = DataValue.StringValue("c"),
                ["Decimal"] = DataValue.DecimalValue(1.2345678901234567890123456789m, source, "1.2345678901234567890123456789"),
                ["Nil"] = DataValue.ExplicitNil(PortShape.Decimal), ["Missing"] = DataValue.Missing(PortShape.Decimal),
                ["Blank"] = DataValue.StringValue("  "), ["Date"] = DataValue.DateValue(new(2025, 12, 31)),
                ["Instant"] = DataValue.InstantValue(new(2025, 12, 31, 4, 5, 6, TimeSpan.FromHours(3))),
                ["Bool"] = DataValue.BoolValue(true), ["Integer"] = DataValue.IntegerValue(long.MaxValue),
                ["Unused"] = DataValue.Opaque(PortShape.Record, new object())
            };
            var row = new DataRecord { RowId = "pk:1", Fields = fields, HasStableRowIdentity = true,
                CellSources = new Dictionary<string, SourceReference> { ["Nil"] = source, ["Decimal"] = new("wrong", "fallback") } };
            spool.AddGroupRecord(row, plan, new(), default);
            var second = new Dictionary<string, DataValue>(fields) { ["K1"] = DataValue.StringValue("a"), ["K2"] = DataValue.StringValue("b|c") };
            spool.AddGroupRecord(new() { RowId = "pk:2", Fields = second }, plan, new(), default);
            Assert.Equal(2, spool.GroupCount);
            var restored = Assert.Single(spool.ReadGroup(1, default));
            Assert.Equal(row.RowId, restored.RowId);
            Assert.True(restored.HasStableRowIdentity);
            Assert.DoesNotContain("Unused", restored.Fields.Keys);
            foreach (var field in fields.Where(p => p.Key != "Unused")) Assert.Equal(field.Value, restored[field.Key]);
            Assert.Equal(source, restored["Decimal"].Source);
            Assert.Equal(fields["Decimal"].LexicalSource, restored["Decimal"].LexicalSource);
            Assert.Equal(source, restored.CellSources!["Nil"]);
            Assert.Equal(source, restored.CellSources["Decimal"]);
            Assert.Equal("pk:2", Assert.Single(spool.ReadGroup(2, default)).RowId);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task PartitionedGroup_MissingNilAndZeroSemanticsMatchIndependentOracle()
    {
        var root = NewDirectory();
        try
        {
            var project = BuildPartitionedGroupProject("unused.db");
            project.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct).Parameters.Remove("decimals");
            var plan = new PlanCompiler(FunctionCatalog.Default).Compile(new MappingGraph(project), SyntheticProfile.Build()).Plan!;
            var values = new[] { DataValue.DecimalValue(10), DataValue.ExplicitNil(PortShape.Decimal),
                DataValue.DecimalValue(0), DataValue.DecimalValue(5), DataValue.DecimalValue(20),
                DataValue.ExplicitNil(PortShape.Decimal), DataValue.Missing(PortShape.Decimal) };
            var keys = new[] { "sum", "nil", "zero", "missing", "sum", "sum", "missing" };
            var rows = values.Select((value, index) => new DataRecord
            {
                RowId = index.ToString(), Fields = new Dictionary<string, DataValue>
                { ["Line"] = DataValue.StringValue(keys[index]), ["Current"] = value },
                CellSources = new Dictionary<string, SourceReference>
                { ["Line"] = new("b", $"{index}.Line"), ["Current"] = new("b", $"{index}.Current") }
            }).ToArray();
            async Task<ExecutionResult> Execute(IReadOnlyList<DataRecord> input)
            {
                var context = new XBRLMappingStudio.Engine.ExecutionContext
                { OperationId = "test", Manifest = new() { RunId = "test", OperationId = "test",
                    ProjectId = project.ProjectId, ProfileDigest = project.ProfileDigest, EngineVersion = "test" },
                    Parameters = new Dictionary<string, string>() };
                context.LoadedSources.Add(project.Bindings[0].BindingId, input);
                return await plan.ExecuteAsync(context);
            }
            var expected = await Execute(rows);
            using var spool = new LargeRunFactStore(Path.Combine(root, "groups.db"), 256, 8 * 1024 * 1024);
            foreach (var row in rows) spool.AddGroupRecord(row, plan.PartitionedGroup!, new(), default);
            var actual = new List<PendingFact>();
            for (var group = 1; group <= spool.GroupCount; group++)
                actual.AddRange((await Execute(spool.ReadGroup(group, default))).Facts);
            Assert.Equal(JsonSerializer.Serialize(expected.Facts), JsonSerializer.Serialize(actual));
            Assert.Equal(new[] { "sum", "nil", "zero" }, actual.Select(f => f.EntityIdentifier));
            Assert.Equal(30m, actual[0].Value);
            Assert.True(actual[1].IsNil);
            Assert.Equal(0m, actual[2].Value);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartitionedGroup_RetainedUnusedFieldsStillReceiveNumericValidation(bool excludeUnused)
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 3, variedValues: true);
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE financials SET Current='1', Prior='12345';";
                await update.ExecuteNonQueryAsync();
            }
            var project = BuildPartitionedGroupProject(database);
            if (excludeUnused)
                project.Nodes.Single(n => n.NodeType == NodeTypeConstants.CollectRows).Parameters["fields"] = "Line,Current";
            var profile = SyntheticProfile.Build();
            using var small = await Run(project, database, profile, new(InMemorySourceRecordLimit: 20, MaxSignificantDigits: 3));
            using var large = await Run(project, database, profile, new(InMemorySourceRecordLimit: 1, MaxSignificantDigits: 3));
            Assert.Equal(small.IsPublishable, large.IsPublishable);
            Assert.Equal(excludeUnused, large.IsPublishable);
            if (!excludeUnused)
                Assert.Contains(large.Manifest.Diagnostics, d => d.Code == DiagnosticCodes.MapNumericOverflow);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartitionedGroup_PublicationRequiresExactStagedValidation(bool reject)
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 12, variedValues: true);
            var project = BuildPartitionedGroupProject(database, "Id");
            var profile = SyntheticProfile.Build();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "partitioned grouping test", ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest, Authority = "test-fixture"
            });
            var worker = new DigestOnlyArtifactWorker(profile);
            var service = new MappingJobService(profile, worker, executionBudget: new(InMemorySourceRecordLimit: 1));
            using var result = await service.RunAsync(project, Connections(project, database), new Dictionary<string, string>(), false, default);
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
    [Trait("Category", "ProcessMemory")]
    public async Task GroupHundredThousandRows_PartitionsWithBoundedMemoryAndExactTotals()
    {
        const int rowCount = 100_000;
        const int groupCount = 10_000;
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, rowCount, variedValues: false);
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var update = connection.CreateCommand();
                update.CommandText = $"UPDATE financials SET Line = 'Entity-' || ((Id-1)%{groupCount});";
                await update.ExecuteNonQueryAsync();
            }
            var project = BuildPartitionedGroupProject(database);
            var samples = new List<long>();
            var progress = new SynchronousGroupProgress(p =>
            {
                if ((p.Stage == "Partitioning source groups on disk" && p.Current % 5120 == 0)
                    || (p.Stage == "Executing bounded disk-partitioned group" && p.Current % 500 == 0))
                    samples.Add(GC.GetTotalMemory(forceFullCollection: true));
            });
            using var result = await Run(project, database, SyntheticProfile.Build(),
                new(InMemorySourceRecordLimit: 128, RowBatchSize: 256, SqliteCacheKiB: 256,
                    MaxSpoolBytes: 384L * 1024 * 1024), progress: progress);
            Assert.True(result.IsPublishable, Describe(result));
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            Assert.Equal(groupCount, artifact.FactCount);
            var index = 0;
            foreach (var fact in artifact.InInputOrder())
            {
                Assert.Equal($"Entity-{index++}", fact.EntityIdentifier);
                Assert.Equal(12_500m, fact.Value);
                Assert.Equal(20, fact.SourceTransformationPaths.Count);
            }
            Assert.True(samples.Count >= 10);
            Assert.True(samples.Max() - samples.Min() < 64L * 1024 * 1024,
                $"Grouped retained managed heap varied beyond 64 MiB: {string.Join(", ", samples)}");
        }
        finally { TryDeleteDirectory(root); }
    }

    private static MappingProject BuildPartitionedGroupProject(string database, string keys = "Line")
    {
        var project = BuildProject(database);
        var remove = project.Nodes.Where(n => n.NodeType is NodeTypeConstants.ExcelField or NodeTypeConstants.Scale)
            .Select(n => n.NodeId).ToHashSet();
        project.Nodes.RemoveAll(n => remove.Contains(n.NodeId));
        project.Edges.RemoveAll(e => remove.Contains(e.SourceNodeId) || remove.Contains(e.TargetNodeId));
        var sink = project.Nodes.Single(n => n.NodeType == NodeTypeConstants.FactConstruct);
        project.Edges.RemoveAll(e => e.TargetNodeId == sink.NodeId && e.TargetPortName == "entity");
        foreach (var name in new[] { "value", "entity" })
        {
            var i = sink.InputPorts.FindIndex(p => p.Name == name);
            sink.InputPorts[i] = sink.InputPorts[i] with { Cardinality = PortCardinality.Collection };
        }
        var collect = new MappingNode
        {
            NodeId = Id.New(), NodeType = NodeTypeConstants.CollectRows, DisplayName = "Collect rows",
            Parameters = new() { ["binding"] = project.Bindings[0].BindingId },
            OutputPorts = new() { new("items", PortShape.Record, PortCardinality.Collection) }
        };
        var group = new MappingNode
        {
            NodeId = Id.New(), NodeType = NodeTypeConstants.GroupBy, DisplayName = "Group source rows",
            Parameters = new() { ["keys"] = keys, ["sumField"] = "Current" },
            InputPorts = new() { new("items", PortShape.Record, PortCardinality.Collection) },
            OutputPorts = new() { new("values", PortShape.Decimal, PortCardinality.Collection), new("keys", PortShape.String, PortCardinality.Collection) }
        };
        project.Nodes.AddRange(new[] { collect, group });
        foreach (var edge in new[] { (collect.NodeId, "items", group.NodeId, "items"),
            (group.NodeId, "values", sink.NodeId, "value"), (group.NodeId, "keys", sink.NodeId, "entity") })
            project.Edges.Add(new MappingEdge { EdgeId = Id.New(), SourceNodeId = edge.Item1, SourcePortName = edge.Item2,
                TargetNodeId = edge.Item3, TargetPortName = edge.Item4 });
        return project;
    }

    private static string Describe(JobResult result) => string.Join(" | ", result.Manifest.Diagnostics.Select(d => d.Message));
    private sealed class SynchronousGroupProgress(Action<MappingJobProgress> action) : IProgress<MappingJobProgress>
    {
        public void Report(MappingJobProgress value) => action(value);
    }
}
