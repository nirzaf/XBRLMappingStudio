using System.Collections;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
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
    [InlineData("csv", "csv-records-v2")]
    [InlineData("excel", "xlsx-records-v1")]
    [InlineData("excel", "xlsx-records-v2")]
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
    [InlineData("csv", "csv-records-v2")]
    [InlineData("excel", "xlsx-records-v2")]
    [InlineData("json", "json-records-v1")]
    [InlineData("xml", "xml-records-v1")]
    public void NewFileDestinationsDefaultToTheCurrentContract(string kind, string format)
        => Assert.Equal(format, DestinationPlanValidator.FileFormatFor(kind));

    [Fact]
    public void V2PresenceMetadataDistinguishesMissingNilBlankAndPresentInStableColumnOrder()
    {
        var headers = new[] { "missing", "nil", "blank", "present" };
        var record = new DataRecord
        {
            RowId = "row-1",
            Fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
            {
                ["nil"] = DataValue.ExplicitNil(XBRLMappingStudio.Project.PortShape.String),
                ["blank"] = DataValue.Blank(),
                ["present"] = DataValue.StringValue("ready")
            }
        };

        var encoded = FileOutputPresenceMetadata.Serialize(record, headers);
        using var document = JsonDocument.Parse(encoded);
        var root = document.RootElement;
        Assert.Equal(FileOutputPresenceMetadata.SchemaId, root.GetProperty("schema").GetString());
        var fields = root.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(headers, fields.Select(field => field.GetProperty("name").GetString()));
        Assert.Equal(new[] { "missing", "nil", "blank", "present" },
            fields.Select(field => field.GetProperty("state").GetString()));

        var withMetadata = Assert.Single(FileOutputPresenceMetadata.Append(new[] { record }, headers));
        Assert.Equal("row-1", withMetadata.RowId);
        Assert.Equal(encoded, withMetadata[FileOutputPresenceMetadata.ColumnName].AsString());
        Assert.Equal("ready", withMetadata["present"].AsString());
        Assert.Equal(headers.Append(FileOutputPresenceMetadata.ColumnName),
            FileOutputPresenceMetadata.AppendHeader(headers));
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
    [InlineData("csv", "csv-records-v2", "report-v2.csv", "csv")]
    [InlineData("excel", "xlsx-records-v1", "report.xlsx", "xlsx")]
    [InlineData("excel", "xlsx-records-v2", "report-v2.xlsx", "xlsx")]
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
                if (DestinationPlanValidator.UsesFieldPresenceMetadata(project.Destination!))
                    AssertFactPresenceMetadata(rows[0][FileOutputPresenceMetadata.ColumnName].AsString());
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
                if (DestinationPlanValidator.UsesFieldPresenceMetadata(project.Destination!))
                    AssertFactPresenceMetadata(rows[0][FileOutputPresenceMetadata.ColumnName].AsString());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishFileDestination_RejectsSourcePathWithoutChangingOriginalFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-file-destination-source-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var originalBytes = await File.ReadAllBytesAsync(workbook);
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            project.Destination = new DestinationPlan
            {
                Kind = "excel",
                Locator = Path.GetFileName(workbook),
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = "xlsx-records-v1",
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
            var configuration = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            using var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var error = await Assert.ThrowsAsync<AdapterException>(() => service.PublishFileDestinationAsync(
                project, result, dir, allowSyntheticProfile: true, CancellationToken.None));

            Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
            Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(workbook));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReportPublication_RejectsSourcePathWithoutChangingOriginalFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-report-source-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var originalBytes = await File.ReadAllBytesAsync(workbook);
            var project = TestFixtures.BuildRevenueMappingProject(workbook, approved: true);
            var binding = project.Bindings.Single();
            var configuration = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            using var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var reportBytes = Encoding.UTF8.GetBytes(result.XbrlJson);
            var publisher = new PublicationService(new RunJournal(databasePath: Path.Combine(dir, "journal.db")));
            var error = await Assert.ThrowsAsync<PublicationException>(() => publisher.PublishAuthorizedAsync(
                workbook,
                reportBytes,
                dir,
                project,
                Synthetic,
                allowSyntheticProfile: true,
                result,
                async (stagedPath, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await using var stream = File.OpenRead(stagedPath);
                    return RunManifest.ComputeSha256(stream);
                }));

            Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
            Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(workbook));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReportPublication_RejectsProjectFileWithoutChangingProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-report-project-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var project = TestFixtures.BuildRevenueMappingProject(workbook, approved: true);
            var projectPath = Path.Combine(dir, "mapping.xmap");
            new ProjectManager(projectPath).Save(project);
            var originalProjectBytes = await File.ReadAllBytesAsync(projectPath);
            var binding = project.Bindings.Single();
            var configuration = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            using var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var publisher = new PublicationService(new RunJournal(databasePath: Path.Combine(dir, "journal.db")));
            var reportBytes = Encoding.UTF8.GetBytes(result.XbrlJson);
            var error = await Assert.ThrowsAsync<PublicationException>(() => publisher.PublishAuthorizedAsync(
                projectPath,
                reportBytes,
                dir,
                project,
                Synthetic,
                allowSyntheticProfile: true,
                result,
                async (stagedPath, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await using var stream = File.OpenRead(stagedPath);
                    return RunManifest.ComputeSha256(stream);
                }));

            Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
            Assert.Equal(originalProjectBytes, await File.ReadAllBytesAsync(projectPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishFileDestination_RejectsEveryNonSuccessOutcomeBeforeAnyFileMutation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-file-outcome-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(directory);
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            var binding = project.Bindings.Single();
            var configuration = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var fileContracts = new[]
            {
                (Kind: "csv", Format: "csv-records-v2", Extension: "csv"),
                (Kind: "excel", Format: "xlsx-records-v2", Extension: "xlsx"),
                (Kind: "json", Format: "json-records-v1", Extension: "json"),
                (Kind: "xml", Format: "xml-records-v1", Extension: "xml")
            };
            var rejectedOutcomes = Enum.GetValues<RunOutcome>()
                .Where(outcome => outcome is not (RunOutcome.Success or RunOutcome.SuccessWithWarnings))
                .ToArray();

            foreach (var outcome in rejectedOutcomes)
            foreach (var contract in fileContracts)
            {
                result.Manifest.Outcome = outcome;
                var fileName = $"{outcome.ToString().ToLowerInvariant()}-{contract.Kind}.{contract.Extension}";
                project.Destination = new DestinationPlan
                {
                    Kind = contract.Kind,
                    Locator = fileName,
                    Table = string.Empty,
                    BusinessKey = new List<string>(),
                    Format = contract.Format,
                    WriteMode = "replace"
                };

                var destinationPath = Path.Combine(directory, fileName);
                var acceptedBytes = Encoding.UTF8.GetBytes($"accepted-{outcome}-{contract.Kind}");
                await File.WriteAllBytesAsync(destinationPath, acceptedBytes);

                var error = await Assert.ThrowsAsync<AdapterException>(() => service.PublishFileDestinationAsync(
                    project, result, directory, allowSyntheticProfile: true, CancellationToken.None));

                Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
                Assert.Contains("Only a successful full run", error.Message, StringComparison.Ordinal);
                Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(destinationPath));
                Assert.DoesNotContain(result.Manifest.Outputs, output => output.Path == destinationPath);
                Assert.False(File.Exists(destinationPath + ".staged"));
            }

            result.Manifest.Outcome = RunOutcome.Success;
            result.Manifest.IsPreview = true;
            project.Destination = new DestinationPlan
            {
                Kind = "csv",
                Locator = "preview-report.csv",
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = "csv-records-v2",
                WriteMode = "replace"
            };
            var previewPath = Path.Combine(directory, project.Destination.Locator);
            var previewPriorBytes = Encoding.UTF8.GetBytes("accepted-before-preview");
            await File.WriteAllBytesAsync(previewPath, previewPriorBytes);

            var previewError = await Assert.ThrowsAsync<AdapterException>(() => service.PublishFileDestinationAsync(
                project, result, directory, allowSyntheticProfile: true, CancellationToken.None));

            Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, previewError.Code);
            Assert.Contains("Only a successful full run", previewError.Message, StringComparison.Ordinal);
            Assert.Equal(previewPriorBytes, await File.ReadAllBytesAsync(previewPath));
            Assert.DoesNotContain(result.Manifest.Outputs, output => output.Path == previewPath);
            Assert.False(File.Exists(previewPath + ".staged"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("csv", "csv-records-v1", "tampered-report.csv")]
    [InlineData("excel", "xlsx-records-v1", "tampered-report.xlsx")]
    [InlineData("json", "json-records-v1", "tampered-report.json")]
    [InlineData("xml", "xml-records-v1", "tampered-report.xml")]
    public async Task PublishFileDestination_RejectsTamperedRunManifestDigestWithoutChangingDestination(
        string kind, string format, string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-file-manifest-tamper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(directory);
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
            var configuration = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var originalOutputs = result.Manifest.Outputs;
            var reportArtifact = Assert.Single(originalOutputs, output => output.Kind == "xbrl-json");
            var tamperedArtifact = reportArtifact with { Digest = "sha256:" + new string('0', 64) };
            result.Manifest.Outputs = originalOutputs
                .Select(output => output == reportArtifact
                    ? tamperedArtifact
                    : output)
                .ToArray();

            var destinationPath = Path.Combine(directory, fileName);
            var acceptedBytes = Encoding.UTF8.GetBytes("previous accepted destination bytes");
            await File.WriteAllBytesAsync(destinationPath, acceptedBytes);

            var error = await Assert.ThrowsAsync<PublicationException>(() =>
                service.PublishFileDestinationAsync(project, result, directory,
                    allowSyntheticProfile: true, CancellationToken.None));

            Assert.Equal(DiagnosticCodes.PubDigestMismatch, error.Code);
            Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.DoesNotContain(result.Manifest.Outputs, output => output.Path == destinationPath);
            Assert.False(File.Exists(destinationPath + ".staged"));

            // A matching report entry cannot legitimize a contradictory duplicate.
            result.Manifest.Outputs = originalOutputs.Append(tamperedArtifact).ToArray();
            var duplicateError = await Assert.ThrowsAsync<PublicationException>(() =>
                service.PublishFileDestinationAsync(project, result, directory,
                    allowSyntheticProfile: true, CancellationToken.None));

            Assert.Equal(DiagnosticCodes.PubDigestMismatch, duplicateError.Code);
            Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.DoesNotContain(result.Manifest.Outputs, output => output.Path == destinationPath);
            Assert.False(File.Exists(destinationPath + ".staged"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("csv", "csv-records-v1", "cancelled-report.csv")]
    [InlineData("excel", "xlsx-records-v1", "cancelled-report.xlsx")]
    [InlineData("json", "json-records-v1", "cancelled-report.json")]
    [InlineData("xml", "xml-records-v1", "cancelled-report.xml")]
    public async Task PublishFileDestination_CancellationAfterStagingPreservesPreviousOutput(
        string kind, string format, string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-file-cancelled-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(directory);
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
            var configuration = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            using var cancellation = new CancellationTokenSource();
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic),
                null, null, null, null, () => cancellation.Cancel());
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var destinationPath = Path.Combine(directory, fileName);
            var acceptedBytes = Encoding.UTF8.GetBytes("previous accepted destination bytes");
            await File.WriteAllBytesAsync(destinationPath, acceptedBytes);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.PublishFileDestinationAsync(project, result, directory,
                    allowSyntheticProfile: true, cancellation.Token));

            Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.DoesNotContain(result.Manifest.Outputs, output => output.Path == destinationPath);
            Assert.False(File.Exists(destinationPath + ".staged"));
            Assert.False(File.Exists(destinationPath + ".xms-rollback-" + result.Manifest.RunId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("csv", "csv-records-v1", "nil-facts.csv")]
    [InlineData("excel", "xlsx-records-v1", "nil-facts.xlsx")]
    [InlineData("json", "json-records-v1", "nil-facts.json")]
    [InlineData("xml", "xml-records-v1", "nil-facts.xml")]
    public async Task PublishFileDestination_PreservesNilFactSemanticsAcrossFormats(
        string kind, string format, string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-file-nil-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "nil-input.json");
            await File.WriteAllTextAsync(inputPath, "[{\"Line\":\"Revenue\",\"Current\":null}]");
            var project = TestFixtures.BuildRevenueMappingProject(inputPath);
            var binding = project.Bindings.Single();
            project.Bindings[0] = binding with
            {
                Kind = "json",
                DisplayName = "Nil facts",
                Configuration = new Dictionary<string, object?>()
            };
            var factNodeIndex = project.Nodes.FindIndex(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var factNode = project.Nodes[factNodeIndex];
            project.Nodes[factNodeIndex] = factNode with
            {
                Parameters = new Dictionary<string, object?>(factNode.Parameters, StringComparer.Ordinal)
                {
                    ["decimals"] = "INF"
                }
            };
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

            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, "json", inputPath)
                },
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable,
                string.Join(Environment.NewLine, result.Diagnostics.Items.Select(item => item.Message)));
            var nilFact = Assert.Single(result.Facts);
            Assert.True(nilFact.IsNil);
            Assert.Null(nilFact.Value);

            var output = await service.PublishFileDestinationAsync(project, result, directory,
                allowSyntheticProfile: true, CancellationToken.None);
            Assert.True(File.Exists(output.Path));

            if (kind == "json")
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output.Path));
                var record = Assert.Single(document.RootElement.EnumerateArray());
                Assert.Equal(JsonValueKind.Null, record.GetProperty("value").ValueKind);
                Assert.True(record.GetProperty("is_nil").GetBoolean());
                Assert.Equal("duration", record.GetProperty("period_kind").GetString());
            }
            else if (kind == "xml")
            {
                var document = XDocument.Load(output.Path);
                var record = Assert.Single(document.Root!.Elements()).Elements().ToArray();
                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
                var value = Assert.Single(record, element => element.Name.LocalName == "value");
                Assert.Equal("true", (string?)value.Attribute(xsi + "nil"));
                Assert.Equal("true", record.Single(element => element.Name.LocalName == "is_nil").Value);
                Assert.Equal("duration", record.Single(element => element.Name.LocalName == "period_kind").Value);
            }
            else
            {
                var connection = new SourceConnection("output", kind, output.Path);
                ISourceAdapter reader = kind == "csv" ? new CsvAdapter() : new ExcelAdapter();
                var schema = await reader.InspectSchemaAsync(connection, CancellationToken.None);
                var rows = new List<DataRecord>();
                await foreach (var row in reader.ReadFullAsync(connection, schema, CancellationToken.None)) rows.Add(row);
                var record = Assert.Single(rows);
                Assert.True(record["value"].IsMissing);
                Assert.True(record["is_nil"].AsBoolean());
                Assert.Equal("duration", record["period_kind"].AsString());
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("csv", "csv-records-v1", "report.csv")]
    [InlineData("excel", "xlsx-records-v1", "report.xlsx")]
    [InlineData("json", "json-records-v1", "report.json")]
    [InlineData("xml", "xml-records-v1", "report.xml")]
    public async Task PublishFileDestination_RestoresPriorBytesWhenAdapterFailsAfterReplacement(
        string kind, string format, string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-file-destination-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var destinationPath = Path.Combine(dir, fileName);
            var priorBytes = Encoding.UTF8.GetBytes("previous accepted artifact\r\n");
            await File.WriteAllBytesAsync(destinationPath, priorBytes);

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
            var configuration = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic),
                null, null, null, () => throw new IOException("Injected failure after replacement."));
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
                }, new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(result.IsPublishable);
            var error = await Assert.ThrowsAsync<IOException>(() => service.PublishFileDestinationAsync(
                project, result, dir, allowSyntheticProfile: true, CancellationToken.None));

            Assert.Equal("Injected failure after replacement.", error.Message);
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.DoesNotContain(result.Manifest.Outputs, output => output.Path == destinationPath);
            Assert.False(File.Exists(destinationPath + $".xms-rollback-{result.Manifest.RunId}"));
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
    public async Task PublishFileDestination_RejectsSourceChangedAfterRunWithoutChangingDestination()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-file-destination-stale-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var destinationPath = Path.Combine(dir, "report.json");
            var originalDestination = Encoding.UTF8.GetBytes("previous report");
            await File.WriteAllBytesAsync(destinationPath, originalDestination);
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
            Assert.True(result.IsPublishable);

            var changedWorkbook = (await File.ReadAllBytesAsync(workbook)).Concat(new byte[] { 0x58 }).ToArray();
            await File.WriteAllBytesAsync(workbook, changedWorkbook);

            var error = await Assert.ThrowsAsync<PublicationException>(() => service.PublishFileDestinationAsync(
                project, result, dir, allowSyntheticProfile: true, CancellationToken.None));

            Assert.Equal(DiagnosticCodes.PubDigestMismatch, error.Code);
            Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
            Assert.Equal(originalDestination, await File.ReadAllBytesAsync(destinationPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RejectsFileChangedAfterSourceMaterialization(bool preview)
    {
        var dir = Path.Combine(Path.GetTempPath(), "xms-run-stale-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(dir);
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            var binding = project.Bindings.Single();
            var config = binding.Configuration?.ToDictionary(
                pair => pair.Key, pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var sourceChanged = false;
            var progress = new CallbackProgress<MappingJobProgress>(update =>
            {
                if (!sourceChanged && update.Stage.StartsWith("Read source", StringComparison.Ordinal))
                {
                    var changedWorkbook = File.ReadAllBytes(workbook).Concat(new byte[] { 0x58 }).ToArray();
                    File.WriteAllBytes(workbook, changedWorkbook);
                    sourceChanged = true;
                }
            });
            var service = new MappingJobService(Synthetic, new LocalSyntheticWorker(Synthetic));

            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, config)
                }, new Dictionary<string, string>(), preview, CancellationToken.None, progress);

            Assert.True(sourceChanged);
            Assert.Equal(preview, result.Manifest.IsPreview);
            Assert.Equal(RunOutcome.DataError, result.Manifest.Outcome);
            var diagnostic = Assert.Single(result.Manifest.Diagnostics,
                item => item.Code == DiagnosticCodes.IoInputIdentityChanged && item.IsBlocking);
            Assert.Equal(binding.BindingId, diagnostic.Source?.SourceId);
            Assert.Empty(result.Facts);
            Assert.Empty(result.NodeOutputPreviews);
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

    private static void AssertFactPresenceMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(FileOutputPresenceMetadata.SchemaId,
            document.RootElement.GetProperty("schema").GetString());
        var states = document.RootElement.GetProperty("fields").EnumerateArray()
            .ToDictionary(
                field => field.GetProperty("name").GetString()!,
                field => field.GetProperty("state").GetString()!,
                StringComparer.Ordinal);
        Assert.Equal("nil", states["instant_date"]);
        Assert.Equal("present", states["period_start"]);
        Assert.Equal("present", states["value"]);
        Assert.Equal("nil", states["text_value"]);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

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
