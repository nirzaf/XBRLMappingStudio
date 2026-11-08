using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.ComponentModel;
using XBRLMappingStudio.Desktop;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Desktop.Tests;

public sealed class FunctionLibraryEditorTests
{
    private static MappingProject Project(string projectId = "function-editor-test")
    {
        var profile = SyntheticProfile.Build();
        return new MappingProject
        {
            ProjectId = projectId,
            Name = "Function editor test",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest,
            FunctionCatalogVersion = FunctionCatalog.Default.Version
        };
    }

    [Fact]
    public void PinTypedFunctionCopiesPinnedRevisionSignature()
    {
        var project = Project();
        var definition = new FunctionDefinition
        {
            FunctionId = "typed", Name = "Typed", CatalogVersion = FunctionCatalog.Default.Version,
            Inputs = new() { new("text", PortShape.String, PortCardinality.Single), new("items", PortShape.Record, PortCardinality.Collection) },
            Outputs = new() { new("label", PortShape.String, PortCardinality.Single), new("ok", PortShape.Boolean, PortCardinality.Single) }
        };
        project.Functions.Add(definition);
        var session = new DesignerSession(project);
        var editor = new FunctionLibraryEditorViewModel(() => session);
        editor.SelectedFunction = Assert.Single(editor.Functions);
        editor.PinSelectedFunctionCommand.Execute(null);
        var call = Assert.Single(session.Project.Nodes, node => node.NodeType == NodeTypeConstants.FunctionCall);
        Assert.Equal(definition.Inputs, call.InputPorts);
        Assert.Equal(definition.Outputs, call.OutputPorts);
        Assert.NotSame(definition.Inputs, call.InputPorts);
        Assert.NotSame(definition.Outputs, call.OutputPorts);
    }

