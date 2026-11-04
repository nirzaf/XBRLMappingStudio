using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

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
            Directory.Delete(dir, recursive: true);
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

            await viewModel.LoadSourceAsync(workbook);

            Assert.Contains("Current", viewModel.SourceColumns);
            Assert.Contains("Prior", viewModel.SourceColumns);
            Assert.Equal("Loaded 4 fields from revenue.xlsx.", viewModel.StatusText);
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
            var viewModel = new MainWindowViewModel(ProjectWithSource());
            viewModel.SaveProjectTo(System.IO.Path.Combine(dir, "mapping.xmap"));

            await viewModel.LoadSourceAsync(database);

            Assert.Contains("account", viewModel.SourceColumns);
            Assert.Contains("Primary key: account", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("stable row identity enabled", viewModel.StatusText, StringComparison.Ordinal);
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
            Assert.Contains("Dry run complete: 1 fact row", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("No database rows were written", viewModel.StatusText, StringComparison.Ordinal);
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
        public bool ConfirmationResult { get; set; }
        public int ConfirmationCount { get; private set; }
        public string? ConfirmedTable { get; private set; }
        public IReadOnlyList<string> ConfirmedBusinessKey { get; private set; } = Array.Empty<string>();
        public int ConfirmedFactCount { get; private set; }

        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveReportAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenSourceAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenDestinationDatabaseAsync() => Task.FromResult(destinationPath);

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
    }
}
