using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.AI;
using XBRLMappingStudio.Engine;

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
                InputPorts = new() { new PortDefinition("in", PortShape.String, PortCardinality.Single) },
                OutputPorts = new() { new PortDefinition("out", PortShape.String, PortCardinality.Single) },
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

    private static MappingProject ProjectWithSource()
    {
        var project = Project();
        project.Bindings.Add(new SourceBinding
        {
            BindingId = "source",
            Kind = "csv",
            DisplayName = "Source"
        });
        return project;
    }

    private static void UpdateNode(MappingProject project, string nodeId, Func<MappingNode, MappingNode> update)
    {
        var index = project.Nodes.FindIndex(node => node.NodeId == nodeId);
        project.Nodes[index] = update(project.Nodes[index]);
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
    public void SetDestination_IsSemanticAndUndoable()
    {
        var session = new DesignerSession(Project());
        var before = session.Project.ComputeSemanticDigest();
        var businessKey = new List<string> { "fact_key" };
        var destination = new DestinationPlan
        {
            Kind = "sqlite",
            Locator = "target.db",
            Table = "facts",
            BusinessKey = businessKey
        };

        Assert.True(session.SetDestination(destination));
        Assert.NotEqual(before, session.Project.ComputeSemanticDigest());
        Assert.Equal("facts", session.Project.Destination?.Table);
        var after = session.Project.ComputeSemanticDigest();

        businessKey.Add("concept");
        Assert.Equal(new[] { "fact_key" }, session.Project.Destination?.BusinessKey);
        Assert.False(session.SetDestination(destination with { BusinessKey = new List<string> { "fact_key" } }));
        Assert.Equal(after, session.Project.ComputeSemanticDigest());

        session.Undo();
        Assert.Null(session.Project.Destination);
        Assert.Equal(before, session.Project.ComputeSemanticDigest());

        session.Redo();
        Assert.Equal("facts", session.Project.Destination?.Table);
        Assert.Equal(new[] { "fact_key" }, session.Project.Destination?.BusinessKey);
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
    public async Task UnsavedProjectPrompt_CancelPreservesWorkspaceForNewAndOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-lifecycle-cancel", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var targetPath = Path.Combine(dir, "target.xmap");
        var activePath = Path.Combine(dir, "active.xmap");
        try
        {
            new ProjectManager(targetPath).Save(Project());
            var dialog = new TestProjectFileDialog(null)
            {
                ProjectOpenPath = targetPath,
                UnsavedChoice = UnsavedProjectChoice.Cancel
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);
            viewModel.SaveProjectTo(activePath);
            var originalProjectId = viewModel.Session.Project.ProjectId;
            viewModel.Session.MoveNode("n0", 777, 888);

            await viewModel.NewProjectAsync();
            Assert.Equal(originalProjectId, viewModel.Session.Project.ProjectId);
            Assert.Equal(777, viewModel.Session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);
            Assert.True(viewModel.IsProjectDirty);

            await viewModel.OpenProjectAsync();
            Assert.Equal(originalProjectId, viewModel.Session.Project.ProjectId);
            Assert.Equal(777, viewModel.Session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);
            Assert.True(viewModel.IsProjectDirty);

            dialog.ProjectOpenPath = activePath;
            await viewModel.OpenProjectAsync();
            Assert.Equal(originalProjectId, viewModel.Session.Project.ProjectId);
            Assert.Equal(777, viewModel.Session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);
            Assert.True(viewModel.IsProjectDirty);
            Assert.Equal("Canceled reopen the current project; the active project is unchanged.", viewModel.StatusText);

            dialog.UnsavedChoice = UnsavedProjectChoice.Discard;
            await viewModel.CloseProjectAsync();
            Assert.Empty(viewModel.Session.Project.Nodes);
            Assert.Null(viewModel.ProjectPath);
            Assert.False(viewModel.IsProjectDirty);
            Assert.Equal("Project closed.", viewModel.StatusText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task UnsavedProjectPrompt_SaveThenNewWritesProjectBeforeReplacingWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-lifecycle-save", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var savedPath = Path.Combine(dir, "saved.xmap");
        try
        {
            var dialog = new TestProjectFileDialog(null)
            {
                ProjectSavePath = savedPath,
                UnsavedChoice = UnsavedProjectChoice.Save
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);
            var originalProjectId = viewModel.Session.Project.ProjectId;
            viewModel.Session.MoveNode("n1", 901, 902);

            await viewModel.NewProjectAsync();

            var saved = new ProjectManager(savedPath).Load();
            Assert.Equal(originalProjectId, saved.ProjectId);
            Assert.Equal(901, saved.Nodes.Single(node => node.NodeId == "n1").Layout!.X);
            Assert.NotEqual(originalProjectId, viewModel.Session.Project.ProjectId);
            Assert.Null(viewModel.ProjectPath);
            Assert.False(viewModel.IsProjectDirty);
            Assert.False(File.Exists(savedPath + ProjectManager.LockSuffix));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAs_RefusesToReplaceAnUnrelatedProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-lifecycle-saveas", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var activePath = Path.Combine(dir, "active.xmap");
        var existingPath = Path.Combine(dir, "existing.xmap");
        try
        {
            var existing = Project();
            new ProjectManager(existingPath).Save(existing);
            var dialog = new TestProjectFileDialog(null) { ProjectSavePath = existingPath };
            var viewModel = new MainWindowViewModel(Project(), dialog);
            viewModel.SaveProjectTo(activePath);
            viewModel.Session.MoveNode("n0", 45, 46);

            await viewModel.SaveProjectAsAsync();

            Assert.Equal(existing.ProjectId, new ProjectManager(existingPath).Load().ProjectId);
            Assert.Equal(activePath, viewModel.ProjectPath);
            Assert.True(viewModel.IsProjectDirty);
            Assert.Contains("avoid replacing it", viewModel.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OpeningProject_ReconnectsSavedWorkspaceSourceBinding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-lifecycle-source", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var sourcePath = Path.Combine(dir, "source.csv");
        var projectPath = Path.Combine(dir, "mapping.xmap");
        File.WriteAllText(sourcePath, "Revenue\n1250\n");
        try
        {
            var firstViewModel = new MainWindowViewModel(ProjectWithSource());
            firstViewModel.SaveProjectTo(projectPath);
            await firstViewModel.LoadSourceAsync(sourcePath);
            Assert.Equal("source.csv", firstViewModel.Session.Project.Bindings[0].Locator);
            firstViewModel.SaveProjectTo(projectPath);

            var dialog = new TestProjectFileDialog(null) { ProjectOpenPath = projectPath };
            var reopened = new MainWindowViewModel(Project(), dialog);
            await reopened.OpenProjectAsync();

            Assert.Contains("Revenue", reopened.SourceColumns);
            Assert.Equal("source", reopened.SelectedSourceBindingId);
            Assert.Equal(firstViewModel.Session.Project.ComputeSemanticDigest(), reopened.Session.Project.ComputeSemanticDigest());
            Assert.False(reopened.IsProjectDirty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
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
    public void Connect_AddsTypedEdge_AndIsIdempotent()
    {
        var session = new DesignerSession(Project());
        int before = session.Project.Edges.Count;
        Assert.True(session.Connect("n2", "n3", "out", "in"));
        Assert.Equal(before + 1, session.Project.Edges.Count);
        Assert.False(session.Connect("n2", "n3", "out", "in")); // duplicate ignored
        Assert.Equal(before + 1, session.Project.Edges.Count);
        var edge = session.Project.Edges.Single(candidate => candidate.SourceNodeId == "n2");
        Assert.Equal("out", edge.SourcePortName);
        Assert.Equal("in", edge.TargetPortName);
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
    public void Connect_RejectsIncompatibleShapeAndUnknownPort()
    {
        var project = Project();
        UpdateNode(project, "n2", node => node with
        {
            OutputPorts = new() { new PortDefinition("out", PortShape.Decimal, PortCardinality.Single) }
        });
        var session = new DesignerSession(project);
        var before = session.Project.Edges.Count;

        Assert.False(session.TryConnect("n2", "n3", "out", "in", out var shapeReason));
        Assert.Contains("Cannot connect Decimal", shapeReason, StringComparison.Ordinal);
        Assert.False(session.TryConnect("n2", "n3", "missing", "in", out var portReason));
        Assert.Contains("no output port", portReason, StringComparison.Ordinal);
        Assert.Equal(before, session.Project.Edges.Count);
    }

    [Fact]
    public void Connect_RejectsOccupiedInputAndCycle()
    {
        var session = new DesignerSession(Project());

        Assert.False(session.TryConnect("n2", "n1", "out", "in", out var occupiedReason));
        Assert.Contains("already has a connection", occupiedReason, StringComparison.Ordinal);
        Assert.True(session.Connect("n1", "n2", "out", "in"));
        Assert.False(session.TryConnect("n2", "n0", "out", "in", out var cycleReason));
        Assert.Contains("create a cycle", cycleReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Connect_RejectsCollectionToSingleInput()
    {
        var project = Project();
        UpdateNode(project, "n2", node => node with
        {
            OutputPorts = new() { new PortDefinition("out", PortShape.String, PortCardinality.Collection) }
        });
        var session = new DesignerSession(project);

        Assert.False(session.TryConnect("n2", "n3", "out", "in", out var reason));
        Assert.Contains("collection output", reason, StringComparison.Ordinal);
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
    public void ReconnectEdge_ChangesTargetPortAndRejectsOccupiedPort()
    {
        var project = Project();
        UpdateNode(project, "n2", node => node with { InputPorts = new()
        {
            new PortDefinition("in", PortShape.String, PortCardinality.Single),
            new PortDefinition("other", PortShape.String, PortCardinality.Single)
        } });
        UpdateNode(project, "n3", node => node with { InputPorts = new()
        {
            new PortDefinition("in", PortShape.String, PortCardinality.Single)
        } });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "e1", SourceNodeId = "n3", SourcePortName = "out",
            TargetNodeId = "n2", TargetPortName = "in"
        });
        var session = new DesignerSession(project);

        Assert.False(session.TryReconnectEdge("e0", "n2", "in", out var reason));
        Assert.Contains("already has a connection", reason, StringComparison.Ordinal);
        Assert.True(session.ReconnectEdge("e0", "n2", "other"));
        var edge = session.Project.Edges.Single(candidate => candidate.EdgeId == "e0");
        Assert.Equal("n2", edge.TargetNodeId);
        Assert.Equal("other", edge.TargetPortName);
    }

    [Fact]
    public void KeyboardNudge_MovesSelectionAsOneUndoableLayoutChange()
    {
        var session = new DesignerSession(Project());
        var semanticDigest = session.Project.ComputeSemanticDigest();
        session.Select("n0", additive: false);
        session.Select("n2", additive: true);

        Assert.True(session.MoveSelectedBy(10, -10));
        Assert.Equal(new LayoutPosition(10, 30), session.Project.Nodes.Single(node => node.NodeId == "n0").Layout);
        Assert.Equal(new LayoutPosition(450, 30), session.Project.Nodes.Single(node => node.NodeId == "n2").Layout);
        Assert.Equal(semanticDigest, session.Project.ComputeSemanticDigest());

        session.Undo();
        Assert.Equal(new LayoutPosition(0, 40), session.Project.Nodes.Single(node => node.NodeId == "n0").Layout);
        Assert.Equal(new LayoutPosition(440, 40), session.Project.Nodes.Single(node => node.NodeId == "n2").Layout);
        session.Redo();
        Assert.Equal(new LayoutPosition(10, 30), session.Project.Nodes.Single(node => node.NodeId == "n0").Layout);
        Assert.Equal(semanticDigest, session.Project.ComputeSemanticDigest());
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
        viewModel.AllowSyntheticProfile = true;

        viewModel.ValidateCommand.Execute(null);

        Assert.Equal("Validation complete.", viewModel.StatusText);
        Assert.DoesNotContain(viewModel.Diagnostics, row => row.Severity == "error");
    }

    [Fact]
    public void DefaultDesktopProject_RequiresSyntheticProfileOptInForValidation()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.ValidateCommand.Execute(null);

        Assert.Equal("Operation blocked by filing-profile resolution.", viewModel.StatusText);
        Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains("XBR017", StringComparison.Ordinal));
    }

    [Fact]
    public void DesktopBlocksUnknownProfileBeforeProfileBoundValidation()
    {
        var source = Project();
        var project = new MappingProject
        {
            ProjectId = source.ProjectId,
            Name = source.Name,
            ProfileId = "uninstalled-desktop-profile",
            ProfileDigest = "sha256:missing",
            SchemaVersion = source.SchemaVersion,
            FunctionCatalogVersion = source.FunctionCatalogVersion,
            SemanticRevision = source.SemanticRevision,
            LayoutGeneration = source.LayoutGeneration,
            Metadata = source.Metadata,
            Nodes = source.Nodes,
            Edges = source.Edges,
            Bindings = source.Bindings,
            Functions = source.Functions,
            Approvals = source.Approvals
        };
        var viewModel = new MainWindowViewModel(project, null,
            (_, _) => Task.FromException<XBRLMappingStudio.Adapters.ISourceSchema>(
                new InvalidOperationException("Source inspection must not run.")),
            FilingProfileResolver.DevelopmentOnly());

        viewModel.ValidateCommand.Execute(null);

        Assert.Equal("Operation blocked by filing-profile resolution.", viewModel.StatusText);
        Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains("XBR015", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.Diagnostics, row => row.Display.Contains("Source inspection must not run.", StringComparison.Ordinal));
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
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));
            viewModel.Session.SetNodeParameter("sample-value", "column", "OldColumn");
            await viewModel.LoadSourceAsync(csv);
            Assert.Contains("Revenue", viewModel.SourceColumns);
            viewModel.SelectedSourceColumn = "Revenue";
            viewModel.Session.Select("sample-fact", additive: false);

            await viewModel.SuggestMappingsAsync();

            var suggestion = Assert.Single(viewModel.AiSuggestions,
                row => row.Concept == "synthetic:Revenue");
            Assert.Contains("Revenue (", suggestion.Display);
            Assert.Contains("→ synthetic:Revenue", suggestion.Display);
            Assert.Contains("rank", suggestion.Display);
            Assert.Contains("not confidence", suggestion.Display);
            Assert.Contains("evidence local-name", suggestion.Display);
            Assert.Contains("urn:synthetic:finance:1.0", suggestion.TaxonomyEvidenceDisplay);
            Assert.Contains(SyntheticProfile.PresentationRole, suggestion.TaxonomyEvidenceDisplay);
            Assert.Contains("scale: NeedsReview", suggestion.RecommendationDisplay);
            Assert.Contains("entity: RunContext", suggestion.RecommendationDisplay);
            Assert.Contains("period: Provided (duration)", suggestion.RecommendationDisplay);
            Assert.Contains("dimensions: NeedsReview", suggestion.RecommendationDisplay);
            Assert.Contains("none proposed", suggestion.RecommendationDisplay);
            viewModel.SelectedSuggestion = suggestion;
            var beforeApply = viewModel.Session.Project.ComputeSemanticDigest();
            viewModel.ApplySuggestedMappingCommand.Execute(null);

            Assert.Contains("Accepted", viewModel.StatusText);
            var source = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-value");
            var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
            Assert.Equal("Revenue", source.Parameters["column"]?.ToString());
            Assert.Equal("synthetic:Revenue", fact.Parameters["concept"]?.ToString());
            viewModel.UndoCommand.Execute(null);
            Assert.Equal(beforeApply, viewModel.Session.Project.ComputeSemanticDigest());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopNoMatch_IsExplicitAndLeavesMappingUnassigned()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-ai-no-match", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var csv = System.IO.Path.Combine(dir, "source.csv");
        File.WriteAllText(csv, "Internal compliance reference\nREF-123\n");
        try
        {
            var viewModel = new MainWindowViewModel();
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));
            await viewModel.LoadSourceAsync(csv);
            viewModel.SelectedSourceColumn = "Internal compliance reference";
            viewModel.Session.Select("sample-fact", additive: false);
            var before = viewModel.Session.Project.ComputeSemanticDigest();

            await viewModel.SuggestMappingsAsync();

            Assert.Empty(viewModel.AiSuggestions);
            Assert.Contains("No match for 'Internal compliance reference'", viewModel.StatusText);
            Assert.Contains("remains unassigned", viewModel.StatusText);
            Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopSuggestionWorkflow_BlocksAcceptanceAfterTargetFactChanges()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-ai-target", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var csv = System.IO.Path.Combine(dir, "source.csv");
        File.WriteAllText(csv, "Revenue\n1250\n");
        try
        {
            var seed = new MainWindowViewModel();
            var project = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(seed.Session.Project));
            var originalFact = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var alternativeFact = originalFact.ShallowCopy() with
            {
                NodeId = "alternative-fact",
                DisplayName = "Alternative fact",
                Layout = new LayoutPosition(500, 300)
            };
            project.Nodes.Add(alternativeFact);

            var viewModel = new MainWindowViewModel(project);
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));
            await viewModel.LoadSourceAsync(csv);
            viewModel.SelectedSourceColumn = "Revenue";
            viewModel.Session.Select(originalFact.NodeId, additive: false);
            await viewModel.SuggestMappingsAsync();

            var suggestion = Assert.Single(viewModel.AiSuggestions,
                row => row.Concept == "synthetic:Revenue");
            Assert.Equal(originalFact.NodeId, suggestion.TargetNodeId);
            viewModel.SelectedSuggestion = suggestion;
            var before = viewModel.Session.Project.ComputeSemanticDigest();

            viewModel.Session.Select(alternativeFact.NodeId, additive: false);
            viewModel.ApplySuggestedMappingCommand.Execute(null);

            Assert.Contains("target fact node changed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.Equal(SuggestionState.Pending, suggestion.ReviewState);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopSuggestionWorkflow_PersistsEditAcceptUndoAndRedo()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-audit-", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbPath = System.IO.Path.Combine(dir, "state.db");
            var store = new SuggestionAuditStore(new LocalApplicationDatabase(dbPath));
            var viewModel = await PrepareSuggestionViewModelAsync(dir, () => store);
            var projectId = viewModel.Session.Project.ProjectId;
            var originalRevision = viewModel.Session.Project.SemanticRevision;
            var beforeAccept = viewModel.Session.Project.ComputeSemanticDigest();
            var approvedRevision = new ApprovalRecord
            {
                SemanticRevision = originalRevision,
                SemanticDigest = beforeAccept,
                ApprovedBy = "prior-reviewer",
                ApprovedUtc = DateTimeOffset.Parse("2026-11-04T12:00:00Z"),
                ProfileDigest = viewModel.Session.Project.ProfileDigest
            };
            viewModel.Session.Project.Approvals.Add(approvedRevision);
            var restartedStore = new SuggestionAuditStore(new LocalApplicationDatabase(dbPath));

            viewModel.SelectedSuggestionConcept = "synthetic:NetIncome";
            viewModel.EditSelectedSuggestionCommand.Execute(null);
            Assert.Contains("saved", viewModel.StatusText.ToLowerInvariant());
            viewModel.ApplySuggestedMappingCommand.Execute(null);

            Assert.Contains("Accepted", viewModel.StatusText);
            Assert.Equal("synthetic:NetIncome", viewModel.Session.Project.Nodes
                .Single(node => node.NodeId == "sample-fact").Parameters["concept"]?.ToString());
            Assert.Equal(originalRevision, viewModel.Session.Project.SemanticRevision);
            Assert.Equal(new[] { approvedRevision }, viewModel.Session.Project.Approvals);
            var decisions = restartedStore.Read(projectId, originalRevision);
            Assert.Contains(decisions, row => row.Decision == "edit"
                && row.Actor == Environment.UserName
                && row.SourceFieldRef == $"{viewModel.Session.Project.Bindings.Single().BindingId}:Revenue");
            Assert.Contains(decisions, row => row.Decision == "accept"
                && row.AcceptedRevision == originalRevision
                && row.ApprovedEditJson.Contains("synthetic:NetIncome", StringComparison.Ordinal));

            viewModel.UndoCommand.Execute(null);
            Assert.Equal(beforeAccept, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.Equal(originalRevision, viewModel.Session.Project.SemanticRevision);
            Assert.Equal(new[] { approvedRevision }, viewModel.Session.Project.Approvals);
            Assert.Equal(SuggestionState.Pending, viewModel.SelectedSuggestion!.ReviewState);
            Assert.Contains(restartedStore.Read(projectId, originalRevision), row => row.Decision == "undo");

            viewModel.RedoCommand.Execute(null);
            Assert.Equal("synthetic:NetIncome", viewModel.Session.Project.Nodes
                .Single(node => node.NodeId == "sample-fact").Parameters["concept"]?.ToString());
            Assert.Equal(originalRevision, viewModel.Session.Project.SemanticRevision);
            Assert.Equal(new[] { approvedRevision }, viewModel.Session.Project.Approvals);
            Assert.Equal(2, restartedStore.Read(projectId, originalRevision).Count(row => row.Decision == "accept"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopSuggestionWorkflow_RejectPersistsWithoutChangingProject()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-audit-", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SuggestionAuditStore(new LocalApplicationDatabase(System.IO.Path.Combine(dir, "state.db")));
            var viewModel = await PrepareSuggestionViewModelAsync(dir, () => store);
            var digest = viewModel.Session.Project.ComputeSemanticDigest();
            var revision = viewModel.Session.Project.SemanticRevision;

            viewModel.RejectSelectedSuggestionCommand.Execute(null);

            Assert.Equal(digest, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.Equal(SuggestionState.Rejected, viewModel.SelectedSuggestion!.ReviewState);
            var row = Assert.Single(store.Read(viewModel.Session.Project.ProjectId, revision));
            Assert.Equal("reject", row.Decision);
            Assert.Equal($"{viewModel.Session.Project.Bindings.Single().BindingId}:Revenue", row.SourceFieldRef);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopSuggestionWorkflow_AuditFailureBlocksGraphAcceptance()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-audit-", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SuggestionAuditStore(new LocalApplicationDatabase(System.IO.Path.Combine(dir, "state.db")));
            var sink = new FailAcceptAuditSink(store);
            var viewModel = await PrepareSuggestionViewModelAsync(dir, () => sink);
            var digest = viewModel.Session.Project.ComputeSemanticDigest();

            viewModel.ApplySuggestedMappingCommand.Execute(null);

            Assert.Contains("rolled back", viewModel.StatusText.ToLowerInvariant());
            Assert.Equal(digest, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.Equal(SuggestionState.Pending, viewModel.SelectedSuggestion!.ReviewState);
            Assert.DoesNotContain(store.Read(viewModel.Session.Project.ProjectId,
                viewModel.Session.Project.SemanticRevision), row => row.Decision == "accept");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<MainWindowViewModel> PrepareSuggestionViewModelAsync(
        string directory, Func<IAuditSink> auditSinkFactory)
    {
        var csv = System.IO.Path.Combine(directory, "source.csv");
        File.WriteAllText(csv, "Revenue\n1250\n");
        var viewModel = new MainWindowViewModel(auditSinkFactory);
        viewModel.AllowSyntheticProfile = true;
        viewModel.SaveProjectTo(System.IO.Path.Combine(directory, "mapping.xmap"));
        await viewModel.LoadSourceAsync(csv);
        viewModel.SelectedSourceColumn = "Revenue";
        viewModel.Session.Select("sample-fact", additive: false);
        await viewModel.SuggestMappingsAsync();
        viewModel.SelectedSuggestion = Assert.Single(viewModel.AiSuggestions,
            row => row.Concept == "synthetic:Revenue");
        return viewModel;
    }

    private sealed class FailAcceptAuditSink(IAuditSink inner) : IAuditSink
    {
        public void Append(AuditRecord record, AuditContext context)
        {
            if (record.Action == "accept") throw new IOException("simulated locked audit database");
            inner.Append(record, context);
        }
    }

    [Fact]
    public async Task DesktopLoadsPinnedAcceptanceWorkbookAndShowsSourceColumns()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-fixture", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = IndependentAcceptanceFixtures.Materialize(dir, "revenue.xlsx");
            var viewModel = new MainWindowViewModel();
            viewModel.AllowSyntheticProfile = true;
            var binding = Assert.Single(viewModel.Session.Project.Bindings);
            viewModel.Session.SetBinding(binding with
            {
                Configuration = new Dictionary<string, object?> { ["sheet"] = "Financials", ["headerRow"] = "3" }
            });
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));
            var sourceColumnNotification = false;
            viewModel.SourceColumns.CollectionChanged += (_, _) => sourceColumnNotification = true;

            await viewModel.LoadSourceAsync(workbook);

            Assert.Contains("Current", viewModel.SourceColumns);
            Assert.Contains("Prior", viewModel.SourceColumns);
            Assert.True(sourceColumnNotification);
            Assert.Equal("Loaded 4 fields from revenue.xlsx.", viewModel.StatusText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GraphSelectionUpdatesSelectedNodeUsedBySuggestionReview()
    {
        var viewModel = new MainWindowViewModel();
        var factNode = Assert.Single(viewModel.Nodes, node => node.NodeType == NodeTypeConstants.FactConstruct);

        viewModel.Session.Select(factNode.NodeId, additive: false);

        Assert.Equal(factNode.NodeId, viewModel.SelectedNode?.NodeId);
        Assert.True(viewModel.CanEditSelectedFact);

        viewModel.Session.ClearSelection();

        Assert.Null(viewModel.SelectedNode);
        Assert.False(viewModel.CanEditSelectedFact);
    }

    [Fact]
    public async Task StaleSqliteSchemaLoadCannotReplaceNewerSourceSelection()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-race", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var database = System.IO.Path.Combine(dir, "wide.sqlite");
        var csv = System.IO.Path.Combine(dir, "current.csv");
        await using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE wide (value TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        File.WriteAllText(csv, "currentField\nvalue\n");
        var schemaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSchema = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            async Task<XBRLMappingStudio.Adapters.ISourceSchema> LoadSchemaAsync(
                XBRLMappingStudio.Adapters.SourceConnection connection, CancellationToken cancellationToken)
            {
                schemaEntered.TrySetResult();
                await releaseSchema.Task.WaitAsync(cancellationToken);
                return await new XBRLMappingStudio.Adapters.Sqlite.SqliteSourceAdapter()
                    .InspectSchemaAsync(connection, cancellationToken);
            }
            var viewModel = new MainWindowViewModel(ProjectWithSource(), null, LoadSchemaAsync);
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));
            var executeDisabledDuringLoad = false;
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(viewModel.IsSourceLoading) && viewModel.IsSourceLoading)
                    executeDisabledDuringLoad = !viewModel.ExecuteCommand.CanExecute(null);
            };
            var staleLoad = viewModel.LoadSourceAsync(database);
            await schemaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(staleLoad.IsCompleted);
            await viewModel.LoadSourceAsync(csv);
            releaseSchema.TrySetResult();
            await staleLoad;
            Assert.Contains("currentField", viewModel.SourceColumns);
            Assert.DoesNotContain("value", viewModel.SourceColumns);
            Assert.Equal("csv", viewModel.Session.Project.Bindings[0].Kind);
            Assert.True(executeDisabledDuringLoad);
            Assert.False(viewModel.IsSourceLoading);
        }
        finally
        {
            releaseSchema.TrySetResult();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteDesignerReportsDetectedPrimaryKeyForStableRowIdentity()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-sqlite-key", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var database = System.IO.Path.Combine(dir, "ledger.sqlite");
        await using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (account TEXT PRIMARY KEY, amount TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            var project = ProjectWithSource();
            project.Bindings[0] = project.Bindings[0] with
            {
                Configuration = new Dictionary<string, object?> { ["decimalColumns"] = "amount" }
            };
            var viewModel = new MainWindowViewModel(project);
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));

            await viewModel.LoadSourceAsync(database);

            Assert.Contains("account", viewModel.SourceColumns);
            Assert.Contains("Primary key: account", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("stable row identity enabled", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("Row identity: sqlite-primary-key-sha256", viewModel.SourceInspectionSummary, StringComparison.Ordinal);
            Assert.Contains("Exact decimal overrides: amount", viewModel.SourceInspectionSummary, StringComparison.Ordinal);
            Assert.Contains("Decimal text culture: InvariantCulture", viewModel.SourceInspectionSummary, StringComparison.Ordinal);
            Assert.Contains("writing is unsupported", viewModel.SourceInspectionSummary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SourceCannotBeLoadedBeforeProjectHasWorkspace()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-unsaved", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var csv = System.IO.Path.Combine(dir, "source.csv");
        File.WriteAllText(csv, "value\n1\n");
        try
        {
            var viewModel = new MainWindowViewModel(ProjectWithSource());
            await viewModel.LoadSourceAsync(csv);

            Assert.Equal("Save the project inside its workspace before adding a source.", viewModel.StatusText);
            Assert.False(viewModel.IsSourceLoading);
            Assert.Empty(viewModel.SourceColumns);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task OpeningProjectInvalidatesPendingSourceSchemaLoad()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-open-race", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var database = System.IO.Path.Combine(dir, "wide.sqlite");
        var projectPath = System.IO.Path.Combine(dir, "mapping.xmap");
        await using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE wide (value TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        var schemaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSchema = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            async Task<XBRLMappingStudio.Adapters.ISourceSchema> LoadSchemaAsync(
                XBRLMappingStudio.Adapters.SourceConnection connection, CancellationToken cancellationToken)
            {
                schemaEntered.TrySetResult();
                await releaseSchema.Task.WaitAsync(cancellationToken);
                return await new XBRLMappingStudio.Adapters.Sqlite.SqliteSourceAdapter()
                    .InspectSchemaAsync(connection, cancellationToken);
            }
            var viewModel = new MainWindowViewModel(ProjectWithSource(), null, LoadSchemaAsync);
            viewModel.SaveProjectTo(projectPath);
            var originalKind = viewModel.Session.Project.Bindings[0].Kind;
            var executeDisabledDuringLoad = false;
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(viewModel.IsSourceLoading) && viewModel.IsSourceLoading)
                    executeDisabledDuringLoad = !viewModel.ExecuteCommand.CanExecute(null);
            };
            var staleLoad = viewModel.LoadSourceAsync(database);
            await schemaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(staleLoad.IsCompleted);
            viewModel.LoadProject(projectPath);
            releaseSchema.TrySetResult();
            await staleLoad;
            Assert.Empty(viewModel.SourceTables);
            Assert.Empty(viewModel.SourceColumns);
            Assert.DoesNotContain("value", viewModel.SourceColumns);
            Assert.Equal(originalKind, viewModel.Session.Project.Bindings[0].Kind);
            Assert.Null(viewModel.Session.Project.Bindings[0].SchemaFingerprint);
            Assert.True(executeDisabledDuringLoad);
            Assert.False(viewModel.IsSourceLoading);
        }
        finally
        {
            releaseSchema.TrySetResult();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopDestinationRequiresSchemaReviewDryRunAndFinalConfirmation()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-destination", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var workbook = TestFixtures.MaterializeWorkbook(dir);
        var projectPath = System.IO.Path.Combine(dir, "mapping.xmap");
        var database = System.IO.Path.Combine(dir, "target.sqlite");
        await CreateFactDatabaseAsync(database);
        var project = TestFixtures.BuildRevenueMappingProject(workbook, approved: true);
        var dialog = new TestProjectFileDialog(database);
        try
        {
            var viewModel = new MainWindowViewModel(project, dialog);
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(projectPath);
            await viewModel.LoadDestinationAsync(database);

            Assert.Equal("facts", viewModel.DestinationTable);
            Assert.Equal(new[] { "fact_key" }, viewModel.SelectedDestinationKey?.Columns);
            Assert.True(viewModel.DestinationSchemaIsValid);
            Assert.False(viewModel.CanSaveDestinationPlan);

            viewModel.IsDestinationSchemaConfirmed = true;
            Assert.True(viewModel.CanSaveDestinationPlan);
            var oldDigest = viewModel.Session.Project.ComputeSemanticDigest();
            viewModel.SaveDestinationPlanCommand.Execute(null);

            Assert.NotEqual(oldDigest, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.Contains("must approve this destination-bound revision", viewModel.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain(viewModel.Session.Project.Approvals,
                approval => approval.SemanticDigest == viewModel.Session.Project.ComputeSemanticDigest());

            var before = await File.ReadAllBytesAsync(database);
            await viewModel.PreviewDestinationAsync();
            Assert.Contains("Dry run complete: 1 insert(s)", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("No database rows were written", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("Candidate estimate: 1 insert(s)", viewModel.DestinationPreviewSummary, StringComparison.Ordinal);
            Assert.Contains("Sample: row 1 insert", viewModel.DestinationPreviewSummary, StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(database));

            viewModel.Session.Project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = viewModel.Session.Project.SemanticRevision,
                SemanticDigest = viewModel.Session.Project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = SyntheticProfile.Build().Digest
            });
            viewModel.SaveProjectTo(projectPath);
            Assert.True(viewModel.CanPublishDestination);

            viewModel.DestinationTable = null;
            Assert.False(viewModel.CanPublishDestination);
            await viewModel.LoadDestinationAsync(database);
            Assert.False(viewModel.IsDestinationSchemaConfirmed);
            viewModel.IsDestinationSchemaConfirmed = true;
            Assert.True(viewModel.CanPublishDestination);

            dialog.ConfirmationResult = false;
            await viewModel.PublishDestinationAsync();
            Assert.Contains("Publication cancelled", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(database));

            dialog.ConfirmationResult = true;
            await viewModel.PublishDestinationAsync();
            Assert.Equal(2, dialog.ConfirmationCount);
            Assert.Equal("facts", dialog.ConfirmedTable);
            Assert.Equal(1, dialog.ConfirmedFactCount);
            Assert.Equal(new[] { "fact_key" }, dialog.ConfirmedBusinessKey);
            Assert.Contains("Published 1 fact row(s)", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("receipt xms-", viewModel.StatusText, StringComparison.Ordinal);

            await using var published = new SqliteConnection($"Data Source={database};Pooling=False;Mode=ReadOnly");
            await published.OpenAsync();
            await using var count = published.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM facts";
            Assert.Equal(1L, await count.ExecuteScalarAsync());
            count.CommandText = "SELECT COUNT(*) FROM xms_idempotency_receipts";
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopFileDestinationRequiresPathReviewDryRunAndFinalConfirmation()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-file-destination", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var workbook = TestFixtures.MaterializeWorkbook(dir);
        var projectPath = System.IO.Path.Combine(dir, "mapping.xmap");
        var outputPath = System.IO.Path.Combine(dir, "facts.json");
        var project = TestFixtures.BuildRevenueMappingProject(workbook, approved: true);
        var dialog = new TestProjectFileDialog(null) { FileDestinationPath = outputPath };
        try
        {
            var viewModel = new MainWindowViewModel(project, dialog);
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(projectPath);
            viewModel.SelectedDestinationKind = viewModel.DestinationKindOptions.Single(option => option.Kind == "json");
            await viewModel.ChooseDestinationAsync();

            Assert.True(viewModel.IsFileDestination);
            Assert.True(viewModel.DestinationSchemaIsValid);
            Assert.False(viewModel.CanSaveDestinationPlan);
            Assert.False(File.Exists(outputPath));

            viewModel.IsDestinationSchemaConfirmed = true;
            Assert.True(viewModel.CanSaveDestinationPlan);
            var priorDigest = viewModel.Session.Project.ComputeSemanticDigest();
            viewModel.SaveDestinationPlanCommand.Execute(null);

            var savedPlan = viewModel.Session.Project.Destination;
            Assert.NotNull(savedPlan);
            Assert.Equal("json", savedPlan.Kind);
            Assert.Equal("json-records-v1", savedPlan.Format);
            Assert.Equal("replace", savedPlan.WriteMode);
            Assert.Empty(savedPlan.BusinessKey);
            Assert.NotEqual(priorDigest, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.DoesNotContain(viewModel.Session.Project.Approvals,
                approval => approval.SemanticDigest == viewModel.Session.Project.ComputeSemanticDigest());

            await viewModel.PreviewDestinationAsync();
            Assert.Contains("Dry run complete", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("No file was written", viewModel.StatusText, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));

            viewModel.Session.Project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = viewModel.Session.Project.SemanticRevision,
                SemanticDigest = viewModel.Session.Project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = SyntheticProfile.Build().Digest
            });
            viewModel.SaveProjectTo(projectPath);
            Assert.True(viewModel.CanPublishDestination);

            dialog.ConfirmationResult = false;
            await viewModel.PublishDestinationAsync();
            Assert.Contains("Publication cancelled", viewModel.StatusText, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));

            dialog.ConfirmationResult = true;
            await viewModel.PublishDestinationAsync();
            Assert.Equal(2, dialog.ConfirmationCount);
            Assert.Equal(outputPath, dialog.ConfirmedFilePath);
            Assert.Equal("json-records-v1", dialog.ConfirmedFileFormat);
            Assert.Contains("Published json file", viewModel.StatusText, StringComparison.Ordinal);
            using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
            var fact = Assert.Single(report.RootElement.EnumerateArray());
            Assert.Equal("1250000", fact.GetProperty("value").GetRawText());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AdapterCapabilities_AreVisibleBeforeSourceAndTargetSelection()
    {
        var viewModel = new MainWindowViewModel(Project());

        Assert.Contains("Excel/CSV/JSON/XML/xBRL-JSON/SQLite", viewModel.SourceCapabilitySummary,
            StringComparison.Ordinal);
        Assert.Contains("Digital PDF (inspect, sample, read, lineage)", viewModel.SourceCapabilitySummary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PDF-01 through PDF-08", viewModel.SourceCapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("Oracle database", viewModel.SourceCapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("inspect, sample, read, lineage", viewModel.SourceCapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("Ready: SQLite (transactional fact upsert)", viewModel.TargetCapabilitySummary,
            StringComparison.Ordinal);
        Assert.Contains("CSV/Excel/JSON/XML (staged output, publish, lineage)",
            viewModel.TargetCapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("xBRL-JSON", viewModel.TargetCapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("input only", viewModel.TargetCapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("PDF output", viewModel.TargetCapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("Oracle database", viewModel.TargetCapabilitySummary, StringComparison.Ordinal);

        Assert.Equal(new[] { "sqlite", "csv", "excel", "json", "xml" },
            viewModel.DestinationKindOptions.Select(option => option.Kind));
    }

    [Fact]
    public void UnsupportedDestinationSelection_IsRefusedWithVisibleCapabilityReason()
    {
        var viewModel = new MainWindowViewModel(Project());
        var selected = viewModel.SelectedDestinationKind;

        viewModel.SelectedDestinationKind = new DestinationKindOption("oracle", "Oracle database");

        Assert.Equal(selected, viewModel.SelectedDestinationKind);
        Assert.Contains("Cannot use Oracle database as a target", viewModel.DestinationCapabilityStatus,
            StringComparison.Ordinal);
        Assert.Contains("not implemented or qualified", viewModel.DestinationCapabilityStatus,
            StringComparison.Ordinal);
        Assert.Equal(viewModel.DestinationCapabilityStatus, viewModel.StatusText);
    }

    private static async Task CreateFactDatabaseAsync(string path)
    {
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE facts (
                fact_key TEXT NOT NULL PRIMARY KEY,
                profile_digest TEXT NOT NULL,
                concept TEXT NOT NULL,
                entity_scheme TEXT NOT NULL,
                entity_identifier TEXT NOT NULL,
                period_kind TEXT NOT NULL,
                instant_date TEXT NULL,
                period_start TEXT NULL,
                period_end TEXT NULL,
                unit TEXT NULL,
                compound_unit TEXT NULL,
                value TEXT NULL,
                text_value TEXT NULL,
                is_nil INTEGER NOT NULL,
                decimals INTEGER NULL,
                language TEXT NULL,
                dimensions_json TEXT NOT NULL,
                provenance_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestProjectFileDialog(string? destinationPath) : IProjectFileDialog
    {
        public string? ProjectOpenPath { get; set; }
        public string? ProjectSavePath { get; set; }
        public UnsavedProjectChoice UnsavedChoice { get; set; } = UnsavedProjectChoice.Cancel;
        public string? FileDestinationPath { get; set; }
        public bool ConfirmationResult { get; set; }
        public int ConfirmationCount { get; private set; }
        public string? ConfirmedTable { get; private set; }
        public IReadOnlyList<string> ConfirmedBusinessKey { get; private set; } = Array.Empty<string>();
        public int ConfirmedFactCount { get; private set; }
        public string? ConfirmedFilePath { get; private set; }
        public string? ConfirmedFileFormat { get; private set; }

        public Task<string?> OpenProjectAsync() => Task.FromResult(ProjectOpenPath);
        public Task<string?> SaveProjectAsync() => Task.FromResult(ProjectSavePath);
        public Task<UnsavedProjectChoice> ConfirmUnsavedProjectChangesAsync(string projectName, string action)
            => Task.FromResult(UnsavedChoice);
        public Task<string?> SaveReportAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenSourceAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenDestinationDatabaseAsync() => Task.FromResult(destinationPath);
        public Task<string?> SaveDestinationFileAsync(string kind) => Task.FromResult(FileDestinationPath);

        public Task<bool> ConfirmDestinationPublicationAsync(
            string databasePath,
            string table,
            IReadOnlyList<string> businessKey,
            int factCount,
            string semanticDigest)
        {
            ConfirmationCount++;
            ConfirmedTable = table;
            ConfirmedBusinessKey = businessKey.ToArray();
            ConfirmedFactCount = factCount;
            return Task.FromResult(ConfirmationResult);
        }

        public Task<bool> ConfirmFileDestinationPublicationAsync(
            string destinationPath,
            string format,
            int factCount,
            string semanticDigest)
        {
            ConfirmationCount++;
            ConfirmedFilePath = destinationPath;
            ConfirmedFileFormat = format;
            ConfirmedFactCount = factCount;
            return Task.FromResult(ConfirmationResult);
        }
    }
}
