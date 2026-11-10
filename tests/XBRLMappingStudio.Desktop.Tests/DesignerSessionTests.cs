using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.AI;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Xbrl;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

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

    private static MappingProject ProjectWithTextFactInput()
    {
        var project = new MainWindowViewModel().Session.Project;
        var fact = project.Nodes.Single(node => node.NodeId == "sample-fact");
        project.Edges.RemoveAll(edge => edge.TargetNodeId == fact.NodeId
            && (edge.TargetPortName is "value" or "values"));
        project.Edges.Add(new MappingEdge
        {
            EdgeId = Id.New(),
            SourceNodeId = "sample-value",
            SourcePortName = "textValue",
            TargetNodeId = fact.NodeId,
            TargetPortName = "textValue"
        });
        return project;
    }

    private static MappingProject TextFactProject()
    {
        var project = ProjectWithTextFactInput();
        var fact = project.Nodes.Single(node => node.NodeId == "sample-fact");
        fact.Parameters["concept"] = SyntheticProfile.Qualified("EntityRegistrantName");
        fact.Parameters.Remove("unit");
        fact.Parameters.Remove("decimals");
        fact.Parameters["language"] = "en";
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
    public void InsertNodeOnEdge_IsOneValidatedUndoableEdit()
    {
        var session = new DesignerSession(Project());
        var before = ProjectSerializer.Serialize(session.Project);
        var inserted = new MappingNode
        {
            NodeId = "inserted-transform",
            NodeType = "test-transform",
            DisplayName = "Inserted transform",
            InputPorts = new() { new PortDefinition("input", PortShape.String, PortCardinality.Single) },
            OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) }
        };

        Assert.True(session.TryInsertNodeOnEdge("e0", inserted, "input", "value", out var refusal));
        Assert.Null(refusal);
        Assert.Equal(5, session.Project.Nodes.Count);
        Assert.Equal(2, session.Project.Edges.Count);
        Assert.Contains(session.Project.Edges, edge => edge.TargetNodeId == inserted.NodeId);
        Assert.Contains(session.Project.Edges, edge => edge.SourceNodeId == inserted.NodeId);

        session.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.True(session.CanRedo);
        session.Redo();
        Assert.Equal(5, session.Project.Nodes.Count);
        Assert.Equal(2, session.Project.Edges.Count);
    }

    [Fact]
    public void InsertNodeOnEdge_RejectsIncompatiblePortsWithoutChangingGraph()
    {
        var session = new DesignerSession(Project());
        var before = ProjectSerializer.Serialize(session.Project);
        var incompatible = new MappingNode
        {
            NodeId = "incompatible-transform",
            NodeType = "test-transform",
            DisplayName = "Incompatible transform",
            InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
            OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) }
        };

        Assert.False(session.TryInsertNodeOnEdge("e0", incompatible, "input", "value", out var refusal));
        Assert.Contains("incompatible", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void InsertNodeWithCompanions_IsValidatedAndUndoesAsOneGraphEdit()
    {
        var project = Project();
        project.Nodes.Clear();
        project.Edges.Clear();
        var decimalPort = new PortDefinition("value", PortShape.Decimal, PortCardinality.Single);
        var input = new MappingNode
        {
            NodeId = "function-input",
            NodeType = "functionInput",
            DisplayName = "Input",
            OutputPorts = new() { decimalPort }
        };
        var output = new MappingNode
        {
            NodeId = "function-output",
            NodeType = "functionOutput",
            DisplayName = "Output",
            InputPorts = new() { decimalPort }
        };
        project.Nodes.AddRange(new[] { input, output });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "e0",
            SourceNodeId = input.NodeId,
            SourcePortName = "value",
            TargetNodeId = output.NodeId,
            TargetPortName = "value"
        });
        var session = new DesignerSession(project);
        var before = ProjectSerializer.Serialize(session.Project);
        var ratio = new MappingNode
        {
            NodeId = "ratio",
            NodeType = NodeTypeConstants.Ratio,
            DisplayName = "Ratio",
            InputPorts = new()
            {
                new("numerator", PortShape.Decimal, PortCardinality.Single),
                new("denominator", PortShape.Decimal, PortCardinality.Single)
            },
            OutputPorts = new() { decimalPort }
        };
        var denominator = new MappingNode
        {
            NodeId = "denominator",
            NodeType = NodeTypeConstants.Constant,
            DisplayName = "Denominator",
            Parameters = new Dictionary<string, object?> { ["literal"] = "2", ["shape"] = "Decimal" },
            OutputPorts = new() { decimalPort }
        };
        var sideInput = new MappingEdge
        {
            EdgeId = "pending",
            SourceNodeId = denominator.NodeId,
            SourcePortName = "value",
            TargetNodeId = ratio.NodeId,
            TargetPortName = "denominator"
        };

        Assert.True(session.TryInsertNodeOnEdgeWithCompanions("e0", ratio, "numerator", "value",
            new[] { denominator }, new[] { sideInput }, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(4, session.Project.Nodes.Count);
        Assert.Equal(3, session.Project.Edges.Count);
        Assert.Contains(session.Project.Edges, edge => edge.SourceNodeId == denominator.NodeId
            && edge.TargetNodeId == ratio.NodeId && edge.TargetPortName == "denominator");

        session.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        session.Redo();
        Assert.Equal(3, session.Project.Edges.Count);
    }

    [Fact]
    public void InsertNodeWithCompanions_RejectsUnconnectedRequiredInputWithoutMutation()
    {
        var project = Project();
        project.Nodes.Clear();
        project.Edges.Clear();
        var decimalPort = new PortDefinition("value", PortShape.Decimal, PortCardinality.Single);
        var input = new MappingNode
        {
            NodeId = "function-input",
            NodeType = "functionInput",
            DisplayName = "Input",
            OutputPorts = new() { decimalPort }
        };
        var output = new MappingNode
        {
            NodeId = "function-output",
            NodeType = "functionOutput",
            DisplayName = "Output",
            InputPorts = new() { decimalPort }
        };
        project.Nodes.AddRange(new[] { input, output });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "e0",
            SourceNodeId = input.NodeId,
            SourcePortName = "value",
            TargetNodeId = output.NodeId,
            TargetPortName = "value"
        });
        var session = new DesignerSession(project);
        var before = ProjectSerializer.Serialize(session.Project);
        var ratio = new MappingNode
        {
            NodeId = "ratio",
            NodeType = NodeTypeConstants.Ratio,
            DisplayName = "Ratio",
            InputPorts = new()
            {
                new("numerator", PortShape.Decimal, PortCardinality.Single),
                new("denominator", PortShape.Decimal, PortCardinality.Single)
            },
            OutputPorts = new() { decimalPort }
        };

        Assert.False(session.TryInsertNodeOnEdgeWithCompanions("e0", ratio, "numerator", "value",
            Array.Empty<MappingNode>(), Array.Empty<MappingEdge>(), out var refusal));

        Assert.Contains("denominator", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.False(session.CanUndo);
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
    public async Task SaveAsToNewPathCreatesIndependentIdentityAndClearsApprovals()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-lifecycle-copy", Guid.NewGuid().ToString("n"));
        var sourceDirectory = Path.Combine(dir, "source");
        var destinationDirectory = Path.Combine(dir, "copy");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "mapping.xmap");
        var copyPath = Path.Combine(destinationDirectory, "mapping-copy.xmap");
        var sourceFile = Path.Combine(sourceDirectory, "input.csv");
        var copySourceFile = Path.Combine(destinationDirectory, "input.csv");
        await File.WriteAllTextAsync(sourceFile, "Revenue\n1250\n");
        await File.WriteAllTextAsync(copySourceFile, "Expense\n2500\n");
        try
        {
            var project = ProjectWithSource();
            project.Bindings[0] = project.Bindings[0] with { Locator = "input.csv" };
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = project.ProfileDigest
            });
            var dialog = new TestProjectFileDialog(null)
            {
                ProjectSavePath = copyPath,
                ConfirmIndependentCopy = true
            };
            var viewModel = new MainWindowViewModel(project, dialog);
            viewModel.SaveProjectTo(sourcePath);
            var sourceManager = new ProjectManager(sourcePath);
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);

            await viewModel.SaveProjectAsAsync();

            var savedSource = sourceManager.Load();
            var savedCopy = new ProjectManager(copyPath).Load();
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.NotEqual(savedSource.ProjectId, savedCopy.ProjectId);
            Assert.Empty(savedCopy.Approvals);
            Assert.Equal(savedSource.ComputeSemanticDigest(), savedCopy.ComputeSemanticDigest());
            Assert.Equal("input.csv", savedCopy.Bindings[0].Locator);
            Assert.Equal(copyPath, viewModel.ProjectPath);
            Assert.Contains("prior approvals were cleared", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("new project folder", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("Expense", viewModel.SourceColumns);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsIndependentCopyCancellationPreservesActiveProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-lifecycle-copy-cancel", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var sourcePath = Path.Combine(dir, "active.xmap");
        var copyPath = Path.Combine(dir, "copy", "copy.xmap");
        Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
        try
        {
            var dialog = new TestProjectFileDialog(null)
            {
                ProjectSavePath = copyPath,
                ConfirmIndependentCopy = false
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);
            viewModel.SaveProjectTo(sourcePath);
            viewModel.Session.MoveNode("n0", 123, 456);
            var activeProjectId = viewModel.Session.Project.ProjectId;
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            await viewModel.SaveProjectAsAsync();

            Assert.Equal(sourcePath, viewModel.ProjectPath);
            Assert.Equal(activeProjectId, viewModel.Session.Project.ProjectId);
            Assert.True(viewModel.IsProjectDirty);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.False(File.Exists(copyPath));
            Assert.Contains("Canceled Save As", viewModel.StatusText, StringComparison.Ordinal);
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
    public async Task DesktopCanPinXbrlOccurrenceAndUpdateOnlyTargetRowToCurrentReportDigest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-xbrl-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "report.xbrl.json");
        var projectPath = Path.Combine(directory, "mapping.xmap");
        var profile = SyntheticProfile.Build();
        var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        XbrlFact Revenue(decimal value) => new()
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "https://example.test/entity",
            EntityIdentifier = "ACME-1",
            Period = period,
            Unit = new XbrlUnit("QAR"),
            Value = value
        };

        try
        {
            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile,
                new[] { Revenue(12m), Revenue(99m) }));
            var project = ProjectWithSource();
            project.Bindings[0] = project.Bindings[0] with { Kind = "xbrl-report" };
            var viewModel = new MainWindowViewModel(project);
            viewModel.SaveProjectTo(projectPath);
            await viewModel.LoadSourceAsync(reportPath);

            Assert.True(viewModel.IsSelectedSourceXbrlReport);
            Assert.Equal(2, viewModel.XbrlOccurrenceOptions.Count);
            viewModel.SelectedXbrlOccurrence = Assert.Single(viewModel.XbrlOccurrenceOptions,
                option => option.OccurrenceId == "source#f2");
            viewModel.XbrlTargetRowId = "sha256:" + new string('a', 64);
            await viewModel.ApplyXbrlOccurrenceSelectionAsync();

            var binding = Assert.Single(viewModel.Session.Project.Bindings);
            Assert.Equal("source#f2", binding.Configuration["occurrenceId"]?.ToString());
            Assert.Equal(viewModel.Session.Project.Bindings[0].Configuration["reportDigest"]?.ToString(),
                "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPath)))
                    .ToLowerInvariant());
            Assert.Equal(viewModel.XbrlTargetRowId, binding.Configuration["targetRowId"]?.ToString());
            Assert.Contains("Pinned source#f2", viewModel.SourceInspectionSummary, StringComparison.Ordinal);

            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile,
                new[] { Revenue(777m), Revenue(12m) }));
            var changedConnection = new SourceConnection(binding.BindingId, binding.Kind, reportPath,
                binding.Configuration.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? "",
                    StringComparer.Ordinal));
            var adapter = new XbrlReportAdapter();
            var schema = await adapter.InspectSchemaAsync(changedConnection, CancellationToken.None);
            await Assert.ThrowsAsync<AdapterException>(async () =>
            {
                await foreach (var _ in adapter.ReadFullAsync(changedConnection, schema, profile, CancellationToken.None)) { }
            });

            viewModel.ClearXbrlOccurrenceSelection();
            var cleared = Assert.Single(viewModel.Session.Project.Bindings);
            Assert.DoesNotContain("occurrenceId", cleared.Configuration.Keys);
            Assert.DoesNotContain("reportDigest", cleared.Configuration.Keys);
            Assert.DoesNotContain("targetRowId", cleared.Configuration.Keys);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopCanAuthorAspectFiltersForMultipleXbrlOccurrences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-xbrl-aspects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "report.xbrl.json");
        var projectPath = Path.Combine(directory, "mapping.xmap");
        var profile = SyntheticProfile.Build();
        var axis = SyntheticProfile.Qualified("RegionAxis");
        var north = SyntheticProfile.Qualified("NorthMember");
        var south = SyntheticProfile.Qualified("SouthMember");
        var selectedPeriod = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var alternatePeriod = XbrlPeriod.OfDuration(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        XbrlFact Revenue(decimal value, string unit, XbrlPeriod period, string member) => new()
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "https://example.test/entity",
            EntityIdentifier = "ACME-1",
            Period = period,
            Unit = new XbrlUnit(unit),
            Value = value,
            Dimensions = [new Dimension(axis, member)]
        };

        var facts = new[]
        {
            Revenue(12m, "QAR", selectedPeriod, north),
            Revenue(13m, "QAR", selectedPeriod, south),
            Revenue(14m, "USD", selectedPeriod, north),
            Revenue(15m, "QAR", alternatePeriod, north)
        };

        try
        {
            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile, facts));
            var project = ProjectWithSource();
            project.Bindings[0] = project.Bindings[0] with { Kind = "xbrl-report" };
            var viewModel = new MainWindowViewModel(project);
            viewModel.SaveProjectTo(projectPath);
            await viewModel.LoadSourceAsync(reportPath);

            viewModel.SelectedXbrlOccurrence = viewModel.XbrlOccurrenceOptions[0];
            viewModel.XbrlOccurrenceId = "";
            viewModel.XbrlConceptFilter = SyntheticProfile.Qualified("Revenue");
            viewModel.XbrlEntitySchemeFilter = "https://example.test/entity";
            viewModel.XbrlEntityIdentifierFilter = "ACME-1";
            viewModel.XbrlPeriodFilter = selectedPeriod.ToIsoString();
            viewModel.XbrlPeriodKindFilter = "duration";
            viewModel.XbrlPeriodStartFilter = "2025-01-01";
            viewModel.XbrlPeriodEndFilter = "2026-01-01";
            viewModel.XbrlUnitFilter = "QAR";
            viewModel.XbrlNilFilter = "false";
            viewModel.XbrlDimensionFiltersJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { [axis] = north });
            await viewModel.ApplyXbrlOccurrenceSelectionAsync();

            var binding = Assert.Single(viewModel.Session.Project.Bindings);
            Assert.DoesNotContain("occurrenceId", binding.Configuration.Keys);
            Assert.Null(viewModel.SelectedXbrlOccurrence);
            Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPath)))
                .ToLowerInvariant(), binding.Configuration["reportDigest"]?.ToString());
            Assert.Equal(SyntheticProfile.Qualified("Revenue"), binding.Configuration["concept"]?.ToString());
            Assert.Equal("https://example.test/entity", binding.Configuration["entityScheme"]?.ToString());
            Assert.Equal("ACME-1", binding.Configuration["entityIdentifier"]?.ToString());
            Assert.Equal(selectedPeriod.ToIsoString(), binding.Configuration["period"]?.ToString());
            Assert.Equal("duration", binding.Configuration["periodKind"]?.ToString());
            Assert.Equal("2025-01-01", binding.Configuration["periodStart"]?.ToString());
            Assert.Equal("2026-01-01", binding.Configuration["periodEnd"]?.ToString());
            Assert.Equal("QAR", binding.Configuration["unit"]?.ToString());
            Assert.Equal("false", binding.Configuration["nil"]?.ToString());
            Assert.Equal(viewModel.XbrlDimensionFiltersJson, binding.Configuration["dimensionFilters"]?.ToString());
            Assert.Contains("Applied xBRL aspect filters", viewModel.SourceInspectionSummary, StringComparison.Ordinal);

            viewModel.SaveProjectTo(projectPath);
            var dialog = new TestProjectFileDialog(null) { ProjectOpenPath = projectPath };
            var reopened = new MainWindowViewModel(Project(), dialog);
            await reopened.OpenProjectAsync();
            Assert.False(reopened.IsProjectDirty);
            Assert.Equal(viewModel.XbrlConceptFilter, reopened.XbrlConceptFilter);
            Assert.Equal(viewModel.XbrlEntitySchemeFilter, reopened.XbrlEntitySchemeFilter);
            Assert.Equal(viewModel.XbrlEntityIdentifierFilter, reopened.XbrlEntityIdentifierFilter);
            Assert.Equal(viewModel.XbrlPeriodFilter, reopened.XbrlPeriodFilter);
            Assert.Equal(viewModel.XbrlPeriodKindFilter, reopened.XbrlPeriodKindFilter);
            Assert.Equal(viewModel.XbrlPeriodStartFilter, reopened.XbrlPeriodStartFilter);
            Assert.Equal(viewModel.XbrlPeriodEndFilter, reopened.XbrlPeriodEndFilter);
            Assert.Equal(viewModel.XbrlUnitFilter, reopened.XbrlUnitFilter);
            Assert.Equal(viewModel.XbrlNilFilter, reopened.XbrlNilFilter);
            Assert.Equal(viewModel.XbrlDimensionFiltersJson, reopened.XbrlDimensionFiltersJson);
            Assert.Equal(binding.Configuration["reportDigest"]?.ToString(),
                reopened.Session.Project.Bindings.Single().Configuration["reportDigest"]?.ToString());

            var configuration = binding.Configuration.ToDictionary(pair => pair.Key,
                pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal);
            var connection = new SourceConnection(binding.BindingId, binding.Kind, reportPath, configuration);
            var adapter = new XbrlReportAdapter();
            var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
            var sample = await adapter.ReadSampleAsync(connection, schema, 10, profile, CancellationToken.None);
            var selected = Assert.Single(sample.Rows);
            Assert.Equal("source#f1", selected.RowId);
            Assert.Equal("12", selected["value"].Raw?.ToString());

            var savedSelection = ProjectSerializer.Serialize(viewModel.Session.Project);
            viewModel.XbrlDimensionFiltersJson = "{\"" + axis + "\":1}";
            await viewModel.ApplyXbrlOccurrenceSelectionAsync();
            Assert.Equal(savedSelection, ProjectSerializer.Serialize(viewModel.Session.Project));
            Assert.Contains("selection was rejected", viewModel.StatusText, StringComparison.Ordinal);

            viewModel.ClearXbrlOccurrenceSelection();
            var cleared = Assert.Single(viewModel.Session.Project.Bindings);
            Assert.Null(viewModel.SelectedXbrlOccurrence);
            Assert.DoesNotContain("reportDigest", cleared.Configuration.Keys);
            Assert.DoesNotContain("concept", cleared.Configuration.Keys);
            Assert.DoesNotContain("entityScheme", cleared.Configuration.Keys);
            Assert.DoesNotContain("entityIdentifier", cleared.Configuration.Keys);
            Assert.DoesNotContain("period", cleared.Configuration.Keys);
            Assert.DoesNotContain("periodKind", cleared.Configuration.Keys);
            Assert.DoesNotContain("periodStart", cleared.Configuration.Keys);
            Assert.DoesNotContain("periodEnd", cleared.Configuration.Keys);
            Assert.DoesNotContain("unit", cleared.Configuration.Keys);
            Assert.DoesNotContain("nil", cleared.Configuration.Keys);
            Assert.DoesNotContain("dimensionFilters", cleared.Configuration.Keys);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyProfileAdoptionCreatesNewFileOnlyAfterExplicitSyntheticOptIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-legacy-profile-adoption", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var sourcePath = Path.Combine(dir, "legacy.xmap");
        var destinationPath = Path.Combine(dir, "adopted", "mapping.xmap");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        try
        {
            var sourceProject = Project();
            sourceProject.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = sourceProject.SemanticRevision,
                SemanticDigest = sourceProject.ComputeSemanticDigest(),
                ApprovedBy = "legacy-reviewer",
                ApprovedUtc = DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
                ProfileDigest = sourceProject.ProfileDigest
            });
            var legacyJson = JsonNode.Parse(ProjectSerializer.Serialize(sourceProject))!.AsObject();
            legacyJson.Remove("profileId");
            legacyJson.Remove("profileDigest");
            await File.WriteAllTextAsync(sourcePath, legacyJson.ToJsonString(ProjectSerializer.Options));
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
            var dialog = new TestProjectFileDialog(null)
            {
                ProjectOpenPath = sourcePath,
                ProjectSavePath = destinationPath,
                LegacyProfileSelection = new LegacyProjectProfileSelection(
                    SyntheticProfile.ProfileId, SyntheticProfile.Build().Digest, AllowSyntheticProfile: true),
                ConfirmLegacyAdoption = true
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);
            var activeProjectId = viewModel.Session.Project.ProjectId;

            await viewModel.AdoptLegacyProjectProfileAsync();

            var adopted = new ProjectManager(destinationPath).Load();
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.NotEqual(sourceProject.ProjectId, adopted.ProjectId);
            Assert.NotEqual(activeProjectId, viewModel.Session.Project.ProjectId);
            Assert.Empty(adopted.Approvals);
            Assert.Equal(SyntheticProfile.ProfileId, adopted.ProfileId);
            Assert.Equal(SyntheticProfile.Build().Digest, adopted.ProfileDigest);
            Assert.Equal(sourceProject.Nodes.Select(node => node.NodeId), adopted.Nodes.Select(node => node.NodeId));
            Assert.Equal(sourceProject.Edges.Select(edge => edge.EdgeId), adopted.Edges.Select(edge => edge.EdgeId));
            Assert.Equal(destinationPath, viewModel.ProjectPath);
            Assert.Equal(new[] { "profileId", "profileDigest" }, dialog.ConfirmedAddedIdentityFields);
            Assert.Contains("source remains unchanged", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("approvals cleared", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OpeningLegacyProjectExplainsExplicitAdoptionAction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-legacy-profile-open-hint", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var sourcePath = Path.Combine(dir, "legacy.xmap");
        try
        {
            var legacy = JsonNode.Parse(ProjectSerializer.Serialize(Project()))!.AsObject();
            legacy.Remove("profileId");
            legacy.Remove("profileDigest");
            await File.WriteAllTextAsync(sourcePath, legacy.ToJsonString(ProjectSerializer.Options));
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            var dialog = new TestProjectFileDialog(null) { ProjectOpenPath = sourcePath };
            var viewModel = new MainWindowViewModel(Project(), dialog);

            await viewModel.OpenProjectAsync();

            Assert.Null(viewModel.ProjectPath);
            Assert.Contains("profile identity cannot be inferred", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Adopt Legacy Profile", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyProfileAdoptionRequiresSyntheticOptInAndPreservesSourceWhenSourceChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-legacy-profile-adoption-gates", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var sourcePath = Path.Combine(dir, "legacy.xmap");
        var destinationPath = Path.Combine(dir, "adopted.xmap");
        try
        {
            var legacy = JsonNode.Parse(ProjectSerializer.Serialize(Project()))!.AsObject();
            legacy.Remove("profileId");
            legacy.Remove("profileDigest");
            await File.WriteAllTextAsync(sourcePath, legacy.ToJsonString(ProjectSerializer.Options));
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            var dialog = new TestProjectFileDialog(null)
            {
                ProjectOpenPath = sourcePath,
                ProjectSavePath = destinationPath,
                LegacyProfileSelection = new LegacyProjectProfileSelection(
                    SyntheticProfile.ProfileId, SyntheticProfile.Build().Digest, AllowSyntheticProfile: false),
                ConfirmLegacyAdoption = true
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);

            await viewModel.AdoptLegacyProjectProfileAsync();

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.False(File.Exists(destinationPath));
            Assert.Equal(0, dialog.SaveProjectRequestCount);
            Assert.Contains("explicit opt-in", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

            dialog.LegacyProfileSelection = new LegacyProjectProfileSelection(
                SyntheticProfile.ProfileId, SyntheticProfile.Build().Digest, AllowSyntheticProfile: true);
            dialog.OnLegacyProfileSelected = () => File.AppendAllText(sourcePath, " ");
            await viewModel.AdoptLegacyProjectProfileAsync();

            Assert.Equal(originalBytes.Concat(new byte[] { (byte)' ' }), await File.ReadAllBytesAsync(sourcePath));
            Assert.False(File.Exists(destinationPath));
            Assert.Contains("changed after profile selection", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OpeningCorruptedProject_OffersValidatedBackupAndSaveAsPreservesOriginals()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-backup-recovery", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, "mapping.xmap");
        var recoveryCopyPath = Path.Combine(dir, "recovered", "mapping-recovered.xmap");
        Directory.CreateDirectory(Path.GetDirectoryName(recoveryCopyPath)!);
        try
        {
            var project = Project();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = project.ProfileDigest
            });
            var manager = new ProjectManager(projectPath);
            var firstSave = manager.Save(project);
            var originalPosition = project.Nodes[0].Layout!;
            var originalX = originalPosition.X;
            project.Nodes[0].Layout = new LayoutPosition(originalX + 250, originalPosition.Y);
            manager.Save(project, firstSave.NewLayoutGeneration);
            var expectedProjectId = manager.LoadBackup().ProjectId;
            var backupBytes = await File.ReadAllBytesAsync(manager.BackupPath);
            await File.WriteAllTextAsync(projectPath, "{ corrupt main project }");
            var corruptedProjectBytes = await File.ReadAllBytesAsync(projectPath);

            var dialog = new TestProjectFileDialog(null)
            {
                ProjectOpenPath = projectPath,
                ConfirmBackupRecovery = true,
                ProjectSavePath = recoveryCopyPath,
                ConfirmIndependentCopy = true
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);

            await viewModel.OpenProjectAsync();

            Assert.Equal(expectedProjectId, viewModel.Session.Project.ProjectId);
            Assert.Equal(originalX, viewModel.Session.Project.Nodes[0].Layout!.X);
            Assert.Equal(projectPath, viewModel.ProjectPath);
            Assert.False(viewModel.IsProjectDirty);
            Assert.Equal(1, dialog.BackupRecoveryConfirmationCount);
            Assert.Contains("validated backup", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Use Save As", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Equal(corruptedProjectBytes, await File.ReadAllBytesAsync(projectPath));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(manager.BackupPath));

            Assert.Throws<InvalidOperationException>(() => viewModel.SaveProjectTo(projectPath));
            Assert.Equal(corruptedProjectBytes, await File.ReadAllBytesAsync(projectPath));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(manager.BackupPath));

            await viewModel.SaveProjectAsAsync();

            var recoveredCopy = new ProjectManager(recoveryCopyPath).Load();
            Assert.NotEqual(expectedProjectId, recoveredCopy.ProjectId);
            Assert.Empty(recoveredCopy.Approvals);
            Assert.Equal(recoveryCopyPath, viewModel.ProjectPath);
            Assert.Equal(corruptedProjectBytes, await File.ReadAllBytesAsync(projectPath));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(manager.BackupPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CancelBackupRecovery_PreservesDirtyActiveProjectAndBothFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-backup-recovery-cancel", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var damagedPath = Path.Combine(dir, "damaged.xmap");
        var activePath = Path.Combine(dir, "active.xmap");
        try
        {
            var damagedManager = new ProjectManager(damagedPath);
            var damagedProject = Project();
            var firstSave = damagedManager.Save(damagedProject);
            damagedProject.Nodes[0].Layout = new LayoutPosition(900, 901);
            damagedManager.Save(damagedProject, firstSave.NewLayoutGeneration);
            await File.WriteAllTextAsync(damagedPath, "{ corrupt main project }");
            var damagedBytes = await File.ReadAllBytesAsync(damagedPath);
            var backupBytes = await File.ReadAllBytesAsync(damagedManager.BackupPath);

            var dialog = new TestProjectFileDialog(null)
            {
                ProjectOpenPath = damagedPath,
                ConfirmBackupRecovery = false
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);
            viewModel.SaveProjectTo(activePath);
            var activeProjectId = viewModel.Session.Project.ProjectId;
            viewModel.Session.MoveNode("n0", 777, 888);

            await viewModel.OpenProjectAsync();

            Assert.Equal(activeProjectId, viewModel.Session.Project.ProjectId);
            Assert.Equal(activePath, viewModel.ProjectPath);
            Assert.Equal(777, viewModel.Session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);
            Assert.True(viewModel.IsProjectDirty);
            Assert.Equal(1, dialog.BackupRecoveryConfirmationCount);
            Assert.Equal("Canceled backup recovery; the active project is unchanged.", viewModel.StatusText);
            Assert.Equal(damagedBytes, await File.ReadAllBytesAsync(damagedPath));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(damagedManager.BackupPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidBackupDoesNotPromptForRecoveryOrReplaceTheActiveProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-project-backup-invalid", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, "damaged.xmap");
        try
        {
            var manager = new ProjectManager(projectPath);
            var project = Project();
            var firstSave = manager.Save(project);
            project.Nodes[0].Layout = new LayoutPosition(901, 902);
            manager.Save(project, firstSave.NewLayoutGeneration);
            await File.WriteAllTextAsync(projectPath, "{ corrupt main project }");
            await File.WriteAllTextAsync(manager.BackupPath, "{ corrupt backup project }");
            var projectBytes = await File.ReadAllBytesAsync(projectPath);
            var backupBytes = await File.ReadAllBytesAsync(manager.BackupPath);

            var dialog = new TestProjectFileDialog(null)
            {
                ProjectOpenPath = projectPath,
                ConfirmBackupRecovery = true
            };
            var viewModel = new MainWindowViewModel(Project(), dialog);
            var activeProjectId = viewModel.Session.Project.ProjectId;

            await viewModel.OpenProjectAsync();

            Assert.Equal(0, dialog.BackupRecoveryConfirmationCount);
            Assert.Equal(activeProjectId, viewModel.Session.Project.ProjectId);
            Assert.Null(viewModel.ProjectPath);
            Assert.Contains("backup could not be recovered", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(projectBytes, await File.ReadAllBytesAsync(projectPath));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(manager.BackupPath));
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
    public void DesktopBlocksDisabledInstalledProfileBeforePreviewReadsSource()
    {
        var catalogRoot = Path.Combine(Path.GetTempPath(), "xms-desktop-disabled-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(catalogRoot);
        try
        {
            var installed = WriteInstalledProfile(catalogRoot, "desktop-disabled-profile", "1.0.0");
            File.WriteAllText(Path.Combine(installed.InstallationDirectory,
                InstalledFilingProfileCatalog.DisabledMarkerFileName), "disabled by local administrator");

            var workbookPath = TestFixtures.MaterializeWorkbook(catalogRoot);
            var project = WithProfileIdentity(TestFixtures.BuildRevenueMappingProject(workbookPath), installed.Profile);
            var viewModel = new MainWindowViewModel(
                project,
                null,
                (_, _) => throw new InvalidOperationException("SQLite source inspection must not run."),
                FilingProfileResolver.FromInstalledDirectory(catalogRoot));
            viewModel.SaveProjectTo(Path.Combine(catalogRoot, "mapping.xmap"));
            File.Delete(workbookPath);

            viewModel.PreviewCommand.Execute(null);

            Assert.Equal("Operation blocked by filing-profile resolution.", viewModel.StatusText);
            Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains("XBR018", StringComparison.Ordinal)
                && row.Display.Contains("disabled", StringComparison.Ordinal));
            Assert.DoesNotContain(viewModel.Diagnostics, row => row.Display.Contains(workbookPath, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public void StaleSyntheticProfileDigest_DoesNotExposeSyntheticConceptsOrRelationships()
    {
        var profile = SyntheticProfile.Build();
        var zeroDigest = "sha256:" + new string('0', 64);
        var staleDigest = StringComparer.Ordinal.Equals(profile.Digest, zeroDigest)
            ? "sha256:" + new string('1', 64)
            : zeroDigest;
        var project = WithProfileIdentity(Project(), SyntheticProfile.ProfileId, staleDigest);
        var viewModel = new MainWindowViewModel(project, null,
            (_, _) => Task.FromException<XBRLMappingStudio.Adapters.ISourceSchema>(
                new InvalidOperationException("Source inspection must not run.")),
            FilingProfileResolver.DevelopmentOnly());

        Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains("XBR015", StringComparison.Ordinal));
        Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains(staleDigest, StringComparison.Ordinal));
        Assert.Empty(viewModel.ConceptResults);
        Assert.Null(viewModel.SelectedConcept);

        var taxonomyField = typeof(MainWindowViewModel).GetField("_taxonomy",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var taxonomy = Assert.IsType<TaxonomyIndex>(taxonomyField?.GetValue(viewModel));
        Assert.Equal(SyntheticProfile.ProfileId, taxonomy.ProfileId);
        Assert.Equal(staleDigest, taxonomy.ProfileDigest);
        Assert.Empty(taxonomy.AllConcepts);
        Assert.Empty(taxonomy.PresentationRolesFor("synthetic:Revenue"));
        Assert.Empty(taxonomy.Network(SyntheticProfile.PresentationRole).Links);
    }

    [Fact]
    public async Task OpeningOwnerGatedProfile_PreservesIdentityAndSkipsSavedBindings()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-desktop-owner-gated-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "source.db");
        try
        {
            var installed = WriteInstalledProfile(root, "desktop-owner-gated-profile", "1.0.0");
            await using (var database = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await database.OpenAsync();
                await using var command = database.CreateCommand();
                command.CommandText = "CREATE TABLE source (value TEXT); INSERT INTO source VALUES ('1250');";
                await command.ExecuteNonQueryAsync();
            }

            var sourceProject = TestFixtures.BuildRevenueMappingProject(databasePath);
            sourceProject.Bindings[0] = sourceProject.Bindings[0] with
            {
                Kind = "sqlite",
                Locator = Path.GetFileName(databasePath),
                Configuration = new Dictionary<string, object?> { ["table"] = "source" }
            };
            var project = WithProfileIdentity(sourceProject, installed.Profile);
            var projectPath = Path.Combine(root, "owner-gated.xmap");
            new ProjectManager(projectPath).Save(project);

            var dialog = new TestProjectFileDialog(null) { ProjectOpenPath = projectPath };
            var sourceInspectionAttempted = 0;
            var viewModel = new MainWindowViewModel(
                Project(),
                dialog,
                (_, _) =>
                {
                    Interlocked.Increment(ref sourceInspectionAttempted);
                    return Task.FromException<XBRLMappingStudio.Adapters.ISourceSchema>(
                        new InvalidOperationException("A gated profile must not inspect saved sources."));
                },
                FilingProfileResolver.FromInstalledDirectory(root));
            viewModel.SelectedConcept = new ConceptRow
            {
                QualifiedName = "synthetic:Revenue",
                Label = "Revenue",
                DataType = "monetary",
                PeriodType = "duration",
                IsFactTargetable = true
            };

            await viewModel.OpenProjectAsync();

            Assert.Equal(project.ProfileId, viewModel.Session.Project.ProfileId);
            Assert.Equal(project.ProfileDigest, viewModel.Session.Project.ProfileDigest);
            Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains("XBR018", StringComparison.Ordinal)
                && row.Display.Contains("GAT-01 and GAT-02", StringComparison.Ordinal));
            Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains($"ProfileId='{project.ProfileId}'", StringComparison.Ordinal)
                && row.Display.Contains($"ProfileDigest='{project.ProfileDigest}'", StringComparison.Ordinal));
            Assert.Null(viewModel.SelectedSourceBindingId);
            Assert.Empty(viewModel.SourceColumns);
            Assert.Empty(viewModel.SourceTables);
            Assert.Empty(viewModel.ConceptResults);
            Assert.Null(viewModel.SelectedConcept);
            Assert.False(viewModel.IsSourceLoading);
            Assert.Equal(0, Volatile.Read(ref sourceInspectionAttempted));

            var factNode = viewModel.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            viewModel.SelectedNode = factNode;
            Assert.Contains(project.ProfileId, viewModel.SelectedFactConceptIdentity, StringComparison.Ordinal);
            Assert.Contains(project.ProfileDigest, viewModel.SelectedFactConceptIdentity, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticProfile.ProfileId, viewModel.SelectedFactConceptIdentity, StringComparison.Ordinal);

            // The synchronous reload route uses the same profile gate before it can
            // start the fire-and-forget saved-source loader.
            viewModel.LoadProject(projectPath);
            Assert.Equal(project.ProfileId, viewModel.Session.Project.ProfileId);
            Assert.Equal(project.ProfileDigest, viewModel.Session.Project.ProfileDigest);
            Assert.Null(viewModel.SelectedSourceBindingId);
            Assert.Empty(viewModel.SourceColumns);
            Assert.False(viewModel.IsSourceLoading);
            Assert.Equal(0, Volatile.Read(ref sourceInspectionAttempted));
            Assert.Contains(viewModel.Diagnostics, row => row.Display.Contains($"ProfileDigest='{project.ProfileDigest}'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static InstalledFilingProfile WriteInstalledProfile(string root, string profileId, string version)
    {
        var directory = Path.Combine(root, profileId, version);
        Directory.CreateDirectory(directory);
        var entryPath = Path.Combine(directory, "entry.xsd");
        File.WriteAllText(entryPath, $"<schema version='{version}'/>");
        var manifest = new FilingProfileManifest
        {
            SchemaVersion = FilingProfileManifest.CurrentSchemaVersion,
            ProfileId = profileId,
            Version = version,
            EntryPoint = "entry.xsd",
            ProfileDigest = "",
            Concepts = new List<ConceptMetadata>
            {
                new("ex:Revenue", "urn:example", "Revenue", "Revenue", "monetary", "duration", false)
            },
            SupportedUnits = new List<string> { "USD" },
            NamespaceUris = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ex"] = "urn:example"
            },
            PackageNames = new List<string> { "example-taxonomy" },
            PackageFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entry.xsd"] = FileDigest(entryPath)
            }
        };
        manifest = manifest with { ProfileDigest = manifest.ComputeDigest() };
        File.WriteAllText(Path.Combine(directory, InstalledFilingProfileCatalog.ManifestFileName), manifest.ToJson());
        return InstalledFilingProfileCatalog.Load(root).Profiles.Single(profile =>
            profile.Profile.ProfileId == profileId && profile.Version == version);
    }

    private static string FileDigest(string path)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static MappingProject WithProfileIdentity(MappingProject source, FilingProfile profile)
        => WithProfileIdentity(source, profile.ProfileId, profile.Digest);

    private static MappingProject WithProfileIdentity(MappingProject source, string profileId, string profileDigest) => new()
    {
        SchemaVersion = source.SchemaVersion,
        ProjectId = source.ProjectId,
        Name = source.Name,
        ProfileId = profileId,
        ProfileDigest = profileDigest,
        FunctionCatalogVersion = source.FunctionCatalogVersion,
        SemanticRevision = source.SemanticRevision,
        LayoutGeneration = source.LayoutGeneration,
        Metadata = source.Metadata,
        Nodes = source.Nodes,
        Edges = source.Edges,
        Bindings = source.Bindings,
        Functions = source.Functions,
        Destination = source.Destination,
        Approvals = source.Approvals
    };

    [Fact]
    public void FactPropertyEdit_IsOneUndoableChange()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactUnit = "EUR";
        viewModel.SelectedFactPeriodType = "duration";
        viewModel.SelectedFactDecimals = "-2";

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        Assert.Equal("EUR", fact.Parameters["unit"]);
        Assert.Equal("duration", fact.Parameters["periodType"]);
        Assert.Equal("-2", fact.Parameters["decimals"]);
        viewModel.Session.Undo();
        fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        Assert.Equal("USD", fact.Parameters["unit"]?.ToString());
        Assert.Equal("duration", fact.Parameters["periodType"]?.ToString());
        Assert.Equal("0", fact.Parameters["decimals"]?.ToString());
    }

    [Fact]
    public void FactPropertyEditorAcceptsCompoundUnitsForNumericNonMonetaryConcepts()
    {
        var synthetic = SyntheticProfile.Build();
        var profile = new FilingProfile
        {
            ProfileId = synthetic.ProfileId,
            Digest = synthetic.Digest,
            Concepts = synthetic.Concepts.Append(new ConceptMetadata(
                "synthetic:UnitsPerShare", SyntheticProfile.Namespace, "UnitsPerShare",
                "Units per share", "decimal", "duration", IsAbstract: false)).ToArray(),
            SupportedUnits = synthetic.SupportedUnits,
            DimensionDomains = synthetic.DimensionDomains,
            TaxonomyVersion = synthetic.TaxonomyVersion,
            OutputFormat = synthetic.OutputFormat,
            NamespaceUris = synthetic.NamespaceUris,
            TaxonomyUrls = synthetic.TaxonomyUrls,
            PackageHashes = synthetic.PackageHashes,
            RequiredPlugins = synthetic.RequiredPlugins,
            RequiredChecks = synthetic.RequiredChecks
        };
        var project = new MainWindowViewModel().Session.Project;
        var target = project.Nodes.Single(node => node.NodeId == "sample-fact");
        target.Parameters["concept"] = "synthetic:UnitsPerShare";
        var viewModel = new MainWindowViewModel(project, null,
            (_, _) => Task.FromException<XBRLMappingStudio.Adapters.ISourceSchema>(
                new InvalidOperationException("Source inspection must not run.")),
            new FilingProfileResolver(new[] { profile }));
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactUnit = "USD/shares";
        viewModel.SelectedFactPeriodType = "duration";
        viewModel.SelectedFactDecimals = "0";

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal("USD/shares", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact")
            .Parameters["unit"]?.ToString());
    }

    [Fact]
    public void FactPropertyEditorRejectsCompoundCurrencyUnitsForMonetaryConcepts()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactUnit = "USD/shares";

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Contains("requires one currency measure", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal("USD", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact")
            .Parameters["unit"]?.ToString());
    }

    [Fact]
    public void EditingOneFactPropertyPreservesTheOtherProperties()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactUnit = "EUR";

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        var entity = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-entity");
        Assert.Equal("EUR", fact.Parameters["unit"]);
        Assert.Equal("duration", fact.Parameters["periodType"]?.ToString());
        Assert.Equal("0", fact.Parameters["decimals"]?.ToString());
        Assert.Equal("ABC-Corp", entity.Parameters["literal"]?.ToString());
    }

    [Fact]
    public void StringPeriodConstantCanBeEditedValidatedAndUndone()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-start");
        Assert.True(viewModel.CanEditSelectedStringConstant);

        viewModel.SelectedStringConstantValue = "2024-01-01";
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        var start = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-start");
        Assert.Equal("2024-01-01", start.Parameters["literal"]?.ToString());
        Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);

        viewModel.Session.Undo();
        start = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-start");
        Assert.Equal("2025-01-01", start.Parameters["literal"]?.ToString());

        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-start");
        viewModel.SelectedStringConstantValue = "2024-1-1";
        Assert.Contains("XBR004", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
        var beforeInvalidEdit = viewModel.Session.Project.ComputeSemanticDigest();
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        Assert.Equal(beforeInvalidEdit, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("XBR004", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
        viewModel.SelectedStringConstantValue = "2024-01-01";
        Assert.Empty(viewModel.SelectedNodeParameterError);
    }

    [Fact]
    public void StringEntityConstantRejectsBlankAndSupportsUndo()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-entity");
        Assert.Contains("XBRL Revenue.entity", viewModel.SelectedNodeParameterSummary, StringComparison.Ordinal);

        viewModel.SelectedStringConstantValue = " ";
        Assert.Contains("XBR005", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
        var beforeInvalidEdit = viewModel.Session.Project.ComputeSemanticDigest();
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        Assert.Equal(beforeInvalidEdit, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal("ABC-Corp", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-entity")
            .Parameters["literal"]?.ToString());

        viewModel.SelectedStringConstantValue = "ABC-Labs";
        Assert.Empty(viewModel.SelectedNodeParameterError);
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        Assert.Equal("ABC-Labs", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-entity")
            .Parameters["literal"]?.ToString());
        viewModel.Session.Undo();
        Assert.Equal("ABC-Corp", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-entity")
            .Parameters["literal"]?.ToString());
    }

    [Fact]
    public void ScaleFactorEditIsExactAndInvalidInputPreservesMapping()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-scale");
        Assert.True(viewModel.CanEditSelectedScaleFactor);

        viewModel.SelectedScaleFactor = "0.001";
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        var scale = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-scale");
        Assert.Equal("0.001", scale.Parameters["factor"]?.ToString());
        viewModel.Session.Undo();
        Assert.Equal("1000", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-scale")
            .Parameters["factor"]?.ToString());

        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-scale");
        viewModel.SelectedScaleFactor = "123456789012345678901234567890";
        Assert.Contains("MAP008", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
        var beforeInvalidEdit = viewModel.Session.Project.ComputeSemanticDigest();
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        Assert.Equal(beforeInvalidEdit, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("MAP008", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundNodeParametersApplyTogetherAndRejectUnsupportedScale()
    {
        var project = new MainWindowViewModel().Session.Project;
        project.Nodes.Add(new MappingNode
        {
            NodeId = "test-round",
            NodeType = NodeTypeConstants.Round,
            DisplayName = "Round amount",
            Parameters = new() { ["digits"] = "0", ["mode"] = "HalfEven" },
            InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
            OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) }
        });
        var viewModel = new MainWindowViewModel(project);
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "test-round");
        Assert.True(viewModel.CanEditSelectedRound);

        viewModel.SelectedRoundDigits = "2";
        viewModel.SelectedRoundMode = "HalfUp";
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        var round = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "test-round");
        Assert.Equal("2", round.Parameters["digits"]?.ToString());
        Assert.Equal("HalfUp", round.Parameters["mode"]?.ToString());
        viewModel.Session.Undo();
        round = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "test-round");
        Assert.Equal("0", round.Parameters["digits"]?.ToString());
        Assert.Equal("HalfEven", round.Parameters["mode"]?.ToString());

        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "test-round");
        viewModel.SelectedRoundDigits = "29";
        Assert.Contains("MAP018", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
        var beforeInvalidEdit = viewModel.Session.Project.ComputeSemanticDigest();
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        Assert.Equal(beforeInvalidEdit, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("MAP018", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinParametersExposeExplicitPoliciesAndApplyAsOneUndoableChange()
    {
        var project = new MainWindowViewModel().Session.Project;
        project.Nodes.Add(new MappingNode
        {
            NodeId = "test-join",
            NodeType = NodeTypeConstants.Join,
            DisplayName = "Join accounts",
            Parameters = new()
            {
                ["leftKey"] = "account_id",
                ["rightKey"] = "account_id"
            },
            InputPorts = new()
            {
                new PortDefinition("left", PortShape.Record, PortCardinality.Collection, true),
                new PortDefinition("right", PortShape.Record, PortCardinality.Collection, true)
            },
            OutputPorts = new()
            {
                new PortDefinition("rows", PortShape.Record, PortCardinality.Collection, true)
            }
        });
        var viewModel = new MainWindowViewModel(project);
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "test-join");

        Assert.True(viewModel.CanEditSelectedJoin);
        Assert.Equal("Inner", viewModel.SelectedJoinCardinality);
        Assert.Equal("All", viewModel.SelectedJoinDuplicateKeyPolicy);
        Assert.Equal("Allow", viewModel.SelectedJoinManyToManyPolicy);
        Assert.Equal("Right", viewModel.SelectedJoinFieldConflictPolicy);
        var initialDigest = viewModel.Session.Project.ComputeSemanticDigest();
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);
        Assert.Equal(initialDigest, viewModel.Session.Project.ComputeSemanticDigest());

        viewModel.SelectedJoinCardinality = "Full";
        viewModel.SelectedJoinDuplicateKeyPolicy = "First";
        viewModel.SelectedJoinManyToManyPolicy = "Fail";
        viewModel.SelectedJoinFieldConflictPolicy = "Left";
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);

        var join = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "test-join");
        Assert.Equal("Full", join.Parameters["cardinality"]?.ToString());
        Assert.Equal("First", join.Parameters["duplicateKey"]?.ToString());
        Assert.Equal("Fail", join.Parameters["manyToMany"]?.ToString());
        Assert.Equal("Left", join.Parameters["fieldConflict"]?.ToString());
        Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);

        viewModel.Session.Undo();
        join = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "test-join");
        Assert.False(join.Parameters.ContainsKey("cardinality"));
        Assert.False(join.Parameters.ContainsKey("duplicateKey"));
        Assert.False(join.Parameters.ContainsKey("manyToMany"));
        Assert.False(join.Parameters.ContainsKey("fieldConflict"));

        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "test-join");
        viewModel.SelectedJoinManyToManyPolicy = "Reject";
        Assert.Contains("MAP030", viewModel.SelectedNodeParameterError, StringComparison.Ordinal);
        var beforeInvalidEdit = viewModel.Session.Project.ComputeSemanticDigest();
        viewModel.ApplySelectedNodeParametersCommand.Execute(null);
        Assert.Equal(beforeInvalidEdit, viewModel.Session.Project.ComputeSemanticDigest());
    }

    [Fact]
    public void ConceptSelectionRejectsIncompatibleConnectedPeriodBeforeMutation()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.AllowSyntheticProfile = true;
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedConcept = new ConceptRow
        {
            QualifiedName = "synthetic:Assets",
            Label = "Total Assets",
            IsFactTargetable = true
        };
        var before = viewModel.Session.Project.ComputeSemanticDigest();

        viewModel.SetConceptCommand.Execute(null);

        Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("XBR004", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("duration period source", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ConceptSelectionRejectsNonNumericConceptUntilTextInputIsBound()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.AllowSyntheticProfile = true;
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedConcept = new ConceptRow
        {
            QualifiedName = "synthetic:EntityRegistrantName",
            Label = "Entity Registrant Name",
            DataType = "string",
            IsFactTargetable = true
        };
        var before = viewModel.Session.Project.ComputeSemanticDigest();

        viewModel.SetConceptCommand.Execute(null);

        Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("XBR011", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("requires a textValue or textValues input", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ConceptSelectionAndFactInspectorSupportTextTargetWhenTextInputIsBound()
    {
        var viewModel = new MainWindowViewModel(ProjectWithTextFactInput());
        viewModel.AllowSyntheticProfile = true;
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedConcept = new ConceptRow
        {
            QualifiedName = SyntheticProfile.Qualified("EntityRegistrantName"),
            Label = "Entity Registrant Name",
            DataType = "string",
            PeriodType = "duration",
            IsFactTargetable = true
        };

        viewModel.SetConceptCommand.Execute(null);

        var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        Assert.Equal(SyntheticProfile.Qualified("EntityRegistrantName"), fact.Parameters["concept"]?.ToString());
        Assert.Null(fact.Parameters["unit"]);
        Assert.Null(fact.Parameters["decimals"]);
        Assert.Contains("Review its text source, language and period", viewModel.StatusText, StringComparison.Ordinal);

        viewModel.SelectedFactLanguage = "fr";
        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal("fr", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact")
            .Parameters["language"]?.ToString());
    }

    [Fact]
    public void InvalidFactPropertyEditLeavesProjectUnchangedWithDiagnostic()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactDimensions = "ex:UnknownAxis=ex:Member";
        var before = viewModel.Session.Project.ComputeSemanticDigest();

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("XBR013", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("not declared", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedFactDimensions = "";
        viewModel.SelectedFactPeriodType = "instant";
        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("XBR004", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("requires a duration", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactInspectorAuthorsProfileDeclaredTypedStringDimensions()
    {
        var source = SyntheticProfile.Build();
        var profile = new FilingProfile
        {
            ProfileId = source.ProfileId,
            Digest = source.Digest,
            Concepts = source.Concepts,
            SupportedUnits = source.SupportedUnits,
            DimensionDomains = source.DimensionDomains,
            TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
            {
                ["synthetic:TypedAxis"] = new("synthetic:TypedMember", "xs:string")
            },
            TaxonomyVersion = source.TaxonomyVersion,
            OutputFormat = source.OutputFormat,
            NamespaceUris = source.NamespaceUris.Concat(new[]
            {
                new KeyValuePair<string, string>("xs", "http://www.w3.org/2001/XMLSchema")
            }).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            TaxonomyUrls = source.TaxonomyUrls,
            PackageHashes = source.PackageHashes,
            RequiredPlugins = source.RequiredPlugins,
            RequiredChecks = source.RequiredChecks
        };
        var project = new MainWindowViewModel().Session.Project;
        var viewModel = new MainWindowViewModel(project, null,
            (_, _) => Task.FromException<XBRLMappingStudio.Adapters.ISourceSchema>(
                new InvalidOperationException("Source inspection must not run.")),
            new FilingProfileResolver(new[] { profile }));
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactDimensions = FactDimensionParser.Format(
            [new Dimension("synthetic:TypedAxis", "", IsExplicit: false) { TypedValue = "0017" }]);

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);
        var persisted = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact")
            .Parameters["dimensions"]!.ToString();
        Assert.Contains("TypedValue", persisted, StringComparison.Ordinal);
        Assert.Contains("0017", persisted, StringComparison.Ordinal);
        var reopened = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(viewModel.Session.Project));
        Assert.Equal(persisted, reopened.Nodes.Single(node => node.NodeId == "sample-fact")
            .Parameters["dimensions"]!.ToString());
    }

    [Fact]
    public void TextFactInspectorEditsSupportedLanguageAsOneUndoableChange()
    {
        var project = TextFactProject();
        var before = project.ComputeSemanticDigest();
        var viewModel = new MainWindowViewModel(project);
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");

        Assert.Equal("en", viewModel.SelectedFactLanguage);
        Assert.Equal("", viewModel.SelectedFactUnit);
        Assert.Equal("", viewModel.SelectedFactDecimals);
        viewModel.SelectedFactLanguage = "fr";

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);
        var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        Assert.Equal("fr", fact.Parameters["language"]?.ToString());
        Assert.Equal(SyntheticProfile.Qualified("EntityRegistrantName"), fact.Parameters["concept"]?.ToString());
        Assert.Equal("duration", fact.Parameters["periodType"]?.ToString());
        Assert.False(fact.Parameters.ContainsKey("unit"));
        Assert.False(fact.Parameters.ContainsKey("decimals"));

        var reopened = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(viewModel.Session.Project));
        Assert.Equal("fr", reopened.Nodes.Single(node => node.NodeId == "sample-fact")
            .Parameters["language"]?.ToString());

        viewModel.Session.Undo();
        Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
    }

    [Theory]
    [InlineData("en_US", "well-formed BCP 47")]
    [InlineData("de", "not supported")]
    public void TextFactInspectorRejectsInvalidOrUnsupportedLanguageWithoutMutation(
        string language,
        string expectedMessage)
    {
        var viewModel = new MainWindowViewModel(TextFactProject());
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactLanguage = language;
        var before = viewModel.Session.Project.ComputeSemanticDigest();

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("XBR011", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NumericFactInspectorRejectsLanguageWithoutMutation()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactLanguage = "en";
        var before = viewModel.Session.Project.ComputeSemanticDigest();

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Equal(before, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Contains("XBR011", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("numeric facts cannot carry a language tag", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityAndDimensionEditIsAtomicAndSurvivesProjectRoundTrip()
    {
        var synthetic = SyntheticProfile.Build();
        var profile = new FilingProfile
        {
            ProfileId = synthetic.ProfileId,
            Digest = synthetic.Digest,
            Concepts = synthetic.Concepts,
            SupportedUnits = synthetic.SupportedUnits,
            DimensionDomains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["ex:RegionAxis"] = new[] { "ex:Domestic", "ex:International" }
            },
            TaxonomyVersion = synthetic.TaxonomyVersion,
            OutputFormat = synthetic.OutputFormat,
            NamespaceUris = synthetic.NamespaceUris,
            TaxonomyUrls = synthetic.TaxonomyUrls,
            PackageHashes = synthetic.PackageHashes,
            RequiredChecks = synthetic.RequiredChecks
        };
        var project = new MainWindowViewModel().Session.Project;
        var viewModel = new MainWindowViewModel(project, null,
            (_, _) => Task.FromException<XBRLMappingStudio.Adapters.ISourceSchema>(
                new InvalidOperationException("Source inspection must not run.")),
            new FilingProfileResolver(new[] { profile }));
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedFactUnit = "EUR";
        viewModel.SelectedFactPeriodType = "duration";
        viewModel.SelectedFactDecimals = "-2";
        viewModel.SelectedFactEntityIdentifier = "ABC-International";
        viewModel.SelectedFactDimensions = "ex:RegionAxis=ex:International";

        viewModel.ApplyFactPropertiesCommand.Execute(null);

        Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);
        var fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        var entity = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-entity");
        Assert.Equal("EUR", fact.Parameters["unit"]);
        Assert.Equal("duration", fact.Parameters["periodType"]);
        Assert.Equal("-2", fact.Parameters["decimals"]);
        Assert.Equal("ex:RegionAxis=ex:International", fact.Parameters["dimensions"]);
        Assert.Equal("ABC-International", entity.Parameters["literal"]);

        var reopened = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(viewModel.Session.Project));
        Assert.Equal("ex:RegionAxis=ex:International",
            reopened.Nodes.Single(node => node.NodeId == "sample-fact").Parameters["dimensions"]?.ToString());
        Assert.Equal("ABC-International",
            reopened.Nodes.Single(node => node.NodeId == "sample-entity").Parameters["literal"]?.ToString());

        viewModel.Session.Undo();
        fact = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact");
        entity = viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-entity");
        Assert.Equal("USD", fact.Parameters["unit"]?.ToString());
        Assert.False(fact.Parameters.ContainsKey("dimensions"));
        Assert.Equal("ABC-Corp", entity.Parameters["literal"]?.ToString());
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
            viewModel.SuggestionFilter = "local-name";
            Assert.Single(viewModel.FilteredAiSuggestions);
            viewModel.SuggestionFilter = "does-not-match";
            Assert.Empty(viewModel.FilteredAiSuggestions);
            Assert.Null(viewModel.SelectedSuggestion);
            viewModel.SuggestionFilter = "";
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
    public async Task GraphProposalOverlay_IsDigestBoundReviewOnlyAndRejectDoesNotChangeGraph()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.AllowSyntheticProfile = true;
        var digestBefore = viewModel.Session.Project.ComputeSemanticDigest();

        await viewModel.SuggestConnectedMappingsAsync();

        var proposal = Assert.Single(viewModel.AiSuggestions,
            row => row.SourceFieldId == "sample-source:Revenue" && row.Concept == "synthetic:Revenue");
        Assert.Same(proposal, Assert.Single(viewModel.GraphProposalSuggestions));
        Assert.Contains(digestBefore, viewModel.SuggestionProposalScopeSummary, StringComparison.Ordinal);
        Assert.Contains("not executable edges", viewModel.SuggestionProposalScopeSummary, StringComparison.Ordinal);

        viewModel.RejectSelectedSuggestionCommand.Execute(null);

        Assert.Equal(digestBefore, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal(SuggestionState.Rejected, proposal.ReviewState);
        Assert.Empty(viewModel.GraphProposalSuggestions);
    }

    [Fact]
    public async Task DesktopConnectedSuggestionBatch_AcceptsUndoAndRedoesOneSelectedMappingPerFact()
    {
        var seed = new MainWindowViewModel();
        var project = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(seed.Session.Project));
        var source = project.Nodes.Single(node => node.NodeId == "sample-value").ShallowCopy() with
        {
            NodeId = "sample-net-income-source",
            DisplayName = "Net income source field",
            Parameters = new Dictionary<string, object?>
            {
                ["bindingId"] = "sample-source",
                ["column"] = "Net Income"
            },
            Layout = new LayoutPosition(40, 440)
        };
        var fact = project.Nodes.Single(node => node.NodeId == "sample-fact").ShallowCopy() with
        {
            NodeId = "sample-net-income-fact",
            DisplayName = "XBRL Net income",
            Layout = new LayoutPosition(760, 440)
        };
        project.Nodes.Add(source);
        project.Nodes.Add(fact);
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "sample-net-income-value",
            SourceNodeId = source.NodeId,
            SourcePortName = "value",
            TargetNodeId = fact.NodeId,
            TargetPortName = "value"
        });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "sample-net-income-entity",
            SourceNodeId = "sample-entity",
            SourcePortName = "value",
            TargetNodeId = fact.NodeId,
            TargetPortName = "entity"
        });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "sample-net-income-period",
            SourceNodeId = "sample-period",
            SourcePortName = "value",
            TargetNodeId = fact.NodeId,
            TargetPortName = "period"
        });

        var viewModel = new MainWindowViewModel(project);
        viewModel.AllowSyntheticProfile = true;
        var digestBefore = viewModel.Session.Project.ComputeSemanticDigest();

        await viewModel.SuggestConnectedMappingsAsync();

        var revenue = Assert.Single(viewModel.AiSuggestions, row =>
            row.SourceFieldId == "sample-source:Revenue" && row.Concept == "synthetic:Revenue");
        var netIncome = Assert.Single(viewModel.AiSuggestions, row =>
            row.SourceFieldId == "sample-source:Net Income" && row.Concept == "synthetic:NetIncome");
        Assert.True(revenue.IsConnectedBatch);
        Assert.True(netIncome.IsConnectedBatch);
        revenue.IsBatchSelected = true;
        netIncome.IsBatchSelected = true;
        viewModel.SelectedSuggestion = revenue;
        Assert.True(viewModel.CanAcceptSelectedSuggestionBatch);

        viewModel.AcceptSelectedSuggestionBatchCommand.Execute(null);

        Assert.Contains("one validated, audited transaction", viewModel.StatusText);
        Assert.Equal(SuggestionState.Accepted, revenue.ReviewState);
        Assert.Equal(SuggestionState.Accepted, netIncome.ReviewState);
        Assert.DoesNotContain("stale suggestion(s) are hidden", viewModel.SuggestionProposalScopeSummary,
            StringComparison.Ordinal);
        Assert.Equal("synthetic:Revenue", viewModel.Session.Project.Nodes
            .Single(node => node.NodeId == "sample-fact").Parameters["concept"]?.ToString());
        Assert.Equal("synthetic:NetIncome", viewModel.Session.Project.Nodes
            .Single(node => node.NodeId == "sample-net-income-fact").Parameters["concept"]?.ToString());
        var digestAfter = viewModel.Session.Project.ComputeSemanticDigest();
        Assert.NotEqual(digestBefore, digestAfter);

        viewModel.UndoCommand.Execute(null);

        Assert.Equal(digestBefore, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal(SuggestionState.Pending, revenue.ReviewState);
        Assert.Equal(SuggestionState.Pending, netIncome.ReviewState);
        Assert.Contains("batch undone as one transaction", viewModel.StatusText);

        viewModel.RedoCommand.Execute(null);

        Assert.Equal(digestAfter, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal(SuggestionState.Accepted, revenue.ReviewState);
        Assert.Equal(SuggestionState.Accepted, netIncome.ReviewState);
    }

    [Fact]
    public async Task DesktopConnectedSuggestionBatch_StaleProjectDoesNotChangeMapping()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.AllowSyntheticProfile = true;

        await viewModel.SuggestConnectedMappingsAsync();

        var suggestion = Assert.Single(viewModel.AiSuggestions,
            row => row.SourceFieldId == "sample-source:Revenue" && row.Concept == "synthetic:Revenue");
        suggestion.IsBatchSelected = true;
        viewModel.Session.SetNodeParameter("sample-fact", "unit", "EUR");
        var currentDigest = viewModel.Session.Project.ComputeSemanticDigest();
        Assert.Empty(viewModel.GraphProposalSuggestions);
        Assert.Contains("stale suggestion(s) are hidden", viewModel.SuggestionProposalScopeSummary,
            StringComparison.Ordinal);
        viewModel.AcceptSelectedSuggestionBatchCommand.Execute(null);

        Assert.Contains("project changed after these suggestions were generated", viewModel.StatusText);
        Assert.Equal(currentDigest, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal("EUR", viewModel.Session.Project.Nodes
            .Single(node => node.NodeId == "sample-fact").Parameters["unit"]?.ToString());
        Assert.Equal(SuggestionState.Pending, suggestion.ReviewState);
    }

    [Fact]
    public async Task DesktopConnectedSuggestionBatch_PreservesDirtyCheckedDraftWhenSelectionChanges()
    {
        var seed = new MainWindowViewModel();
        var project = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(seed.Session.Project));
        var source = project.Nodes.Single(node => node.NodeId == "sample-value").ShallowCopy() with
        {
            NodeId = "sample-net-income-source",
            DisplayName = "Net income source field",
            Parameters = new Dictionary<string, object?>
            {
                ["bindingId"] = "sample-source",
                ["column"] = "Net Income"
            },
            Layout = new LayoutPosition(40, 440)
        };
        var fact = project.Nodes.Single(node => node.NodeId == "sample-fact").ShallowCopy() with
        {
            NodeId = "sample-net-income-fact",
            DisplayName = "XBRL Net income",
            Layout = new LayoutPosition(760, 440)
        };
        project.Nodes.Add(source);
        project.Nodes.Add(fact);
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "sample-net-income-value",
            SourceNodeId = source.NodeId,
            SourcePortName = "value",
            TargetNodeId = fact.NodeId,
            TargetPortName = "value"
        });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "sample-net-income-entity",
            SourceNodeId = "sample-entity",
            SourcePortName = "value",
            TargetNodeId = fact.NodeId,
            TargetPortName = "entity"
        });

        var viewModel = new MainWindowViewModel(project);
        viewModel.AllowSyntheticProfile = true;
        await viewModel.SuggestConnectedMappingsAsync();

        var revenue = Assert.Single(viewModel.AiSuggestions, row =>
            row.SourceFieldId == "sample-source:Revenue" && row.Concept == "synthetic:Revenue");
        var netIncome = Assert.Single(viewModel.AiSuggestions, row =>
            row.SourceFieldId == "sample-source:Net Income" && row.Concept == "synthetic:NetIncome");
        revenue.IsBatchSelected = true;
        netIncome.IsBatchSelected = true;
        viewModel.SelectedSuggestion = revenue;
        viewModel.SelectedSuggestionConcept = "synthetic:Cash";
        viewModel.Session.Select("sample-net-income-fact", additive: false);
        Assert.Equal("synthetic:Cash", viewModel.SelectedSuggestionConcept);
        viewModel.SelectedSuggestion = netIncome;

        var digestBefore = viewModel.Session.Project.ComputeSemanticDigest();
        viewModel.AcceptSelectedSuggestionBatchCommand.Execute(null);

        Assert.Contains("Save the checked candidate edits", viewModel.StatusText);
        Assert.Equal(digestBefore, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal(SuggestionState.Pending, revenue.ReviewState);
        Assert.Equal(SuggestionState.Pending, netIncome.ReviewState);
        viewModel.SelectedSuggestion = revenue;
        Assert.Equal("synthetic:Cash", viewModel.SelectedSuggestionConcept);
    }

    [Fact]
    public async Task DesktopConnectedSuggestionBatch_ExcludesSourceConnectedOnlyToFactEntity()
    {
        var seed = new MainWindowViewModel();
        var project = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(seed.Session.Project));
        var entitySource = project.Nodes.Single(node => node.NodeId == "sample-value").ShallowCopy() with
        {
            NodeId = "sample-entity-source",
            DisplayName = "Revenue entity field",
            Parameters = new Dictionary<string, object?>
            {
                ["bindingId"] = "entity-source",
                ["column"] = "Revenue"
            },
            OutputPorts = new List<PortDefinition>
            {
                new PortDefinition("value", PortShape.String, PortCardinality.Single)
            },
            Layout = new LayoutPosition(40, 440)
        };
        project.Bindings.Add(new SourceBinding
        {
            BindingId = "entity-source",
            Kind = "csv",
            DisplayName = "Entity source"
        });
        project.Nodes.Add(entitySource);
        project.Edges.RemoveAll(edge => edge.EdgeId == "sample-e3");
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "sample-entity-source-edge",
            SourceNodeId = entitySource.NodeId,
            SourcePortName = "value",
            TargetNodeId = "sample-fact",
            TargetPortName = "entity"
        });

        var viewModel = new MainWindowViewModel(project);
        viewModel.AllowSyntheticProfile = true;
        await viewModel.SuggestConnectedMappingsAsync();

        Assert.DoesNotContain(viewModel.AiSuggestions, row => row.SourceFieldId == "entity-source:Revenue");
        Assert.Contains(viewModel.Diagnostics, row =>
            row.Display.Contains("not connected to a fact value input", StringComparison.Ordinal));
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

    [Fact]
    public async Task DesktopSuggestionWorkflow_LockedAuditDatabaseBlocksGraphAcceptance()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-audit-lock-", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbPath = System.IO.Path.Combine(dir, "state.db");
            var store = new SuggestionAuditStore(new LocalApplicationDatabase(dbPath));
            var viewModel = await PrepareSuggestionViewModelAsync(dir, () => store);
            var digest = viewModel.Session.Project.ComputeSemanticDigest();
            var projectId = viewModel.Session.Project.ProjectId;
            var revision = viewModel.Session.Project.SemanticRevision;

            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 0
            }.ToString());
            writer.Open();
            using (var acquireWriteLock = writer.CreateCommand())
            {
                acquireWriteLock.CommandText = "BEGIN IMMEDIATE; INSERT INTO app_settings(key,value,updated_utc) VALUES('audit-lock-fixture','held','2026-11-06T00:00:00Z');";
                acquireWriteLock.ExecuteNonQuery();
            }

            viewModel.ApplySuggestedMappingCommand.Execute(null);

            Assert.Contains("audit persistence failed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rolled back", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(digest, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.Equal(SuggestionState.Pending, viewModel.SelectedSuggestion!.ReviewState);
            Assert.DoesNotContain(store.Read(projectId, revision), row => row.Decision == "accept");
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
            Assert.True(viewModel.HasUnresolvedSourceFieldMappings);
            Assert.False(viewModel.PreviewCommand.CanExecute(null));
            Assert.False(viewModel.ExecuteCommand.CanExecute(null));
            Assert.Contains("Revenue source field.column='Revenue'", viewModel.SourceInspectionSummary, StringComparison.Ordinal);
            Assert.Contains("Rebind each affected source-field node", viewModel.SourceInspectionSummary, StringComparison.Ordinal);

            viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-value");
            viewModel.SelectedSourceColumn = "Current";
            viewModel.BindSourceColumnCommand.Execute(null);

            Assert.False(viewModel.HasUnresolvedSourceFieldMappings);
            Assert.True(viewModel.PreviewCommand.CanExecute(null));
            Assert.True(viewModel.ExecuteCommand.CanExecute(null));
            Assert.Contains("Bound Current", viewModel.StatusText, StringComparison.Ordinal);
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
        Assert.Contains("synthetic:Revenue", viewModel.SelectedFactConceptIdentity, StringComparison.Ordinal);
        Assert.Contains("Revenue", viewModel.SelectedFactSourceSummary, StringComparison.Ordinal);
        Assert.Contains("Scale to thousands", viewModel.SelectedFactSourceSummary, StringComparison.Ordinal);
        Assert.Contains("2025-01-01", viewModel.SelectedFactPeriodSummary, StringComparison.Ordinal);
        Assert.True(viewModel.CanEditSelectedFactEntity);
        Assert.Equal("ABC-Corp", viewModel.SelectedFactEntityIdentifier);
        Assert.Contains(viewModel.SelectedFactInputBindings, row => row.TargetPortName == "value");

        viewModel.Session.ClearSelection();

        Assert.Null(viewModel.SelectedNode);
        Assert.False(viewModel.CanEditSelectedFact);
    }

    [Fact]
    public void FactInspectorRebindsCompatibleInputAsOneUndoableValidatedEdit()
    {
        var seed = new MainWindowViewModel();
        var project = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(seed.Session.Project));
        project.Nodes.Add(new MappingNode
        {
            NodeId = "sample-prior",
            NodeType = NodeTypeConstants.ExcelField,
            DisplayName = "Prior revenue",
            Parameters = new Dictionary<string, object?>
            {
                ["bindingId"] = "sample-source",
                ["column"] = "Prior"
            },
            OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) }
        });
        var viewModel = new MainWindowViewModel(project);
        viewModel.AllowSyntheticProfile = true;
        var fact = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        viewModel.SelectedNode = fact;

        var input = Assert.Single(viewModel.SelectedFactInputBindings, row => row.TargetPortName == "value");
        var originalDigest = viewModel.Session.Project.ComputeSemanticDigest();
        var priorOption = Assert.Single(input.Options, option => option.NodeId == "sample-prior");
        input.SelectedOption = priorOption;

        var rebound = Assert.Single(viewModel.Session.Project.Edges, edge =>
            edge.TargetNodeId == "sample-fact" && edge.TargetPortName == "value");
        Assert.Equal("sample-prior", rebound.SourceNodeId);
        Assert.NotEqual(originalDigest, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.True(viewModel.StatusText.Contains("validation", StringComparison.OrdinalIgnoreCase),
            $"Unexpected rebind feedback: {viewModel.StatusText}");

        viewModel.Session.Undo();

        var restored = Assert.Single(viewModel.Session.Project.Edges, edge =>
            edge.TargetNodeId == "sample-fact" && edge.TargetPortName == "value");
        Assert.Equal("sample-scale", restored.SourceNodeId);
        Assert.Equal(originalDigest, viewModel.Session.Project.ComputeSemanticDigest());
    }

    [Fact]
    public void DesignerSessionRejectsIncompatibleInputRebindWithoutChangingProject()
    {
        var viewModel = new MainWindowViewModel();
        var digest = viewModel.Session.Project.ComputeSemanticDigest();

        var rebound = viewModel.Session.TryRebindInput(
            "sample-fact", "value", "sample-entity", "value", out var reason);

        Assert.False(rebound);
        Assert.Contains("Cannot connect String output", reason, StringComparison.Ordinal);
        Assert.Equal(digest, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal("sample-scale", Assert.Single(viewModel.Session.Project.Edges,
            edge => edge.TargetNodeId == "sample-fact" && edge.TargetPortName == "value").SourceNodeId);
    }

    [Fact]
    public void FactInspectorKeepsPeriodBindingWhenProfileValidationRejectsTheCandidate()
    {
        var seed = new MainWindowViewModel();
        var project = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(seed.Session.Project));
        project.Nodes.AddRange(new[]
        {
            new MappingNode
            {
                NodeId = "instant-start",
                NodeType = NodeTypeConstants.Constant,
                DisplayName = "Instant start",
                Parameters = new Dictionary<string, object?> { ["literal"] = "2026-01-01", ["shape"] = "String" },
                OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) }
            },
            new MappingNode
            {
                NodeId = "instant-period",
                NodeType = NodeTypeConstants.BuildInstantDate,
                DisplayName = "Instant period",
                InputPorts = new()
                {
                    new PortDefinition("dateText", PortShape.String, PortCardinality.Single)
                },
                OutputPorts = new() { new PortDefinition("value", PortShape.Instant, PortCardinality.Single) }
            }
        });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "instant-start-edge",
            SourceNodeId = "instant-start",
            SourcePortName = "value",
            TargetNodeId = "instant-period",
            TargetPortName = "dateText"
        });
        var viewModel = new MainWindowViewModel(project);
        viewModel.AllowSyntheticProfile = true;
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        var periodInput = Assert.Single(viewModel.SelectedFactInputBindings, row => row.TargetPortName == "period");
        var oldSource = Assert.IsType<FactInputOption>(periodInput.SelectedOption);
        var instantSource = Assert.Single(periodInput.Options, option => option.NodeId == "instant-period");
        var originalDigest = viewModel.Session.Project.ComputeSemanticDigest();

        periodInput.SelectedOption = instantSource;

        Assert.Equal(oldSource, periodInput.SelectedOption);
        Assert.Contains("XBR004", periodInput.ValidationMessage, StringComparison.Ordinal);
        Assert.Equal(originalDigest, viewModel.Session.Project.ComputeSemanticDigest());
        Assert.Equal("sample-period", Assert.Single(viewModel.Session.Project.Edges,
            edge => edge.TargetNodeId == "sample-fact" && edge.TargetPortName == "period").SourceNodeId);
    }

    [Fact]
    public async Task SourceSchemaRecheck_DetectsDriftAndKeepsSavedMappingPinnedUntilReload()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-source-schema-recheck", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var csv = System.IO.Path.Combine(dir, "source.csv");
        var projectPath = System.IO.Path.Combine(dir, "mapping.xmap");
        File.WriteAllText(csv, "Revenue\n1250\n");
        try
        {
            var viewModel = new MainWindowViewModel();
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(projectPath);
            await viewModel.LoadSourceAsync(csv);

            var originalFingerprint = Assert.Single(viewModel.Session.Project.Bindings,
                binding => binding.Kind != "param").SchemaFingerprint;
            Assert.False(string.IsNullOrWhiteSpace(originalFingerprint));
            Assert.True(viewModel.CanRevalidateSourceSchema);
            Assert.True(viewModel.PreviewCommand.CanExecute(null));
            Assert.True(viewModel.ExecuteCommand.CanExecute(null));
            var originalSemanticDigest = viewModel.Session.Project.ComputeSemanticDigest();

            await viewModel.RevalidateSourceSchemaAsync();

            Assert.False(viewModel.IsSourceSchemaDrifted);
            Assert.Contains("recheck passed", viewModel.SourceInspectionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalFingerprint, viewModel.Session.Project.Bindings.Single(binding => binding.Kind != "param").SchemaFingerprint);

            File.WriteAllText(csv, "Operating Revenue\n1250\n");
            await viewModel.RevalidateSourceSchemaAsync();

            Assert.True(viewModel.IsSourceSchemaDrifted);
            Assert.False(viewModel.PreviewCommand.CanExecute(null));
            Assert.False(viewModel.ExecuteCommand.CanExecute(null));
            Assert.Contains("schema changed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Operating Revenue", viewModel.SourceColumns);
            Assert.Equal(originalFingerprint, viewModel.Session.Project.Bindings.Single(binding => binding.Kind != "param").SchemaFingerprint);
            Assert.Equal(originalSemanticDigest, viewModel.Session.Project.ComputeSemanticDigest());
            Assert.Equal(originalFingerprint, ProjectSerializer.Deserialize(
                ProjectSerializer.Serialize(viewModel.Session.Project)).Bindings.Single(binding => binding.Kind != "param").SchemaFingerprint);

            await viewModel.LoadSourceAsync(csv);

            Assert.False(viewModel.IsSourceSchemaDrifted);
            Assert.True(viewModel.HasUnresolvedSourceFieldMappings);
            Assert.False(viewModel.PreviewCommand.CanExecute(null));
            Assert.False(viewModel.ExecuteCommand.CanExecute(null));
            Assert.NotEqual(originalFingerprint, viewModel.Session.Project.Bindings.Single(binding => binding.Kind != "param").SchemaFingerprint);
            Assert.Contains("Operating Revenue", viewModel.SourceColumns);

            viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-value");
            viewModel.SelectedSourceColumn = "Operating Revenue";
            viewModel.BindSourceColumnCommand.Execute(null);

            Assert.False(viewModel.HasUnresolvedSourceFieldMappings);
            Assert.True(viewModel.PreviewCommand.CanExecute(null));
            Assert.True(viewModel.ExecuteCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SourceSchemaRecheck_BlocksMissingMappedFieldUntilSourceFieldIsRebound()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-source-column-recheck", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var csv = System.IO.Path.Combine(dir, "source.csv");
        var projectPath = System.IO.Path.Combine(dir, "mapping.xmap");
        File.WriteAllText(csv, "Operating Revenue\n1250\n");
        try
        {
            var viewModel = new MainWindowViewModel();
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(projectPath);

            await viewModel.LoadSourceAsync(csv);

            Assert.False(viewModel.IsSourceSchemaDrifted);
            Assert.True(viewModel.HasUnresolvedSourceFieldMappings);
            Assert.False(viewModel.PreviewCommand.CanExecute(null));
            Assert.False(viewModel.ExecuteCommand.CanExecute(null));
            Assert.Contains("Revenue source field.column='Revenue'", viewModel.SourceInspectionSummary, StringComparison.Ordinal);
            Assert.Contains("Rebind each affected source-field node", viewModel.SourceInspectionSummary, StringComparison.Ordinal);

            await viewModel.RevalidateSourceSchemaAsync();

            Assert.False(viewModel.IsSourceSchemaDrifted);
            Assert.True(viewModel.HasUnresolvedSourceFieldMappings);
            Assert.Contains("schema fingerprint is unchanged", viewModel.SourceInspectionSummary, StringComparison.Ordinal);
            Assert.Contains("Operating Revenue", viewModel.SourceColumns);

            viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-value");
            viewModel.SelectedSourceColumn = "Operating Revenue";
            viewModel.BindSourceColumnCommand.Execute(null);

            Assert.False(viewModel.HasUnresolvedSourceFieldMappings);
            Assert.True(viewModel.PreviewCommand.CanExecute(null));
            Assert.True(viewModel.ExecuteCommand.CanExecute(null));
            Assert.Equal("Operating Revenue", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-value")
                .Parameters.GetValueOrDefault("column")?.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
            Assert.Contains("destination selection changed", viewModel.DestinationPreviewSummary, StringComparison.OrdinalIgnoreCase);
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
    public async Task DesktopCanSaveSqliteUpdateOnlyModeInDestinationPlan()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-desktop-update-only", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var workbook = TestFixtures.MaterializeWorkbook(dir);
        var projectPath = System.IO.Path.Combine(dir, "mapping.xmap");
        var database = System.IO.Path.Combine(dir, "target.sqlite");
        await CreateFactDatabaseAsync(database);
        var project = TestFixtures.BuildRevenueMappingProject(workbook, approved: true);
        try
        {
            var viewModel = new MainWindowViewModel(project, new TestProjectFileDialog(database));
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(projectPath);
            await viewModel.LoadDestinationAsync(database);

            Assert.True(viewModel.DestinationSchemaIsValid);
            viewModel.UpdateExistingDestinationRowsOnly = true;
            Assert.Contains("update existing only", viewModel.DestinationFormatDisplay, StringComparison.Ordinal);
            Assert.False(viewModel.CanSaveDestinationPlan);
            viewModel.IsDestinationSchemaConfirmed = true;
            Assert.True(viewModel.CanSaveDestinationPlan);

            viewModel.SaveDestinationPlanCommand.Execute(null);

            Assert.Equal("update-only", viewModel.Session.Project.Destination?.WriteMode);
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
    public async Task CsvAndExcelDestinationFormatsUseV2DefaultsAndKeepSavedV1Visible()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-destination-contract-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(directory);
            var projectPath = Path.Combine(directory, "mapping.xmap");
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            var defaultViewModel = new MainWindowViewModel(project);

            defaultViewModel.SelectedDestinationKind = defaultViewModel.DestinationKindOptions
                .Single(option => option.Kind == "csv");
            Assert.Contains("csv-records-v2", defaultViewModel.DestinationFormatDisplay, StringComparison.Ordinal);
            defaultViewModel.SelectedDestinationKind = defaultViewModel.DestinationKindOptions
                .Single(option => option.Kind == "excel");
            Assert.Contains("xlsx-records-v2", defaultViewModel.DestinationFormatDisplay, StringComparison.Ordinal);

            project.Destination = new DestinationPlan
            {
                Kind = "csv",
                Locator = "legacy.csv",
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = "csv-records-v1",
                WriteMode = "replace"
            };
            var dialog = new TestProjectFileDialog(null) { ProjectOpenPath = projectPath };
            var writer = new MainWindowViewModel(project, dialog);
            writer.AllowSyntheticProfile = true;
            writer.SaveProjectTo(projectPath);

            var reopened = new MainWindowViewModel(TestFixtures.BuildRevenueMappingProject(workbook), dialog);
            reopened.AllowSyntheticProfile = true;
            await reopened.OpenProjectAsync();

            Assert.Contains("csv-records-v1", reopened.DestinationFormatDisplay, StringComparison.Ordinal);
            reopened.SelectedDestinationKind = reopened.DestinationKindOptions
                .Single(option => option.Kind == "excel");
            Assert.Contains("xlsx-records-v2", reopened.DestinationFormatDisplay, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
        public bool ConfirmIndependentCopy { get; set; }
        public bool ConfirmBackupRecovery { get; set; }
        public LegacyProjectProfileSelection? LegacyProfileSelection { get; set; }
        public bool ConfirmLegacyAdoption { get; set; }
        public Action? OnLegacyProfileSelected { get; set; }
        public int LegacyProfileSelectionCallCount { get; private set; }
        public int AdoptionConfirmationCount { get; private set; }
        public int SaveProjectRequestCount { get; private set; }
        public IReadOnlyList<string> ConfirmedAddedIdentityFields { get; private set; } = Array.Empty<string>();
        public int BackupRecoveryConfirmationCount { get; private set; }
        public int ConfirmationCount { get; private set; }
        public string? ConfirmedTable { get; private set; }
        public IReadOnlyList<string> ConfirmedBusinessKey { get; private set; } = Array.Empty<string>();
        public int ConfirmedFactCount { get; private set; }
        public string? ConfirmedFilePath { get; private set; }
        public string? ConfirmedFileFormat { get; private set; }

        public Task<string?> OpenProjectAsync() => Task.FromResult(ProjectOpenPath);
        public Task<string?> SaveProjectAsync()
        {
            SaveProjectRequestCount++;
            return Task.FromResult(ProjectSavePath);
        }
        public Task<LegacyProjectProfileSelection?> SelectLegacyProjectProfileAsync(
            IReadOnlyList<LegacyProjectProfileOption> candidates,
            string? declaredProfileId,
            string? declaredProfileDigest)
        {
            LegacyProfileSelectionCallCount++;
            OnLegacyProfileSelected?.Invoke();
            return Task.FromResult(LegacyProfileSelection);
        }
        public Task<bool> ConfirmLegacyProjectProfileAdoptionAsync(
            string sourcePath,
            string destinationPath,
            LegacyProjectProfileOption selectedProfile,
            IReadOnlyList<string> addedIdentityFields)
        {
            AdoptionConfirmationCount++;
            ConfirmedAddedIdentityFields = addedIdentityFields.ToArray();
            return Task.FromResult(ConfirmLegacyAdoption);
        }
        public Task<string?> OpenFunctionPackageAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveFunctionPackageAsync() => Task.FromResult<string?>(null);
        public Task<bool> ConfirmSaveAsIndependentCopyAsync(string projectName, string destinationPath)
            => Task.FromResult(ConfirmIndependentCopy);
        public Task<bool> ConfirmOpenProjectBackupAsync(string projectPath, string backupPath)
        {
            BackupRecoveryConfirmationCount++;
            return Task.FromResult(ConfirmBackupRecovery);
        }
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
