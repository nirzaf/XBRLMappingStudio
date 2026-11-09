using System.Text.Json;
using System.Xml.Linq;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class ReverseMappingDiagnosticTests
{
    [Fact]
    public async Task XbrlReportAdapter_DoesNotInventNumericAccuracyForTextFacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-aspects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
            var reportPath = Path.Combine(directory, "input.json");
            var facts = new[]
            {
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
                    EntityScheme = "http://xbrl.org/entity/",
                    EntityIdentifier = "ABC123",
                    Period = period,
                    TextValue = "Example reporting entity",
                    Language = "en"
                },
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("Revenue"),
                    EntityScheme = "http://xbrl.org/entity/",
                    EntityIdentifier = "ABC123",
                    Period = period,
                    Unit = new XbrlUnit("QAR"),
                    Value = 125m
                },
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("Assets"),
                    EntityScheme = "http://xbrl.org/entity/",
                    EntityIdentifier = "ABC123",
                    Period = XbrlPeriod.OfInstant(new DateOnly(2025, 12, 31)),
                    Unit = new XbrlUnit("QAR"),
                    Value = 900m
                }
            };
            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile, facts));

            var adapter = new XbrlReportAdapter();
            var connection = new SourceConnection("report", adapter.Kind, reportPath);
            var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
            var rows = new List<DataRecord>();
            await foreach (var row in adapter.ReadFullAsync(connection, schema, CancellationToken.None))
                rows.Add(row);

            var text = Assert.Single(rows, row => row.Fields["textValue"].Raw?.ToString() == "Example reporting entity");
            Assert.Equal(ValuePresence.Missing, text.Fields["decimals"].Presence);
            Assert.Equal(ValuePresence.Missing, text.Fields["unit"].Presence);
            Assert.Equal("en", text.Fields["lang"].Raw);
            Assert.Equal("duration", text.Fields["periodKind"].Raw);
            Assert.Equal(ValuePresence.Missing, text.Fields["instantDate"].Presence);
            Assert.Equal("2025-01-01", text.Fields["periodStart"].Raw);
            Assert.Equal("2026-01-01", text.Fields["periodEnd"].Raw);
            Assert.Equal(new SourceReference("report", text.RowId), text.CellSources!["periodKind"]);
            Assert.Equal(new SourceReference("report", text.RowId), text.CellSources["periodStart"]);

            var numeric = Assert.Single(rows, row => row.Fields["concept"].Raw?.ToString() == SyntheticProfile.Qualified("Revenue"));
            Assert.Equal("INF", numeric.Fields["decimals"].Raw);
            Assert.Equal("QAR", numeric.Fields["unit"].Raw);

            var instant = Assert.Single(rows, row => row.Fields["concept"].Raw?.ToString() == SyntheticProfile.Qualified("Assets"));
            Assert.Equal("instant", instant.Fields["periodKind"].Raw);
            Assert.Equal("2025-12-31", instant.Fields["instantDate"].Raw);
            Assert.Equal(ValuePresence.Missing, instant.Fields["periodStart"].Presence);
            Assert.Equal(ValuePresence.Missing, instant.Fields["periodEnd"].Presence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReportSource_RecordsNonBlockingReconstructionLimitation()
    {
        var diagnostics = new DiagnosticBag();

        MappingJobService.AddReverseMappingLossWarning(
            diagnostics,
            new SourceConnection("report-binding", "xbrl-report", "synthetic-report.json"),
            "operation-123");

        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal(DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.False(warning.IsBlocking);
        Assert.Equal("operation-123", warning.OperationId);
        Assert.Equal(new SourceReference("report-binding", "fact-occurrences"), warning.Source);
        Assert.Equal("source-detail-not-present-in-report", warning.Details!["limitation"]);
        Assert.Contains("cannot reconstruct source detail absent from the report", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonReportSource_DoesNotRecordReverseMappingLimitation()
    {
        var diagnostics = new DiagnosticBag();

        MappingJobService.AddReverseMappingLossWarning(
            diagnostics,
            new SourceConnection("workbook-binding", "excel", "synthetic.xlsx"),
            "operation-123");

        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public async Task RunAsync_PersistsReverseMappingLimitationWithSourceFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-warning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var reportPath = Path.Combine(directory, "input.json");
            await File.WriteAllTextAsync(reportPath, "not-json");
            var project = TestFixtures.BuildRevenueMappingProject(reportPath);
            var binding = project.Bindings.Single();
            project.Bindings[0] = binding with
            {
                Kind = "xbrl-report",
                Configuration = new Dictionary<string, object?>(),
                SchemaFingerprint = null
            };
            var fieldIndex = project.Nodes.FindIndex(node => node.NodeType == NodeTypeConstants.ExcelField);
            var field = project.Nodes[fieldIndex];
            var parameters = new Dictionary<string, object?>(field.Parameters, StringComparer.Ordinal)
            {
                ["column"] = "value"
            };
            parameters.Remove("rowFilterColumn");
            parameters.Remove("rowFilterValue");
            project.Nodes[fieldIndex] = field with { Parameters = parameters };

            var result = await new MappingJobService(SyntheticProfile.Build(), new SuccessfulWorker()).RunAsync(
                project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, "xbrl-report", reportPath)
                },
                new Dictionary<string, string>(),
                preview: false,
                CancellationToken.None);

            Assert.Equal(RunOutcome.DataError, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail
                && diagnostic.Severity == DiagnosticSeverity.Warning
                && !diagnostic.IsBlocking
                && diagnostic.Source?.SourceId == binding.BindingId);
            Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.IoUnsupportedEncoding && diagnostic.IsBlocking);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ReportsReverseMappingLimitationWithoutBlockingValidFacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-warning-success-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var reportPath = IndependentAcceptanceFixtures.Materialize(directory, "revenue-report.json");
            var project = TestFixtures.BuildRevenueMappingProject(reportPath);
            var binding = project.Bindings.Single();
            project.Bindings[0] = binding with
            {
                Kind = "xbrl-report",
                Configuration = new Dictionary<string, object?>(),
                SchemaFingerprint = null
            };
            var fieldIndex = project.Nodes.FindIndex(node => node.NodeType == NodeTypeConstants.ExcelField);
            var field = project.Nodes[fieldIndex];
            var parameters = new Dictionary<string, object?>(field.Parameters, StringComparer.Ordinal)
            {
                ["column"] = "value"
            };
            parameters.Remove("rowFilterColumn");
            parameters.Remove("rowFilterValue");
            project.Nodes[fieldIndex] = field with { Parameters = parameters };

            var result = await new MappingJobService(SyntheticProfile.Build(), new SuccessfulWorker()).RunAsync(
                project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, "xbrl-report", reportPath)
                },
                new Dictionary<string, string>(),
                preview: false,
                CancellationToken.None);

            Assert.Equal(RunOutcome.SuccessWithWarnings, result.Manifest.Outcome);
            var fact = Assert.Single(result.Facts);
            Assert.Equal(1_250_000_000m, fact.Value);
            var warning = Assert.Single(result.Manifest.Diagnostics,
                diagnostic => diagnostic.Code == DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail);
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.False(warning.IsBlocking);
            Assert.Equal(binding.BindingId, warning.Source?.SourceId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("json", "json-records-v1", "reverse-records.json")]
    [InlineData("xml", "xml-records-v1", "reverse-records.xml")]
    public async Task RunAsync_PublishesFilteredRepeatedReportOccurrencesWithSourceLocators(
        string kind, string format, string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
            var priorPeriod = XbrlPeriod.OfDuration(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1));
            XbrlFact SourceFact(decimal value, XbrlPeriod factPeriod, string unit) => new()
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "ABC123",
                Period = factPeriod,
                Unit = new XbrlUnit(unit),
                Value = value,
                Decimals = -3
            };
            var sourceFacts = new[]
            {
                SourceFact(100m, period, "QAR"),
                SourceFact(100m, period, "QAR"),
                SourceFact(300m, priorPeriod, "QAR"),
                SourceFact(500m, period, "USD")
            };
            var reportPath = Path.Combine(directory, "input.json");
            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile, sourceFacts));
            var expectedLocators = OimSerializer.ReadFacts(await File.ReadAllTextAsync(reportPath))
                .Select((fact, index) => (fact, index))
                .Where(item => item.fact.ConceptName == SyntheticProfile.Qualified("Revenue")
                    && item.fact.Period.ToIsoString() == period.ToIsoString()
                    && item.fact.Unit?.ToString() == "QAR")
                .Select(item => $"report#f{item.index + 1}")
                .ToArray();
            Assert.Equal(2, expectedLocators.Length);

            var project = TestFixtures.BuildRevenueMappingProject(reportPath);
            project.Destination = new DestinationPlan
            {
                Kind = kind,
                Locator = fileName,
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = format,
                WriteMode = "replace"
            };
            var binding = Assert.Single(project.Bindings);
            var configuration = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["concept"] = SyntheticProfile.Qualified("Revenue"),
                ["period"] = period.ToIsoString(),
                ["unit"] = "QAR"
            };
            project.Bindings[0] = binding with
            {
                Kind = "xbrl-report",
                Locator = Path.GetFileName(reportPath),
                Configuration = configuration,
                SchemaFingerprint = null
            };
            var fieldIndex = project.Nodes.FindIndex(node => node.NodeType == NodeTypeConstants.ExcelField);
            var field = project.Nodes[fieldIndex];
            var parameters = new Dictionary<string, object?>(field.Parameters, StringComparer.Ordinal)
            {
                ["column"] = "value"
            };
            parameters.Remove("rowFilterColumn");
            parameters.Remove("rowFilterValue");
            project.Nodes[fieldIndex] = field with { Parameters = parameters };
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "reverse-mapping-fixture-reviewer",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest
            });

            var service = new MappingJobService(profile, new SuccessfulWorker());
            var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, "xbrl-report", reportPath,
                        configuration.ToDictionary(pair => pair.Key, pair => pair.Value!.ToString()!, StringComparer.Ordinal))
                },
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Equal(RunOutcome.SuccessWithWarnings, result.Manifest.Outcome);
            Assert.True(result.IsPublishable, string.Join(Environment.NewLine, result.Diagnostics.Items.Select(item => item.Message)));
            Assert.Equal(2, result.Facts.Count);
            Assert.All(result.Facts, fact => Assert.Equal(100_000m, fact.Value));
            Assert.Equal(expectedLocators, result.Facts.Select(fact => Assert.Single(fact.ContributingSources).Locator));
            var warning = Assert.Single(result.Manifest.Diagnostics,
                diagnostic => diagnostic.Code == DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail);
            Assert.False(warning.IsBlocking);
            Assert.Equal("source-detail-not-present-in-report", warning.Details!["limitation"]);

            var output = await service.PublishFileDestinationAsync(project, result, directory,
                allowSyntheticProfile: true, CancellationToken.None);
            Assert.True(File.Exists(output.Path));
            Assert.Contains(result.Manifest.Outputs, item => item.Path == output.Path && item.Digest == output.Digest);
            IReadOnlyList<string> locators;
            if (kind == "json")
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output.Path));
                var records = document.RootElement.EnumerateArray().ToArray();
                Assert.Equal(2, records.Length);
                Assert.All(records, record =>
                {
                    Assert.Equal("synthetic:Revenue", record.GetProperty("concept").GetString());
                    Assert.Equal("ABC-Corp", record.GetProperty("entity_identifier").GetString());
                    Assert.Equal("2025-01-01", record.GetProperty("period_start").GetString());
                    Assert.Equal("2026-01-01", record.GetProperty("period_end").GetString());
                    Assert.Equal("QAR", record.GetProperty("unit").GetString());
                    Assert.Equal(100_000m, record.GetProperty("value").GetDecimal());
                });
                locators = records.SelectMany(ReadRecordLocators).ToArray();
            }
            else
            {
                var document = XDocument.Load(output.Path);
                var records = document.Root!.Elements().ToArray();
                Assert.Equal(2, records.Length);
                Assert.All(records, record =>
                {
                    Assert.Equal("synthetic:Revenue", record.Elements().Single(element => element.Name.LocalName == "concept").Value);
                    Assert.Equal("ABC-Corp", record.Elements().Single(element => element.Name.LocalName == "entity_identifier").Value);
                    Assert.Equal("2025-01-01", record.Elements().Single(element => element.Name.LocalName == "period_start").Value);
                    Assert.Equal("2026-01-01", record.Elements().Single(element => element.Name.LocalName == "period_end").Value);
                    Assert.Equal("QAR", record.Elements().Single(element => element.Name.LocalName == "unit").Value);
                    Assert.Equal("100000", record.Elements().Single(element => element.Name.LocalName == "value").Value);
                });
                locators = records.SelectMany(record => ReadJsonLocators(
                    record.Elements().Single(element => element.Name.LocalName == "provenance_json").Value)).ToArray();
            }
            Assert.Equal(expectedLocators.Order(StringComparer.Ordinal), locators.Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static IEnumerable<string> ReadRecordLocators(JsonElement record)
            => ReadJsonLocators(record.GetProperty("provenance_json").GetString()!);

        static IEnumerable<string> ReadJsonLocators(string json)
        {
            using var provenance = JsonDocument.Parse(json);
            return provenance.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("Locator").GetString()!)
                .ToArray();
        }
    }

    private sealed class SuccessfulWorker : IWorkerClient
    {
        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new WorkerHandshake(
                WorkerProtocol.CurrentVersion,
                "reverse-warning-test/1.0",
                new[] { WorkerOperations.LoadTaxonomy, WorkerOperations.ValidateReport }));

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            JsonElement? result = request.Operation == WorkerOperations.ValidateReport
                && request.ValidationRequirements is { } requirements
                ? JsonSerializer.SerializeToElement(new
                {
                    validationCompletion = new
                    {
                        profileId = requirements.ProfileId,
                        profileDigest = requirements.ProfileDigest,
                        requiredRules = requirements.RequiredRules.Select(identifier => new
                        {
                            identifier,
                            status = "pass"
                        }),
                        requiredPlugins = requirements.RequiredPlugins.Select(identifier => new
                        {
                            identifier,
                            status = "pass"
                        })
                    }
                })
                : null;
            return Task.FromResult(new WorkerResponse(
                request.OperationId,
                WorkerStatus.Success,
                Result: result,
                Diagnostics: Array.Empty<Diagnostic>(),
                Error: null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
