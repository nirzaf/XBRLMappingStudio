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
    public void AuthoringPreviewAndRevisionSelection_PreservePinnedBodiesAndUndoPinning()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);

        editor.DraftName = "Scale amount";
        editor.AddScaleCommand.Execute(null);
        editor.SelectedScaleFactor = "10";
        editor.ApplyNodeParametersCommand.Execute(null);
        editor.PreviewInput = "5";
        editor.TestFunctionCommand.Execute(null);
        Assert.Contains("output: 50", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);

        editor.SaveFunctionRevisionCommand.Execute(null);
        var revision1 = Assert.Single(projectSession.Project.Functions);
        Assert.Equal(1, revision1.Revision);

        editor.SelectedFunction = editor.Functions.Single(row => row.FunctionId == revision1.FunctionId
            && row.Revision == 1);
        var loadedScale = editor.Session.Project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        editor.Session.Select(loadedScale.NodeId, additive: false);
        editor.SelectedScaleFactor = "100";
        editor.ApplyNodeParametersCommand.Execute(null);
        editor.TestFunctionCommand.Execute(null);
        Assert.Contains("output: 500", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
        editor.SaveFunctionRevisionCommand.Execute(null);

        Assert.Equal(new[] { 1, 2 }, projectSession.Project.Functions
            .Where(function => function.FunctionId == revision1.FunctionId)
            .Select(function => function.Revision));
        Assert.Equal("10", Parameter(projectSession.Project.Functions.Single(function => function.Revision == 1), "factor"));
        Assert.Equal("100", Parameter(projectSession.Project.Functions.Single(function => function.Revision == 2), "factor"));

        editor.SelectedFunction = editor.Functions.Single(row => row.FunctionId == revision1.FunctionId
            && row.Revision == 1);
        editor.TestFunctionCommand.Execute(null);
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
    public void SaveFunctionRevision_RejectsInvalidDraftWithoutChangingProject()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);
        editor.AddScaleCommand.Execute(null);
        var scale = editor.Session.Project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        editor.Session.SetNodeParameter(scale.NodeId, "factor", "not-a-decimal");

        editor.SaveFunctionRevisionCommand.Execute(null);

        Assert.Empty(projectSession.Project.Functions);
        Assert.Contains("synthetic validation failed", editor.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyntheticPreview_ShowsOrderedPartialValuesForEachFunctionNode()
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
        editor.TestFunctionCommand.Execute(null);

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
    public void SyntheticPreview_BoundsDisplayedIntermediateNodes()
    {
        var projectSession = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => projectSession);
        for (var index = 0; index < 66; index++)
            editor.AddChangeSignCommand.Execute(null);

        editor.TestFunctionCommand.Execute(null);

        Assert.Equal(65, editor.NodePreviews.Count);
        Assert.Equal(64, editor.NodePreviews.Count(row => row.NodeId.Length > 0));
        Assert.Contains("first 64 of 66", editor.NodePreviews[^1].Preview, StringComparison.OrdinalIgnoreCase);
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
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenFunctionPackageAsync() => Task.FromResult(OpenPath);
        public Task<string?> SaveFunctionPackageAsync() => Task.FromResult(SavePath);
        public Task<bool> ConfirmSaveAsIndependentCopyAsync(string projectName, string destinationPath) => Task.FromResult(false);
        public Task<bool> ConfirmOpenProjectBackupAsync(string projectPath, string backupPath) => Task.FromResult(false);
        public Task<UnsavedProjectChoice> ConfirmUnsavedProjectChangesAsync(string projectName, string action)
            => Task.FromResult(UnsavedProjectChoice.Cancel);
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
