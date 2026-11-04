using System.Collections;
using System.Text.Json;
using System.Xml.Linq;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
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

    [Theory]
    [InlineData("csv", "csv-records-v1")]
    [InlineData("excel", "xlsx-records-v1")]
    [InlineData("json", "json-records-v1")]
    [InlineData("xml", "xml-records-v1")]
    public void ValidateProject_AcceptsExplicitVersionedFileContracts(string kind, string format)
    {
        var project = TestFixtures.BuildRevenueMappingProject("unused.xlsx");
        project.Destination = new DestinationPlan
        {
            Kind = kind,
            Locator = $"out/report.{kind}",
            Table = string.Empty,
            BusinessKey = new List<string>(),
            Format = format,
            WriteMode = "replace"
        };

        var outcome = MappingJobService.ValidateProject(project, Synthetic);

        Assert.True(outcome.Success, string.Join(Environment.NewLine, outcome.Diagnostics.Items.Select(item => item.Message)));
    }

    [Theory]
    [InlineData("csv", "xlsx-records-v1", "replace")]
    [InlineData("json", "json-records-v1", "upsert")]
    public void ValidateProject_RejectsMismatchedFileContract(string kind, string format, string writeMode)
    {
        var project = TestFixtures.BuildRevenueMappingProject("unused.xlsx");
        project.Destination = new DestinationPlan
        {
            Kind = kind,
            Locator = "out/report.dat",
            Table = string.Empty,
            BusinessKey = new List<string>(),
            Format = format,
            WriteMode = writeMode
        };

        var outcome = MappingJobService.ValidateProject(project, Synthetic);

        Assert.False(outcome.Success);
        Assert.Contains(outcome.Diagnostics.Items, diagnostic => diagnostic.IsBlocking
            && diagnostic.Code == DiagnosticCodes.IoUnsupportedOperation);
    }

    [Theory]
    [InlineData("mapping.xmap")]
    [InlineData("mapping.xmap.lock")]
    [InlineData("mapping.xmap.bak")]
    public void ValidateFileDestinationPath_ProtectsProjectArtifacts(string fileName)
    {
        var diagnostics = DestinationPlanValidator.ValidateFileDestinationPath(Path.GetFullPath(fileName));

        Assert.True(diagnostics.HasBlockingErrors);
        Assert.Contains(diagnostics.Items, diagnostic => diagnostic.Code == DiagnosticCodes.IoSchemaMismatch);
    }

    [Theory]
    [InlineData("csv", "csv-records-v1", "report.csv", "csv")]
    [InlineData("excel", "xlsx-records-v1", "report.xlsx", "xlsx")]
    [InlineData("json", "json-records-v1", "report.json", "json")]
    [InlineData("xml", "xml-records-v1", "report.xml", "xml")]
    public async Task PublishFileDestination_UsesExplicitAdapterAndRecordsReconciledOutput(
        string kind, string format, string fileName, string expectedOutputKind)
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-file-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            project.Destination = new DestinationPlan
            {
                Kind = kind,
                Locator = fileName,
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = format,
                WriteMode = "replace"
            };
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = Synthetic.Digest
            });

            var binding = project.Bindings.Single();
            var config = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, config)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var output = await service.PublishFileDestinationAsync(project, result, dir,
                allowSyntheticProfile: true, CancellationToken.None);

            Assert.Equal(expectedOutputKind, output.Kind);
            Assert.True(File.Exists(Path.Combine(dir, fileName)));
            Assert.Contains(result.Manifest.Outputs, item => item.Path == output.Path && item.Digest == output.Digest);
            if (kind == "json")
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output.Path));
                var fact = Assert.Single(document.RootElement.EnumerateArray());
                Assert.Equal("1250000", fact.GetProperty("value").GetRawText());
                Assert.Equal(JsonValueKind.Null, fact.GetProperty("instant_date").ValueKind);
            }
            else if (kind == "xml")
            {
                var document = System.Xml.Linq.XDocument.Load(output.Path);
                var fact = Assert.Single(document.Root!.Elements());
                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
                Assert.Equal("true", (string?)fact.Element("instant_date")?.Attribute(xsi + "nil"));
                Assert.Equal("1250000", fact.Element("value")?.Value);
            }
            else if (kind == "excel")
            {
                var reader = new ExcelAdapter();
                var connection = new SourceConnection("output", "excel", output.Path);
                var schema = await reader.InspectSchemaAsync(connection, CancellationToken.None);
                var rows = new List<DataRecord>();
                await foreach (var row in reader.ReadFullAsync(connection, schema, CancellationToken.None)) rows.Add(row);
                Assert.Single(rows);
                Assert.Equal("1250000", rows[0]["value"].AsString());
            }
            else
            {
                var reader = new CsvAdapter();
                var connection = new SourceConnection("output", "csv", output.Path);
                var schema = await reader.InspectSchemaAsync(connection, CancellationToken.None);
                var rows = new List<DataRecord>();
                await foreach (var row in reader.ReadFullAsync(connection, schema, CancellationToken.None)) rows.Add(row);
                Assert.Single(rows);
                Assert.Equal("1250000", rows[0]["value"].AsString());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishFileDestination_RefusesProjectArtifactWithoutChangingIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-file-destination-protected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destinationPath = Path.Combine(dir, "mapping.xmap");
        var originalBytes = System.Text.Encoding.UTF8.GetBytes("project bytes must be preserved");
        await File.WriteAllBytesAsync(destinationPath, originalBytes);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            project.Destination = new DestinationPlan
            {
                Kind = "json",
                Locator = Path.GetFileName(destinationPath),
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = "json-records-v1",
                WriteMode = "replace"
            };
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = Synthetic.Digest
            });

            var binding = project.Bindings.Single();
            var config = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, config)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            var error = await Assert.ThrowsAsync<AdapterException>(() => service.PublishFileDestinationAsync(
                project, result, dir, allowSyntheticProfile: true, CancellationToken.None));

            Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(destinationPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
    [InlineData("unknown", "IO008")]
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
        project.Destination = ValidPlan() with { Kind = "unknown" };
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
