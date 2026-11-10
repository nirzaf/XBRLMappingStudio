using System.Security.Cryptography;
using System.Text;
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
    public async Task XbrlReportAdapter_FiltersByLanguageAndNilState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-language-nil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var reportPath = Path.Combine(directory, "language-nil.json");
            var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
            var facts = new[]
            {
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
                    EntityScheme = "https://example.test/entity", EntityIdentifier = "ACME-1",
                    Period = period, TextValue = "Example entity", Language = "en"
                },
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
                    EntityScheme = "https://example.test/entity", EntityIdentifier = "ACME-1",
                    Period = period, TextValue = "Entité exemple", Language = "fr"
                },
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("Revenue"),
                    EntityScheme = "https://example.test/entity", EntityIdentifier = "ACME-1",
                    Period = period, Unit = new XbrlUnit("QAR"), IsNil = true
                },
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("Revenue"),
                    EntityScheme = "https://example.test/entity", EntityIdentifier = "ACME-1",
                    Period = period, Unit = new XbrlUnit("QAR"), Value = 0m
                }
            };
            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile, facts));

            var adapter = new XbrlReportAdapter();
            async Task<IReadOnlyList<DataRecord>> ReadRows(Dictionary<string, string> filters)
            {
                var connection = new SourceConnection("report", adapter.Kind, reportPath, filters);
                var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
                var rows = new List<DataRecord>();
                await foreach (var row in adapter.ReadFullAsync(connection, schema, CancellationToken.None))
                    rows.Add(row);
                return rows;
            }

            var englishRows = await ReadRows(new(StringComparer.Ordinal) { ["lang"] = "en" });
            var english = Assert.Single(englishRows);
            Assert.Equal("report#f1", english.RowId);
            Assert.Equal("en", english["lang"].AsString());

            var nilRows = await ReadRows(new(StringComparer.Ordinal) { ["nil"] = "true" });
            var nil = Assert.Single(nilRows);
            Assert.Equal(SyntheticProfile.Qualified("Revenue"), nil["concept"].AsString());
            Assert.True(nil["nil"].AsBoolean());

            var nonNilRows = await ReadRows(new(StringComparer.Ordinal) { ["nil"] = "false" });
            Assert.Equal(3, nonNilRows.Count);
            Assert.All(nonNilRows, row => Assert.False(row["nil"].AsBoolean()));

            var invalid = new SourceConnection("report", adapter.Kind, reportPath,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["nil"] = "yes" });
            var error = await Assert.ThrowsAsync<AdapterException>(async () =>
                await adapter.InspectSchemaAsync(invalid, CancellationToken.None));
            Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task XbrlReportAdapter_FiltersByStructuredPeriodPartsAndPreservesRepeatedOccurrences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-period-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var reportPath = Path.Combine(directory, "period-filter.xml");
            var facts = new[]
            {
                ReverseRevenue(10m, XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1))),
                ReverseRevenue(10m, XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1))),
                ReverseRevenue(20m, XbrlPeriod.OfDuration(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1))),
                new XbrlFact
                {
                    ConceptName = SyntheticProfile.Qualified("Assets"),
                    EntityScheme = "https://example.test/entity",
                    EntityIdentifier = "ACME-1",
                    Period = XbrlPeriod.OfInstant(new DateOnly(2025, 12, 31)),
                    Unit = new XbrlUnit("QAR"),
                    Value = 30m
                },
                ReverseRevenue(40m, XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
                    "https://example.test/other-entity", "ACME-2")
            };
            await File.WriteAllTextAsync(reportPath, XBRLMappingStudio.XBRL.XmlSerializer.Serialize(profile, facts));

            var adapter = new XbrlReportAdapter();
            var durationSelection = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["concept"] = SyntheticProfile.Qualified("Revenue"),
                ["entityScheme"] = "https://example.test/entity",
                ["entityIdentifier"] = "ACME-1",
                ["periodKind"] = "duration",
                ["periodStart"] = "2025-01-01",
                ["periodEnd"] = "2026-01-01"
            };
            var durationConnection = new SourceConnection("report", adapter.Kind, reportPath, durationSelection);
            var durationSchema = await adapter.InspectSchemaAsync(durationConnection, CancellationToken.None);
            var durationRows = new List<DataRecord>();
            await foreach (var row in adapter.ReadFullAsync(durationConnection, durationSchema, CancellationToken.None))
                durationRows.Add(row);

            Assert.Equal(new[] { "report#f1", "report#f2" }, durationRows.Select(row => row.RowId));
            Assert.Equal(new[] { "10", "10" }, durationRows.Select(row => row["value"].LexicalSource));
            Assert.All(durationRows, row =>
            {
                Assert.Equal("duration", row["periodKind"].AsString());
                Assert.Equal("2025-01-01", row["periodStart"].AsString());
                Assert.Equal("2026-01-01", row["periodEnd"].AsString());
                Assert.Equal(new SourceReference("report", row.RowId), row.CellSources!["periodStart"]);
            });

            foreach (var (filterKey, filterValue) in new[]
            {
                ("entityScheme", "https://example.test/entity"),
                ("entityIdentifier", "ACME-1")
            })
            {
                var entityConnection = new SourceConnection("report", adapter.Kind, reportPath,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [filterKey] = filterValue });
                var entitySchema = await adapter.InspectSchemaAsync(entityConnection, CancellationToken.None);
                var entityRows = new List<DataRecord>();
                await foreach (var row in adapter.ReadFullAsync(entityConnection, entitySchema, CancellationToken.None))
                    entityRows.Add(row);

                Assert.Equal(new[] { "report#f1", "report#f2", "report#f3", "report#f4" },
                    entityRows.Select(row => row.RowId));
            }

            var instantConnection = new SourceConnection("report", adapter.Kind, reportPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["periodKind"] = "instant",
                    ["instantDate"] = "2025-12-31"
                });
            var instantSchema = await adapter.InspectSchemaAsync(instantConnection, CancellationToken.None);
            var instantRows = new List<DataRecord>();
            await foreach (var row in adapter.ReadFullAsync(instantConnection, instantSchema, CancellationToken.None))
                instantRows.Add(row);

            var instant = Assert.Single(instantRows);
            Assert.Equal("report#f4", instant.RowId);
            Assert.Equal("instant", instant["periodKind"].AsString());
            Assert.Equal("2025-12-31", instant["instantDate"].AsString());
            Assert.Equal(ValuePresence.Missing, instant["periodStart"].Presence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static XbrlFact ReverseRevenue(decimal value, XbrlPeriod period,
            string entityScheme = "https://example.test/entity", string entityIdentifier = "ACME-1") => new()
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = entityScheme,
            EntityIdentifier = entityIdentifier,
            Period = period,
            Unit = new XbrlUnit("QAR"),
            Value = value
        };
    }

    [Theory]
    [InlineData("periodKind", "fiscal-year")]
    [InlineData("instantDate", "31/12/2025")]
    [InlineData("periodStart", "2026-01-01")]
    public async Task XbrlReportAdapter_RejectsInvalidStructuredPeriodFilters(string key, string value)
    {
        var adapter = new XbrlReportAdapter();
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
        if (key == "periodStart") configuration["periodEnd"] = "2026-01-01";
        var connection = new SourceConnection("report", adapter.Kind, "unused.json", configuration);

        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
            await adapter.InspectSchemaAsync(connection, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
    }

    [Fact]
    public async Task XbrlReportAdapter_RejectsContradictoryStructuredPeriodKinds()
    {
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, "unused.json",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["periodKind"] = "duration",
                ["instantDate"] = "2025-12-31"
            });

        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
            await adapter.InspectSchemaAsync(connection, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
    }

    [Fact]
    public async Task XbrlReportAdapter_BindsOccurrenceSelectionToExactReportBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-report-digest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var path = Path.Combine(directory, "report.json");
            var firstPeriod = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
            var secondPeriod = XbrlPeriod.OfDuration(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1));
            XbrlFact Revenue(decimal value, XbrlPeriod period) => new()
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "https://example.test/entity",
                EntityIdentifier = "ACME-1",
                Period = period,
                Unit = new XbrlUnit("QAR"),
                Value = value
            };

            await File.WriteAllTextAsync(path,
                OimSerializer.Serialize(profile, new[] { Revenue(12m, firstPeriod), Revenue(99m, secondPeriod) }));
            var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))
                .ToLowerInvariant();
            var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["occurrenceId"] = "report#f1",
                ["reportDigest"] = digest
            };
            var connection = new SourceConnection("report", "xbrl-report", path, configuration);
            var adapter = new XbrlReportAdapter();
            var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
            var rows = new List<DataRecord>();
            await foreach (var row in adapter.ReadFullAsync(connection, schema, profile, CancellationToken.None))
                rows.Add(row);

            var selected = Assert.Single(rows);
            Assert.Equal("99", selected["value"].LexicalSource);
            Assert.DoesNotContain(schema.Columns, column => column.Name == "reportDigest");

            // An unchanged ordinal in different report bytes must not select a new fact.
            await File.WriteAllTextAsync(path,
                OimSerializer.Serialize(profile, new[] { Revenue(777m, secondPeriod), Revenue(12m, firstPeriod) }));
            var error = await Assert.ThrowsAsync<AdapterException>(async () =>
            {
                await foreach (var _ in adapter.ReadFullAsync(connection, schema, profile, CancellationToken.None)) { }
            });
            Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
            Assert.Contains("digest does not match the current report bytes", error.Message, StringComparison.Ordinal);

            // A report digest is an assertion about the source bytes even when the
            // caller did not also pin an ordinal occurrence selector.
            var digestOnlyConnection = new SourceConnection("report", "xbrl-report", path,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reportDigest"] = digest
                });
            var digestOnlySchema = await adapter.InspectSchemaAsync(digestOnlyConnection, CancellationToken.None);
            var digestOnlyError = await Assert.ThrowsAsync<AdapterException>(async () =>
            {
                await foreach (var _ in adapter.ReadFullAsync(digestOnlyConnection, digestOnlySchema, profile,
                    CancellationToken.None)) { }
            });
            Assert.Equal(DiagnosticCodes.IoSchemaMismatch, digestOnlyError.Code);
            Assert.Contains("digest does not match the current report bytes", digestOnlyError.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task XbrlReportAdapter_AllowsExistingTargetSelectionOnlyForDigestPinnedOccurrence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-target-row-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var path = Path.Combine(directory, "report.json");
            var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
            XbrlFact Revenue(decimal value) => new()
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "https://example.test/entity",
                EntityIdentifier = "ACME-1",
                Period = period,
                Unit = new XbrlUnit("QAR"),
                Value = value
            };
            await File.WriteAllTextAsync(path, OimSerializer.Serialize(profile, new[] { Revenue(12m), Revenue(99m) }));
            var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))
                .ToLowerInvariant();
            var targetRowId = RunManifest.ComputeSha256("existing-ledger-row");
            var connection = new SourceConnection("report", "xbrl-report", path,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["occurrenceId"] = "report#f2",
                    ["reportDigest"] = digest,
                    ["targetRowId"] = targetRowId
                });
            var adapter = new XbrlReportAdapter();
            var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
            var sample = await adapter.ReadSampleAsync(connection, schema, 10, profile, CancellationToken.None);
            var row = Assert.Single(sample.Rows);
            Assert.Equal("report#f2", row.RowId);
            Assert.Equal("99", row["value"].LexicalSource);

            var missingDigest = new SourceConnection("report", "xbrl-report", path,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["occurrenceId"] = "report#f2",
                    ["targetRowId"] = targetRowId
                });
            var error = await Assert.ThrowsAsync<AdapterException>(() =>
                adapter.InspectSchemaAsync(missingDigest, CancellationToken.None));
            Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
            Assert.Contains("reportDigest", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task XbrlReportAdapter_TargetRowsSelectsExplicitDigestPinnedOccurrencesAndRejectsAmbiguousMappings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-target-rows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var inputPath = Path.Combine(directory, "report.json");
            var templatePath = IndependentAcceptanceFixtures.Materialize(directory, "revenue-report.json");
            var template = Assert.Single(OimSerializer.ReadFacts(await File.ReadAllTextAsync(templatePath)));
            XbrlFact ForYear(decimal value, int year) => new()
            {
                ConceptName = template.ConceptName,
                EntityScheme = template.EntityScheme,
                EntityIdentifier = template.EntityIdentifier,
                Period = XbrlPeriod.OfDuration(new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1)),
                Unit = template.Unit,
                Value = value,
                Decimals = template.Decimals,
                Dimensions = template.Dimensions
            };
            var facts = new[] { ForYear(12.5m, 2025), ForYear(99.25m, 2026) };
            await File.WriteAllTextAsync(inputPath, OimSerializer.Serialize(profile, facts));
            var digest = RunManifest.ComputeSha256(await File.ReadAllBytesAsync(inputPath));
            var targetRows = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["report#f1"] = RunManifest.ComputeSha256("first-existing-ledger-row"),
                ["report#f2"] = RunManifest.ComputeSha256("second-existing-ledger-row")
            };
            var connection = new SourceConnection("report", "xbrl-report", inputPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reportDigest"] = digest,
                    ["targetRows"] = JsonSerializer.Serialize(targetRows)
                });
            var adapter = new XbrlReportAdapter();
            var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
            var sample = await adapter.ReadSampleAsync(connection, schema, 10, profile, CancellationToken.None);

            Assert.Equal(new[] { "report#f1", "report#f2" }, sample.Rows.Select(row => row.RowId));
            Assert.Equal(new[] { "2025-01-01", "2026-01-01" },
                sample.Rows.Select(row => row["periodStart"].AsString()));
            Assert.Equal(new[] { "12.5", "99.25" },
                sample.Rows.Select(row => row["value"].LexicalSource));

            var missingDigest = new SourceConnection("report", "xbrl-report", inputPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["targetRows"] = JsonSerializer.Serialize(targetRows)
                });
            var digestError = await Assert.ThrowsAsync<AdapterException>(() =>
                adapter.InspectSchemaAsync(missingDigest, CancellationToken.None));
            Assert.Contains("reportDigest", digestError.Message, StringComparison.Ordinal);

            var duplicateTargetRows = new SourceConnection("report", "xbrl-report", inputPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reportDigest"] = digest,
                    ["targetRows"] = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["report#f1"] = targetRows["report#f1"],
                        ["report#f2"] = targetRows["report#f1"]
                    })
                });
            var duplicateError = await Assert.ThrowsAsync<AdapterException>(() =>
                adapter.InspectSchemaAsync(duplicateTargetRows, CancellationToken.None));
            Assert.Contains("unique", duplicateError.Message, StringComparison.Ordinal);
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
    [InlineData("xml")]
    [InlineData("json")]
    public async Task RunAsync_ReversesProfileDeclaredTypedStringDimensionsFromReport(string format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-typed-string-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = TypedStringReverseProfile();
            var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
            var sourceFacts = new[]
            {
                CreateReverseRevenueFact(10m, period, "North"),
                CreateReverseRevenueFact(20m, period, "South")
            };
            var reportPath = Path.Combine(directory, $"typed-report.{format}");
            var reportContent = format == "xml"
                ? XBRLMappingStudio.XBRL.XmlSerializer.Serialize(profile, sourceFacts)
                : OimSerializer.Serialize(profile, sourceFacts);
            await File.WriteAllTextAsync(reportPath, reportContent);

            if (format == "xml")
            {
                var adapter = new XbrlReportAdapter();
                var connection = new SourceConnection("typed-report", "xbrl-report", reportPath);
                var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
                var sample = await adapter.ReadSampleAsync(connection, schema, 10, profile, CancellationToken.None);
                var importedValues = sample.Rows.Select(row =>
                {
                    var evidence = (string)row.Fields["dimensionsJson"].Raw!;
                    Assert.True(FactDimensionParser.TryParseJson(evidence, profile, out var parsed, out var evidenceError),
                        $"{evidenceError} Evidence: {evidence}");
                    return Assert.Single(parsed).TypedValue;
                }).ToArray();
                Assert.Equal(new[] { "North", "South" }, importedValues);
            }

            var baseline = TestFixtures.BuildRevenueMappingProject(reportPath);
            var binding = baseline.Bindings.Single() with
            {
                Kind = "xbrl-report",
                Locator = reportPath,
                Configuration = new Dictionary<string, object?>(StringComparer.Ordinal),
                SchemaFingerprint = null
            };
            var nodes = baseline.Nodes.Select(node => node.ShallowCopy()).ToList();
            var revenueFieldIndex = nodes.FindIndex(node => node.NodeType == NodeTypeConstants.ExcelField);
            var revenueField = nodes[revenueFieldIndex];
            var revenueParameters = new Dictionary<string, object?>(revenueField.Parameters, StringComparer.Ordinal)
            {
                ["column"] = "value"
            };
            revenueParameters.Remove("rowFilterColumn");
            revenueParameters.Remove("rowFilterValue");
            nodes[revenueFieldIndex] = revenueField with { Parameters = revenueParameters };

            var factIndex = nodes.FindIndex(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var factNode = nodes[factIndex];
            nodes[factIndex] = factNode with
            {
                InputPorts = factNode.InputPorts.Append(
                    new PortDefinition("dimensionsJson", PortShape.String, PortCardinality.Single)).ToList()
            };
            var dimensionsFieldId = Id.New();
            nodes.Add(new MappingNode
            {
                NodeId = dimensionsFieldId,
                NodeType = NodeTypeConstants.ExcelField,
                DisplayName = "Imported report dimensions",
                Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["bindingId"] = binding.BindingId,
                    ["column"] = "dimensionsJson"
                },
                OutputPorts = new() { new PortDefinition("textValue", PortShape.String, PortCardinality.Single) }
            });
            var edges = baseline.Edges.ToList();
            edges.Add(new MappingEdge
            {
                EdgeId = Id.New(),
                SourceNodeId = dimensionsFieldId,
                SourcePortName = "textValue",
                TargetNodeId = factNode.NodeId,
                TargetPortName = "dimensionsJson"
            });
            var project = new MappingProject
            {
                ProjectId = baseline.ProjectId,
                Name = baseline.Name,
                ProfileId = profile.ProfileId,
                ProfileDigest = profile.Digest,
                FunctionCatalogVersion = baseline.FunctionCatalogVersion,
                Metadata = baseline.Metadata,
                Bindings = new List<SourceBinding> { binding },
                Nodes = nodes,
                Edges = edges
            };

            var result = await new MappingJobService(profile, new ProfileMatchingWorker(profile)).RunAsync(
                project,
                new Dictionary<string, SourceConnection>(StringComparer.Ordinal)
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, "xbrl-report", reportPath)
                },
                new Dictionary<string, string>(StringComparer.Ordinal),
                preview: true,
                CancellationToken.None);

            Assert.True(result.Manifest.IsPreview);
            Assert.True(result.Manifest.Outcome == RunOutcome.SuccessWithWarnings,
                string.Join(" | ", result.Manifest.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(2, result.Facts.Count);
            Assert.Equal(new[] { "North", "South" }, result.Facts
                .Select(fact => Assert.Single(fact.Dimensions).TypedValue));
            Assert.All(result.Facts, fact =>
            {
                Assert.False(Assert.Single(fact.Dimensions).IsExplicit);
                Assert.Contains(fact.ContributingSources,
                    source => source.Locator.Contains("#f", StringComparison.Ordinal));
            });
            Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail
                && !diagnostic.IsBlocking);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReportPeriodFields_MapRepeatedAndAlternateDurationsIntoGeneratedFacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-period-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = SyntheticProfile.Build();
            var sourceFacts = new[]
            {
                ReverseRevenue(12.5m, XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1))),
                ReverseRevenue(12.5m, XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1))),
                ReverseRevenue(20m, XbrlPeriod.OfDuration(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1))),
                ReverseRevenue(null, XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)))
            };
            var reportPath = Path.Combine(directory, "periods.json");
            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile, sourceFacts));

            var project = TestFixtures.BuildRevenueMappingProject(reportPath);
            var binding = Assert.Single(project.Bindings);
            project.Bindings[0] = binding with
            {
                Kind = "xbrl-report",
                Configuration = new Dictionary<string, object?>(),
                SchemaFingerprint = null
            };

            foreach (var field in project.Nodes.Where(node => node.NodeType == NodeTypeConstants.ExcelField).ToArray())
            {
                var parameters = new Dictionary<string, object?>(field.Parameters, StringComparer.Ordinal)
                {
                    ["column"] = "value"
                };
                parameters.Remove("rowFilterColumn");
                parameters.Remove("rowFilterValue");
                project.Nodes[project.Nodes.FindIndex(node => node.NodeId == field.NodeId)] = field with
                {
                    Parameters = parameters
                };
            }

            string? sourceColumn = null;
            foreach (var node in project.Nodes.ToArray())
            {
                sourceColumn = node.DisplayName switch
                {
                    "Entity" => "entityIdentifier",
                    "Period start (2025-01-01)" => "periodStart",
                    "Period end (2026-01-01)" => "periodEnd",
                    _ => sourceColumn
                };
                if (sourceColumn is null) continue;
                var updated = node with
                {
                    NodeType = NodeTypeConstants.ExcelField,
                    Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["bindingId"] = binding.BindingId,
                        ["column"] = sourceColumn
                    },
                    OutputPorts = new() { new PortDefinition("textValue", PortShape.String, PortCardinality.Single) }
                };
                project.Nodes[project.Nodes.FindIndex(candidate => candidate.NodeId == node.NodeId)] = updated;
                sourceColumn = null;
            }

            var sourceNodeIds = project.Nodes
                .Where(node => node.NodeType == NodeTypeConstants.ExcelField)
                .Select(node => node.NodeId)
                .ToHashSet(StringComparer.Ordinal);
            for (var edgeIndex = 0; edgeIndex < project.Edges.Count; edgeIndex++)
            {
                var edge = project.Edges[edgeIndex];
                if (sourceNodeIds.Contains(edge.SourceNodeId)
                    && project.Nodes.Single(node => node.NodeId == edge.SourceNodeId)
                        .OutputPorts.Any(port => port.Name == "textValue"))
                    project.Edges[edgeIndex] = edge with { SourcePortName = "textValue" };
            }

            var factIndex = project.Nodes.FindIndex(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var factNode = project.Nodes[factIndex];
            var dynamicAspects = new (string Port, string Column)[]
            {
                ("conceptName", "concept"),
                ("entityScheme", "entityScheme"),
                ("unitExpression", "unit"),
                ("dimensionsJson", "dimensionsJson"),
                ("decimalsValue", "decimals")
            };
            var factInputs = factNode.InputPorts.ToList();
            foreach (var (port, column) in dynamicAspects)
            {
                factInputs.Add(new PortDefinition(port, PortShape.String, PortCardinality.Single));
                var aspectNodeId = Id.New();
                project.Nodes.Add(new MappingNode
                {
                    NodeId = aspectNodeId,
                    NodeType = NodeTypeConstants.ExcelField,
                    DisplayName = $"Imported {column}",
                    Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["bindingId"] = binding.BindingId,
                        ["column"] = column
                    },
                    OutputPorts = new() { new PortDefinition("textValue", PortShape.String, PortCardinality.Single) }
                });
                project.Edges.Add(new MappingEdge
                {
                    EdgeId = Id.New(),
                    SourceNodeId = aspectNodeId,
                    SourcePortName = "textValue",
                    TargetNodeId = factNode.NodeId,
                    TargetPortName = port
                });
            }
            factInputs.Add(new PortDefinition("isNil", PortShape.Boolean, PortCardinality.Single));
            var nilNodeId = Id.New();
            project.Nodes.Add(new MappingNode
            {
                NodeId = nilNodeId,
                NodeType = NodeTypeConstants.ExcelField,
                DisplayName = "Imported nil flag",
                Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["bindingId"] = binding.BindingId,
                    ["column"] = "nil"
                },
                OutputPorts = new() { new PortDefinition("value", PortShape.Boolean, PortCardinality.Single) }
            });
            project.Edges.Add(new MappingEdge
            {
                EdgeId = Id.New(),
                SourceNodeId = nilNodeId,
                SourcePortName = "value",
                TargetNodeId = factNode.NodeId,
                TargetPortName = "isNil"
            });
            var factParameters = new Dictionary<string, object?>(factNode.Parameters, StringComparer.Ordinal);
            factParameters.Remove("concept");
            factParameters.Remove("unit");
            factParameters.Remove("decimals");
            project.Nodes[factIndex] = factNode with { InputPorts = factInputs, Parameters = factParameters };

            var result = await new MappingJobService(profile, new SuccessfulWorker()).RunAsync(
                project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, "xbrl-report", reportPath)
                },
                new Dictionary<string, string>(),
                preview: false,
                CancellationToken.None);

            Assert.Equal(RunOutcome.SuccessWithWarnings, result.Manifest.Outcome);
            Assert.Equal(4, result.Facts.Count);
            Assert.Equal(new[] { "2025-01-01/2026-01-01", "2025-01-01/2026-01-01", "2025-01-01/2026-01-01", "2026-01-01/2027-01-01" },
                result.Facts.Select(fact => fact.Period.ToIsoString()));
            Assert.Equal(new decimal?[] { 12_500m, 12_500m, null, 20_000m }, result.Facts.Select(fact => fact.Value));
            Assert.True(result.Facts[2].IsNil);
            Assert.All(result.Facts, fact => Assert.Equal("https://example.test/xbrl/entity", fact.EntityScheme));
            Assert.All(result.Facts, fact => Assert.Contains(fact.ContributingSources,
                source => source.SourceId == binding.BindingId && source.Locator.StartsWith("report#f", StringComparison.Ordinal)));
            Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail
                && diagnostic.Severity == DiagnosticSeverity.Warning
                && !diagnostic.IsBlocking);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static XbrlFact ReverseRevenue(decimal? value, XbrlPeriod period) => new()
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "https://example.test/xbrl/entity",
            EntityIdentifier = "ABC-Corp",
            Period = period,
            Unit = new XbrlUnit("QAR"),
            Value = value,
            Decimals = value is null ? null : -3,
            IsNil = value is null
        };
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

    private static XbrlFact CreateReverseRevenueFact(decimal? value, XbrlPeriod period, string typedValue) => new()
    {
        ConceptName = SyntheticProfile.Qualified("Revenue"),
        EntityScheme = "https://example.test/entity",
        EntityIdentifier = "ACME-1",
        Period = period,
        Unit = new XbrlUnit("QAR"),
        Value = value,
        Dimensions = new[]
        {
            new Dimension("synthetic:TypedAxis", "", IsExplicit: false) { TypedValue = typedValue }
        }
    };

    private static FilingProfile TypedStringReverseProfile()
    {
        var baseline = SyntheticProfile.Build();
        const string profileId = "synthetic-typed-reverse-v1";
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("synthetic-typed-reverse-v1|TypedAxis|TypedMember|xs:string"))).ToLowerInvariant();
        return new FilingProfile
        {
            ProfileId = profileId,
            Digest = digest,
            TaxonomyVersion = profileId,
            Concepts = baseline.Concepts,
            SupportedUnits = baseline.SupportedUnits,
            DimensionDomains = baseline.DimensionDomains,
            TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
            {
                ["synthetic:TypedAxis"] = new("synthetic:TypedMember", "xs:string")
            },
            NamespaceUris = baseline.NamespaceUris.Concat(new[]
            {
                new KeyValuePair<string, string>("xs", "http://www.w3.org/2001/XMLSchema")
            }).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            OutputFormat = "xbrl-json",
            RequiredChecks = new HashSet<string>(StringComparer.Ordinal),
            TaxonomyUrls = new[] { "synthetic-typed-reverse-v1-entry.xsd" }
        };
    }

    private sealed class ProfileMatchingWorker(FilingProfile profile) : IWorkerClient
    {
        private const int ProfileSchemaVersion = FilingProfileManifest.CurrentSchemaVersion;
        private static readonly string[] Capabilities =
        {
            WorkerOperations.LoadTaxonomy,
            WorkerOperations.ValidateReport
        };
        private readonly SuccessfulWorker _validationWorker = new();
        private string EntryPoint => profile.TaxonomyUrls.Single();

        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new WorkerHandshake(
                WorkerProtocol.CurrentVersion,
                "profile-matching-test/1.0",
                Capabilities)
            {
                ProfileSchemaVersion = ProfileSchemaVersion,
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.TaxonomyVersion,
                ProfileDigest = profile.Digest,
                TaxonomyEntryPoint = EntryPoint,
                RequiredPlugins = profile.RequiredPlugins.Order(StringComparer.Ordinal).ToArray(),
                RequiredChecks = profile.RequiredChecks.Order(StringComparer.Ordinal).ToArray()
            });

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            if (request.Operation != WorkerOperations.LoadTaxonomy)
                return _validationWorker.RequestAsync(request, cancellationToken);

            var plugins = profile.RequiredPlugins.Order(StringComparer.Ordinal).ToArray();
            var checks = profile.RequiredChecks.Order(StringComparer.Ordinal).ToArray();
            var loaded = new WorkerTaxonomyLoadResult(
                "profile-matching-test",
                "1.0",
                Capabilities,
                Completed: true,
                ProfileSchemaVersion,
                profile.ProfileId,
                profile.TaxonomyVersion,
                profile.Digest,
                EntryPoint,
                plugins,
                profile.Concepts.Count)
            {
                RequiredChecks = checks,
                LoadedPlugins = plugins
            };
            return Task.FromResult(new WorkerResponse(
                request.OperationId,
                WorkerStatus.Success,
                JsonSerializer.SerializeToElement(loaded),
                Diagnostics: Array.Empty<Diagnostic>(),
                Error: null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
