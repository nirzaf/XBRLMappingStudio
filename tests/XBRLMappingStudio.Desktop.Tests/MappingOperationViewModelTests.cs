using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Desktop;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;
using System.Text.Json.Nodes;

namespace XBRLMappingStudio.Desktop.Tests;

public sealed class MappingOperationViewModelTests
{
    [AvaloniaFact]
    public void FactLanguageInspectorEditsAccessibleTextFieldAndAppliesProfileValidatedValue()
    {
        var project = new MainWindowViewModel().Session.Project;
        var fact = project.Nodes.Single(node => node.NodeId == "sample-fact");
        fact.Parameters["concept"] = SyntheticProfile.Qualified("EntityRegistrantName");
        fact.Parameters.Remove("unit");
        fact.Parameters.Remove("decimals");
        fact.Parameters["language"] = "en";
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

        var viewModel = new MainWindowViewModel(project);
        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.NodeId == "sample-fact");
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            var languageBox = Assert.IsType<TextBox>(window.FindControl<TextBox>("FactLanguageTextBox"));
            var applyButton = Assert.IsType<Button>(window.FindControl<Button>("ApplyFactPropertiesButton"));

            // Observe the bound control before editing, including its accessible name.
            Assert.Equal("en", languageBox.Text);
            Assert.Equal("Fact language tag", AutomationProperties.GetName(languageBox));
            Assert.Equal("en", viewModel.SelectedFactLanguage);

            // Edit the text field, move focus to Apply, and invoke its bound command.
            languageBox.Text = "fr";
            applyButton.Focus();
            Assert.NotNull(applyButton.Command);
            applyButton.Command!.Execute(applyButton.CommandParameter);

            // Verify the editor binding and undoable graph update reached the model.
            Assert.Equal("fr", viewModel.SelectedFactLanguage);
            Assert.Equal("fr", viewModel.Session.Project.Nodes.Single(node => node.NodeId == "sample-fact")
                .Parameters["language"]?.ToString());
            Assert.Contains("one undoable change", viewModel.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SourceInspectorExposesAccessibleSchemaRecheckAndBlocksRunAfterDrift()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-source-schema-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.csv");
        File.WriteAllText(sourcePath, "Revenue\n1250\n");
        var viewModel = new MainWindowViewModel();
        try
        {
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(Path.Combine(directory, "mapping.xmap"));
            await viewModel.LoadSourceAsync(sourcePath);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                var recheckButton = Assert.IsType<Button>(window.FindControl<Button>("RevalidateSourceSchemaButton"));

                // Observe the actual XAML command control and accessibility name before use.
                Assert.Equal("Recheck the current source schema against the saved mapping",
                    AutomationProperties.GetName(recheckButton));
                Assert.True(recheckButton.IsEnabled);
                Assert.NotNull(recheckButton.Command);

                async Task ExecuteRecheckAndWaitAsync()
                {
                    var operationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var observedLoading = false;
                    PropertyChangedEventHandler handler = (_, eventArgs) =>
                    {
                        if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsSourceLoading)) return;
                        if (viewModel.IsSourceLoading) observedLoading = true;
                        else if (observedLoading) operationCompleted.TrySetResult();
                    };
                    viewModel.PropertyChanged += handler;
                    try
                    {
                        recheckButton.Command!.Execute(recheckButton.CommandParameter);
                        await operationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        viewModel.PropertyChanged -= handler;
                    }
                }

                // Act through the bound UI command; verify the source remains accepted.
                await ExecuteRecheckAndWaitAsync();
                Assert.False(viewModel.IsSourceSchemaDrifted);
                Assert.Contains("recheck passed", viewModel.SourceInspectionSummary, StringComparison.OrdinalIgnoreCase);

                File.WriteAllText(sourcePath, "Operating Revenue\n1250\n");
                await ExecuteRecheckAndWaitAsync();

