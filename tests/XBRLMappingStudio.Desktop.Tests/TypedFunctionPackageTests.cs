using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.Text.Json;
using XBRLMappingStudio.Desktop;
using XBRLMappingStudio.Desktop.Controls;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Desktop.Tests;

public sealed class TypedFunctionPackageTests
{
    [Theory]
    [InlineData(PortShape.Boolean, PortCardinality.Single)]
    [InlineData(PortShape.Integer, PortCardinality.Single)]
    [InlineData(PortShape.String, PortCardinality.Single)]
    [InlineData(PortShape.Date, PortCardinality.Single)]
    [InlineData(PortShape.Instant, PortCardinality.Single)]
    [InlineData(PortShape.QualifiedName, PortCardinality.Single)]
    [InlineData(PortShape.Identifier, PortCardinality.Single)]
    [InlineData(PortShape.Fact, PortCardinality.Single)]
    [InlineData(PortShape.Record, PortCardinality.Collection)]
    [InlineData(PortShape.Decimal, PortCardinality.Collection)]
    public async Task TypedPackagesPreserveMixedSignaturesAllRevisionsAndNestedPins(
        PortShape shape, PortCardinality cardinality)
    {
        using var files = new PackageFiles();
        var original = NestedPackage(shape, cardinality);
        await files.WriteAsync(original);
        var project = Project();
        var approvedDigest = project.ComputeSemanticDigest();
        var approval = new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision, SemanticDigest = approvedDigest,
            ApprovedBy = "Existing reviewer", ApprovedUtc = DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
            ProfileDigest = project.ProfileDigest, Authority = "Existing authority", Notes = "Prior graph only"
        };
        project.Approvals.Add(approval);
        var session = new DesignerSession(project);
        var dialog = new PackageDialog { OpenPath = files.Input, SavePath = files.Output };
        var editor = new FunctionLibraryEditorViewModel(() => session, dialog)
        {
            ImportFunctionId = "copied", ImportFunctionName = "Imported typed function"
        };

        await ImportAsync(editor);

        Assert.Equal(4, session.Project.Functions.Count);
        Assert.Equal(approval, Assert.Single(session.Project.Approvals));
        Assert.NotEqual(approvedDigest, session.Project.ComputeSemanticDigest());
        Assert.Equal(2, editor.SelectedFunction!.Revision);
        Assert.Equal("copied", editor.SelectedFunction.FunctionId);
        Assert.False(editor.CanEditDraft);
        Assert.Contains("inspection-only", editor.Status, StringComparison.OrdinalIgnoreCase);
        foreach (var definition in session.Project.Functions)
        {
            Assert.Equal(ExpectedInputs(shape, cardinality), definition.Inputs);
            Assert.Equal(ExpectedOutputs(shape, cardinality), definition.Outputs);
            Assert.Equal(FunctionCatalog.Default.Version, definition.CatalogVersion);
        }
        foreach (var revision in new[] { 1, 2 })
        {
            var root = session.Project.Functions.Single(f => f.FunctionId == "copied" && f.Revision == revision);
            Assert.Equal("Imported typed function", root.Name);
            var nested = Assert.Single(root.Nodes, node => node.NodeType == NodeTypeConstants.FunctionCall);
            Assert.Equal("copied__dependency", Parameter(nested, "functionId"));
            Assert.Equal(revision.ToString(), Parameter(nested, "revision"));
            Assert.Equal(ExpectedInputs(shape, cardinality), nested.InputPorts);
            Assert.Equal(ExpectedOutputs(shape, cardinality), nested.OutputPorts);
        }

        editor.PinSelectedFunctionCommand.Execute(null);
        var call = Assert.Single(session.Project.Nodes);
        Assert.Equal(approval, Assert.Single(session.Project.Approvals));
        Assert.NotEqual(approvedDigest, session.Project.ComputeSemanticDigest());
        Assert.Equal("copied", Parameter(call, "functionId"));
        Assert.Equal("2", Parameter(call, "revision"));
        Assert.Equal(ExpectedInputs(shape, cardinality), call.InputPorts);
        Assert.Equal(ExpectedOutputs(shape, cardinality), call.OutputPorts);
        Assert.NotSame(editor.SelectedFunction.Definition.Inputs, call.InputPorts);
        Assert.NotSame(editor.SelectedFunction.Definition.Outputs, call.OutputPorts);

        await ExportAsync(editor);
        var exported = FunctionPackageSerializer.Deserialize(await File.ReadAllTextAsync(files.Output));
        Assert.Equal("copied", exported.RootFunctionId);
        Assert.Equal(2, exported.RootRevision);
        Assert.Equal(4, exported.Functions.Count);
        foreach (var definition in exported.Functions)
        {
            var expected = original.Functions.Single(f =>
                (f.FunctionId == "root" ? "copied" : "copied__dependency") == definition.FunctionId
                && f.Revision == definition.Revision);
            Assert.Equal(ExpectedInputs(shape, cardinality), definition.Inputs);
            Assert.Equal(ExpectedOutputs(shape, cardinality), definition.Outputs);
            Assert.Equal(expected.Edges, definition.Edges);
            Assert.Equal(expected.Nodes.Select(n => (n.NodeId, n.NodeType, n.DisplayName, n.Layout)),
                definition.Nodes.Select(n => (n.NodeId, n.NodeType, n.DisplayName, n.Layout)));
        }

        session.Undo(); // Pinning is a separate edit from the atomic package import.
        Assert.Empty(session.Project.Nodes);
        Assert.Equal(4, session.Project.Functions.Count);
        session.Undo();
        Assert.Empty(session.Project.Functions);
        session.Redo();
        Assert.Equal(4, session.Project.Functions.Count);
        session.Redo();
        Assert.Equal(ExpectedInputs(shape, cardinality), Assert.Single(session.Project.Nodes).InputPorts);

