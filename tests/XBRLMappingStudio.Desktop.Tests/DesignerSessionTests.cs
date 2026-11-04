using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;
using Microsoft.Data.Sqlite;

namespace XBRLMappingStudio.Desktop.Tests;

/// <summary>
/// UI02 — headless tests over the portable designer editing model. These exercise
/// multi-select, reconnection, deletion, undo/redo, and the BASE03 acceptance that moving
/// a node (a layout-only edit) preserves the project's semantic digest across save/reopen.
/// No Avalonia display is required: the session operates purely on <see cref="MappingProject"/>.
/// </summary>
public sealed class DesignerSessionTests
{
    private static MappingProject Project()
    {
        var profile = SyntheticProfile.Build();
        var p = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "Designer test",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = profile.Digest
        };
        for (int i = 0; i < 4; i++)
        {
            p.Nodes.Add(new MappingNode
            {
                NodeId = $"n{i}",
                NodeType = "constant",
                DisplayName = $"Node {i}",
                Layout = new LayoutPosition(i * 220, 40)
            });
        }
        p.Edges.Add(new MappingEdge
        {
            EdgeId = "e0",
            SourceNodeId = "n0",
            SourcePortName = "out",
            TargetNodeId = "n1",
            TargetPortName = "in"
        });
        return p;
    }

    [Fact]
    public void MoveNode_PreservesSemanticDigest()
    {
        var session = new DesignerSession(Project());
        var before = session.Project.ComputeSemanticDigest();

        session.MoveNode("n2", 999, 999);

        var after = session.Project.ComputeSemanticDigest();
        Assert.Equal(before, after);
        // Layout actually changed.
        Assert.Equal(999, session.Project.Nodes.Single(n => n.NodeId == "n2").Layout!.X);
    }

    [Fact]
    public void MoveNode_SaveReopen_LayoutPersisted_DigestUnchanged()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-designer", Guid.NewGuid().ToString("n"));
        var file = System.IO.Path.Combine(dir, "proj.xmap");
        try
        {
            var session = new DesignerSession(Project());
            var originalDigest = session.Project.ComputeSemanticDigest();

            var manager = new ProjectManager(file);
            manager.Save(session.Project);

            session.MoveNode("n3", 1234, 5678);
            var movedDigest = session.Project.ComputeSemanticDigest();
            Assert.Equal(originalDigest, movedDigest); // layout-only edit

            var save = manager.Save(session.Project);
            Assert.False(save.SemanticChanged);       // BASE03: layout save is not a semantic change
            Assert.Equal(originalDigest, save.SemanticDigest);

            var reopened = manager.Load();
            Assert.Equal(originalDigest, reopened.ComputeSemanticDigest());
            Assert.Equal(1234, reopened.Nodes.Single(n => n.NodeId == "n3").Layout!.X);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RubberBandSelect_SelectsIntersectingNodes()
    {
        var session = new DesignerSession(Project());
        // Nodes are at x = 0,220,440,660 (y=40), width 180 height 64.
        // Window covering first two nodes only.
        int hits = session.RubberBandSelect(-10, 0, 300, 100);
        Assert.Equal(2, hits);
        Assert.Contains("n0", session.SelectedNodeIds);
        Assert.Contains("n1", session.SelectedNodeIds);
        Assert.DoesNotContain("n2", session.SelectedNodeIds);
    }

    [Fact]
    public void Select_NonAdditive_ReplacesSelection()
    {
        var session = new DesignerSession(Project());
        session.Select("n0", additive: false);
        session.Select("n1", additive: false);
        Assert.Equal(new[] { "n1" }, session.SelectedNodeIds);
    }

    [Fact]
    public void ToggleSelection_AddsThenRemoves()
    {
        var session = new DesignerSession(Project());
        session.ToggleSelection("n2");
        Assert.Contains("n2", session.SelectedNodeIds);
        session.ToggleSelection("n2");
        Assert.DoesNotContain("n2", session.SelectedNodeIds);
    }

    [Fact]
    public void Connect_AddsEdge_AndIsIdempotent()
    {
        var session = new DesignerSession(Project());
        int before = session.Project.Edges.Count;
        session.Connect("n2", "n3");
        Assert.Equal(before + 1, session.Project.Edges.Count);
        session.Connect("n2", "n3"); // duplicate ignored
        Assert.Equal(before + 1, session.Project.Edges.Count);
    }

    [Fact]
    public void Connect_SelfLoop_Ignored()
    {
        var session = new DesignerSession(Project());
        int before = session.Project.Edges.Count;
        session.Connect("n1", "n1");
        Assert.Equal(before, session.Project.Edges.Count);
    }

    [Fact]
    public void ReconnectEdge_RetargetsExistingEdge()
    {
        var session = new DesignerSession(Project());
        Assert.True(session.ReconnectEdge("e0", "n2"));
        var edge = session.Project.Edges.Single(e => e.EdgeId == "e0");
        Assert.Equal("n2", edge.TargetNodeId);
    }

    [Fact]
    public void DeleteSelected_RemovesNodesAndIncidentEdges()
    {
        var session = new DesignerSession(Project());
        session.Select("n1", additive: false); // n1 is the target of e0
        int removed = session.DeleteSelected();
        Assert.Equal(1, removed);
        Assert.DoesNotContain(session.Project.Nodes, n => n.NodeId == "n1");
        Assert.DoesNotContain(session.Project.Edges, e => e.TargetNodeId == "n1" || e.SourceNodeId == "n1");
    }

    [Fact]
    public void AddNode_ThenUndo_Redo_RestoresDigest()
    {
        var session = new DesignerSession(Project());
        var digest0 = session.Project.ComputeSemanticDigest();

        session.AddNode("scale", "Extra", 0, 0);
        var digestAdded = session.Project.ComputeSemanticDigest();
        Assert.NotEqual(digest0, digestAdded);
        Assert.Equal(5, session.Project.Nodes.Count);

        Assert.True(session.CanUndo);
        session.Undo();
        Assert.Equal(4, session.Project.Nodes.Count);
        Assert.Equal(digest0, session.Project.ComputeSemanticDigest());

        Assert.True(session.CanRedo);
        session.Redo();
        Assert.Equal(5, session.Project.Nodes.Count);
        Assert.Equal(digestAdded, session.Project.ComputeSemanticDigest());
    }

    [Fact]
    public void DeleteSelected_IsUndoable()
    {
        var session = new DesignerSession(Project());
        var digest0 = session.Project.ComputeSemanticDigest();
        session.SelectAll();
        session.DeleteSelected();
        Assert.Empty(session.Project.Nodes);
        session.Undo();
        Assert.Equal(4, session.Project.Nodes.Count);
        Assert.Equal(digest0, session.Project.ComputeSemanticDigest());
    }

    [Fact]
    public void MoveNode_DoesNotAffectOtherNodes()
    {
        var session = new DesignerSession(Project());
        session.MoveNode("n0", 500, 500);
        // n1..n3 keep their original layout.
        Assert.Equal(220, session.Project.Nodes.Single(n => n.NodeId == "n1").Layout!.X);
        Assert.Equal(660, session.Project.Nodes.Single(n => n.NodeId == "n3").Layout!.X);
    }

    [Fact]
    public void DefaultDesktopProject_ValidatesThroughSharedJobService()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.ValidateCommand.Execute(null);

        Assert.Equal("Validation complete.", viewModel.StatusText);
        Assert.DoesNotContain(viewModel.Diagnostics, row => row.Severity == "error");
    }

    [Fact]
    public void FactPropertyEdit_IsOneUndoableChange()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactUnit = "EUR";
        viewModel.SelectedFactPeriodType = "instant";
        viewModel.SelectedFactDecimals = "-2";

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        Assert.Equal("EUR", fact.Parameters["unit"]);
        Assert.Equal("instant", fact.Parameters["periodType"]);
        Assert.Equal("-2", fact.Parameters["decimals"]);
        viewModel.Session.Undo();
        fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        Assert.Equal("USD", fact.Parameters["unit"]?.ToString());
        Assert.Equal("duration", fact.Parameters["periodType"]?.ToString());
        Assert.Equal("0", fact.Parameters["decimals"]?.ToString());
    }

    [Fact]
    public async Task DesktopOfflineSuggestion_ValidatesAndAppliesSourceAndFactTogether()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-ai", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var csv = System.IO.Path.Combine(dir, "source.csv");
        File.WriteAllText(csv, "Revenue\n1250\n");
        try
        {
            var viewModel = new MainWindowViewModel();
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));
            viewModel.Session.SetNodeParameter("sample-value", "column", "OldColumn");
            await viewModel.LoadSourceAsync(csv);
            Assert.Contains("Revenue", viewModel.SourceColumns);
            viewModel.SelectedSourceColumn = "Revenue";

            await viewModel.SuggestMappingsAsync();

            var suggestion = Assert.Single(viewModel.AiSuggestions,
                row => row.Concept == "synthetic:Revenue");
            viewModel.SelectedSuggestion = suggestion;
            viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
            var beforeApply = viewModel.Session.Project.ComputeSemanticDigest();
            viewModel.ApplySuggestedMappingCommand.Execute(null);

            Assert.Contains("Applied", viewModel.StatusText);
            var source = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-value");
            var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
            Assert.Equal("Revenue", source.Parameters["column"]?.ToString());
            Assert.Equal("synthetic:Revenue", fact.Parameters["concept"]?.ToString());
            viewModel.Session.Undo();
            Assert.Equal(beforeApply, viewModel.Session.Project.ComputeSemanticDigest());
        }
        finally
        {
            await DeleteDirectoryWhenReleasedAsync(dir);
        }
    }

    [Fact]
    public async Task StaleSqliteSchemaLoadCannotReplaceNewerSourceSelection()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-race", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var database = System.IO.Path.Combine(dir, "wide.sqlite");
        var csv = System.IO.Path.Combine(dir, "current.csv");
        await using (var db = new SqliteConnection($"Data Source={database}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            var columns = string.Join(",", Enumerable.Range(0, 900).Select(i => $"c{i} TEXT"));
            command.CommandText = $"CREATE TABLE wide ({columns});";
            await command.ExecuteNonQueryAsync();
        }
        File.WriteAllText(csv, "currentField\nvalue\n");
        try
        {
            var viewModel = new MainWindowViewModel();
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));
            var staleLoad = viewModel.LoadSourceAsync(database);
            await viewModel.LoadSourceAsync(csv);
            await staleLoad;
            await Task.Delay(100);
            Assert.Contains("currentField", viewModel.SourceColumns);
            Assert.DoesNotContain("c0", viewModel.SourceColumns);
            Assert.Equal("csv", viewModel.Session.Project.Bindings[0].Kind);
        }
        finally
        {
            await DeleteDirectoryWhenReleasedAsync(dir);
        }
    }

    [Fact]
    public async Task OpeningProjectInvalidatesPendingSourceSchemaLoad()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-open-race", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var database = System.IO.Path.Combine(dir, "wide.sqlite");
        var projectPath = System.IO.Path.Combine(dir, "mapping.xmap");
        await using (var db = new SqliteConnection($"Data Source={database}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            var columns = string.Join(",", Enumerable.Range(0, 900).Select(i => $"c{i} TEXT"));
            command.CommandText = $"CREATE TABLE wide ({columns});";
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            var viewModel = new MainWindowViewModel();
            viewModel.SaveProjectTo(projectPath);
            var originalKind = viewModel.Session.Project.Bindings[0].Kind;
            var staleLoad = viewModel.LoadSourceAsync(database);
            viewModel.LoadProject(projectPath);
            await staleLoad;
            await Task.Delay(100);
            Assert.Empty(viewModel.SourceTables);
            Assert.Empty(viewModel.SourceColumns);
            Assert.Equal(originalKind, viewModel.Session.Project.Bindings[0].Kind);
            Assert.Null(viewModel.Session.Project.Bindings[0].SchemaFingerprint);
        }
        finally
        {
            await DeleteDirectoryWhenReleasedAsync(dir);
        }
    }

    private static async Task DeleteDirectoryWhenReleasedAsync(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 50; attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (IOException) { await Task.Delay(100); }
        }
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