    [AvaloniaFact]
    public async Task FunctionAuthoringWindowSupportsDraftUndoRedoPreviewSaveAndPin()
    {
        var viewModel = new MainWindowViewModel(Project(), new FunctionPackageDialog
        {
            CloseChoice = UnsavedProjectChoice.Discard
        });
        MainWindow? window = null;
        try
        {
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            var tabs = Assert.IsType<TabControl>(window.FindControl<TabControl>("WorkspaceTabs"));
            tabs.SelectedIndex = 1;

            var editor = viewModel.FunctionLibrary;
            var addScale = Assert.IsType<Button>(window.FindControl<Button>("AddFunctionScaleButton"));
            var undo = Assert.IsType<Button>(window.FindControl<Button>("UndoFunctionDraftButton"));
            var redo = Assert.IsType<Button>(window.FindControl<Button>("RedoFunctionDraftButton"));
            var scaleFactor = Assert.IsType<TextBox>(window.FindControl<TextBox>("FunctionScaleFactorTextBox"));
            var applyParameters = Assert.IsType<Button>(window.FindControl<Button>("ApplyFunctionOperationParametersButton"));
            var previewInput = Assert.IsType<TextBox>(window.FindControl<TextBox>("SyntheticFunctionPreviewInputTextBox"));
            var testDraft = Assert.IsType<Button>(window.FindControl<Button>("TestFunctionDraftButton"));
            var previewStatus = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("FunctionPreviewStatusText"));
            var draftName = Assert.IsType<TextBox>(window.FindControl<TextBox>("FunctionDraftNameTextBox"));
            var saveRevision = Assert.IsType<Button>(window.FindControl<Button>("SaveFunctionRevisionButton"));
            var pinRevision = Assert.IsType<Button>(window.FindControl<Button>("PinFunctionRevisionButton"));

            // Observe the loaded Functions tab before acting.
            Assert.Equal("Function graph. Decimal input passes through to output.", editor.DraftGraphSummary);
            Assert.Equal("Run a synthetic numeric preview to inspect the draft output.", previewStatus.Text);
            Assert.Equal("Undo function draft edit", AutomationProperties.GetName(undo));
            Assert.Equal("Redo function draft edit", AutomationProperties.GetName(redo));
            Assert.False(undo.IsEnabled);
            Assert.False(redo.IsEnabled);
            Assert.True(addScale.IsEnabled);

            // Add an operation, edit its parameter, then undo and redo that inspector edit.
            addScale.Command!.Execute(addScale.CommandParameter);

            var scaleNode = Assert.Single(editor.Session.Project.Nodes,
                node => node.NodeType == NodeTypeConstants.Scale);
            Assert.True(undo.IsEnabled);
            Assert.False(redo.IsEnabled);
            Assert.True(scaleFactor.IsEnabled);
            Assert.Equal("1", scaleFactor.Text);

            scaleFactor.Text = string.Empty;
            scaleFactor.Text = "10";
            Assert.Equal("10", editor.SelectedScaleFactor);
            applyParameters.Command!.Execute(applyParameters.CommandParameter);
            Assert.Equal("10", Parameter(scaleNode, "factor"));

            undo.Command!.Execute(undo.CommandParameter);
            Assert.Equal("1", Parameter(editor.Session.Project.Nodes.Single(node =>
                node.NodeType == NodeTypeConstants.Scale), "factor"));
            Assert.True(redo.IsEnabled);
            redo.Command!.Execute(redo.CommandParameter);
            Assert.Equal("10", Parameter(editor.Session.Project.Nodes.Single(node =>
                node.NodeType == NodeTypeConstants.Scale), "factor"));
            Assert.False(redo.IsEnabled);
            Assert.Empty(viewModel.Session.Project.Functions);

            // Verify a synthetic output, save the immutable revision, and pin that revision.
            previewInput.Text = string.Empty;
            previewInput.Text = "5";
            Assert.Equal("5", editor.PreviewInput);
            var previewCompleted = WaitForEditorPropertyAsync(editor,
                nameof(FunctionLibraryEditorViewModel.PreviewStatus),
                () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal)
                    || editor.PreviewStatus.StartsWith("Preview blocked:", StringComparison.Ordinal));
            testDraft.Command!.Execute(testDraft.CommandParameter);
            await previewCompleted;
            Assert.Contains("output: 50", previewStatus.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No filing or publication occurred", previewStatus.Text, StringComparison.Ordinal);

            draftName.Text = string.Empty;
            draftName.Text = "Scale by ten";
            Assert.Equal("Scale by ten", editor.DraftName);
            var saveCompleted = WaitForEditorPropertyAsync(editor,
                nameof(FunctionLibraryEditorViewModel.Status),
                () => editor.Status.StartsWith("Saved immutable function revision", StringComparison.Ordinal)
                    || editor.Status.StartsWith("Function revision was not saved", StringComparison.Ordinal));
            saveRevision.Command!.Execute(saveRevision.CommandParameter);
            await saveCompleted;
            var saved = Assert.Single(viewModel.Session.Project.Functions);
            Assert.Equal(1, saved.Revision);
            Assert.Equal("Scale by ten", saved.Name);
            Assert.Equal("10", Parameter(saved, "factor"));
            Assert.Contains("Saved immutable function revision", editor.Status, StringComparison.Ordinal);
            Assert.True(pinRevision.IsEnabled);

            pinRevision.Command!.Execute(pinRevision.CommandParameter);
            var pinnedCall = Assert.Single(viewModel.Session.Project.Nodes,
                node => node.NodeType == NodeTypeConstants.FunctionCall);
            Assert.Equal(saved.FunctionId, Parameter(pinnedCall, "functionId"));
            Assert.Equal("1", Parameter(pinnedCall, "revision"));
        }
        finally
        {
            if (window is not null) window.DataContext = null;
            window?.Close();
            viewModel.Dispose();
        }
    }

    private static async Task WaitForEditorPropertyAsync(FunctionLibraryEditorViewModel editor,
        string propertyName, Func<bool> completed)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == propertyName && completed()) completion.TrySetResult();
        };
        editor.PropertyChanged += handler;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { editor.PropertyChanged -= handler; }
    }

    [Fact]
    public async Task AuthoringPreviewAndRevisionSelection_PreservePinnedBodiesAndUndoPinning()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);

        editor.DraftName = "Scale amount";
        editor.AddScaleCommand.Execute(null);
        editor.SelectedScaleFactor = "10";
        editor.ApplyNodeParametersCommand.Execute(null);
        editor.PreviewInput = "5";
        var firstPreview = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await firstPreview;
        Assert.Contains("output: 50", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);

        var firstSave = WaitForEditorPropertyAsync(editor, nameof(editor.Status),
            () => editor.Status.StartsWith("Saved immutable function revision", StringComparison.Ordinal));
        editor.SaveFunctionRevisionCommand.Execute(null);
        await firstSave;
        var revision1 = Assert.Single(projectSession.Project.Functions);
        Assert.Equal(1, revision1.Revision);

        editor.SelectedFunction = editor.Functions.Single(row => row.FunctionId == revision1.FunctionId
            && row.Revision == 1);
        var loadedScale = editor.Session.Project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        editor.Session.Select(loadedScale.NodeId, additive: false);
        editor.SelectedScaleFactor = "100";
        editor.ApplyNodeParametersCommand.Execute(null);
        var secondPreview = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await secondPreview;
        Assert.Contains("output: 500", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
        var secondSave = WaitForEditorPropertyAsync(editor, nameof(editor.Status),
            () => editor.Status.StartsWith("Saved immutable function revision", StringComparison.Ordinal));
        editor.SaveFunctionRevisionCommand.Execute(null);
        await secondSave;

        Assert.Equal(new[] { 1, 2 }, projectSession.Project.Functions
            .Where(function => function.FunctionId == revision1.FunctionId)
            .Select(function => function.Revision));
        Assert.Equal("10", Parameter(projectSession.Project.Functions.Single(function => function.Revision == 1), "factor"));
        Assert.Equal("100", Parameter(projectSession.Project.Functions.Single(function => function.Revision == 2), "factor"));

        editor.SelectedFunction = editor.Functions.Single(row => row.FunctionId == revision1.FunctionId
            && row.Revision == 1);
        var pinnedRevisionPreview = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await pinnedRevisionPreview;
        Assert.Contains("output: 50", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
        editor.PinSelectedFunctionCommand.Execute(null);

        var call = Assert.Single(projectSession.Project.Nodes, node => node.NodeType == NodeTypeConstants.FunctionCall);
        Assert.Equal(revision1.FunctionId, Parameter(call, "functionId"));
        Assert.Equal("1", Parameter(call, "revision"));
        projectSession.Undo();
        Assert.DoesNotContain(projectSession.Project.Nodes, node => node.NodeType == NodeTypeConstants.FunctionCall);
        Assert.Equal(2, projectSession.Project.Functions.Count);
        projectSession.Undo();
        Assert.Single(projectSession.Project.Functions, function => function.FunctionId == revision1.FunctionId);
        projectSession.Redo();
        Assert.Equal(2, projectSession.Project.Functions.Count);
    }

    [Fact]
    public async Task SaveFunctionRevision_RejectsInvalidDraftWithoutChangingProject()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);
        editor.AddScaleCommand.Execute(null);
        var scale = editor.Session.Project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        editor.Session.SetNodeParameter(scale.NodeId, "factor", "not-a-decimal");

        var saveRejected = WaitForEditorPropertyAsync(editor, nameof(editor.Status),
            () => editor.Status.StartsWith("Function revision was not saved", StringComparison.Ordinal));
        editor.SaveFunctionRevisionCommand.Execute(null);
        await saveRejected;

        Assert.Empty(projectSession.Project.Functions);
        Assert.Contains("synthetic validation failed", editor.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyntheticPreview_ShowsOrderedPartialValuesForEachFunctionNode()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);

        editor.AddScaleCommand.Execute(null);
        var scale = editor.Session.Project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        editor.Session.Select(scale.NodeId, additive: false);
        editor.SelectedScaleFactor = "10";
        editor.ApplyNodeParametersCommand.Execute(null);
        editor.AddChangeSignCommand.Execute(null);
        editor.AddRoundCommand.Execute(null);
        var round = editor.Session.Project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Round);
        editor.Session.Select(round.NodeId, additive: false);
        editor.SelectedRoundDigits = "0";
        editor.SelectedRoundMode = "HalfUp";
        editor.ApplyNodeParametersCommand.Execute(null);

        editor.PreviewInput = "1.26";
        var previewCompleted = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await previewCompleted;

        Assert.Equal(3, editor.NodePreviews.Count);
        Assert.Equal(new[] { NodeTypeConstants.Scale, NodeTypeConstants.ChangeSign, NodeTypeConstants.Round },
            editor.NodePreviews.Select(row => editor.Session.Project.Nodes
                .Single(node => node.NodeId == row.NodeId).NodeType));
        Assert.Equal(12.6m, editor.NodePreviews[0].Value);
        Assert.Equal(-12.6m, editor.NodePreviews[1].Value);
        Assert.Equal(-13m, editor.NodePreviews[2].Value);
        Assert.All(editor.NodePreviews, row => Assert.Contains("Not a filing result", row.Preview,
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains("output: -13", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyntheticPreview_BoundsDisplayedIntermediateNodes()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);
        for (var index = 0; index < 66; index++)
            editor.AddChangeSignCommand.Execute(null);

        var previewCompleted = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await previewCompleted;

        Assert.Equal(65, editor.NodePreviews.Count);
        Assert.Equal(64, editor.NodePreviews.Count(row => row.NodeId.Length > 0));
        Assert.Contains("first 64 of 66", editor.NodePreviews[^1].Preview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RatioAuthoring_RequiresAndPreviewsExplicitDenominatorConstant()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);

        editor.AddRatioCommand.Execute(null);

        var ratio = Assert.Single(editor.Session.Project.Nodes, node => node.NodeType == NodeTypeConstants.Ratio);
        var denominator = Assert.Single(editor.Session.Project.Nodes,
            node => node.NodeType == NodeTypeConstants.Constant);
        Assert.Contains("Operations: Ratio", editor.DraftGraphSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 explicit Decimal side-input constant", editor.DraftGraphSummary,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ratio → Set", editor.DraftGraphSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(denominator.NodeId, Assert.Single(editor.Session.SelectedNodeIds));
        Assert.Equal("", Parameter(denominator, "literal"));
        Assert.Equal(3, editor.Session.Project.Edges.Count);

        editor.PreviewInput = "10";
        var missingDenominatorPreview = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Preview blocked:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await missingDenominatorPreview;
        Assert.Contains("Preview blocked", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
        var invalidSave = WaitForEditorPropertyAsync(editor, nameof(editor.Status),
            () => editor.Status.StartsWith("Function revision was not saved", StringComparison.Ordinal));
        editor.SaveFunctionRevisionCommand.Execute(null);
        await invalidSave;
        Assert.Empty(projectSession.Project.Functions);
        Assert.Contains("not saved", editor.Status, StringComparison.OrdinalIgnoreCase);

        editor.SelectedConstantValue = "2";
        editor.ApplyNodeParametersCommand.Execute(null);
        var completedPreview = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await completedPreview;

        Assert.Equal("2", Parameter(denominator, "literal"));
        Assert.Equal(5m, Assert.Single(editor.NodePreviews, row => row.NodeId == ratio.NodeId).Value);
        Assert.Contains("output: 5", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CoalesceAuthoring_InsertsAndReviewsExplicitFallbackConstant()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);

        editor.AddCoalesceCommand.Execute(null);
        var coalesce = Assert.Single(editor.Session.Project.Nodes, node => node.NodeType == NodeTypeConstants.Coalesce);
        var fallback = Assert.Single(editor.Session.Project.Nodes, node => node.NodeType == NodeTypeConstants.Constant);
        editor.SelectedConstantValue = "0";
        editor.ApplyNodeParametersCommand.Execute(null);
        editor.PreviewInput = "12.5";
        var previewCompleted = WaitForEditorPropertyAsync(editor, nameof(editor.PreviewStatus),
            () => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal));
        editor.TestFunctionCommand.Execute(null);
        await previewCompleted;

        Assert.Equal("0", Parameter(fallback, "literal"));
        Assert.Equal(12.5m, Assert.Single(editor.NodePreviews, row => row.NodeId == coalesce.NodeId).Value);
        Assert.Contains("output: 12.5", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveFunctionRevision_RefusesDraftAfterActiveProjectChanges()
    {
        var activeSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => activeSession);
        editor.AddScaleCommand.Execute(null);

        activeSession = new DesignerSession(Project("other-function-editor-test"));
        editor.RefreshFromProject();
        editor.SaveFunctionRevisionCommand.Execute(null);

        Assert.Empty(activeSession.Project.Functions);
        Assert.Contains("different project", editor.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FunctionPackageCommands_ExportSelectedRevisionAndImportAsNewIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-function-package", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var packagePath = Path.Combine(directory, "scale.xfunc.json");
        try
        {
            var sourceSession = new DesignerSession(Project());
            var sourceDialog = new FunctionPackageDialog { SavePath = packagePath };
            var sourceEditor = new FunctionLibraryEditorViewModel(() => sourceSession, sourceDialog)
            {
                DraftName = "Exported scale"
            };
            sourceEditor.AddScaleCommand.Execute(null);
            sourceEditor.SelectedScaleFactor = "1000";
            sourceEditor.ApplyNodeParametersCommand.Execute(null);
            sourceEditor.SaveFunctionRevisionCommand.Execute(null);
            await WaitForStatusAsync(sourceEditor, "Saved immutable function revision");
            sourceEditor.ExportFunctionPackageCommand.Execute(null);
            await WaitForStatusAsync(sourceEditor, "Exported ");
            Assert.True(File.Exists(packagePath));

            var destinationSession = new DesignerSession(Project());
            var destinationDialog = new FunctionPackageDialog { OpenPath = packagePath };
            var destinationEditor = new FunctionLibraryEditorViewModel(() => destinationSession, destinationDialog)
            {
                ImportFunctionId = "imported-scale",
                ImportFunctionName = "Imported scale"
            };
            destinationEditor.ImportFunctionPackageCommand.Execute(null);
            await WaitForStatusAsync(destinationEditor, "Imported ");

            var imported = Assert.Single(destinationSession.Project.Functions);
            Assert.Equal("imported-scale", imported.FunctionId);
            Assert.Equal("Imported scale", imported.Name);
            Assert.Equal("1000", Parameter(imported, "factor"));
            destinationSession.Undo();
            Assert.Empty(destinationSession.Project.Functions);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPackage_RejectsRecursiveFunctionWithoutChangingProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-recursive-function-package", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var packagePath = Path.Combine(directory, "recursive.xfunc.json");
        try
        {
            var decimalPort = new PortDefinition("value", PortShape.Decimal, PortCardinality.Single);
            var input = new MappingNode
            {
                NodeId = "recursive-input",
                NodeType = "functionInput",
                DisplayName = "Input",
                OutputPorts = new List<PortDefinition> { decimalPort }
            };
            var call = new MappingNode
            {
                NodeId = "recursive-call",
                NodeType = NodeTypeConstants.FunctionCall,
                DisplayName = "Recursive call",
                Parameters = new Dictionary<string, object?>
                {
                    ["functionId"] = "recursive",
                    ["revision"] = "1"
                },
                InputPorts = new List<PortDefinition> { new("input", PortShape.Decimal, PortCardinality.Single) },
                OutputPorts = new List<PortDefinition> { decimalPort }
            };
            var output = new MappingNode
            {
                NodeId = "recursive-output",
                NodeType = "functionOutput",
                DisplayName = "Output",
                InputPorts = new List<PortDefinition> { decimalPort }
            };
            var function = new FunctionDefinition
            {
                FunctionId = "recursive",
                Name = "Recursive",
                Revision = 1,
                CatalogVersion = FunctionCatalog.Default.Version,
                Inputs = new List<PortDefinition> { new("input", PortShape.Decimal, PortCardinality.Single) },
                Outputs = new List<PortDefinition> { decimalPort },
                Nodes = new List<MappingNode> { input, call, output },
                Edges = new List<MappingEdge>
                {
                    new() { EdgeId = "recursive-e1", SourceNodeId = input.NodeId, SourcePortName = "value", TargetNodeId = call.NodeId, TargetPortName = "input" },
                    new() { EdgeId = "recursive-e2", SourceNodeId = call.NodeId, SourcePortName = "value", TargetNodeId = output.NodeId, TargetPortName = "value" }
                }
            };
            await File.WriteAllTextAsync(packagePath, FunctionPackageSerializer.Serialize(new FunctionPackageDocument
            {
                RootFunctionId = "recursive",
                RootRevision = 1,
                Functions = new List<FunctionDefinition> { function }
            }));

            var destination = new DesignerSession(Project());
            var editor = new FunctionLibraryEditorViewModel(() => destination,
                new FunctionPackageDialog { OpenPath = packagePath })
            {
                ImportFunctionId = "recursive-import"
            };
            editor.ImportFunctionPackageCommand.Execute(null);
            await WaitForStatusAsync(editor, "Function package import failed:");

            Assert.Empty(destination.Project.Functions);
            Assert.Contains("recursive", editor.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string? Parameter(FunctionDefinition function, string name)
        => Parameter(function.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale), name);

    private static string? Parameter(MappingNode node, string name)
        => node.Parameters.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static async Task WaitForStatusAsync(FunctionLibraryEditorViewModel editor, string prefix)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!editor.Status.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Function editor did not report '{prefix}'. Last status: {editor.Status}");
            await Task.Delay(10);
        }
    }

    private sealed class FunctionPackageDialog : IProjectFileDialog
    {
        public string? OpenPath { get; init; }
        public string? SavePath { get; init; }
        public UnsavedProjectChoice CloseChoice { get; init; } = UnsavedProjectChoice.Cancel;
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveProjectAsync() => Task.FromResult<string?>(null);
        public Task<LegacyProjectProfileSelection?> SelectLegacyProjectProfileAsync(
            IReadOnlyList<LegacyProjectProfileOption> candidates,
            string? declaredProfileId,
            string? declaredProfileDigest) => Task.FromResult<LegacyProjectProfileSelection?>(null);
        public Task<bool> ConfirmLegacyProjectProfileAdoptionAsync(
            string sourcePath,
            string destinationPath,
            LegacyProjectProfileOption selectedProfile,
            IReadOnlyList<string> addedIdentityFields) => Task.FromResult(false);
        public Task<string?> OpenFunctionPackageAsync() => Task.FromResult(OpenPath);
        public Task<string?> SaveFunctionPackageAsync() => Task.FromResult(SavePath);
        public Task<bool> ConfirmSaveAsIndependentCopyAsync(string projectName, string destinationPath) => Task.FromResult(false);
        public Task<bool> ConfirmOpenProjectBackupAsync(string projectPath, string backupPath) => Task.FromResult(false);
        public Task<UnsavedProjectChoice> ConfirmUnsavedProjectChangesAsync(string projectName, string action)
            => Task.FromResult(CloseChoice);
        public Task<string?> SaveReportAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenSourceAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenDestinationDatabaseAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveDestinationFileAsync(string kind) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmDestinationPublicationAsync(string databasePath, string table,
            IReadOnlyList<string> businessKey, int factCount, string semanticDigest) => Task.FromResult(false);
        public Task<bool> ConfirmFileDestinationPublicationAsync(string destinationPath, string format,
            int factCount, string semanticDigest) => Task.FromResult(false);
    }
}
