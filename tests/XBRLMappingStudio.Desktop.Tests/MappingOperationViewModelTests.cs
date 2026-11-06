using System.ComponentModel;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;
using System.Text.Json.Nodes;

namespace XBRLMappingStudio.Desktop.Tests;

public sealed class MappingOperationViewModelTests
{
    [Fact]
    public async Task PreviewResultLabelsPartialOutputAndExposesFactMappingEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-preview-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbookPath = IndependentAcceptanceFixtures.Materialize(directory, "revenue.xlsx");
            var mappingOracle = IndependentAcceptanceFixtures.ReadFactOracle("workbookMapping");
            var project = TestFixtures.BuildRevenueMappingProject(workbookPath);
            var viewModel = new MainWindowViewModel(project, null,
                (_, _) => throw new NotSupportedException("The fixture uses an Excel source."));
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(Path.Combine(directory, "mapping.xmap"));

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
            var fact = Assert.Single(viewModel.PreviewFacts);
            Assert.Contains($"{mappingOracle.Concept} = {mappingOracle.ValueLexical}", fact.Display, StringComparison.Ordinal);
            Assert.Contains(mappingOracle.Unit, fact.Display, StringComparison.Ordinal);
            Assert.Contains($"{mappingOracle.PeriodStart:yyyy-MM-dd}/{mappingOracle.PeriodEnd:yyyy-MM-dd}",
                fact.Display, StringComparison.Ordinal);
            Assert.NotNull(fact.SemanticFact);
            Assert.Empty(IndependentAcceptanceFixtures.CompareFact(mappingOracle, fact.SemanticFact!));
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
        }
        finally
        {
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

    [Fact]
    public async Task JoinedPreviewShowsOneToManyContributorsAndKeepsMissingDistinctFromZero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-one-to-many-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbookPath = TestFixtures.MaterializeOneToManyRevenueWorkbook(directory);
            var project = TestFixtures.BuildOneToManyGroupedRevenueMappingProject(workbookPath);
            var viewModel = new MainWindowViewModel(project, null,
                (_, _) => throw new NotSupportedException("The fixture uses Excel sources."));
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(Path.Combine(directory, "one-to-many-mapping.xmap"));

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
                viewModel.PreviewCommand.Execute(null);
                await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { viewModel.PropertyChanged -= handler; }

            Assert.StartsWith("Preview only", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            Assert.Contains("partial", viewModel.PreviewResultSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, viewModel.PreviewFacts.Count);
            var revenueFact = Assert.Single(viewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Revenue");
            Assert.Equal(20m, revenueFact.SemanticFact!.Value);
            var locators = revenueFact.Evidence.Select(evidence => evidence.Source.Locator)
                .OrderBy(locator => locator, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "Dimensions!A4", "Dimensions!A5", "Dimensions!B4", "Dimensions!B5",
                "Transactions!A4", "Transactions!C4"
            }, locators);
            Assert.Contains(viewModel.PreviewFacts,
                fact => fact.SemanticFact is { EntityIdentifier: "Zero", Value: 0m });
            Assert.DoesNotContain(viewModel.PreviewFacts,
                fact => fact.SemanticFact?.EntityIdentifier == "Missing amount");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DuplicateFactPreviewKeepsEachOccurrenceAndItsSourceRowSeparate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-desktop-duplicate-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbookPath = TestFixtures.MaterializeGroupedRevenueWorkbook(directory);
            var project = TestFixtures.BuildRevenueMappingProject(workbookPath);
            var viewModel = new MainWindowViewModel(project, null,
                (_, _) => throw new NotSupportedException("The fixture uses an Excel source."));
            viewModel.AllowSyntheticProfile = true;
            viewModel.SaveProjectTo(Path.Combine(directory, "duplicate-mapping.xmap"));

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
                viewModel.PreviewCommand.Execute(null);
                await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { viewModel.PropertyChanged -= handler; }

            Assert.StartsWith("Preview only", viewModel.PreviewResultSummary, StringComparison.Ordinal);
            Assert.Equal(2, viewModel.PreviewFacts.Count);
            var facts = viewModel.PreviewFacts
                .OrderBy(fact => fact.Evidence.Single().Source.Locator, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(10_000m, facts[0].SemanticFact!.Value);
            Assert.Equal(10_000m, facts[1].SemanticFact!.Value);
            Assert.Equal(facts[0].SemanticFact!.ConceptName, facts[1].SemanticFact!.ConceptName);
            Assert.Equal(facts[0].SemanticFact!.EntityIdentifier, facts[1].SemanticFact!.EntityIdentifier);
            Assert.Equal("Financials!B4", facts[0].Evidence.Single().Source.Locator);
            Assert.Equal("Financials!B5", facts[1].Evidence.Single().Source.Locator);
        }
        finally
        {
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

    private sealed class FixedReportFileDialog(string outputPath) : IProjectFileDialog
    {
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveProjectAsync() => Task.FromResult<string?>(null);
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