        var secondSession = new DesignerSession(Project("round-trip"));
        var secondEditor = new FunctionLibraryEditorViewModel(() => secondSession,
            new PackageDialog { OpenPath = files.Output }) { ImportFunctionId = "round-trip-root" };
        await ImportAsync(secondEditor);
        Assert.Equal(4, secondSession.Project.Functions.Count);
        Assert.All(secondSession.Project.Functions, definition =>
        {
            Assert.Equal(ExpectedInputs(shape, cardinality), definition.Inputs);
            Assert.Equal(ExpectedOutputs(shape, cardinality), definition.Outputs);
        });
        Assert.Equal("round-trip-root__copied__dependency", Parameter(
            secondEditor.SelectedFunction!.Definition.Nodes.Single(n => n.NodeType == NodeTypeConstants.FunctionCall),
            "functionId"));
    }

    [Fact]
    public async Task ZeroInputPackageCanBeImportedExportedAndPinnedWithoutNumericEditing()
    {
        using var files = new PackageFiles();
        var definition = ZeroInputFunction();
        await files.WriteAsync(Package(definition));
        var session = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => session,
            new PackageDialog { OpenPath = files.Input, SavePath = files.Output }) { ImportFunctionId = "fixed" };
        await ImportAsync(editor);
        Assert.False(editor.CanEditDraft);
        Assert.False(editor.CanTestFunction);
        Assert.False(editor.CanSaveFunctionRevision);
        editor.PinSelectedFunctionCommand.Execute(null);
        var call = Assert.Single(session.Project.Nodes);
        Assert.Empty(call.InputPorts);
        Assert.Equal(new[] { new PortDefinition("fixedAmount", PortShape.Decimal, PortCardinality.Single,
            IsRequired: false, Description: "A fixed amount") }, call.OutputPorts);
        await ExportAsync(editor);
        var exported = Assert.Single(FunctionPackageSerializer.Deserialize(await File.ReadAllTextAsync(files.Output)).Functions);
        Assert.Empty(exported.Inputs);
        Assert.Equal(definition.Outputs, exported.Outputs);
        Assert.Equal("7", Parameter(exported.Nodes.Single(n => n.NodeType == NodeTypeConstants.Constant), "literal"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomDecimalRevisionRetainsPublicMetadataAndImmutablePriorBody(bool legacyMarkerAliases)
    {
        using var files = new PackageFiles();
        var original = DecimalFunction(legacyMarkerAliases);
        await files.WriteAsync(Package(original));
        var session = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => session,
            new PackageDialog { OpenPath = files.Input, SavePath = files.Output }) { ImportFunctionId = "amount" };
        await ImportAsync(editor);
        Assert.True(editor.CanEditDraft);
        var oldRevision = Assert.Single(session.Project.Functions);
        var oldSnapshot = FunctionPackageSerializer.Serialize(Package(oldRevision));
        var scale = editor.Session.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Scale);
        editor.Session.Select(scale.NodeId, additive: false);
        editor.SelectedScaleFactor = "10";
        editor.ApplyNodeParametersCommand.Execute(null);
        editor.UndoDraftCommand.Execute(null);
        Assert.Equal("2", Parameter(editor.Session.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Scale), "factor"));
        editor.RedoDraftCommand.Execute(null);
        editor.PreviewInput = "3";
        editor.TestFunctionCommand.Execute(null);
        await WaitUntilAsync(() => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal)
            || editor.PreviewStatus.StartsWith("Preview blocked:", StringComparison.Ordinal), () => editor.PreviewStatus);
        Assert.Contains("output: 30", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
        editor.DraftName = "Adjusted amount";
        editor.SaveFunctionRevisionCommand.Execute(null);
        await WaitUntilAsync(() => editor.Status.StartsWith("Saved immutable function revision", StringComparison.Ordinal)
            || editor.Status.StartsWith("Function revision was not saved", StringComparison.Ordinal), () => editor.Status);
        Assert.StartsWith("Saved immutable function revision", editor.Status, StringComparison.Ordinal);

        var saved = session.Project.Functions.Single(f => f.Revision == 2);
        Assert.Equal(new[] { new PortDefinition("netAmount", PortShape.Decimal, PortCardinality.Single,
            IsRequired: false, Description: "Unadjusted amount") }, saved.Inputs);
        Assert.Equal(new[] { new PortDefinition("grossAmount", PortShape.Decimal, PortCardinality.Single,
            IsRequired: true, Description: "Adjusted amount") }, saved.Outputs);
        Assert.Equal(FunctionCatalog.Default.Version, saved.CatalogVersion);
        Assert.Equal("Adjusted amount", saved.Name);
        Assert.Equal("10", Parameter(saved.Nodes.Single(n => n.NodeType == NodeTypeConstants.Scale), "factor"));
        Assert.Equal(oldSnapshot, FunctionPackageSerializer.Serialize(Package(session.Project.Functions.Single(f => f.Revision == 1))));
        Assert.NotSame(oldRevision.Inputs, saved.Inputs);
        Assert.NotSame(oldRevision.Outputs, saved.Outputs);
        await ExportAsync(editor);
        var exported = FunctionPackageSerializer.Deserialize(await File.ReadAllTextAsync(files.Output));
        Assert.Equal(new[] { 1, 2 }, exported.Functions.Select(f => f.Revision));
        Assert.All(exported.Functions, f =>
        {
            Assert.Equal(original.Inputs, f.Inputs);
            Assert.Equal(original.Outputs, f.Outputs);
        });
        session.Undo();
        Assert.Equal(oldSnapshot, FunctionPackageSerializer.Serialize(Package(Assert.Single(session.Project.Functions))));
        session.Redo();
        Assert.Equal(saved.Inputs, session.Project.Functions.Single(f => f.Revision == 2).Inputs);
    }

    [Fact]
    public void TypedDraftRejectsNumericCommandsEvenWhenExecuteBypassesCanExecute()
    {
        var function = TypedFunction("typed", 1, PortShape.Decimal, PortCardinality.Single);
        var scale = ScaleNode();
        function.Nodes.Add(scale);
        function.Edges.RemoveAt(1);
        function.Edges.Add(Link("input", "other", scale.NodeId, "input"));
        function.Edges.Add(Link(scale.NodeId, "value", "output", "result"));
        var project = Project();
        project.Functions.Add(function);
        var session = new DesignerSession(project);
        var editor = new FunctionLibraryEditorViewModel(() => session);
        editor.SelectedFunction = Assert.Single(editor.Functions);
        editor.Session.Select(scale.NodeId, additive: false);
        Assert.False(editor.CanEditDraft);
        Assert.False(editor.CanEditScaleFactor);
        Assert.Contains("inspection-only", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text", editor.DraftSignatureSummary, StringComparison.Ordinal);
        Assert.Contains("String", editor.DraftSignatureSummary, StringComparison.Ordinal);
        var beforeDraft = ProjectSerializer.Serialize(editor.Session.Project);
        var beforeProject = ProjectSerializer.Serialize(session.Project);
        editor.SelectedScaleFactor = "999";
        foreach (var command in new[] { editor.AddScaleCommand, editor.AddChangeSignCommand, editor.AddRoundCommand,
            editor.AddCoalesceCommand, editor.AddRatioCommand, editor.ApplyNodeParametersCommand,
            editor.TestFunctionCommand, editor.SaveFunctionRevisionCommand, editor.UndoDraftCommand, editor.RedoDraftCommand })
        {
            Assert.False(command.CanExecute(null));
            command.Execute(null);
            Assert.Equal(beforeDraft, ProjectSerializer.Serialize(editor.Session.Project));
            Assert.Equal(beforeProject, ProjectSerializer.Serialize(session.Project));
        }
        Assert.Empty(editor.NodePreviews);
        Assert.False(editor.Session.CanUndo);
        Assert.True(editor.CanPinSelectedFunction);
        editor.NewFunctionCommand.Execute(null);
        Assert.True(editor.CanEditDraft);
        Assert.True(editor.CanTestFunction);
        Assert.True(editor.CanSaveFunctionRevision);
        Assert.True(editor.AddScaleCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void FunctionWindowTransitionsBetweenInspectionAndNumericAuthoring()
    {
        var project = Project();
        project.Functions.Add(TypedFunction("typed", 1, PortShape.Record, PortCardinality.Collection));
        project.Functions.Add(DecimalFunction());
        var viewModel = new MainWindowViewModel(project, new PackageDialog());
        MainWindow? window = null;
        try
        {
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 1;
            var editor = viewModel.FunctionLibrary;
            var canvas = Assert.IsType<GraphCanvas>(window.FindControl<GraphCanvas>("FunctionGraphCanvas"));
            var scale = window.FindControl<Button>("AddFunctionScaleButton")!;
            var save = window.FindControl<Button>("SaveFunctionRevisionButton")!;
            var preview = window.FindControl<Button>("TestFunctionDraftButton")!;
            var pin = window.FindControl<Button>("PinFunctionRevisionButton")!;
            var draftName = window.FindControl<TextBox>("FunctionDraftNameTextBox")!;
            var previewInput = window.FindControl<TextBox>("SyntheticFunctionPreviewInputTextBox")!;
            Assert.True(canvas.IsEnabled);
            editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "typed");
            Assert.False(canvas.IsEnabled);
            // Button command availability affects effective enabled state rather
            // than the locally configured IsEnabled property in Avalonia.
            Assert.False(scale.IsEffectivelyEnabled);
            Assert.False(save.IsEffectivelyEnabled);
            Assert.False(preview.IsEffectivelyEnabled);
            Assert.False(draftName.IsEnabled);
            Assert.False(previewInput.IsEnabled);
            Assert.True(pin.IsEnabled);
            Assert.True(editor.CanExportFunctionPackage);
            editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "decimal");
            Assert.True(canvas.IsEnabled);
            Assert.True(scale.IsEffectivelyEnabled);
            Assert.True(save.IsEffectivelyEnabled);
            Assert.True(preview.IsEffectivelyEnabled);
            Assert.True(draftName.IsEnabled);
            Assert.True(previewInput.IsEnabled);
            editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "typed");
            Assert.False(canvas.IsEnabled);
            editor.NewFunctionCommand.Execute(null);
            Assert.True(canvas.IsEnabled);
            Assert.True(scale.IsEffectivelyEnabled);
            Assert.True(save.IsEffectivelyEnabled);
            Assert.False(pin.IsEnabled);
            Assert.Null(editor.SelectedFunction);
        }
        finally
        {
            if (window is not null) window.DataContext = null;
            window?.Close();
            viewModel.Dispose();
        }
    }

    [Theory]
    [InlineData("oldRevision")]
    [InlineData("unreferencedRevision")]
    [InlineData("missingDependency")]
    [InlineData("recursive")]
    [InlineData("catalog")]
    [InlineData("rootCollision")]
    [InlineData("dependencyCollision")]
    [InlineData("unknownPort")]
    [InlineData("missingNode")]
    [InlineData("markerShape")]
    [InlineData("duplicateOutputEdge")]
    [InlineData("unknownNodeType")]
    [InlineData("unwiredOutput")]
    [InlineData("duplicatePublicPort")]
    [InlineData("excessiveNesting")]
    [InlineData("inliningNodeCollision")]
    [InlineData("numericParameter")]
    public async Task InvalidPackageIsRejectedAtomicallyWithoutChangingProjectOrHistory(string corruption)
    {
        using var files = new PackageFiles();
        var package = NestedPackage(PortShape.Boolean, PortCardinality.Single);
        var project = Project();
        var root = package.Functions.Single(f => f.FunctionId == "root" && f.Revision == 2);
        switch (corruption)
        {
            case "oldRevision": package.Functions.Single(f => f.FunctionId == "root" && f.Revision == 1).Edges.RemoveAt(0); break;
            case "unreferencedRevision":
                var unused = TypedFunction("unused", 1, PortShape.String, PortCardinality.Single);
                unused.Edges.Clear();
                package.Functions.Add(unused);
                break;
            case "missingDependency": package.Functions.RemoveAll(f => f.FunctionId == "dependency" && f.Revision == 2); break;
            case "recursive": root.Nodes.Single(n => n.NodeType == NodeTypeConstants.FunctionCall).Parameters["functionId"] = "root"; break;
            case "catalog": package.Functions[0] = package.Functions[0] with { CatalogVersion = "unsupported-catalog" }; break;
            case "rootCollision": project.Functions.Add(DecimalFunction() with { FunctionId = "imported" }); break;
            case "dependencyCollision": project.Functions.Add(DecimalFunction() with { FunctionId = "imported__dependency" }); break;
            case "unknownPort": root.Edges[0] = root.Edges[0] with { SourcePortName = "unknown" }; break;
            case "missingNode": root.Edges[0] = root.Edges[0] with { SourceNodeId = "missing" }; break;
            case "markerShape": root.Nodes.Single(n => n.NodeType == "functionInput").OutputPorts[0] =
                    new("text", PortShape.Decimal, PortCardinality.Single); break;
            case "duplicateOutputEdge": root.Edges.Add(root.Edges[^1] with { EdgeId = "duplicate-output" }); break;
            case "unknownNodeType": root.Nodes.Add(new MappingNode
                { NodeId = "unknown", NodeType = "unrecognizedOperation", DisplayName = "Unknown operation" }); break;
            case "unwiredOutput": root.Edges.RemoveAt(root.Edges.Count - 1); break;
            case "duplicatePublicPort": root.Inputs.Add(root.Inputs[0]); break;
            case "excessiveNesting":
                package = new FunctionPackageDocument
                {
                    RootFunctionId = "depth-00", RootRevision = 1,
                    Functions = Enumerable.Range(0, 65).Select(index => TypedFunction($"depth-{index:D2}", 1,
                        PortShape.Boolean, PortCardinality.Single, index < 64 ? $"depth-{index + 1:D2}" : null)).ToList()
                };
                break;
            case "inliningNodeCollision":
                var constant = ZeroInputFunction().Nodes.Single(n => n.NodeType == NodeTypeConstants.Constant);
                package.Functions.Single(f => f.FunctionId == "dependency" && f.Revision == 2).Nodes
                    .Add(constant with { NodeId = "extra" });
                root.Nodes.Add(constant with { NodeId = "nested::extra" });
                break;
            case "numericParameter":
                var numeric = DecimalFunction();
                numeric.Nodes.Single(n => n.NodeType == NodeTypeConstants.Scale).Parameters["factor"] = "not-a-number";
                package = Package(numeric);
                break;
        }
        // Malformed external packages must reach the import boundary, not fail in
        // the fixture serializer's own validation before the command executes.
        await File.WriteAllTextAsync(files.Input, JsonSerializer.Serialize(package, ProjectSerializer.Options));
        var session = new DesignerSession(project);
        var before = ProjectSerializer.Serialize(session.Project);
        var editor = new FunctionLibraryEditorViewModel(() => session,
            new PackageDialog { OpenPath = files.Input }) { ImportFunctionId = "imported" };
        var beforeDraft = ProjectSerializer.Serialize(editor.Session.Project);
        editor.ImportFunctionPackageCommand.Execute(null);
        await WaitForPackageOutcomeAsync(editor, "Imported ");
        Assert.StartsWith("Function package import failed:", editor.Status, StringComparison.Ordinal);
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.Equal(beforeDraft, ProjectSerializer.Serialize(editor.Session.Project));
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.Null(editor.SelectedFunction);
    }

    [Fact]
    public async Task ImportMayUseAnExistingPinnedTypedDependencyWithoutRenamingIt()
    {
        using var files = new PackageFiles();
        var package = NestedPackage(PortShape.Record, PortCardinality.Collection);
        var project = Project();
        project.Functions.AddRange(package.Functions.Where(f => f.FunctionId == "dependency"));
        package.Functions.RemoveAll(f => f.FunctionId == "dependency");
        await files.WriteAsync(package);
        var session = new DesignerSession(project);
        var dependencySnapshots = project.Functions.Select(f => FunctionPackageSerializer.Serialize(Package(f))).ToArray();
        var editor = new FunctionLibraryEditorViewModel(() => session,
            new PackageDialog { OpenPath = files.Input }) { ImportFunctionId = "imported" };
        await ImportAsync(editor);
        Assert.Equal(4, session.Project.Functions.Count);
        Assert.All(session.Project.Functions.Where(f => f.FunctionId == "imported"), f =>
            Assert.Equal("dependency", Parameter(f.Nodes.Single(n => n.NodeType == NodeTypeConstants.FunctionCall), "functionId")));
        Assert.Equal(dependencySnapshots, session.Project.Functions.Where(f => f.FunctionId == "dependency")
            .Select(f => FunctionPackageSerializer.Serialize(Package(f))));
    }

    [Fact]
    public async Task DelayedImportFreezesIdentityAndIgnoresRepeatedExecution()
    {
        using var files = new PackageFiles();
        await files.WriteAsync(Package(ZeroInputFunction()));
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new PackageDialog { PendingOpen = pending };
        var session = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => session, dialog)
        {
            ImportFunctionId = "frozen-id", ImportFunctionName = "Frozen name"
        };
        editor.ImportFunctionPackageCommand.Execute(null);
        Assert.Equal(1, dialog.OpenCalls);
        Assert.False(editor.ImportFunctionPackageCommand.CanExecute(null));
        editor.ImportFunctionId = "later-id";
        editor.ImportFunctionName = "Later name";
        editor.ImportFunctionPackageCommand.Execute(null);
        Assert.Equal(1, dialog.OpenCalls);
        pending.SetResult(files.Input);
        await WaitForPackageOutcomeAsync(editor, "Imported ");
        Assert.StartsWith("Imported ", editor.Status, StringComparison.Ordinal);
        var imported = Assert.Single(session.Project.Functions);
        Assert.Equal("frozen-id", imported.FunctionId);
        Assert.Equal("Frozen name", imported.Name);
        Assert.Equal("frozen-id", editor.SelectedFunction!.FunctionId);
        Assert.Equal("later-id", editor.ImportFunctionId);
        Assert.Equal("Later name", editor.ImportFunctionName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedImportRejectsChangedProjectEvenWithTheSameProjectId(bool replaceSession)
    {
        using var files = new PackageFiles();
        await files.WriteAsync(Package(ZeroInputFunction()));
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new DesignerSession(Project());
        var active = original;
        var beforeOriginal = ProjectSerializer.Serialize(original.Project);
        var editor = new FunctionLibraryEditorViewModel(() => active,
            new PackageDialog { PendingOpen = pending }) { ImportFunctionId = "imported" };
        editor.ImportFunctionPackageCommand.Execute(null);
        if (replaceSession) active = new DesignerSession(Project());
        else active.AddNode(ScaleNode());
        var beforeActive = ProjectSerializer.Serialize(active.Project);
        editor.RefreshFromProject();
        pending.SetResult(files.Input);
        await WaitForPackageOutcomeAsync(editor, "Imported ");
        Assert.StartsWith("Function package import failed:", editor.Status, StringComparison.Ordinal);
        Assert.Equal(beforeActive, ProjectSerializer.Serialize(active.Project));
        if (replaceSession) Assert.Equal(beforeOriginal, ProjectSerializer.Serialize(original.Project));
        Assert.Empty(active.Project.Functions);
    }

    [Fact]
    public async Task CancelledImportLeavesStateUntouchedAndAllowsAFreshAttempt()
    {
        using var files = new PackageFiles();
        await files.WriteAsync(Package(ZeroInputFunction()));
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new PackageDialog { PendingOpen = pending, OpenPath = files.Input };
        var session = new DesignerSession(Project());
        var editor = new FunctionLibraryEditorViewModel(() => session, dialog) { ImportFunctionId = "imported" };
        var before = ProjectSerializer.Serialize(session.Project);
        var beforeDraft = ProjectSerializer.Serialize(editor.Session.Project);
        editor.ImportFunctionPackageCommand.Execute(null);
        pending.SetResult(null);
        await WaitUntilAsync(() => editor.CanImportFunctionPackage, () => editor.Status);
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.Equal(beforeDraft, ProjectSerializer.Serialize(editor.Session.Project));
        Assert.False(session.CanUndo);
        dialog.PendingOpen = null;
        await ImportAsync(editor);
        Assert.Single(session.Project.Functions);
        Assert.Equal(2, dialog.OpenCalls);
        var after = ProjectSerializer.Serialize(session.Project);
        editor.ImportFunctionPackageCommand.Execute(null);
        await WaitUntilAsync(() => editor.Status.StartsWith("Function package import failed:", StringComparison.Ordinal), () => editor.Status);
        Assert.Equal(after, ProjectSerializer.Serialize(session.Project));
        session.Undo();
        Assert.Empty(session.Project.Functions);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedExportDoesNotOverwriteAFileAfterProjectContextChanges(bool replaceSession)
    {
        using var files = new PackageFiles();
        await File.WriteAllTextAsync(files.Output, "existing destination");
        var project = Project();
        project.Functions.Add(ZeroInputFunction());
        var active = new DesignerSession(project);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new FunctionLibraryEditorViewModel(() => active,
            new PackageDialog { PendingSave = pending });
        editor.SelectedFunction = Assert.Single(editor.Functions);
        editor.ExportFunctionPackageCommand.Execute(null);
        if (replaceSession) active = new DesignerSession(Project());
        else active.AddNode(ScaleNode());
        var before = ProjectSerializer.Serialize(active.Project);
        editor.RefreshFromProject();
        pending.SetResult(files.Output);
        await WaitForPackageOutcomeAsync(editor, "Exported ");
        Assert.StartsWith("Function package export failed:", editor.Status, StringComparison.Ordinal);
        Assert.Equal("existing destination", await File.ReadAllTextAsync(files.Output));
        Assert.Equal(before, ProjectSerializer.Serialize(active.Project));
        Assert.Empty(Directory.GetFiles(files.Directory, "*.tmp"));
    }

    [Fact]
    public async Task DelayedExportUsesTheSelectedRevisionAtInvocationAndDoesNotDuplicatePicker()
    {
        using var files = new PackageFiles();
        var project = Project();
        project.Functions.AddRange(NestedPackage(PortShape.Boolean, PortCardinality.Single).Functions);
        var session = new DesignerSession(project);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new PackageDialog { PendingSave = pending };
        var editor = new FunctionLibraryEditorViewModel(() => session, dialog);
        editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "root" && f.Revision == 2);
        var before = ProjectSerializer.Serialize(session.Project);
        editor.ExportFunctionPackageCommand.Execute(null);
        Assert.False(editor.CanExportFunctionPackage);
        editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "root" && f.Revision == 1);
        editor.ExportFunctionPackageCommand.Execute(null);
        Assert.Equal(1, dialog.SaveCalls);
        pending.SetResult(files.Output);
        await WaitForPackageOutcomeAsync(editor, "Exported ");
        Assert.StartsWith("Exported root@2", editor.Status, StringComparison.Ordinal);
        var exported = FunctionPackageSerializer.Deserialize(await File.ReadAllTextAsync(files.Output));
        Assert.Equal(2, exported.RootRevision);
        Assert.Equal(4, exported.Functions.Count);
        Assert.Equal(1, editor.SelectedFunction.Revision);
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopeningSameProjectIdentityCannotSaveAnEarlierSessionDraft(bool savedRevision)
    {
        var project = Project();
        if (savedRevision) project.Functions.Add(DecimalFunction());
        var original = new DesignerSession(project);
        var active = original;
        var editor = new FunctionLibraryEditorViewModel(() => active);
        if (savedRevision) editor.SelectedFunction = Assert.Single(editor.Functions);
        editor.AddScaleCommand.Execute(null);
        var originalSnapshot = ProjectSerializer.Serialize(original.Project);
        active = new DesignerSession(ProjectSerializer.Deserialize(originalSnapshot));
        var reopenedSnapshot = ProjectSerializer.Serialize(active.Project);
        editor.RefreshFromProject();
        editor.SaveFunctionRevisionCommand.Execute(null);
        Assert.Contains("different project", editor.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalSnapshot, ProjectSerializer.Serialize(original.Project));
        Assert.Equal(reopenedSnapshot, ProjectSerializer.Serialize(active.Project));
        Assert.False(active.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleSelectedRevisionCannotBePinnedIntoAnotherSession(bool matchingRevisionIdentity)
    {
        var project = Project();
        project.Functions.Add(TypedFunction("typed", 1, PortShape.Record, PortCardinality.Collection));
        var original = new DesignerSession(project);
        var active = original;
        var editor = new FunctionLibraryEditorViewModel(() => active);
        editor.SelectedFunction = Assert.Single(editor.Functions);
        var replacement = Project();
        if (matchingRevisionIdentity)
            replacement.Functions.Add(TypedFunction("typed", 1, PortShape.Boolean, PortCardinality.Single));
        active = new DesignerSession(replacement);
        var originalSnapshot = ProjectSerializer.Serialize(original.Project);
        var replacementSnapshot = ProjectSerializer.Serialize(active.Project);
        // Deliberately bypass RefreshFromProject and CanExecute, as a stale control can.
        editor.PinSelectedFunctionCommand.Execute(null);
        Assert.Equal(replacementSnapshot, ProjectSerializer.Serialize(active.Project));
        Assert.Equal(originalSnapshot, ProjectSerializer.Serialize(original.Project));
        Assert.False(active.CanUndo);
        Assert.Contains("active project", editor.Status, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task SelectingTypedRevisionDuringNumericPreviewDoesNotOverwriteInspectionState()
    {
        var project = Project();
        project.Functions.Add(DecimalFunction());
        project.Functions.Add(TypedFunction("typed", 1, PortShape.Record, PortCardinality.Collection));
        var session = new DesignerSession(project);
        var editor = new FunctionLibraryEditorViewModel(() => session);
        editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "decimal");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaitingCompletion = false;
        EventHandler handler = (_, _) => { if (awaitingCompletion) completion.TrySetResult(); };
        editor.TestFunctionCommand.CanExecuteChanged += handler;
        try
        {
            editor.TestFunctionCommand.Execute(null);
            Assert.StartsWith("Running synthetic numeric preview", editor.PreviewStatus, StringComparison.Ordinal);
            editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "typed");
            var inspectionStatus = editor.PreviewStatus;
            var draftSnapshot = ProjectSerializer.Serialize(editor.Session.Project);
            awaitingCompletion = true;
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(inspectionStatus, editor.PreviewStatus);
            Assert.Contains("inspection-only", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(editor.NodePreviews);
            Assert.False(editor.CanEditDraft);
            Assert.Equal(draftSnapshot, ProjectSerializer.Serialize(editor.Session.Project));
            Assert.False(session.CanUndo);
        }
        finally { editor.TestFunctionCommand.CanExecuteChanged -= handler; }
    }

    [AvaloniaFact]
    public async Task SwitchingSignatureDuringSaveRefusesTheStaleRevision()
    {
        var project = Project();
        project.Functions.Add(DecimalFunction());
        project.Functions.Add(TypedFunction("typed", 1, PortShape.Boolean, PortCardinality.Single));
        var session = new DesignerSession(project);
        var editor = new FunctionLibraryEditorViewModel(() => session);
        editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "decimal");
        var before = ProjectSerializer.Serialize(session.Project);
        editor.SaveFunctionRevisionCommand.Execute(null);
        Assert.StartsWith("Validating the function draft", editor.Status, StringComparison.Ordinal);
        editor.SelectedFunction = editor.Functions.Single(f => f.FunctionId == "typed");
        await WaitUntilAsync(() => editor.Status.Contains("changed during validation", StringComparison.Ordinal), () => editor.Status);
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.Equal("typed", editor.SelectedFunction.FunctionId);
        Assert.False(editor.CanEditDraft);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task SavingEarlierRevisionRetainsItsCompatibleCatalogSpelling()
    {
        var project = Project(catalogVersion: "1");
        var legacy = DecimalFunction() with { CatalogVersion = "1" };
        project.Functions.Add(legacy);
        project.Functions.Add(DecimalFunction() with { Revision = 2, CatalogVersion = "1.0" });
        var session = new DesignerSession(project);
        var editor = new FunctionLibraryEditorViewModel(() => session);
        editor.SelectedFunction = editor.Functions.Single(f => f.Revision == 1);
        editor.SaveFunctionRevisionCommand.Execute(null);
        await WaitUntilAsync(() => editor.Status.StartsWith("Saved immutable function revision", StringComparison.Ordinal)
            || editor.Status.StartsWith("Function revision was not saved", StringComparison.Ordinal), () => editor.Status);
        Assert.StartsWith("Saved immutable function revision", editor.Status, StringComparison.Ordinal);
        Assert.Equal("1", session.Project.Functions.Single(f => f.Revision == 3).CatalogVersion);
        Assert.Equal("1.0", session.Project.Functions.Single(f => f.Revision == 2).CatalogVersion);
        Assert.Equal("1", session.Project.Functions.Single(f => f.Revision == 1).CatalogVersion);
    }

    [Theory]
    [InlineData(false, double.NaN)]
    [InlineData(false, double.PositiveInfinity)]
    [InlineData(false, double.NegativeInfinity)]
    [InlineData(true, double.NaN)]
    [InlineData(true, double.PositiveInfinity)]
    [InlineData(true, double.NegativeInfinity)]
    public async Task NonfiniteDraftLayoutBlocksPreviewAndRecoversAfterFiniteLayoutRestoration(
        bool batchMove, double nonfinite)
    {
        var project = Project();
        project.Functions.Add(DecimalFunction());
        var session = new DesignerSession(project);
        var editor = new FunctionLibraryEditorViewModel(() => session);
        editor.SelectedFunction = Assert.Single(editor.Functions);
        editor.PreviewInput = "3";
        var scale = editor.Session.Project.Nodes.Single(n => n.NodeType == NodeTypeConstants.Scale);
        var finiteLayout = new LayoutPosition(200, 150);
        editor.Session.MoveNode(scale.NodeId, finiteLayout.X, finiteLayout.Y);
        var savedProject = ProjectSerializer.Serialize(session.Project);

        editor.TestFunctionCommand.Execute(null);
        await WaitUntilAsync(() => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal),
            () => editor.PreviewStatus);
        Assert.Single(editor.NodePreviews);
        Assert.Contains("output: 6", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);

        if (batchMove)
            Assert.True(editor.Session.MoveNodesTo(new Dictionary<string, LayoutPosition>
                { [scale.NodeId] = new(finiteLayout.X, nonfinite) }));
        else
            editor.Session.MoveNode(scale.NodeId, nonfinite, finiteLayout.Y);

        editor.TestFunctionCommand.Execute(null);
        await WaitUntilAsync(() => editor.PreviewStatus.StartsWith("Preview blocked:", StringComparison.Ordinal)
            && editor.CanTestFunction, () => editor.PreviewStatus);
        Assert.Empty(editor.NodePreviews);
        Assert.True(editor.TestFunctionCommand.CanExecute(null));
        Assert.Equal(savedProject, ProjectSerializer.Serialize(session.Project));
        Assert.False(session.CanUndo);

        // MoveNode snapshots the current graph before moving. Restore its public
        // layout first so that snapshotting the invalid old coordinate cannot fail.
        scale.Layout = finiteLayout;
        editor.Session.MoveNode(scale.NodeId, finiteLayout.X, finiteLayout.Y);
        editor.TestFunctionCommand.Execute(null);
        await WaitUntilAsync(() => editor.PreviewStatus.StartsWith("Synthetic preview output:", StringComparison.Ordinal)
            && editor.CanTestFunction, () => editor.PreviewStatus);
        Assert.Contains("output: 6", editor.PreviewStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6m, Assert.Single(editor.NodePreviews).Value);
        Assert.Equal(savedProject, ProjectSerializer.Serialize(session.Project));
    }

    private static MappingProject Project(string id = "typed-function-test", string? catalogVersion = null)
    {
        var profile = SyntheticProfile.Build();
        return new MappingProject
        {
            ProjectId = id, Name = "Typed function test", ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest, FunctionCatalogVersion = catalogVersion ?? FunctionCatalog.Default.Version
        };
    }

    // These expected signatures are authored independently of imported/project/call data.
    private static PortDefinition[] ExpectedInputs(PortShape shape, PortCardinality cardinality) =>
    [
        new("text", PortShape.String, PortCardinality.Single, IsRequired: false, Description: "Optional source label"),
        new("other", shape, cardinality, IsRequired: true, Description: "Typed source value")
    ];

    private static PortDefinition[] ExpectedOutputs(PortShape shape, PortCardinality cardinality) =>
    [
        new("label", PortShape.String, PortCardinality.Single, IsRequired: true, Description: "Original label"),
        new("result", shape, cardinality, IsRequired: false, Description: "Typed result value")
    ];

    private static FunctionDefinition TypedFunction(string id, int revision, PortShape shape,
        PortCardinality cardinality, string? dependency = null)
    {
        var input = new MappingNode
        {
            NodeId = "input", NodeType = "functionInput", DisplayName = "Typed input",
            OutputPorts = ExpectedInputs(shape, cardinality).ToList(), Layout = new(10, 20)
        };
        var output = new MappingNode
        {
            NodeId = "output", NodeType = "functionOutput", DisplayName = "Typed output",
            InputPorts = ExpectedOutputs(shape, cardinality).ToList(), Layout = new(500, 20)
        };
        var nodes = new List<MappingNode> { input, output };
        var edges = new List<MappingEdge>();
        if (dependency is null)
        {
            edges.Add(Link("input", "text", "output", "label"));
            edges.Add(Link("input", "other", "output", "result"));
        }
        else
        {
            nodes.Add(new MappingNode
            {
                NodeId = "nested", NodeType = NodeTypeConstants.FunctionCall, DisplayName = "Pinned dependency",
                Parameters = new() { ["functionId"] = dependency, ["revision"] = revision.ToString() },
                InputPorts = ExpectedInputs(shape, cardinality).ToList(),
                OutputPorts = ExpectedOutputs(shape, cardinality).ToList(), Layout = new(250, 20)
            });
            edges.Add(Link("input", "text", "nested", "text"));
            edges.Add(Link("input", "other", "nested", "other"));
            edges.Add(Link("nested", "label", "output", "label"));
            edges.Add(Link("nested", "result", "output", "result"));
        }
        return new FunctionDefinition
        {
            FunctionId = id, Name = $"{id} revision {revision}", Revision = revision,
            CatalogVersion = FunctionCatalog.Default.Version, Inputs = ExpectedInputs(shape, cardinality).ToList(),
            Outputs = ExpectedOutputs(shape, cardinality).ToList(), Nodes = nodes, Edges = edges
        };
    }

    private static FunctionPackageDocument NestedPackage(PortShape shape, PortCardinality cardinality) => new()
    {
        RootFunctionId = "root", RootRevision = 2,
        Functions =
        [
            TypedFunction("root", 2, shape, cardinality, "dependency"),
            TypedFunction("dependency", 1, shape, cardinality),
            TypedFunction("root", 1, shape, cardinality, "dependency"),
            TypedFunction("dependency", 2, shape, cardinality)
        ]
    };

    private static FunctionDefinition DecimalFunction(bool aliases = false)
    {
        var inputPort = new PortDefinition("netAmount", PortShape.Decimal, PortCardinality.Single,
            IsRequired: false, Description: "Unadjusted amount");
        var outputPort = new PortDefinition("grossAmount", PortShape.Decimal, PortCardinality.Single,
            IsRequired: true, Description: "Adjusted amount");
        var localInput = aliases ? "localInput" : inputPort.Name;
        var localOutput = aliases ? "localOutput" : outputPort.Name;
        return new FunctionDefinition
        {
            FunctionId = "decimal", Name = "Custom amount", CatalogVersion = FunctionCatalog.Default.Version,
            Inputs = [inputPort], Outputs = [outputPort],
            Nodes =
            [
                new() { NodeId = "input", NodeType = "functionInput", DisplayName = "Amount input",
                    OutputPorts = [inputPort with { Name = localInput }] },
                ScaleNode(),
                new() { NodeId = "output", NodeType = "functionOutput", DisplayName = "Amount output",
                    InputPorts = [outputPort with { Name = localOutput }] }
            ],
            Edges = [Link("input", localInput, "scale", "input"), Link("scale", "value", "output", localOutput)]
        };
    }

    private static MappingNode ScaleNode() => new()
    {
        NodeId = "scale", NodeType = NodeTypeConstants.Scale, DisplayName = "Scale",
        Parameters = new() { ["factor"] = "2" },
        InputPorts = [new("input", PortShape.Decimal, PortCardinality.Single)],
        OutputPorts = [new("value", PortShape.Decimal, PortCardinality.Single)]
    };

    private static FunctionDefinition ZeroInputFunction()
    {
        var output = new PortDefinition("fixedAmount", PortShape.Decimal, PortCardinality.Single,
            IsRequired: false, Description: "A fixed amount");
        return new FunctionDefinition
        {
            FunctionId = "constant", Name = "Fixed amount", CatalogVersion = FunctionCatalog.Default.Version,
            Outputs = [output],
            Nodes =
            [
                new() { NodeId = "input", NodeType = "functionInput", DisplayName = "No inputs" },
                new() { NodeId = "constant", NodeType = NodeTypeConstants.Constant, DisplayName = "Seven",
                    Parameters = new() { ["literal"] = "7", ["shape"] = "Decimal" },
                    OutputPorts = [new("value", PortShape.Decimal, PortCardinality.Single)] },
                new() { NodeId = "output", NodeType = "functionOutput", DisplayName = "Fixed output", InputPorts = [output] }
            ],
            Edges = [Link("constant", "value", "output", "fixedAmount")]
        };
    }

    private static MappingEdge Link(string source, string sourcePort, string target, string targetPort) => new()
    {
        EdgeId = $"{source}-{sourcePort}-{target}-{targetPort}", SourceNodeId = source,
        SourcePortName = sourcePort, TargetNodeId = target, TargetPortName = targetPort
    };

    private static FunctionPackageDocument Package(FunctionDefinition function) => new()
    {
        RootFunctionId = function.FunctionId, RootRevision = function.Revision, Functions = [function]
    };

    private static string? Parameter(MappingNode node, string name) => node.Parameters[name]?.ToString();

    private static async Task ImportAsync(FunctionLibraryEditorViewModel editor)
    {
        editor.ImportFunctionPackageCommand.Execute(null);
        await WaitForPackageOutcomeAsync(editor, "Imported ");
        Assert.StartsWith("Imported ", editor.Status, StringComparison.Ordinal);
    }

    private static async Task ExportAsync(FunctionLibraryEditorViewModel editor)
    {
        editor.ExportFunctionPackageCommand.Execute(null);
        await WaitForPackageOutcomeAsync(editor, "Exported ");
        Assert.StartsWith("Exported ", editor.Status, StringComparison.Ordinal);
    }

    private static Task WaitForPackageOutcomeAsync(FunctionLibraryEditorViewModel editor, string success)
        => WaitUntilAsync(() => editor.Status.StartsWith(success, StringComparison.Ordinal)
            || editor.Status.StartsWith("Function package import failed:", StringComparison.Ordinal)
            || editor.Status.StartsWith("Function package export failed:", StringComparison.Ordinal), () => editor.Status);

    private static async Task WaitUntilAsync(Func<bool> complete, Func<string> status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!complete())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Function operation did not complete. {status()}");
            await Task.Delay(10);
        }
    }

    private sealed class PackageFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "xms-typed-functions", Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Directory, "input.xfunc.json");
        public string Output => Path.Combine(Directory, "output.xfunc.json");
        public PackageFiles() => System.IO.Directory.CreateDirectory(Directory);
        public Task WriteAsync(FunctionPackageDocument package) => File.WriteAllTextAsync(Input, FunctionPackageSerializer.Serialize(package));
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class PackageDialog : IProjectFileDialog
    {
        public string? OpenPath { get; init; }
        public string? SavePath { get; init; }
        public TaskCompletionSource<string?>? PendingOpen { get; set; }
        public TaskCompletionSource<string?>? PendingSave { get; init; }
        public int OpenCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public Task<string?> OpenFunctionPackageAsync() { OpenCalls++; return PendingOpen?.Task ?? Task.FromResult(OpenPath); }
        public Task<string?> SaveFunctionPackageAsync() { SaveCalls++; return PendingSave?.Task ?? Task.FromResult(SavePath); }
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveProjectAsync() => Task.FromResult<string?>(null);
        public Task<LegacyProjectProfileSelection?> SelectLegacyProjectProfileAsync(IReadOnlyList<LegacyProjectProfileOption> candidates,
            string? declaredProfileId, string? declaredProfileDigest) => Task.FromResult<LegacyProjectProfileSelection?>(null);
        public Task<bool> ConfirmLegacyProjectProfileAdoptionAsync(string sourcePath, string destinationPath,
            LegacyProjectProfileOption selectedProfile, IReadOnlyList<string> addedIdentityFields) => Task.FromResult(false);
        public Task<bool> ConfirmSaveAsIndependentCopyAsync(string projectName, string destinationPath) => Task.FromResult(false);
        public Task<bool> ConfirmOpenProjectBackupAsync(string projectPath, string backupPath) => Task.FromResult(false);
        public Task<UnsavedProjectChoice> ConfirmUnsavedProjectChangesAsync(string projectName, string action)
            => Task.FromResult(UnsavedProjectChoice.Discard);
        public Task<string?> SaveReportAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenSourceAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenDestinationDatabaseAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveDestinationFileAsync(string kind) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmDestinationPublicationAsync(string databasePath, string table, IReadOnlyList<string> businessKey,
            int factCount, string semanticDigest) => Task.FromResult(false);
        public Task<bool> ConfirmFileDestinationPublicationAsync(string destinationPath, string format, int factCount,
            string semanticDigest) => Task.FromResult(false);
    }
}
