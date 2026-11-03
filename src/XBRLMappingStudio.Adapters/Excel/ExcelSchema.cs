using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Excel;

/// <summary>
/// Schema discovered in an XLSX workbook for one selected sheet or named range.
/// A fingerprint covers sheet name + ordered column names + inferred types so that
/// header shifts or column renames produce a detectable change (IO02).
/// </summary>
public sealed record ExcelSchema(
    string SourceId,
    string SheetName,
    int HeaderRow,
    int FirstDataRow,
    int LastDataRow,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<string> UnitHeadingHints,
    string Fingerprint) : ISourceSchema
{
    public static string ComputeFingerprint(string sheet, IEnumerable<ColumnSchema> cols)
    {
        var joined = string.Join('|', new[] { sheet }
            .Concat(cols.OrderBy(c => c.Ordinal)
                .Select(c => $"{c.Ordinal}:{c.Name}:{c.InferredShape}")));
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
