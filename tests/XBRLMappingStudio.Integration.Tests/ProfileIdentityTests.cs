using System.Collections;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class ProfileIdentityTests
{
    private static readonly FilingProfile Synthetic = SyntheticProfile.Build();

    [Theory]
    [InlineData("another-profile", "sha256:another-digest")]
    [InlineData(SyntheticProfile.ProfileId, "sha256:another-digest")]
    public void Validate_RejectsProfileIdOrDigestMismatch(string profileId, string profileDigest)
    {
        var project = ProjectWithProfile(profileId, profileDigest);
        var service = new MappingJobService(Synthetic, new UnexpectedWorker());

        var result = service.Validate(project);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics.Items);
        Assert.Equal(DiagnosticCodes.XbrProfileMismatch, diagnostic.Code);
        Assert.Equal(DiagnosticStage.Validation, diagnostic.Stage);
        Assert.True(diagnostic.IsBlocking);
    }

    [Fact]
    public async Task RunAsync_RejectsProfileMismatchBeforeReadingSourcesOrCallingWorker()
    {
        var project = ProjectWithProfile("another-profile", "sha256:another-digest");
        var worker = new UnexpectedWorker();
        var service = new MappingJobService(Synthetic, worker);

        var result = await service.RunAsync(
            project,
            new UnexpectedConnections(),
            new Dictionary<string, string>(),
            preview: true,
            CancellationToken.None);

        Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
        Assert.Empty(result.Manifest.Inputs);
        Assert.Empty(result.Facts);
        Assert.Empty(result.XbrlJson);
        Assert.Contains(result.Manifest.Diagnostics, d => d.Code == DiagnosticCodes.XbrProfileMismatch);
        Assert.Equal(0, worker.HandshakeCalls);
        Assert.Equal(0, worker.RequestCalls);
    }

    private static MappingProject ProjectWithProfile(string profileId, string profileDigest)
    {
        var source = TestFixtures.BuildRevenueMappingProject("unused.xlsx", approved: false);
        return new MappingProject
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
            Approvals = source.Approvals
        };
    }

    private sealed class UnexpectedWorker : IWorkerClient
    {
        public int HandshakeCalls { get; private set; }
        public int RequestCalls { get; private set; }

        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
        {
            HandshakeCalls++;
            throw new InvalidOperationException("Worker must not be contacted for a mismatched profile.");
        }

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            throw new InvalidOperationException("Worker must not be contacted for a mismatched profile.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnexpectedConnections : IReadOnlyDictionary<string, SourceConnection>
    {
        private static InvalidOperationException ReadAttempt()
            => new("Source connections must not be enumerated for a mismatched profile.");

        public SourceConnection this[string key] => throw ReadAttempt();
        public IEnumerable<string> Keys => throw ReadAttempt();
        public IEnumerable<SourceConnection> Values => throw ReadAttempt();
        public int Count => throw ReadAttempt();
        public bool ContainsKey(string key) => throw ReadAttempt();
        public bool TryGetValue(string key, out SourceConnection value) => throw ReadAttempt();
        public IEnumerator<KeyValuePair<string, SourceConnection>> GetEnumerator() => throw ReadAttempt();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
