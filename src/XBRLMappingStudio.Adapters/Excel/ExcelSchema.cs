using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Excel;

/// <summary>
/// Schema discovered in an XLSX workbook for one selected sheet or named range.
/// A fingerprint covers sheet name + ordered column names + inferred types + the
/// unit/scale/period heading anchors found above the header (IG01), so that header
/// shifts, column renames, or a change from e.g. "QAR thousands" to "QAR units"
/// produce a detectable change (IO02) rather than silently re-emitting a stale,
/// wrongly-scaled amount.
/// </summary>
public sealed record ExcelSchema(
    string SourceId,
    string SheetName,
    int HeaderRow,
    int FirstDataRow,
    int LastDataRow,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<string> UnitHeadingHints,
    string Fingerprint,
    string? RegionAddress = null,
    ExcelAdapter.HiddenRowPolicy HiddenRowPolicy = ExcelAdapter.HiddenRowPolicy.Include) : ISourceSchema
{
    public static string ComputeFingerprint(
        string sheet,
        IEnumerable<ColumnSchema> cols,
        IEnumerable<string>? unitHeadingHints = null,
        string? regionAddress = null,
        ExcelAdapter.HiddenRowPolicy hiddenRowPolicy = ExcelAdapter.HiddenRowPolicy.Include,
        string? formulaCacheFingerprint = null)
    {
        // Heading anchors are sorted so that a pure reorder of the banner cells above
        // the header row (without changing which anchors exist) does not look like a
        // schema change; only appearing/disappearing/altering anchor text does.
        var anchors = (unitHeadingHints ?? Array.Empty<string>())
            .Select(h => h.Trim())
            .Where(h => h.Length > 0)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        var anchorPart = anchors.Count > 0
            ? new[] { "anchors:" + string.Join(",", anchors) }
            : Array.Empty<string>();
        var joined = string.Join('|', new[]
            {
                sheet,
                "region:" + (regionAddress ?? "used-range"),
                "hidden-rows:" + hiddenRowPolicy,
                "formula-cache:" + (formulaCacheFingerprint ?? "none")
            }
            .Concat(cols.OrderBy(c => c.Ordinal)
                .Select(c => $"{c.Ordinal}:{c.Name}:{c.InferredShape}"))
            .Concat(anchorPart));
        return XBRLMappingStudio.Diagnostics.RunManifest.ComputeSha256(joined);
    }
}

/// <summary>
/// One row of an Excel table with sheet/row/cell references preserved for provenance.
/// </summary>
public sealed class ExcelRowMetadata
{
    public required int SheetRowIndex { get; init; }
    public required IReadOnlyDictionary<string, string> CellLocators { get; init; }
}
