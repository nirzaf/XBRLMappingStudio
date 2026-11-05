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
            var fact = Assert.Single(viewModel.PreviewFacts);
            Assert.Contains($"{mappingOracle.Concept} = {mappingOracle.ValueLexical}", fact.Display, StringComparison.Ordinal);
            Assert.Contains(mappingOracle.Unit, fact.Display, StringComparison.Ordinal);
            Assert.Contains($"{mappingOracle.PeriodStart:yyyy-MM-dd}/{mappingOracle.PeriodEnd:yyyy-MM-dd}",
                fact.Display, StringComparison.Ordinal);
            Assert.NotNull(fact.SemanticFact);
            Assert.Empty(IndependentAcceptanceFixtures.CompareFact(mappingOracle, fact.SemanticFact!));
            Assert.Equal(mappingOracle.SourceLocator, Assert.Single(fact.Evidence).Source.Locator);
            Assert.Contains("Fact construction", fact.StageSummary, StringComparison.Ordinal);
            Assert.Contains("Scale to units", fact.StageSummary, StringComparison.Ordinal);
            Assert.NotEmpty(fact.Evidence);
            Assert.Same(fact, viewModel.SelectedPreviewFact);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
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
