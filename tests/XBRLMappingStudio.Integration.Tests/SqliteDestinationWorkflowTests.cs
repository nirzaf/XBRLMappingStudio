using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class SqliteDestinationWorkflowTests : IAsyncLifetime
{
    private string _directory = "";
    private string _workbookPath = "";
    private string _databasePath = "";
    private readonly FilingProfile _profile = SyntheticProfile.Build();

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "xms-sqlite-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _workbookPath = IndependentAcceptanceFixtures.Materialize(_directory, "revenue.xlsx");
        _databasePath = Path.Combine(_directory, "destination.db");
        await CreateDestinationDatabase(_databasePath);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_directory, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExcelToSqlite_PreservesExactDecimalAndReplaysDurableReceipt()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);
        var receipt = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);
        var replay = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);

        Assert.Equal(1, receipt.WrittenRows);
        Assert.Equal(receipt.OperationKey, replay.OperationKey);
        Assert.Equal(receipt.IntentFingerprint, replay.IntentFingerprint);
        Assert.Equal(1, replay.WrittenRows);

        await using var database = new SqliteConnection($"Data Source={_databasePath};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT value,decimals,provenance_json FROM facts";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(result.Facts.Single().Value!.Value.ToString(CultureInfo.InvariantCulture), reader.GetString(0));
        Assert.Equal(-3, reader.GetInt32(1));
        var sourceLocator = Assert.Single(result.Facts).ContributingSources.Single().Locator;
        Assert.Contains(sourceLocator, reader.GetString(2), StringComparison.Ordinal);
        Assert.False(await reader.ReadAsync());

        var receiptOutput = Assert.Single(result.Manifest.Outputs, output => output.Kind == "db-receipt");
        Assert.Equal(1, receiptOutput.RowsWritten);
        Assert.Equal("facts", receiptOutput.Table);
        Assert.Equal(receipt.OperationKey, receiptOutput.OperationKey);
    }

    [Fact]
    public async Task XbrlReportToSqlite_PreservesFactValuePeriodAndReportProvenance()
    {
        var reportPath = IndependentAcceptanceFixtures.Materialize(_directory, "revenue-report.json");
        var source = new SourceConnection("report", "xbrl-report", reportPath);
        var adapter = new XbrlReportAdapter();
        var schema = await adapter.InspectSchemaAsync(source, CancellationToken.None);
        var rows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(source, schema, CancellationToken.None))
            rows.Add(row);
        var reportRow = Assert.Single(rows);
        Assert.Equal("1250000.0000", reportRow["value"].LexicalSource);

        var facts = OimSerializer.ReadFacts(await File.ReadAllTextAsync(reportPath));
        var fact = Assert.Single(facts);
        facts = new[]
        {
            new XbrlFact
            {
                ConceptName = fact.ConceptName,
                EntityScheme = fact.EntityScheme,
                EntityIdentifier = fact.EntityIdentifier,
                Period = fact.Period,
                Unit = fact.Unit,
                CompoundUnit = fact.CompoundUnit,
                Value = fact.Value,
                TextValue = fact.TextValue,
                IsNil = fact.IsNil,
                Decimals = fact.Decimals,
                Language = fact.Language,
                Dimensions = fact.Dimensions,
                ContributingSources = new[] { new SourceReference("report", "report#f1") }
            }
        };

        var project = ConfiguredProject(_workbookPath);
        var json = OimSerializer.Serialize(_profile, facts);
        var result = PublishableResult(project, facts, json,
            new InputIdentity("report", "xbrl-report", RunManifest.ComputeSha256(await File.ReadAllBytesAsync(reportPath)),
                IsReplayable: true, OriginalPath: reportPath, RowIdentityStrategy: "report-fact-index"));
        var service = NewService();

        var receipt = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);

        Assert.Equal(1, receipt.WrittenRows);
        await using var database = new SqliteConnection($"Data Source={_databasePath};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT value,period_start,period_end,provenance_json FROM facts";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("1250000.0000", reader.GetString(0));
        Assert.Equal("2025-01-01", reader.GetString(1));
        Assert.Equal("2026-01-01", reader.GetString(2));
        Assert.Contains("report#f1", reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewPublicationIsRejectedWithoutChangingDatabaseBytes()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: true, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(_databasePath);

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            service.PublishDestinationAsync(project, result, _directory, allowSyntheticProfile: true));

        var after = await File.ReadAllBytesAsync(_databasePath);
        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
        Assert.Equal(before, after);
    }

    private MappingProject ConfiguredProject(string workbookPath)
    {
        var project = TestFixtures.BuildRevenueMappingProject(workbookPath);
        project.Destination = new DestinationPlan
        {
            Kind = DestinationPlanValidator.SupportedKind,
            Locator = "destination.db",
            Table = "facts",
            BusinessKey = new List<string> { "fact_key" }
        };
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = _profile.Digest
        });
        return project;
    }

    private MappingJobService NewService() => new(_profile, new LocalSyntheticWorker(_profile));

    private static IReadOnlyDictionary<string, SourceConnection> Connections(MappingProject project, string workbookPath)
    {
        var binding = Assert.Single(project.Bindings);
        var configuration = binding.Configuration.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.ToString() ?? "",
            StringComparer.Ordinal);
        return new Dictionary<string, SourceConnection>(StringComparer.Ordinal)
        {
            [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbookPath, configuration)
        };
    }

    private JobResult PublishableResult(
        MappingProject project,
        IReadOnlyList<XbrlFact> facts,
        string json,
        InputIdentity input)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var manifest = new RunManifest
        {
            RunId = Guid.NewGuid().ToString("N"),
            OperationId = Guid.NewGuid().ToString("N"),
            ProjectId = project.ProjectId,
            SemanticRevision = project.SemanticRevision,
            ProjectSemanticDigest = project.ComputeSemanticDigest(),
            ProfileDigest = _profile.Digest,
            EngineVersion = "fixture",
            StartedUtc = DateTimeOffset.UtcNow,
            Inputs = new[] { input },
            Outputs = new[]
            {
                new OutputIdentity("<memory>", RunManifest.ComputeSha256(bytes), bytes.Length, "xbrl-json")
            }
        };
        manifest.Outcome = RunOutcome.Success;
        return new JobResult(manifest, facts, json, new DiagnosticBag(), Array.Empty<XBRLMappingStudio.Engine.SampledTotal>());
    }

    private static async Task CreateDestinationDatabase(string path)
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
}
