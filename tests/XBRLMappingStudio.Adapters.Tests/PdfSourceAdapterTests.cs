using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class PdfSourceAdapterTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [Fact]
    public async Task ApprovedManifestExposesTypedSchemaStableLineageAndSampleFullParity()
    {
        using var workspace = new TempWorkspace();
        var fixture = CreateFixture(workspace);
        WriteManifest(fixture);
        var adapter = new PdfSourceAdapter(fixture.Catalog);

        var schema = await adapter.InspectSchemaAsync(fixture.Connection, CancellationToken.None);
        var sample = await adapter.ReadSampleAsync(fixture.Connection, schema, maxRows: 2, CancellationToken.None);
        var full = await ReadFullAsync(adapter, fixture.Connection, schema);

        Assert.Equal(new[] { "row_label", "reporting_year", "value", "currency", "scale_factor" },
            schema.Columns.Select(column => column.Name));
        Assert.Equal(new[] { PortShape.String, PortShape.Integer, PortShape.Decimal, PortShape.String, PortShape.Integer },
            schema.Columns.Select(column => column.InferredShape));
        Assert.True(sample.IsTruncated);
        Assert.Equal(2, sample.Rows.Count);
        Assert.Equal(6, full.Count);
        Assert.Equal(full.Take(2).Select(row => row.RowId), sample.Rows.Select(row => row.RowId));
        Assert.Equal(full[0].Fields["value"], sample.Rows[0].Fields["value"]);
        Assert.Equal(100m, full[0].Fields["value"].AsDecimal());
        Assert.Equal(2024L, (long)full[0].Fields["reporting_year"].Raw!);
        Assert.Equal("Revenue 1", full[0].Fields["row_label"].AsString());
        Assert.Contains("pdf-evidence:", full[0].Fields["value"].Source!.Locator, StringComparison.Ordinal);
        Assert.Contains("page=1", full[0].Fields["value"].Source!.Locator, StringComparison.Ordinal);
        Assert.True(full[0].HasStableRowIdentity);
        Assert.Equal(9, fixture.Catalog.LastExpectations.Count);
        Assert.Equal(6, fixture.Catalog.LastExpectations.Count(expectation => expectation.ExpectedExtractedText is not null));

        var changedRows = fixture.Manifest.Rows.Select((row, rowIndex) => rowIndex == 0
            ? row with { Cells = row.Cells.Select((cell, cellIndex) => cellIndex == 0
                ? cell with { RawText = "999", CanonicalValue = "999" }
                : cell).ToArray() }
            : row).ToArray();
        WriteManifest(fixture with { Manifest = fixture.Manifest with { Rows = changedRows } });
        var dataChangedSchema = await adapter.InspectSchemaAsync(fixture.Connection, CancellationToken.None);
        var existingSchemaSample = await adapter.ReadSampleAsync(
            fixture.Connection, schema, maxRows: 1, CancellationToken.None);

        Assert.Equal(schema.Fingerprint, dataChangedSchema.Fingerprint);
        Assert.Equal(999m, existingSchemaSample.Rows[0].Fields["value"].AsDecimal());
    }

    [Fact]
    public async Task SchemaMismatchAndUnapprovedOrUnreconciledEvidenceFailClosed()
    {
        using var workspace = new TempWorkspace();
        var fixture = CreateFixture(workspace);
        WriteManifest(fixture);
        var adapter = new PdfSourceAdapter(fixture.Catalog);
        var schema = await adapter.InspectSchemaAsync(fixture.Connection, CancellationToken.None);
        var wrongSchema = new TabularSchema(fixture.Connection.SourceId,
            new[] { new ColumnSchema("other", PortShape.String, 0) }, RunManifest.ComputeSha256("other-schema"));

        var schemaError = await Assert.ThrowsAsync<AdapterException>(() => adapter.ReadSampleAsync(
            fixture.Connection, wrongSchema, maxRows: 1, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, schemaError.Code);

        var unapprovedCell = fixture.Manifest.Rows[0].Cells[0] with { IsApproved = false };
        var unapprovedRow = fixture.Manifest.Rows[0] with
        {
            Cells = new[] { unapprovedCell }.Concat(fixture.Manifest.Rows[0].Cells.Skip(1)).ToArray()
        };
        var unapprovedRows = new[] { unapprovedRow }.Concat(fixture.Manifest.Rows.Skip(1)).ToArray();
        WriteManifest(fixture with { Manifest = fixture.Manifest with { Rows = unapprovedRows } });
        var reviewError = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(
            fixture.Connection, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoPdfReviewRequired, reviewError.Code);

        WriteManifest(fixture);
        fixture.Catalog.IsCurrent = false;
        var evidenceError = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(
            fixture.Connection, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoPdfEvidenceReferenceInvalid, evidenceError.Code);
    }

    [Fact]
    public async Task SourceDigestDriftBlocksReviewedManifest()
    {
        using var workspace = new TempWorkspace();
        var fixture = CreateFixture(workspace);
        WriteManifest(fixture);
        await File.WriteAllBytesAsync(fixture.SourcePath, Encoding.ASCII.GetBytes("%PDF changed"));
        var adapter = new PdfSourceAdapter(fixture.Catalog);

        var error = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(
            fixture.Connection, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.IoPdfEvidenceStale, error.Code);
        Assert.Empty(fixture.Catalog.LastExpectations);
    }

    private static async Task<List<DataRecord>> ReadFullAsync(
        PdfSourceAdapter adapter,
        SourceConnection connection,
        ISourceSchema schema)
    {
        var records = new List<DataRecord>();
        await foreach (var record in adapter.ReadFullAsync(connection, schema, CancellationToken.None))
            records.Add(record);
        return records;
    }

    private static PdfFixture CreateFixture(TempWorkspace workspace)
    {
        var sourcePath = workspace.PathFor("source.pdf");
        File.WriteAllBytes(sourcePath, Encoding.ASCII.GetBytes("%PDF synthetic source bytes"));
        var sourceDigest = RunManifest.ComputeSha256(File.ReadAllBytes(sourcePath));
        var revision = RunManifest.ComputeSha256("template-v1");
        var years = new[] { 2024, 2025 };
        var rows = Enumerable.Range(1, 3).Select(occurrence => new PdfReviewedSourceRow
        {
            Occurrence = occurrence,
            LabelText = $"Revenue {occurrence}",
            LabelEvidenceId = Guid.NewGuid().ToString("N"),
            LabelEvidence = Region(0.05, occurrence * 0.05),
            Cells = years.Select((year, index) =>
            {
                var value = (occurrence * 100 + index * 50).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new PdfReviewedSourceCell
                {
                    ReportingYear = year,
                    RawText = value,
                    CanonicalValue = value,
                    EvidenceId = Guid.NewGuid().ToString("N"),
                    Evidence = Region(0.4 + index * 0.15, occurrence * 0.05),
                    IsApproved = true
                };
            }).ToArray()
        }).ToArray();
        var manifest = new PdfReviewedSourceManifest
        {
            FormatVersion = 1,
            SourceSha256 = sourceDigest,
            TemplateRevision = revision,
            Reviewer = "test-reviewer",
            ReviewedUtc = DateTimeOffset.UtcNow,
            IsApproved = true,
            RowLabelHeader = "Description",
            NumericPolicy = new PdfNumericFormatPolicy('.', null, false, 1, "QAR",
                AllowLeadingSign: true,
                AllowParenthesizedNegative: true,
                AllowTrailingMinus: false,
                PdfNumericPolicyBasis.ExplicitTemplate),
            PeriodColumns = new[]
            {
                new PdfPeriodColumnAnchor("2024", 2024, 0.55),
                new PdfPeriodColumnAnchor("2025", 2025, 0.75)
            },
            Rows = rows
        };
        return new PdfFixture(sourcePath, sourceDigest, new SourceConnection("source", "pdf-digital", sourcePath),
            manifest, new RecordingEvidenceCatalog(sourceDigest, revision));
    }

    private static PdfEvidenceRegion Region(double left, double bottom)
        => new(1, left, bottom, left + 0.04, bottom + 0.02);

    private static void WriteManifest(PdfFixture fixture)
        => File.WriteAllText(PdfSourceAdapter.ResolveReviewManifestPath(fixture.Connection),
            JsonSerializer.Serialize(fixture.Manifest, ManifestJsonOptions));

    private sealed record PdfFixture(
        string SourcePath,
        string SourceDigest,
        SourceConnection Connection,
        PdfReviewedSourceManifest Manifest,
        RecordingEvidenceCatalog Catalog);

    private sealed class RecordingEvidenceCatalog(string sourceDigest, string templateRevision) : IPdfEvidenceCatalog
    {
        public bool IsCurrent { get; set; } = true;
        public IReadOnlyList<PdfEvidenceExpectation> LastExpectations { get; private set; }
            = Array.Empty<PdfEvidenceExpectation>();

        public bool AreCurrent(
            IReadOnlyList<PdfEvidenceExpectation> expectedReferences,
            string currentSourcePath,
            string expectedSourceSha256)
        {
            LastExpectations = expectedReferences;
            return IsCurrent
                && File.Exists(currentSourcePath)
                && StringComparer.Ordinal.Equals(expectedSourceSha256, sourceDigest)
                && expectedReferences.All(reference =>
                    StringComparer.Ordinal.Equals(reference.TemplateRevision, templateRevision));
        }
    }
}
