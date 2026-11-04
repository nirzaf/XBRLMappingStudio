using System.Collections;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class DestinationPlanTests
{
    private static readonly FilingProfile Synthetic = SyntheticProfile.Build();

    [Fact]
    public void ValidateProject_AcceptsSupportedVersionedSqlitePlan()
    {
        var project = TestFixtures.BuildRevenueMappingProject("unused.xlsx");
        project.Destination = ValidPlan();

        var outcome = MappingJobService.ValidateProject(project, Synthetic);

        Assert.True(outcome.Success);
    }

    [Fact]
    public void ValidateSchemaRejectsNumericAffinityAndMissingFactColumns()
    {
        var columns = XbrlFactRecordMapper.OutputColumns.ToDictionary(
            column => column,
            column => column switch
            {
                "value" => "NUMERIC",
                "is_nil" or "decimals" => "INTEGER",
                _ => "TEXT"
            },
            StringComparer.Ordinal);
        columns.Remove("provenance_json");
        var schema = new SqliteTargetTableSchema("facts", columns,
            new IReadOnlyList<string>[] { new[] { "fact_key" } });

        var diagnostics = DestinationPlanValidator.ValidateSchema(ValidPlan(), schema);

        Assert.Contains(diagnostics.Items, diagnostic => diagnostic.Code == DiagnosticCodes.IoNumberOutOfBudget);
        Assert.Contains(diagnostics.Items, diagnostic => diagnostic.Code == DiagnosticCodes.IoColumnMissing);
    }

    [Theory]
    [InlineData("json", "IO008")]
    [InlineData("sqlite", "IO001")]
    public void ValidateProject_RejectsUnsupportedOrIncompleteDestination(string kind, string expectedCode)
    {
        var project = TestFixtures.BuildRevenueMappingProject("unused.xlsx");
        project.Destination = kind == "sqlite"
            ? ValidPlan() with { BusinessKey = new List<string>() }
            : ValidPlan() with { Kind = kind };

        var outcome = MappingJobService.ValidateProject(project, Synthetic);

        Assert.False(outcome.Success);
        Assert.Contains(outcome.Diagnostics.Items, diagnostic =>
            diagnostic.Code == expectedCode && diagnostic.IsBlocking);
    }

    [Fact]
    public async Task RunAsync_InvalidDestinationFailsBeforeInspectingSourceConnections()
    {
        var project = TestFixtures.BuildRevenueMappingProject("unused.xlsx");
        project.Destination = ValidPlan() with { Kind = "json" };
        var worker = new UnexpectedWorker();
        var service = new MappingJobService(Synthetic, worker);

        var result = await service.RunAsync(project, new UnexpectedConnections(),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.InvalidMapping, result.Manifest.Outcome);
        Assert.Empty(result.Manifest.Inputs);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
            diagnostic.Code == DiagnosticCodes.IoUnsupportedOperation);
        Assert.Equal(0, worker.HandshakeCalls);
        Assert.Equal(0, worker.RequestCalls);
    }

    private static DestinationPlan ValidPlan() => new()
    {
        Kind = DestinationPlanValidator.SupportedKind,
        Locator = "output/report.db",
        Table = "facts",
        BusinessKey = new List<string> { "fact_key" }
    };

    private sealed class UnexpectedWorker : IWorkerClient
    {
        public int HandshakeCalls { get; private set; }
        public int RequestCalls { get; private set; }

        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
        {
            HandshakeCalls++;
            throw new InvalidOperationException("Worker must not be contacted for an invalid destination.");
        }

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            throw new InvalidOperationException("Worker must not be contacted for an invalid destination.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnexpectedConnections : IReadOnlyDictionary<string, SourceConnection>
    {
        private static InvalidOperationException ReadAttempt()
            => new("Source connections must not be inspected for an invalid destination.");

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
