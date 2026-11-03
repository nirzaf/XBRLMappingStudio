using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;

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
}
