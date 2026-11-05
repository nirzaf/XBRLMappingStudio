using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Pdf;

/// <summary>
/// Reads approved PDF table cells through the shared source-adapter contract. Raw PDF
/// extraction and analyst approval remain separate: this adapter requires a reviewed
/// sidecar and verifies that every lineage link resolves to the same immutable source.
/// </summary>
public sealed class PdfSourceAdapter(IPdfEvidenceCatalog? evidenceCatalog = null) : ISourceAdapter
{
    public const string ReviewManifestConfigurationKey = "reviewManifestPath";
    public const int MaxReviewManifestBytes = 32 * 1024 * 1024;
    private const int MaxRows = 100_000;
    private const int MaxReviewedCells = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly ColumnSchema[] Columns =
    {
        new("row_label", PortShape.String, 0, "Row label"),
        new("reporting_year", PortShape.Integer, 1, "Reporting year"),
        new("value", PortShape.Decimal, 2, "Reviewed value"),
        new("currency", PortShape.String, 3, "Currency"),
        new("scale_factor", PortShape.Integer, 4, "Scale factor")
    };

    public string Kind => "pdf-digital";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var document = ReadReviewedDocument(connection, cancellationToken);
        return Task.FromResult<ISourceSchema>(CreateSchema(connection.SourceId, document.Manifest));
    }

    /// <summary>Returns a sidecar only after its source digest, schema and persisted evidence validate.</summary>
    public PdfReviewedSourceManifest ReadReviewedSourceManifest(
        SourceConnection connection,
        CancellationToken cancellationToken)
        => ReadReviewedDocument(connection, cancellationToken).Manifest;

    public Task<DataSample> ReadSampleAsync(
        SourceConnection connection,
        ISourceSchema schema,
        int maxRows,
        CancellationToken cancellationToken)
    {
        if (maxRows is <= 0 or > 10_000)
            throw InvalidManifest("The PDF sample row limit must be between one and ten thousand.");
        var document = ReadReviewedDocument(connection, cancellationToken);
        ValidateSchema(connection, schema, document.Manifest);
        var truncated = document.Records.Count > maxRows;
        return Task.FromResult(new DataSample(schema,
            truncated ? document.Records.Take(maxRows).ToArray() : document.Records,
            truncated));
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(
        SourceConnection connection,
        ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var document = ReadReviewedDocument(connection, cancellationToken);
        ValidateSchema(connection, schema, document.Manifest);
        foreach (var record in document.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
            await Task.Yield();
        }
    }

    /// <summary>Resolve the explicit review sidecar path; absent configuration uses a sibling file.</summary>
    public static string ResolveReviewManifestPath(SourceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var sourcePath = connection.Path is null
            ? throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                "A digital PDF source path is required.", DiagnosticStage.Introspection)
            : Path.GetFullPath(connection.Path);
        if (connection.Configuration is not null
            && connection.Configuration.TryGetValue(ReviewManifestConfigurationKey, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Path.GetDirectoryName(sourcePath)!, configured));
        }
        return sourcePath + ".xms-review.json";
    }

    private ReviewedDocument ReadReviewedDocument(SourceConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = connection.Path is null
            ? throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                "A digital PDF source path is required.", DiagnosticStage.Introspection)
            : Path.GetFullPath(connection.Path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(sourcePath), ".pdf"))
            throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                "The digital PDF source path must identify a .pdf file.", DiagnosticStage.Introspection);

        var sourceDigest = ComputeSourceDigest(sourcePath, cancellationToken);
        var reviewPath = ResolveReviewManifestPath(connection);
        var manifest = ReadManifest(reviewPath, cancellationToken);
        ValidateManifest(manifest);
        if (!StringComparer.Ordinal.Equals(sourceDigest, manifest.SourceSha256))
            throw new AdapterException(DiagnosticCodes.IoPdfEvidenceStale,
                "The source PDF no longer matches the reviewed evidence digest.", DiagnosticStage.Validation);

        var expectedReferences = CreateEvidenceExpectations(manifest);
        var evidenceIsCurrent = false;
        try
        {
            evidenceIsCurrent = evidenceCatalog?.AreCurrent(expectedReferences, sourcePath, sourceDigest) ?? false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not OperationCanceledException)
        {
            // Evidence infrastructure errors must fail closed with the same stable
            // adapter diagnostic as a missing or stale evidence reference.
        }
        if (!evidenceIsCurrent)
        {
            throw new AdapterException(DiagnosticCodes.IoPdfEvidenceReferenceInvalid,
                "One or more reviewed PDF regions do not resolve to current persisted evidence.",
                DiagnosticStage.Validation);
        }

        return new ReviewedDocument(manifest, BuildRecords(connection.SourceId, manifest));
    }

    private static string ComputeSourceDigest(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
                throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                    "The digital PDF source file does not exist.", DiagnosticStage.Introspection);
            if (info.Length is <= 0 or > PdfDigitalExtractionOptions.AbsoluteMaxFileBytes)
                throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                    "The digital PDF source exceeds the supported byte limit.", DiagnosticStage.Validation);
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (total > PdfDigitalExtractionOptions.AbsoluteMaxFileBytes)
                    throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                        "The digital PDF source exceeds the supported byte limit.", DiagnosticStage.Validation);
                hash.AppendData(buffer, 0, read);
            }
            if (total == 0 || total != info.Length)
                throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                    "The digital PDF source changed while it was being read.", DiagnosticStage.Validation);
            return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (AdapterException) { throw; }
        catch (IOException)
        {
            throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                "The digital PDF source could not be read.", DiagnosticStage.Introspection);
        }
        catch (UnauthorizedAccessException)
        {
            throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                "The digital PDF source could not be read.", DiagnosticStage.Introspection);
        }
    }

    private static PdfReviewedSourceManifest ReadManifest(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
                throw ReviewRequired("A reviewed PDF manifest is required before the source can be mapped.");
            var bytes = ReadBoundedManifest(path, cancellationToken);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            RejectDuplicateProperties(json.RootElement);
            return JsonSerializer.Deserialize<PdfReviewedSourceManifest>(json.RootElement.GetRawText(), JsonOptions)
                ?? throw InvalidManifest("The PDF review manifest is empty.");
        }
        catch (AdapterException) { throw; }
        catch (JsonException)
        {
            throw InvalidManifest("The PDF review manifest is malformed or contains unsupported fields.");
        }
        catch (IOException)
        {
            throw InvalidManifest("The PDF review manifest could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw InvalidManifest("The PDF review manifest could not be read.");
        }
    }

    private static byte[] ReadBoundedManifest(string path, CancellationToken cancellationToken)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxReviewManifestBytes)
            throw InvalidManifest("The PDF review manifest exceeds the supported size limit.");

        using var output = new MemoryStream((int)input.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaxReviewManifestBytes)
                throw InvalidManifest("The PDF review manifest exceeds the supported size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw InvalidManifest("The PDF review manifest contains a duplicate property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static void ValidateManifest(PdfReviewedSourceManifest manifest)
    {
        if (manifest.FormatVersion != 1 || !IsSha256(manifest.SourceSha256)
            || !IsSha256(manifest.TemplateRevision)
            || string.IsNullOrWhiteSpace(manifest.Reviewer) || manifest.Reviewer.Length > 128
            || manifest.ReviewedUtc == default || manifest.ReviewedUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(manifest.RowLabelHeader)
            || manifest.RowLabelHeader.Length > 128
            || manifest.TemplatePageNumber is < 1 or > PdfDigitalExtractionOptions.AbsoluteMaxPages
            || manifest.PeriodColumns is null || manifest.PeriodColumns.Count is < 2 or > 30
            || manifest.Rows is null || manifest.Rows.Count is < 1 or > MaxRows
            || manifest.NumericPolicy is null)
            throw InvalidManifest("The PDF review manifest is incomplete or has not been approved.");
        if (!manifest.IsApproved)
            throw ReviewRequired("The PDF review manifest has not been approved.");
        try { _ = manifest.NumericPolicy.Validate(); }
        catch (AdapterException) { throw; }

        var years = new HashSet<int>();
        var headerTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var period in manifest.PeriodColumns)
        {
            if (period is null || string.IsNullOrWhiteSpace(period.HeaderText)
                || period.HeaderText.Length > 64 || period.ReportingYear is < 1900 or > 2200
                || !double.IsFinite(period.CenterOnColumnAxisNormalized)
                || period.CenterOnColumnAxisNormalized is <= 0 or >= 1
                || !years.Add(period.ReportingYear) || !headerTexts.Add(period.HeaderText))
                throw InvalidManifest("The PDF review manifest contains invalid or duplicate year columns.");
        }

        var occurrences = new HashSet<int>();
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var cellCount = 0;
        foreach (var row in manifest.Rows)
        {
            if (row is null || row.Occurrence <= 0 || !occurrences.Add(row.Occurrence)
                || string.IsNullOrWhiteSpace(row.LabelText) || row.LabelText.Length > 2_048
                || !IsEvidenceId(row.LabelEvidenceId) || !evidenceIds.Add(row.LabelEvidenceId)
                || !ValidRegion(row.LabelEvidence)
                || row.Cells is null || row.Cells.Count is < 1 or > 30)
                throw InvalidManifest("The PDF review manifest contains an invalid row or row-label evidence link.");

            var rowYears = new HashSet<int>();
            foreach (var cell in row.Cells)
            {
                if (cell is null || !years.Contains(cell.ReportingYear) || !rowYears.Add(cell.ReportingYear)
                    || string.IsNullOrWhiteSpace(cell.RawText) || cell.RawText.Length > 128
                    || !IsEvidenceId(cell.EvidenceId) || !evidenceIds.Add(cell.EvidenceId)
                    || !ValidRegion(cell.Evidence))
                    throw InvalidManifest("The PDF review manifest contains an invalid or duplicate table cell.");
                if (!cell.IsApproved)
                    throw ReviewRequired("Every PDF value must be reviewed before it can enter a mapping run.");

                var parsed = PdfNumericInterpreter.Parse(cell.RawText, cell.ReportingYear, cell.Evidence,
                    manifest.NumericPolicy);
                if (parsed.State != PdfNumericParseState.ParsedRequiresReview || parsed.Value is null)
                    throw new AdapterException(parsed.Diagnostic?.Code ?? DiagnosticCodes.IoPdfReviewRequired,
                        "A PDF value remains ambiguous and cannot enter a mapping run.", DiagnosticStage.Validation);
                if (!decimal.TryParse(cell.CanonicalValue, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var approvedValue)
                    || !StringComparer.Ordinal.Equals(approvedValue.ToString(CultureInfo.InvariantCulture), cell.CanonicalValue))
                    throw InvalidManifest("A reviewed PDF value is not a canonical invariant decimal.");
                if (approvedValue != parsed.Value.Value && string.IsNullOrWhiteSpace(cell.CorrectionReason))
                    throw ReviewRequired("A corrected PDF value requires a reviewer reason.");
                if (cell.CorrectionReason is { Length: > 2_048 })
                    throw InvalidManifest("A PDF value correction reason exceeds the supported length.");

                cellCount++;
                if (cellCount > MaxReviewedCells)
                    throw InvalidManifest("The PDF review manifest exceeds the supported reviewed-cell limit.");
            }

        }
    }

    private static IReadOnlyList<PdfEvidenceExpectation> CreateEvidenceExpectations(PdfReviewedSourceManifest manifest)
    {
        var references = new List<PdfEvidenceExpectation>(manifest.Rows.Count + manifest.Rows.Sum(row => row.Cells.Count));
        foreach (var row in manifest.Rows)
        {
            references.Add(new PdfEvidenceExpectation(row.LabelEvidenceId, row.LabelEvidence, manifest.TemplateRevision));
            references.AddRange(row.Cells.Select(cell =>
                new PdfEvidenceExpectation(cell.EvidenceId, cell.Evidence, manifest.TemplateRevision, cell.RawText)));
        }
        return references;
    }

    private static IReadOnlyList<DataRecord> BuildRecords(string sourceId, PdfReviewedSourceManifest manifest)
    {
        var rows = new List<DataRecord>();
        foreach (var row in manifest.Rows.OrderBy(item => item.Occurrence))
        {
            var labelSource = ToSourceReference(sourceId, row.LabelEvidenceId, row.LabelEvidence);
            foreach (var cell in row.Cells.OrderBy(item => item.ReportingYear))
            {
                var valueSource = ToSourceReference(sourceId, cell.EvidenceId, cell.Evidence);
                var templateSource = new SourceReference(sourceId,
                    $"pdf-template:{manifest.TemplateRevision}");
                var yearSource = new SourceReference(sourceId,
                    $"pdf-template:{manifest.TemplateRevision}:year={cell.ReportingYear}");
                var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
                {
                    ["row_label"] = DataValue.StringValue(row.LabelText, labelSource),
                    ["reporting_year"] = DataValue.IntegerValue(cell.ReportingYear, yearSource),
                    ["value"] = DataValue.DecimalValue(
                        decimal.Parse(cell.CanonicalValue, CultureInfo.InvariantCulture), valueSource, cell.CanonicalValue),
                    ["currency"] = DataValue.StringValue(manifest.NumericPolicy.CurrencyCode, templateSource),
                    ["scale_factor"] = DataValue.IntegerValue(manifest.NumericPolicy.ScaleFactor, templateSource)
                };
                var sources = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Source!, StringComparer.Ordinal);
                rows.Add(new DataRecord
                {
                    RowId = $"{sourceId}#pdf-r{row.Occurrence}-y{cell.ReportingYear}",
                    Fields = fields,
                    CellSources = sources,
                    HasStableRowIdentity = true
                });
            }
        }
        return rows;
    }

    private static SourceReference ToSourceReference(string sourceId, string evidenceId, PdfEvidenceRegion region)
    {
        var coordinates = string.Join(",", new[]
        {
            region.LeftNormalized, region.BottomNormalized, region.RightNormalized, region.TopNormalized
        }.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        return new SourceReference(sourceId,
            $"pdf-evidence:{evidenceId};page={region.PageNumber};region={coordinates}",
            $"PDF page {region.PageNumber}");
    }

    private static TabularSchema CreateSchema(string sourceId, PdfReviewedSourceManifest manifest)
    {
        // Long-form records keep the year dimension explicit while presenting the
        // same stable typed schema for tables with different period columns.
        _ = manifest;
        return TabularSchema.FromSource(sourceId, Columns);
    }

    private static void ValidateSchema(SourceConnection connection, ISourceSchema schema, PdfReviewedSourceManifest manifest)
    {
        var expected = CreateSchema(connection.SourceId, manifest);
        if (!StringComparer.Ordinal.Equals(schema.SourceId, connection.SourceId)
            || !StringComparer.Ordinal.Equals(schema.Fingerprint, expected.Fingerprint))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "The PDF source schema no longer matches the reviewed mapping.", DiagnosticStage.Introspection);
    }

    private static bool ValidRegion(PdfEvidenceRegion? region)
        => region is not null && region.PageNumber is > 0 and <= PdfDigitalExtractionOptions.AbsoluteMaxPages
            && double.IsFinite(region.LeftNormalized) && region.LeftNormalized >= 0
            && double.IsFinite(region.BottomNormalized) && region.BottomNormalized >= 0
            && double.IsFinite(region.RightNormalized) && region.RightNormalized <= 1
            && double.IsFinite(region.TopNormalized) && region.TopNormalized <= 1
            && region.LeftNormalized < region.RightNormalized
            && region.BottomNormalized < region.TopNormalized;

    private static bool IsEvidenceId(string? value)
        => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);

    private static bool IsSha256(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value.AsSpan(7).ToString().All(Uri.IsHexDigit);

    private static AdapterException ReviewRequired(string message)
        => new(DiagnosticCodes.IoPdfReviewRequired, message, DiagnosticStage.Validation);

    private static AdapterException InvalidManifest(string message)
        => new(DiagnosticCodes.IoPdfReviewManifestInvalid, message, DiagnosticStage.Validation);

    private sealed record ReviewedDocument(PdfReviewedSourceManifest Manifest, IReadOnlyList<DataRecord> Records);
}
