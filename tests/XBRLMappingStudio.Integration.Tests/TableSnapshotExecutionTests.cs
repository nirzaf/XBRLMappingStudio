using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed partial class LargeRunExecutionTests
{
    [Theory]
    [InlineData("group")]
    [InlineData("join")]
    public async Task TableSnapshot_ReplaysExactReportDigestsAndPathsThenPublishes(string mode)
    {
        var root = NewDirectory();
        try
        {
            MappingProject project;
            IReadOnlyDictionary<string, SourceConnection> connections;
            if (mode == "join")
            {
                var fixture = await CreateJoinFixture(root);
                project = fixture.Project;
                connections = fixture.Connections;
            }
            else
            {
                var database = await CreateSourceDatabase(root, 40, variedValues: true);
                project = BuildPartitionedGroupProject(database, "Id");
                connections = Connections(project, database);
            }
            var profile = SyntheticProfile.Build();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
                ProfileDigest = profile.Digest, ApprovedBy = "Table snapshot reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow, Authority = "test-fixture"
            });
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 3);
            var service = new MappingJobService(profile, new DigestOnlyArtifactWorker(profile), executionBudget: budget);
            using var baseline = await service.RunAsync(project, connections, new Dictionary<string, string>(), false, default);
            Assert.True(baseline.IsPublishable, Describe(baseline));
            var replayConnections = new Dictionary<string, SourceConnection>(StringComparer.Ordinal);
            foreach (var source in connections.Values)
            {
                var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source,
                    Path.Combine(root, "table-" + source.SourceId), default);
                replayConnections.Add(source.SourceId, await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default));
            }
            foreach (var path in connections.Values.Select(c => c.Path).Distinct()) File.Delete(path!);
            var replayService = new MappingJobService(profile, new DigestOnlyArtifactWorker(profile), executionBudget: budget);
            using var replay = await replayService.RunAsync(project, replayConnections, new Dictionary<string, string>(), false, default);
            Assert.True(replay.IsPublishable, Describe(replay));
            Assert.Equal(baseline.Manifest.Outputs.Single().Digest, replay.Manifest.Outputs.Single().Digest);
            Assert.Equal(JsonSerializer.Serialize(baseline.Facts.ToArray()), JsonSerializer.Serialize(replay.Facts.ToArray()));
            Assert.Equal(baseline.Manifest.Inputs.Select(i => i.Digest), replay.Manifest.Inputs.Select(i => i.Digest));
            Assert.All(replay.Manifest.Inputs, input =>
            {
                Assert.True(input.IsReplayable);
                Assert.Equal(SqliteSourceSnapshotStore.Strategy, input.SnapshotStrategy);
                Assert.Equal(replayConnections[input.SourceId].RetainedSnapshot!.DatabaseDigest, input.SnapshotDigest);
                Assert.Equal(connections[input.SourceId].Path, input.OriginalSourcePath);
            });
            var publisher = new PublicationService(new RunJournal(root));
            var destination = Path.Combine(root, "replayed.json");
            Task<PublicationReceipt> Publish(string target) => publisher.PublishAuthorizedLargeArtifactAsync(target, replay.LargeArtifact!, root,
                    project, profile, true, replay,
                    (staged, token) => replayService.ValidateStagedArtifactAsync(staged, target, replay, token), default);
            var metadataPath = replayConnections.Values.First().RetainedSnapshot!.MetadataPath;
            var originalMetadata = await File.ReadAllBytesAsync(metadataPath);
            var protectedError = await Assert.ThrowsAsync<PublicationException>(() => Publish(metadataPath));
            Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, protectedError.Code);
            Assert.Equal(originalMetadata, await File.ReadAllBytesAsync(metadataPath));
            var receipt = await Publish(destination);
            Assert.Equal(baseline.Manifest.Outputs.Single().Digest, receipt.FinalDigest);
            var accepted = await File.ReadAllBytesAsync(destination);
            await File.WriteAllTextAsync(replayConnections.Values.Last().Path + "-journal", "changed source evidence");
            var changedError = await Assert.ThrowsAsync<PublicationException>(() => Publish(destination));
            Assert.Equal(DiagnosticCodes.PubDigestMismatch, changedError.Code);
            Assert.Equal(accepted, await File.ReadAllBytesAsync(destination));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    [Trait("Category", "ProcessMemory")]
    public async Task TableSnapshotHundredThousandRows_CopiesWithBoundedMemoryAndExactRows()
    {
        var root = NewDirectory();
        try
        {
            const int rowCount = 100_000;
            var database = await CreateSourceDatabase(root, rowCount, variedValues: true);
            await using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE confidential_other(data BLOB); INSERT INTO confidential_other VALUES(zeroblob(8388608));";
                await command.ExecuteNonQueryAsync();
            }
            var project = BuildProject(database);
            var source = Connections(project, database).Values.Single();
            var samples = new List<long> { GC.GetTotalMemory(forceFullCollection: true) };
            var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(root, "table"), default,
                new InlineProgress<long>(rows => { if (rows % 5000 == 0) samples.Add(GC.GetTotalMemory(forceFullCollection: true)); }));
            var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
            samples.Add(GC.GetTotalMemory(forceFullCollection: true));
            Assert.True(new FileInfo(database).Length - new FileInfo(replay.Path!).Length >= 8 * 1024 * 1024);
            File.Delete(database);
            var adapter = new XBRLMappingStudio.Adapters.Sqlite.SqliteSourceAdapter();
            var schema = await adapter.InspectSchemaAsync(replay, default);
            Assert.Equal(new[] { "financials" }, await adapter.DiscoverTablesAsync(replay, default));
            var count = 0;
            await foreach (var row in adapter.ReadFullAsync(replay, schema, default))
            {
                Assert.Equal(++count, Assert.IsType<long>(row["Id"].Raw));
                Assert.Equal(1250m + (count - 1) % 3, row["Current"].AsDecimal());
                Assert.Equal((1250 + (count - 1) % 3).ToString(System.Globalization.CultureInfo.InvariantCulture), row["Current"].LexicalSource);
                Assert.True(row.HasStableRowIdentity);
                Assert.Equal(new SourceReference(source.SourceId, "financials.Current", row.RowId), row.CellSources!["Current"]);
            }
            Assert.Equal(rowCount, count);
            Assert.True(samples.Count >= 20);
            Assert.True(samples.Max() - samples.Min() < 64L * 1024 * 1024,
                $"Table snapshot retained managed heap exceeded 64 MiB range: {string.Join(", ", samples)}");
        }
        finally { TryDeleteDirectory(root); }
    }

}
