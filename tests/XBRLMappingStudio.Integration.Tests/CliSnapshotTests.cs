using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed partial class CliParityTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Cli_SnapshotCaptureAndReplayPreserveApprovalAndIntegrityGates(bool approved, bool corrupt)
    {
        var sourcePath = Path.Combine(_dir, "live.sqlite");
        using (var db = new SqliteConnection($"Data Source={sourcePath};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE financials (Line TEXT PRIMARY KEY, Current TEXT, Prior TEXT); INSERT INTO financials VALUES ('Revenue','1250','1100');";
            command.ExecuteNonQuery();
        }
        var project = TestFixtures.BuildRevenueMappingProject(sourcePath);
        project.Bindings[0] = project.Bindings[0] with
        {
            Kind = "sqlite", Locator = sourcePath,
            Configuration = new Dictionary<string, object?> { ["table"] = "financials", ["decimalColumns"] = "Current,Prior", ["unusedOptional"] = null }
        };
        if (approved)
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
                ProfileDigest = SyntheticProfile.Build().Digest, ApprovedBy = "Snapshot test reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow, Authority = "test-fixture"
            });
        var projectPath = Path.Combine(_dir, "replay.xmap");
        new ProjectManager(projectPath).Save(project);
        var bundle = Path.Combine(_dir, "retained");
        var cli = LocateCli();
        var (captureExit, captureJson, captureError) = await RunCliAsync(cli, "snapshot-source", projectPath,
            "--binding", project.Bindings[0].BindingId, "--out", bundle);
        Assert.True(captureExit == 0, captureError);
        using var capture = JsonDocument.Parse(captureJson);
        Assert.True(capture.RootElement.GetProperty("entireDatabase").GetBoolean());
        var metadata = capture.RootElement.GetProperty("snapshot").GetString()!;
        File.Delete(sourcePath);
        var originalMetadata = await File.ReadAllBytesAsync(metadata);
        var (repeatExit, _, _) = await RunCliAsync(cli, "snapshot-source", projectPath,
            "--binding", project.Bindings[0].BindingId, "--out", bundle);
        Assert.NotEqual(0, repeatExit);
        Assert.Equal(originalMetadata, await File.ReadAllBytesAsync(metadata));
        if (corrupt) await File.WriteAllTextAsync(Path.Combine(bundle, "source.sqlite-journal"), "untrusted sidecar");
        var output = Path.Combine(_dir, "replay-output.json");
        var manifest = Path.Combine(_dir, "run.json");
        await File.WriteAllTextAsync(output, "previous accepted artifact");
        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--source-snapshot", project.Bindings[0].BindingId + "=" + metadata,
            "--output", output, "--manifest", manifest, "--allow-synthetic-profile");
        if (!approved || corrupt)
        {
            Assert.Equal(!approved ? 5 : 3, exit);
            Assert.Equal("previous accepted artifact", await File.ReadAllTextAsync(output));
            Assert.False(File.Exists(manifest));
            return;
        }
        Assert.True(exit == 0, stderr);
        var fact = Assert.Single(OimSerializer.ReadFacts(await File.ReadAllTextAsync(output)));
        Assert.Equal(1250000m, fact.Value);
        var saved = JsonSerializer.Deserialize<RunManifest>(await File.ReadAllTextAsync(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var input = Assert.Single(saved.Inputs);
        Assert.True(input.IsReplayable);
        Assert.Equal(SqliteSourceSnapshotStore.Strategy, input.SnapshotStrategy);
        Assert.NotNull(input.SnapshotDigest);
        Assert.Equal(sourcePath, input.OriginalSourcePath);
        var accepted = await File.ReadAllBytesAsync(output);
        var (retryExit, _, retryError) = await RunCliAsync(cli, "execute", projectPath,
            "--source-snapshot", project.Bindings[0].BindingId + "=" + metadata,
            "--output", output, "--allow-synthetic-profile");
        Assert.True(retryExit == 0, retryError);
        Assert.Equal(accepted, await File.ReadAllBytesAsync(output));
    }
}