                Assert.True(viewModel.IsSourceSchemaDrifted);
                Assert.False(viewModel.PreviewCommand.CanExecute(null));
                Assert.False(viewModel.ExecuteCommand.CanExecute(null));
                Assert.Contains("schema changed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            viewModel.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task PreviewResultLabelsPartialOutputAndExposesFactMappingEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-preview-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindowViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            var workbookPath = IndependentAcceptanceFixtures.Materialize(directory, "revenue.xlsx");
            var mappingOracle = IndependentAcceptanceFixtures.ReadFactOracle("workbookMapping");
            var project = TestFixtures.BuildRevenueMappingProject(workbookPath);
            var originalFactNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var duplicateFactNode = originalFactNode.ShallowCopy() with
            {
                NodeId = Id.New(),
                DisplayName = "Second Revenue fact"
            };
            var duplicateEdges = project.Edges
                .Where(edge => StringComparer.Ordinal.Equals(edge.TargetNodeId, originalFactNode.NodeId))
                .Select(edge => edge with { EdgeId = Id.New(), TargetNodeId = duplicateFactNode.NodeId })
                .ToArray();
            project.Nodes.Add(duplicateFactNode);
            project.Edges.AddRange(duplicateEdges);
            viewModel = new MainWindowViewModel(project, null,
                (_, _) => throw new NotSupportedException("The fixture uses an Excel source."));
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(Path.Combine(directory, "mapping.xmap"));
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            var resultTabs = Assert.IsType<TabControl>(window.FindControl<TabControl>("RunResultTabs"));
            var outputsList = Assert.IsType<ListBox>(window.FindControl<ListBox>("IntermediateOutputsList"));
            var samplesList = Assert.IsType<ListBox>(window.FindControl<ListBox>("IntermediateOutputSamplesList"));
            var contributorsList = Assert.IsType<ListBox>(window.FindControl<ListBox>("IntermediateOutputEvidenceList"));
            Assert.Equal("Intermediate node outputs", AutomationProperties.GetName(outputsList));
            Assert.Equal("Sampled intermediate values", AutomationProperties.GetName(samplesList));
            Assert.Equal("Intermediate output contributors", AutomationProperties.GetName(contributorsList));
            Assert.Equal(0, resultTabs.SelectedIndex);
            Assert.Same(viewModel.IntermediateOutputs, outputsList.ItemsSource);
            Assert.Empty(viewModel.IntermediateOutputs);

            var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedBusy = false;
            viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsBusy)) return;
                if (viewModel.IsBusy) observedBusy = true;
                else if (observedBusy) operationStopped.TrySetResult();
            };

            viewModel.PreviewCommand.Execute(null);
            await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.StartsWith("Preview only", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            Assert.Contains("partial", viewModel.PreviewResultSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Provenance incomplete", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            Assert.Contains("worker finding-to-fact links", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            Assert.Equal(2, viewModel.PreviewFacts.Count);
            foreach (var previewFact in viewModel.PreviewFacts)
            {
                Assert.NotNull(previewFact.SemanticFact);
                Assert.Empty(IndependentAcceptanceFixtures.CompareFact(mappingOracle, previewFact.SemanticFact!));
                var producingNode = Assert.Single(project.Nodes, node =>
                    node.NodeType == NodeTypeConstants.FactConstruct
                    && StringComparer.Ordinal.Equals(node.Parameters.GetValueOrDefault("concept")?.ToString(),
                        previewFact.SemanticFact!.ConceptName)
                    && previewFact.SemanticFact.SourceTransformationPaths.Any(path =>
                        path.TransformationIds.Contains(node.NodeId, StringComparer.Ordinal)));
                Assert.Contains($"{producingNode.DisplayName} ({producingNode.NodeId})",
                    previewFact.StageSummary, StringComparison.Ordinal);
                Assert.DoesNotContain("ambiguous", previewFact.StageSummary, StringComparison.OrdinalIgnoreCase);
            }

            var fact = viewModel.PreviewFacts[0];
            Assert.Contains($"{mappingOracle.Concept} = {mappingOracle.ValueLexical}", fact.Display, StringComparison.Ordinal);
            Assert.Contains(mappingOracle.Unit, fact.Display, StringComparison.Ordinal);
            Assert.Contains($"{mappingOracle.PeriodStart:yyyy-MM-dd}/{mappingOracle.PeriodEnd:yyyy-MM-dd}",
                fact.Display, StringComparison.Ordinal);
            Assert.NotNull(fact.SemanticFact);
            var evidence = Assert.Single(fact.Evidence);
            Assert.Equal(mappingOracle.SourceLocator, evidence.Source.Locator);
            var sourcePath = Assert.Single(fact.SemanticFact!.SourceTransformationPaths);
            Assert.Equal(sourcePath.Source, evidence.Source);
            foreach (var transformationId in sourcePath.TransformationIds)
            {
                var node = Assert.Single(project.Nodes, candidate => candidate.NodeId == transformationId);
                Assert.Contains($"{node.DisplayName} ({node.NodeId})", evidence.TransformationPathDisplay,
                    StringComparison.Ordinal);
            }
            Assert.Same(evidence, viewModel.SelectedPreviewEvidence);
            Assert.Contains("Fact construction", fact.StageSummary, StringComparison.Ordinal);
            Assert.Contains("Scale to units", fact.StageSummary, StringComparison.Ordinal);
            Assert.Contains("Scale to units", evidence.TransformationPathDisplay, StringComparison.Ordinal);
            Assert.NotEmpty(fact.Evidence);
            Assert.Same(fact, viewModel.SelectedPreviewFact);

            Assert.StartsWith("Partial preview", viewModel.IntermediateOutputSummary, StringComparison.Ordinal);
            resultTabs.SelectedIndex = 1;
            var sourceNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField);
            var sourceOutput = Assert.Single(viewModel.IntermediateOutputs, output =>
                output.NodeId == sourceNode.NodeId && output.PortName == "value");
            Assert.Contains("partial sampled value(s)", sourceOutput.Display, StringComparison.Ordinal);
            Assert.Same(viewModel.IntermediateOutputs, outputsList.ItemsSource);
            outputsList.SelectedItem = sourceOutput;
            Assert.Same(sourceOutput, viewModel.SelectedIntermediateOutput);
            var sampledOutput = sourceOutput.Samples.First();
            Assert.Same(sampledOutput, samplesList.SelectedItem);
            var sampleEvidence = Assert.Single(sampledOutput.Evidence);
            Assert.Equal(mappingOracle.SourceLocator, sampleEvidence.Source.Locator);
            Assert.Contains(sourceNode.DisplayName, sampleEvidence.TransformationPathDisplay, StringComparison.Ordinal);
            Assert.Same(sampleEvidence, contributorsList.SelectedItem);
        }
        finally
        {
            window?.Close();
            viewModel?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRunFactResultsArePagedForMemoryAndSpoolBackedRuns(bool forceLargeRun)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-fact-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindowViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            var databasePath = await CreatePagedFactDatabaseAsync(directory, rowCount: 501);
            var project = TestFixtures.BuildRevenueMappingProject(databasePath);
            var binding = project.Bindings.Single();
            project.Bindings[0] = binding with
            {
                Kind = "sqlite",
                Locator = Path.GetFileName(databasePath),
                Configuration = new Dictionary<string, object?>
                {
                    ["table"] = "financials",
                    ["decimalColumns"] = "Current,Prior"
                }
            };
            var entityNodeIndex = project.Nodes.FindIndex(node => node.DisplayName == "Entity");
            Assert.True(entityNodeIndex >= 0);
            var entityNode = project.Nodes[entityNodeIndex];
            project.Nodes[entityNodeIndex] = entityNode with
            {
                NodeType = NodeTypeConstants.ExcelField,
                DisplayName = "Source entity",
                Parameters = new Dictionary<string, object?>
                {
                    ["bindingId"] = binding.BindingId,
                    ["column"] = "Entity"
                }
            };
            var profile = SyntheticProfile.Build();
            project.Approvals.Clear();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "fact-page-test-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest,
                Authority = "test-fixture"
            });

            var outputPath = Path.Combine(directory, "paged-result.json");
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: forceLargeRun ? 1 : 1_000,
                RowBatchSize: 64);
            var activeViewModel = new MainWindowViewModel(project, new FixedReportFileDialog(outputPath),
                (_, _) => throw new NotSupportedException("The fixture uses a SQLite source."),
                executionBudget: budget);
            viewModel = activeViewModel;
            activeViewModel.AllowSyntheticProfile = true;
            activeViewModel.SaveProjectTo(Path.Combine(directory, "mapping.xmap"));

            window = new MainWindow { DataContext = activeViewModel };
            window.Show();
            var resultSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("PreviewResultSummaryText"));
            var resultsList = Assert.IsType<ListBox>(window.FindControl<ListBox>("GeneratedFactResultsList"));
            var previousButton = Assert.IsType<Button>(window.FindControl<Button>("PreviousFactPreviewPageButton"));
            var nextButton = Assert.IsType<Button>(window.FindControl<Button>("NextFactPreviewPageButton"));
            var pageSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("FactPageSummaryText"));
            var stageSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedFactStageText"));
            var evidenceList = Assert.IsType<ListBox>(window.FindControl<ListBox>("SelectedFactEvidenceList"));
            var evidencePath = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedFactTransformationPathText"));
            var intermediateSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("IntermediateOutputSummaryText"));
            var intermediateList = Assert.IsType<ListBox>(window.FindControl<ListBox>("IntermediateOutputsList"));
            var intermediateSamples = Assert.IsType<ListBox>(window.FindControl<ListBox>("IntermediateOutputSamplesList"));
            var intermediateEvidence = Assert.IsType<ListBox>(window.FindControl<ListBox>("IntermediateOutputEvidenceList"));
            Assert.Equal("Previous fact results page", AutomationProperties.GetName(previousButton));
            Assert.Equal("Next fact results page", AutomationProperties.GetName(nextButton));
            Assert.Equal("Generated fact results", AutomationProperties.GetName(resultsList));
            Assert.Equal("Selected fact contributing source evidence", AutomationProperties.GetName(evidenceList));
            Assert.Equal("Intermediate node outputs", AutomationProperties.GetName(intermediateList));
            Assert.Equal("Sampled intermediate values", AutomationProperties.GetName(intermediateSamples));
            Assert.Equal("Intermediate output contributors", AutomationProperties.GetName(intermediateEvidence));
            Assert.Equal("Run a preview or full execution to inspect generated facts and evidence.", resultSummary.Text);
            Assert.Equal("No fact results are available.", pageSummary.Text);
            Assert.Equal("Run a preview or full execution to inspect intermediate node values.", intermediateSummary.Text);
            Assert.Same(activeViewModel.PreviewFacts, resultsList.ItemsSource);
            Assert.Empty(activeViewModel.PreviewFacts);
            Assert.Same(activeViewModel.IntermediateOutputs, intermediateList.ItemsSource);
            Assert.Empty(activeViewModel.IntermediateOutputs);
            Assert.False(previousButton.IsEnabled);
            Assert.False(nextButton.IsEnabled);

            var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedBusy = false;
            PropertyChangedEventHandler handler = (_, eventArgs) =>
            {
                if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsBusy)) return;
                if (activeViewModel.IsBusy) observedBusy = true;
                else if (observedBusy) operationStopped.TrySetResult();
            };
            activeViewModel.PropertyChanged += handler;
            try
            {
                activeViewModel.ExecuteCommand.Execute(null);
                await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally { activeViewModel.PropertyChanged -= handler; }

            Assert.StartsWith("Full run · 501 fact(s)", resultSummary.Text, StringComparison.Ordinal);
            Assert.StartsWith("Published paged-result.json", activeViewModel.StatusText, StringComparison.Ordinal);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(500, activeViewModel.PreviewFacts.Count);
            Assert.Same(activeViewModel.PreviewFacts, resultsList.ItemsSource);
            Assert.Equal("Facts 1–500 of 501", activeViewModel.FactPageSummary);
            Assert.Equal("Facts 1–500 of 501", pageSummary.Text);
            Assert.StartsWith("Full run:", intermediateSummary.Text, StringComparison.Ordinal);
            var currentSourceNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField
                && StringComparer.Ordinal.Equals(node.Parameters.GetValueOrDefault("column")?.ToString(), "Current"));
            var currentOutput = Assert.Single(activeViewModel.IntermediateOutputs, output =>
                output.NodeId == currentSourceNode.NodeId && output.PortName == "value");
            Assert.Contains("501 value(s)", currentOutput.Display, StringComparison.Ordinal);
            Assert.Contains("501 present", currentOutput.Display, StringComparison.Ordinal);
            Assert.Same(activeViewModel.IntermediateOutputs, intermediateList.ItemsSource);
            intermediateList.SelectedItem = currentOutput;
            Assert.Same(currentOutput, activeViewModel.SelectedIntermediateOutput);
            var firstSample = currentOutput.Samples[0];
            Assert.Same(firstSample, intermediateSamples.SelectedItem);
            Assert.Contains("Row 1:", firstSample.Display, StringComparison.Ordinal);
            var firstSampleEvidence = Assert.Single(firstSample.Evidence);
            Assert.Contains("financials.Current", firstSampleEvidence.Source.Locator, StringComparison.Ordinal);
            Assert.Same(firstSampleEvidence, intermediateEvidence.SelectedItem);
            var firstPageFact = Assert.Single(activeViewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Entity-0001");
            Assert.Same(firstPageFact, resultsList.SelectedItem);
            Assert.Contains("2 contributing source references", firstPageFact.Display, StringComparison.Ordinal);
            Assert.Equal(firstPageFact.StageSummary, stageSummary.Text);
            var firstPageEvidence = Assert.IsAssignableFrom<IEnumerable<PreviewEvidenceRow>>(evidenceList.ItemsSource)
                .ToArray();
            Assert.Equal(new[] { "financials.Current", "financials.Entity" },
                firstPageEvidence.Select(evidence => evidence.Source.Locator)
                    .OrderBy(locator => locator, StringComparer.Ordinal));
            Assert.Contains(firstPageFact.Display, Assert.IsAssignableFrom<IEnumerable<FactRow>>(resultsList.ItemsSource)
                .Select(fact => fact.Display), StringComparer.Ordinal);
            var currentValueEvidence = Assert.Single(firstPageEvidence,
                evidence => evidence.Source.Locator == "financials.Current");
            evidenceList.SelectedItem = currentValueEvidence;
            Assert.Same(currentValueEvidence, activeViewModel.SelectedPreviewEvidence);
            Assert.Equal(currentValueEvidence.TransformationPathDisplay, evidencePath.Text);
            Assert.False(previousButton.IsEnabled);
            Assert.True(nextButton.IsEnabled);
            Assert.False(activeViewModel.PreviousFactPreviewPageCommand.CanExecute(null));
            Assert.True(activeViewModel.NextFactPreviewPageCommand.CanExecute(null));

            activeViewModel.NextFactPreviewPageCommand.Execute(null);

            Assert.Single(activeViewModel.PreviewFacts);
            Assert.Same(activeViewModel.PreviewFacts, resultsList.ItemsSource);
            Assert.Equal("Facts 501–501 of 501", activeViewModel.FactPageSummary);
            Assert.Equal("Facts 501–501 of 501", pageSummary.Text);
            Assert.Equal("Entity-0501", activeViewModel.PreviewFacts[0].SemanticFact!.EntityIdentifier);
            Assert.Same(activeViewModel.PreviewFacts[0], resultsList.SelectedItem);
            Assert.Contains("financials.Current", Assert.IsAssignableFrom<IEnumerable<PreviewEvidenceRow>>(evidenceList.ItemsSource)
                .Select(evidence => evidence.Source.Locator), StringComparer.Ordinal);
            Assert.Equal(activeViewModel.PreviewFacts[0].StageSummary, stageSummary.Text);
            Assert.True(previousButton.IsEnabled);
            Assert.False(nextButton.IsEnabled);
            Assert.True(activeViewModel.PreviousFactPreviewPageCommand.CanExecute(null));
            Assert.False(activeViewModel.NextFactPreviewPageCommand.CanExecute(null));

            activeViewModel.PreviousFactPreviewPageCommand.Execute(null);

            Assert.Equal(500, activeViewModel.PreviewFacts.Count);
            Assert.Equal("Facts 1–500 of 501", activeViewModel.FactPageSummary);
            Assert.Equal("Facts 1–500 of 501", pageSummary.Text);
            Assert.Equal("Entity-0001", activeViewModel.PreviewFacts[0].SemanticFact!.EntityIdentifier);
        }
        finally
        {
            window?.Close();
            viewModel?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GroupedPreviewReconcilesWithFullRunAndKeepsBlankDistinctFromZero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-group-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbookPath = TestFixtures.MaterializeGroupedRevenueWorkbook(directory);
            var project = TestFixtures.BuildGroupedRevenueMappingProject(workbookPath);
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = SyntheticProfile.Build().Digest
            });
            var outputPath = Path.Combine(directory, "grouped-result.json");
            var viewModel = new MainWindowViewModel(project, new FixedReportFileDialog(outputPath),
                (_, _) => throw new NotSupportedException("The fixture uses an Excel source."));
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(Path.Combine(directory, "grouped-mapping.xmap"));

            async Task RunAndWaitAsync(RelayCommand command)
            {
                var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var observedBusy = false;
                PropertyChangedEventHandler handler = (_, eventArgs) =>
                {
                    if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsBusy)) return;
                    if (viewModel.IsBusy) observedBusy = true;
                    else if (observedBusy) operationStopped.TrySetResult();
                };
                viewModel.PropertyChanged += handler;
                try
                {
                    command.Execute(null);
                    await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally { viewModel.PropertyChanged -= handler; }
            }

            await RunAndWaitAsync(viewModel.PreviewCommand);

            Assert.StartsWith("Preview only", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            Assert.Contains("2 sampled aggregate(s) are partial", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            var revenueFact = Assert.Single(viewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Revenue");
            Assert.Equal(20m, revenueFact.SemanticFact!.Value);
            var previewFacts = viewModel.PreviewFacts
                .Select(fact => (fact.SemanticFact!.ConceptName,
                    fact.SemanticFact.EntityIdentifier, fact.SemanticFact.Value))
                .OrderBy(fact => fact.EntityIdentifier, StringComparer.Ordinal)
                .ToArray();
            var previewRevenueEvidence = revenueFact.Evidence
                .Select(evidence => $"{evidence.Source.Locator}|{evidence.TransformationPathDisplay}")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[]
            {
                "Financials!A4", "Financials!A5", "Financials!B4", "Financials!B5"
            }, revenueFact.Evidence.Select(evidence => evidence.Source.Locator)
                .OrderBy(locator => locator, StringComparer.Ordinal));
            Assert.Contains(viewModel.PreviewFacts,
                fact => fact.SemanticFact is { EntityIdentifier: "Zero", Value: 0m });
            Assert.DoesNotContain(viewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Missing amount");

            foreach (var evidence in revenueFact.Evidence)
            {
                Assert.Contains("Collect financial rows", evidence.TransformationPathDisplay, StringComparison.Ordinal);
                Assert.Contains("Sum by statement line", evidence.TransformationPathDisplay, StringComparison.Ordinal);
                Assert.Contains("Grouped Revenue fact", evidence.TransformationPathDisplay, StringComparison.Ordinal);
            }
            Assert.Same(revenueFact, viewModel.SelectedPreviewFact);
            Assert.Same(revenueFact.Evidence[0], viewModel.SelectedPreviewEvidence);

            await RunAndWaitAsync(viewModel.ExecuteCommand);

            Assert.StartsWith("Full run ·", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            Assert.StartsWith("Published grouped-result.json", viewModel.StatusText, StringComparison.Ordinal);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(previewFacts, viewModel.PreviewFacts
                .Select(fact => (fact.SemanticFact!.ConceptName,
                    fact.SemanticFact.EntityIdentifier, fact.SemanticFact.Value))
                .OrderBy(fact => fact.EntityIdentifier, StringComparer.Ordinal)
                .ToArray());
            var fullRevenueFact = Assert.Single(viewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Revenue");
            Assert.Equal(20m, fullRevenueFact.SemanticFact!.Value);
            Assert.Equal(previewRevenueEvidence, fullRevenueFact.Evidence
                .Select(evidence => $"{evidence.Source.Locator}|{evidence.TransformationPathDisplay}")
                .OrderBy(value => value, StringComparer.Ordinal));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task JoinedPreviewShowsOneToManyContributorsAndKeepsMissingDistinctFromZero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-one-to-many-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindowViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            var workbookPath = TestFixtures.MaterializeOneToManyRevenueWorkbook(directory);
            var project = TestFixtures.BuildOneToManyGroupedRevenueMappingProject(workbookPath);
            var activeViewModel = new MainWindowViewModel(project, null,
                (_, _) => throw new NotSupportedException("The fixture uses Excel sources."));
            viewModel = activeViewModel;
            activeViewModel.AllowSyntheticProfile = true;
            activeViewModel.SaveProjectTo(Path.Combine(directory, "one-to-many-mapping.xmap"));

            window = new MainWindow { DataContext = activeViewModel };
            window.Show();
            var resultSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("PreviewResultSummaryText"));
            var resultsList = Assert.IsType<ListBox>(window.FindControl<ListBox>("GeneratedFactResultsList"));
            var pageSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("FactPageSummaryText"));
            var stageSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedFactStageText"));
            var evidenceList = Assert.IsType<ListBox>(window.FindControl<ListBox>("SelectedFactEvidenceList"));
            var evidencePath = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedFactTransformationPathText"));

            // Observe the loaded result pane before starting the preview.
            Assert.Equal("Run a preview or full execution to inspect generated facts and evidence.", resultSummary.Text);
            Assert.Equal("No fact results are available.", pageSummary.Text);
            Assert.Same(activeViewModel.PreviewFacts, resultsList.ItemsSource);
            Assert.Empty(activeViewModel.PreviewFacts);
            Assert.Equal("Generated fact results", AutomationProperties.GetName(resultsList));
            Assert.Equal("Selected fact contributing source evidence", AutomationProperties.GetName(evidenceList));

            var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedBusy = false;
            PropertyChangedEventHandler handler = (_, eventArgs) =>
            {
                if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsBusy)) return;
                if (activeViewModel.IsBusy) observedBusy = true;
                else if (observedBusy) operationStopped.TrySetResult();
            };
            activeViewModel.PropertyChanged += handler;
            try
            {
                activeViewModel.PreviewCommand.Execute(null);
                await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { activeViewModel.PropertyChanged -= handler; }

            Assert.StartsWith("Preview only", resultSummary.Text, StringComparison.Ordinal);
            Assert.Contains("partial", resultSummary.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Facts 1–2 of 2", pageSummary.Text);
            Assert.Same(activeViewModel.PreviewFacts, resultsList.ItemsSource);
            Assert.Equal(2, activeViewModel.PreviewFacts.Count);
            var revenueFact = Assert.Single(activeViewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Revenue");
            Assert.Equal(20m, revenueFact.SemanticFact!.Value);
            Assert.Contains("6 contributing source references", revenueFact.Display, StringComparison.Ordinal);
            Assert.Contains(revenueFact.Display, Assert.IsAssignableFrom<IEnumerable<FactRow>>(resultsList.ItemsSource)
                .Select(row => row.Display), StringComparer.Ordinal);
            var locators = revenueFact.Evidence.Select(evidence => evidence.Source.Locator)
                .OrderBy(locator => locator, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "Dimensions!A4", "Dimensions!A5", "Dimensions!B4", "Dimensions!B5",
                "Transactions!A4", "Transactions!C4"
            }, locators);
            Assert.Contains(activeViewModel.PreviewFacts,
                fact => fact.SemanticFact is { EntityIdentifier: "Zero", Value: 0m });
            var zeroFact = Assert.Single(activeViewModel.PreviewFacts,
                fact => fact.SemanticFact is { EntityIdentifier: "Zero", Value: 0m });
            Assert.Contains("= 0", zeroFact.Display, StringComparison.Ordinal);
            Assert.DoesNotContain(activeViewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Missing amount");

            // Act through the result-list selection and verify the rendered detail pane.
            resultsList.SelectedItem = revenueFact;
            Assert.Same(revenueFact, activeViewModel.SelectedPreviewFact);
            Assert.Equal(revenueFact.StageSummary, stageSummary.Text);
            var renderedEvidence = Assert.IsAssignableFrom<IEnumerable<PreviewEvidenceRow>>(evidenceList.ItemsSource)
                .ToArray();
            Assert.Equal(6, renderedEvidence.Length);
            Assert.Equal(locators, renderedEvidence.Select(evidence => evidence.Source.Locator)
                .OrderBy(locator => locator, StringComparer.Ordinal).ToArray());
            evidenceList.SelectedIndex = 0;
            Assert.Same(renderedEvidence[0], activeViewModel.SelectedPreviewEvidence);
            Assert.Equal(renderedEvidence[0].TransformationPathDisplay, evidencePath.Text);
            Assert.Contains("Joined Revenue fact", stageSummary.Text, StringComparison.Ordinal);

            foreach (var evidence in renderedEvidence)
            {
                var expectedCollector = evidence.Source.Locator.StartsWith("Dimensions!", StringComparison.Ordinal)
                    ? "Collect dimensions"
                    : "Collect transactions";
                Assert.Contains(expectedCollector, evidence.TransformationPathDisplay, StringComparison.Ordinal);
                Assert.Contains("Match transaction dimensions", evidence.TransformationPathDisplay, StringComparison.Ordinal);
            }
        }
        finally
        {
            window?.Close();
            viewModel?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task DuplicateFactPreviewKeepsEachOccurrenceAndItsSourceRowSeparate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-duplicate-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindowViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            var workbookPath = TestFixtures.MaterializeGroupedRevenueWorkbook(directory);
            var project = TestFixtures.BuildRevenueMappingProject(workbookPath);
            var activeViewModel = new MainWindowViewModel(project, null,
                (_, _) => throw new NotSupportedException("The fixture uses an Excel source."));
            viewModel = activeViewModel;
            activeViewModel.AllowSyntheticProfile = true;
            activeViewModel.SaveProjectTo(Path.Combine(directory, "duplicate-mapping.xmap"));

            window = new MainWindow { DataContext = activeViewModel };
            window.Show();
            var resultSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("PreviewResultSummaryText"));
            var resultsList = Assert.IsType<ListBox>(window.FindControl<ListBox>("GeneratedFactResultsList"));
            var pageSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("FactPageSummaryText"));
            var stageSummary = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedFactStageText"));
            var evidenceList = Assert.IsType<ListBox>(window.FindControl<ListBox>("SelectedFactEvidenceList"));
            var evidencePath = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedFactTransformationPathText"));

            // Observe the initial XAML result controls before triggering the preview.
            Assert.Equal("Run a preview or full execution to inspect generated facts and evidence.", resultSummary.Text);
            Assert.Equal("No fact results are available.", pageSummary.Text);
            Assert.Same(activeViewModel.PreviewFacts, resultsList.ItemsSource);
            Assert.Empty(activeViewModel.PreviewFacts);

            var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedBusy = false;
            PropertyChangedEventHandler handler = (_, eventArgs) =>
            {
                if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsBusy)) return;
                if (activeViewModel.IsBusy) observedBusy = true;
                else if (observedBusy) operationStopped.TrySetResult();
            };
            activeViewModel.PropertyChanged += handler;
            try
            {
                activeViewModel.PreviewCommand.Execute(null);
                await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { activeViewModel.PropertyChanged -= handler; }

            Assert.StartsWith("Preview only", resultSummary.Text, StringComparison.Ordinal);
            Assert.Equal("Facts 1–2 of 2", pageSummary.Text);
            var facts = activeViewModel.PreviewFacts
                .OrderBy(fact => fact.Evidence.Single().Source.Locator, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, facts.Length);
            Assert.Equal(10_000m, facts[0].SemanticFact!.Value);
            Assert.Equal(10_000m, facts[1].SemanticFact!.Value);
            Assert.Equal(facts[0].SemanticFact!.ConceptName, facts[1].SemanticFact!.ConceptName);
            Assert.Equal(facts[0].SemanticFact!.EntityIdentifier, facts[1].SemanticFact!.EntityIdentifier);
            Assert.Equal("Financials!B4", facts[0].Evidence.Single().Source.Locator);
            Assert.Equal("Financials!B5", facts[1].Evidence.Single().Source.Locator);
            Assert.Contains("source Financials!B4", facts[0].Display, StringComparison.Ordinal);
            Assert.Contains("source Financials!B5", facts[1].Display, StringComparison.Ordinal);

            // Select both duplicate business facts and verify source evidence follows each selection.
            var displayedFacts = Assert.IsAssignableFrom<IEnumerable<FactRow>>(resultsList.ItemsSource).ToArray();
            Assert.Equal(2, displayedFacts.Length);
            var firstIndex = Array.FindIndex(displayedFacts, fact => ReferenceEquals(fact, facts[0]));
            var secondIndex = Array.FindIndex(displayedFacts, fact => ReferenceEquals(fact, facts[1]));
            Assert.True(firstIndex >= 0);
            Assert.True(secondIndex >= 0);
            resultsList.SelectedIndex = firstIndex;
            Assert.Same(facts[0], activeViewModel.SelectedPreviewFact);
            Assert.Equal(facts[0].StageSummary, stageSummary.Text);
            var firstEvidence = Assert.IsAssignableFrom<IEnumerable<PreviewEvidenceRow>>(evidenceList.ItemsSource).ToArray();
            Assert.Equal("Financials!B4", Assert.Single(firstEvidence).Source.Locator);
            Assert.Equal(firstEvidence[0].TransformationPathDisplay, evidencePath.Text);

            resultsList.SelectedIndex = secondIndex;
            Assert.Same(facts[1], activeViewModel.SelectedPreviewFact);
            Assert.Equal(facts[1].StageSummary, stageSummary.Text);
            var secondEvidence = Assert.IsAssignableFrom<IEnumerable<PreviewEvidenceRow>>(evidenceList.ItemsSource).ToArray();
            Assert.Equal("Financials!B5", Assert.Single(secondEvidence).Source.Locator);
            Assert.Equal(secondEvidence[0].TransformationPathDisplay, evidencePath.Text);
        }
        finally
        {
            window?.Close();
            viewModel?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PreviewEvidenceRowDisplaysStableBoundedTransformationPathsAndMissingState()
    {
        var source = new XBRLMappingStudio.Diagnostics.SourceReference("excel", "Income!B3", "Revenue workbook");
        var paths = Enumerable.Range(1, 10)
            .Select(index => new XBRLMappingStudio.Diagnostics.SourceTransformationPath(
                source, new[] { $"node-{index}" }))
            .ToArray();
        var transformationNames = Enumerable.Range(1, 10)
            .ToDictionary(index => $"node-{index}", index => $"Transform {index}", StringComparer.Ordinal);

        var evidence = new PreviewEvidenceRow(source, paths, transformationNames: transformationNames);

        Assert.Contains("Path 1: Transform 1 (node-1)", evidence.TransformationPathDisplay, StringComparison.Ordinal);
        Assert.Contains("Path 8: Transform 8 (node-8)", evidence.TransformationPathDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("Transform 9 (node-9)", evidence.TransformationPathDisplay, StringComparison.Ordinal);
        Assert.Contains("2 additional path(s) not shown.", evidence.TransformationPathDisplay, StringComparison.Ordinal);

        var pathlessEvidence = new PreviewEvidenceRow(source, Array.Empty<XBRLMappingStudio.Diagnostics.SourceTransformationPath>());
        Assert.Equal("Transformation path not recorded.", pathlessEvidence.TransformationPathDisplay);
    }

    [Fact]
    public async Task CancelCommandCancelsActivePreviewAndRestoresIdleState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var worker = new CancellationWaitingWorker();
            var viewModel = CreateViewModel(directory, worker);

            var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsBusy) && !viewModel.IsBusy)
                    operationStopped.TrySetResult();
            };

            Assert.True(viewModel.PreviewCommand.CanExecute(null));
            viewModel.PreviewCommand.Execute(null);
            await worker.HandshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(viewModel.IsBusy);
            Assert.True(viewModel.CanCancelOperation);
            Assert.True(viewModel.CancelOperationCommand.CanExecute(null));
            viewModel.CancelOperationCommand.Execute(null);
            await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.CanCancelOperation);
            Assert.Contains("canceled", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(viewModel.PreviewFacts);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MappingChangeCancelsPreviewAndDiscardsItsStaleResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var worker = new CancellationWaitingWorker();
            var viewModel = CreateViewModel(directory, worker);
            var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsBusy) && !viewModel.IsBusy)
                    operationStopped.TrySetResult();
            };

            viewModel.PreviewCommand.Execute(null);
            await worker.HandshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(viewModel.Session.SetDestination(new DestinationPlan
            {
                Kind = "json", Locator = "changed.json", Table = "", Format = "xbrl-facts-v1"
            }));
            await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(viewModel.IsBusy);
            Assert.Contains("mapping changed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(viewModel.PreviewFacts);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static MainWindowViewModel CreateViewModel(string directory, CancellationWaitingWorker worker)
    {
        var workbookPath = TestFixtures.MaterializeWorkbook(directory);
        var projectJson = JsonNode.Parse(ProjectSerializer.Serialize(
            TestFixtures.BuildRevenueMappingProject(workbookPath)))!;
        var profile = BuildTestProfile();
        projectJson["profileId"] = profile.ProfileId;
        projectJson["profileDigest"] = profile.Digest;
        var project = ProjectSerializer.Deserialize(projectJson.ToJsonString());
        var resolver = new FilingProfileResolver(new[] { profile });
        var workerFactory = new FilingProfileWorkerFactory(_ => worker);
        var viewModel = new MainWindowViewModel(
            project,
            null,
            (_, _) => throw new NotSupportedException("The fixture uses an Excel source."),
            resolver,
            workerFactory);
        viewModel.SaveProjectTo(Path.Combine(directory, "mapping.xmap"));
        return viewModel;
    }

    private static async Task<string> CreatePagedFactDatabaseAsync(string directory, int rowCount)
    {
        var path = Path.Combine(directory, "paged-source.db");
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        await using (var create = database.CreateCommand())
        {
            create.CommandText = "CREATE TABLE financials (Id INTEGER PRIMARY KEY, Line TEXT NOT NULL, "
                + "Current TEXT NOT NULL, Prior TEXT NOT NULL, Entity TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }
        await using var insert = database.CreateCommand();
        insert.CommandText = "INSERT INTO financials(Id, Line, Current, Prior, Entity) "
            + "VALUES($id, 'Revenue', $current, '1100', $entity);";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var current = insert.Parameters.Add("$current", SqliteType.Text);
        var entity = insert.Parameters.Add("$entity", SqliteType.Text);
        for (var row = 0; row < rowCount; row++)
        {
            id.Value = row + 1;
            current.Value = (1_250 + row).ToString(System.Globalization.CultureInfo.InvariantCulture);
            entity.Value = $"Entity-{row + 1:D4}";
            await insert.ExecuteNonQueryAsync();
        }
        return path;
    }

    private sealed class FixedReportFileDialog(string outputPath) : IProjectFileDialog
    {
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
        public Task<string?> OpenFunctionPackageAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveFunctionPackageAsync() => Task.FromResult<string?>(null);
        public Task<bool> ConfirmSaveAsIndependentCopyAsync(string projectName, string destinationPath)
            => Task.FromResult(false);
        public Task<bool> ConfirmOpenProjectBackupAsync(string projectPath, string backupPath)
            => Task.FromResult(false);
        public Task<UnsavedProjectChoice> ConfirmUnsavedProjectChangesAsync(string projectName, string action)
            => Task.FromResult(UnsavedProjectChoice.Cancel);
        public Task<string?> SaveReportAsync() => Task.FromResult<string?>(outputPath);
        public Task<string?> OpenSourceAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenDestinationDatabaseAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveDestinationFileAsync(string kind) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmDestinationPublicationAsync(string databasePath, string table,
            IReadOnlyList<string> businessKey, int factCount, string semanticDigest)
            => Task.FromResult(false);
        public Task<bool> ConfirmFileDestinationPublicationAsync(string destinationPath, string format,
            int factCount, string semanticDigest)
            => Task.FromResult(false);
    }

    private static FilingProfile BuildTestProfile()
    {
        var synthetic = SyntheticProfile.Build();
        return new FilingProfile
        {
            ProfileId = "desktop-cancellation-test",
            Digest = synthetic.Digest,
            Concepts = synthetic.Concepts,
            SupportedUnits = synthetic.SupportedUnits,
            TaxonomyVersion = synthetic.TaxonomyVersion,
            DimensionDomains = synthetic.DimensionDomains,
            OutputFormat = synthetic.OutputFormat,
            NamespaceUris = synthetic.NamespaceUris,
            TaxonomyUrls = new[] { "https://example.invalid/test-taxonomy.xsd" },
            PackageHashes = synthetic.PackageHashes,
            RequiredChecks = synthetic.RequiredChecks
        };
    }

    private sealed class CancellationWaitingWorker : IWorkerClient
    {
        public TaskCompletionSource HandshakeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
        {
            HandshakeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The canceled handshake unexpectedly continued.");
        }

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No worker request should follow a canceled handshake.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
