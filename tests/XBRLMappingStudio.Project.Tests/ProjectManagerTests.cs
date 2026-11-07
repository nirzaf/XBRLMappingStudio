using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using System.Text.Json.Nodes;

namespace XBRLMappingStudio.Project.Tests;

/// <summary>
/// BASE03: project serialization, semantic revision, atomic save, migration policy.
/// Acceptance: save/reopen preserves stable IDs; layout-only edit does not bump the
/// semantic revision; concurrent save with stale generation is rejected.
/// </summary>
public sealed class ProjectManagerTests : IDisposable
{
    private readonly string _dir;

    public ProjectManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xbrl-prj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string ProjectPath(string name = "project.xmap") => Path.Combine(_dir, name);

    [Fact]
    public void SaveThenLoad_PreservesStableNodeIds()
    {
        var seed = Guid.Parse("33333333-1111-2222-3333-444444444444");
        var original = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var mgr = new ProjectManager(ProjectPath());

        mgr.Save(original);
        var reloaded = mgr.Load();

        Assert.Equal(original.ProjectId, reloaded.ProjectId);
        Assert.Equal(
            original.Nodes.Select(n => n.NodeId).OrderBy(x => x, StringComparer.Ordinal),
            reloaded.Nodes.Select(n => n.NodeId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            original.Edges.Select(e => e.EdgeId).OrderBy(x => x, StringComparer.Ordinal),
            reloaded.Edges.Select(e => e.EdgeId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(original.ProfileDigest, reloaded.ProfileDigest);
        Assert.Equal(original.ComputeSemanticDigest(), reloaded.ComputeSemanticDigest());
    }

    [Fact]
    public void LoadBackupAfterMainProjectIsCorrupted_ValidatesBackupWithoutChangingFiles()
    {
        var path = ProjectPath();
        var manager = new ProjectManager(path);
        var project = TestFixtures.BuildRevenueMappingProject(
            idSeed: Guid.Parse("33333333-1111-2222-3333-444444444453"));
        var firstSave = manager.Save(project);
        var originalPosition = project.Nodes[0].Layout!;
        var originalX = originalPosition.X;
        project.Nodes[0].Layout = new LayoutPosition(originalX + 250, originalPosition.Y);
        manager.Save(project, firstSave.NewLayoutGeneration);

        var expectedBackupBytes = File.ReadAllBytes(manager.BackupPath);
        var expectedProjectId = manager.LoadBackup().ProjectId;
        File.WriteAllText(path, "{ this is not a project }");
        var corruptedProjectBytes = File.ReadAllBytes(path);

        var recovered = manager.LoadBackup();

        Assert.Equal(expectedProjectId, recovered.ProjectId);
        Assert.Equal(originalX, recovered.Nodes[0].Layout!.X);
        Assert.Equal(corruptedProjectBytes, File.ReadAllBytes(path));
        Assert.Equal(expectedBackupBytes, File.ReadAllBytes(manager.BackupPath));
        Assert.False(File.Exists(manager.LockPath));
    }

    [Fact]
    public void SaveNew_RefusesToReplaceExistingProject()
    {
        var path = ProjectPath();
        var manager = new ProjectManager(path);
        manager.Save(TestFixtures.BuildRevenueMappingProject());
        var originalBytes = File.ReadAllBytes(path);

        var exception = Assert.Throws<ProjectSaveException>(() =>
            manager.SaveNew(TestFixtures.BuildRevenueMappingProject()));

        Assert.Equal(Diagnostics.DiagnosticCodes.PrjRevisionConflict, exception.Code);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(manager.LockPath));
    }

    [Fact]
    public void LayoutOnlyEdit_DoesNotBump_SemanticRevision()
    {
        var seed = Guid.Parse("33333333-1111-2222-3333-444444444445");
        var project = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var mgr = new ProjectManager(ProjectPath());

        var first = mgr.Save(project);
        var firstRevision = first.NewSemanticRevision;

        // Move every node's layout, keep semantics identical.
        foreach (var n in project.Nodes)
            n.Layout = new LayoutPosition(n.Layout!.X + 250, n.Layout!.Y + 100);

        var second = mgr.Save(project, expectedLayoutGeneration: first.NewLayoutGeneration);
        Assert.Equal(firstRevision, second.NewSemanticRevision);
        Assert.True(second.NewLayoutGeneration > first.NewLayoutGeneration);
    }

    [Fact]
    public void SemanticEdit_Bumps_SemanticRevision()
    {
        var seed = Guid.Parse("33333333-1111-2222-3333-444444444446");
        var project = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var mgr = new ProjectManager(ProjectPath());

        var first = mgr.Save(project);
        var factNode = project.Nodes.Single(n => n.NodeType == Engine.NodeTypeConstants.FactConstruct);
        factNode.Parameters["unit"] = "USD";

        var second = mgr.Save(project, expectedLayoutGeneration: first.NewLayoutGeneration);
        Assert.Equal(first.NewSemanticRevision + 1, second.NewSemanticRevision);
    }

    [Fact]
    public void StaleExpectedGeneration_SaveIsRejected()
    {
        var seed = Guid.Parse("33333333-1111-2222-3333-444444444447");
        var project = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var mgr = new ProjectManager(ProjectPath());

        var first = mgr.Save(project);
        // Simulate another writer saving in between.
        project.Nodes[0].Layout = new LayoutPosition(99, 99);
        mgr.Save(project, expectedLayoutGeneration: first.NewLayoutGeneration);

        // Now attempt a save against the stale generation.
        var ex = Assert.Throws<ProjectSaveException>(() =>
            mgr.Save(project, expectedLayoutGeneration: first.NewLayoutGeneration));
        Assert.Equal(Diagnostics.DiagnosticCodes.PrjRevisionConflict, ex.Code);
    }

    [Fact]
    public void Save_LeavesNoStaleTempFile()
    {
        var seed = Guid.Parse("33333333-1111-2222-3333-444444444448");
        var project = TestFixtures.BuildRevenueMappingProject(idSeed: seed);
        var mgr = new ProjectManager(ProjectPath());

        mgr.Save(project);
        mgr.Save(project);

        var leftover = Directory.GetFiles(_dir, "*.tmp.*");
        Assert.Empty(leftover);
        Assert.False(File.Exists(mgr.LockPath));
    }

    [Fact]
    public void RoundTrip_JSONSerializeDeserialize_PreservesGraph()
    {
        var seed = Guid.Parse("33333333-1111-2222-3333-444444444449");
        var project = TestFixtures.BuildRevenueMappingProject(idSeed: seed);

        var json = ProjectSerializer.Serialize(project);
        var reloaded = ProjectSerializer.Deserialize(json);

        Assert.Equal(project.ComputeSemanticDigest(), reloaded.ComputeSemanticDigest());
        Assert.Equal(project.Nodes.Count, reloaded.Nodes.Count);
        Assert.Equal(project.Edges.Count, reloaded.Edges.Count);
    }

    [Fact]
    public void LegacyProjectWithoutDestination_RemainsReadableAndKeepsItsSemanticDigest()
    {
        var project = TestFixtures.BuildRevenueMappingProject(
            idSeed: Guid.Parse("33333333-1111-2222-3333-444444444450"));
        var legacyJson = ProjectSerializer.Serialize(project);

        Assert.DoesNotContain("\"destination\"", legacyJson, StringComparison.Ordinal);
        var reloaded = ProjectSerializer.Deserialize(legacyJson);

        Assert.Null(reloaded.Destination);
        Assert.Equal(project.ComputeSemanticDigest(), reloaded.ComputeSemanticDigest());
    }

    [Theory]
    [InlineData("profileId")]
    [InlineData("profileDigest")]
    public void LegacyProjectMissingProfileIdentityIsBlockedWithoutRewritingTheFile(string missingField)
    {
        var project = TestFixtures.BuildRevenueMappingProject(
            idSeed: Guid.Parse("33333333-1111-2222-3333-444444444452"));
        var document = JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();
        Assert.True(document.Remove(missingField));
        var path = ProjectPath("legacy-unpinned.xmap");
        File.WriteAllText(path, document.ToJsonString(ProjectSerializer.Options));
        var originalBytes = File.ReadAllBytes(path);

        var blocked = Assert.Throws<ProjectSchemaException>(() => new ProjectManager(path).Load());

        Assert.Contains(missingField, blocked.Message, StringComparison.Ordinal);
        Assert.Contains("profile identity cannot be inferred", blocked.Message, StringComparison.Ordinal);
        Assert.True(blocked.IsProfileIdentityMissing);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void MalformedProjectDoesNotReportMissingProfileIdentity()
    {
        var error = Assert.Throws<ProjectSchemaException>(() => ProjectSerializer.Deserialize("{"));

        Assert.False(error.IsProfileIdentityMissing);
        Assert.DoesNotContain("profile identity cannot be inferred", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidProfileIdentityTypeDoesNotReportMissingProfileIdentity()
    {
        var legacy = JsonNode.Parse(ProjectSerializer.Serialize(TestFixtures.BuildRevenueMappingProject()))!.AsObject();
        legacy["profileId"] = 42;
        legacy.Remove("profileDigest");

        var error = Assert.Throws<ProjectSchemaException>(() =>
            ProjectSerializer.Deserialize(legacy.ToJsonString(ProjectSerializer.Options)));

        Assert.False(error.IsProfileIdentityMissing);
        Assert.DoesNotContain("profile identity cannot be inferred", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DestinationPlan_RoundTripsAndChangesSemanticRevision()
    {
        var project = TestFixtures.BuildRevenueMappingProject(
            idSeed: Guid.Parse("33333333-1111-2222-3333-444444444451"));
        var manager = new ProjectManager(ProjectPath());
        var first = manager.Save(project);

        project.Destination = new DestinationPlan
        {
            Kind = "sqlite",
            Locator = "output/report.db",
            Table = "facts",
            BusinessKey = new List<string> { "fact_key" }
        };
        var second = manager.Save(project, expectedLayoutGeneration: first.NewLayoutGeneration);
        var reloaded = manager.Load();

        Assert.Equal(first.NewSemanticRevision + 1, second.NewSemanticRevision);
        Assert.NotNull(reloaded.Destination);
        Assert.Equal(project.Destination!.Kind, reloaded.Destination.Kind);
        Assert.Equal(project.Destination.Locator, reloaded.Destination.Locator);
        Assert.Equal(project.Destination.Table, reloaded.Destination.Table);
        Assert.Equal(project.Destination.BusinessKey, reloaded.Destination.BusinessKey);
        Assert.Equal(project.ComputeSemanticDigest(), reloaded.ComputeSemanticDigest());
    }
}
