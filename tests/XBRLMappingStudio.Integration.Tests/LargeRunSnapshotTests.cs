using System.Text;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed partial class LargeRunExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedSqliteSource_ReplaysIdenticalReportAndLineageAfterLiveSourceDeletion(bool large)
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 40, variedValues: true);
            var project = BuildProject(database, approved: true);
            var profile = SyntheticProfile.Build();
            var connections = Connections(project, database);
            var source = Assert.Single(connections).Value;
            var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(root, "retained"), default);
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: large ? 2 : 100, RowBatchSize: 8);
            var service = new MappingJobService(profile, new DigestOnlyArtifactWorker(profile), executionBudget: budget);
            using var baseline = await service.RunAsync(project, connections, new Dictionary<string, string>(), false, default);
            Assert.True(baseline.IsPublishable);
            var baselineDigest = Assert.Single(baseline.Manifest.Outputs).Digest;
            var baselineInput = Assert.Single(baseline.Manifest.Inputs);
            Assert.False(baselineInput.IsReplayable);
            File.Delete(database);

            // A new store open and a new mapping service simulate process restart.
            var replayConnection = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
            var replayService = new MappingJobService(profile, new DigestOnlyArtifactWorker(profile), executionBudget: budget);
            using var replay = await replayService.RunAsync(project,
                new Dictionary<string, SourceConnection> { [source.SourceId] = replayConnection },
                new Dictionary<string, string>(), false, default);
            Assert.True(replay.IsPublishable, string.Join(" | ", replay.Manifest.Diagnostics.Select(d => d.Message)));
            Assert.Equal(baselineDigest, Assert.Single(replay.Manifest.Outputs).Digest);
            var identity = Assert.Single(replay.Manifest.Inputs);
            Assert.Equal(baselineInput.Digest, identity.Digest);
            Assert.True(identity.IsReplayable);
            Assert.Equal(SqliteSourceSnapshotStore.Strategy, identity.SnapshotStrategy);
            Assert.Equal(replayConnection.RetainedSnapshot!.DatabaseDigest, identity.SnapshotDigest);
            Assert.Equal(database, identity.OriginalSourcePath);
            if (!large)
                Assert.Equal(baseline.Facts.SelectMany(fact => fact.ContributingSources),
                    replay.Facts.SelectMany(fact => fact.ContributingSources));

            var publisher = new PublicationService(new RunJournal(root));
            var metadataBefore = await File.ReadAllBytesAsync(metadata);
            var metadataError = await Assert.ThrowsAsync<PublicationException>(async () =>
            {
                if (large)
                    await publisher.PublishAuthorizedLargeArtifactAsync(metadata, replay.LargeArtifact!, root,
                        project, profile, true, replay,
                        (staged, token) => replayService.ValidateStagedArtifactAsync(staged, metadata, replay, token), default);
                else
                    await publisher.PublishAuthorizedAsync(metadata, Encoding.UTF8.GetBytes(replay.XbrlJson), root,
                        project, profile, true, replay,
                        (staged, token) => replayService.ValidateStagedArtifactAsync(staged, metadata, replay, token), default);
            });
            Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, metadataError.Code);
            Assert.Equal(metadataBefore, await File.ReadAllBytesAsync(metadata));
            var destination = Path.Combine(root, "replayed.json");
            if (large)
                await publisher.PublishAuthorizedLargeArtifactAsync(destination, replay.LargeArtifact!, root,
                    project, profile, true, replay,
                    (staged, token) => replayService.ValidateStagedArtifactAsync(staged, destination, replay, token), default);
            else
                await publisher.PublishAuthorizedAsync(destination, Encoding.UTF8.GetBytes(replay.XbrlJson), root,
                    project, profile, true, replay,
                    (staged, token) => replayService.ValidateStagedArtifactAsync(staged, destination, replay, token), default);
            var accepted = await File.ReadAllBytesAsync(destination);
            File.Delete(replayConnection.Path!);
            var refused = await Assert.ThrowsAsync<PublicationException>(async () =>
            {
                if (large)
                    await publisher.PublishAuthorizedLargeArtifactAsync(destination, replay.LargeArtifact!, root,
                        project, profile, true, replay,
                        (staged, token) => replayService.ValidateStagedArtifactAsync(staged, destination, replay, token), default);
                else
                    await publisher.PublishAuthorizedAsync(destination, Encoding.UTF8.GetBytes(replay.XbrlJson), root,
                        project, profile, true, replay,
                        (staged, token) => replayService.ValidateStagedArtifactAsync(staged, destination, replay, token), default);
            });
            Assert.Equal(DiagnosticCodes.PubDigestMismatch, refused.Code);
            Assert.Equal(accepted, await File.ReadAllBytesAsync(destination));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedSqliteSource_MutationDuringExecutionFailsClosed(bool large)
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 40, variedValues: true);
            var project = BuildProject(database);
            var source = Assert.Single(Connections(project, database)).Value;
            var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(root, "retained"), default);
            var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
            var profile = SyntheticProfile.Build();
            var service = new MappingJobService(profile, new DigestOnlyArtifactWorker(profile),
                executionBudget: new ExecutionBudget(InMemorySourceRecordLimit: large ? 2 : 100, RowBatchSize: 8));
            bool changed = false;
            var progress = new InlineProgress<MappingJobProgress>(stage =>
            {
                if (changed || !(stage.Stage.StartsWith("Validating generated", StringComparison.Ordinal)
                    || stage.Stage.StartsWith("Validating staged report", StringComparison.Ordinal))) return;
                // A sidecar is sufficient to invalidate the immutable-backup contract.
                File.WriteAllText(replay.Path + "-journal", "unexpected external mutation");
                changed = true;
            });
            using var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection> { [source.SourceId] = replay },
                new Dictionary<string, string>(), false, default, progress);
            Assert.True(changed);
            Assert.Equal(RunOutcome.DataError, result.Manifest.Outcome);
            Assert.False(result.IsPublishable);
            Assert.Empty(result.Manifest.Outputs);
            Assert.Contains(result.Manifest.Diagnostics, d => d.Code == DiagnosticCodes.IoInputIdentityChanged);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task RetainedSqliteSource_PreviewStillCannotClaimFullReplayOrPublication()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 40, variedValues: true);
            var project = BuildProject(database);
            var source = Assert.Single(Connections(project, database)).Value;
            var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(root, "retained"), default);
            var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
            var profile = SyntheticProfile.Build();
            using var result = await new MappingJobService(profile, new DigestOnlyArtifactWorker(profile)).RunAsync(project,
                new Dictionary<string, SourceConnection> { [source.SourceId] = replay },
                new Dictionary<string, string>(), true, default);
            Assert.True(result.Manifest.IsPreview);
            Assert.False(result.IsPublishable);
            Assert.False(Assert.Single(result.Manifest.Inputs).IsReplayable);
            Assert.Equal("sqlite-preview", Assert.Single(result.Manifest.Inputs).Kind);
        }
        finally { TryDeleteDirectory(root); }
    }
}
