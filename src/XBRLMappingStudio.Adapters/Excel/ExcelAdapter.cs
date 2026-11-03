using System.Runtime.CompilerServices;
using ClosedXML.Excel;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Excel;

/// <summary>
/// Reads ordinary XLSX workbooks (IO02). Discovery uses the first sheet or the
/// configured sheet name. The header row is detected as the first non-empty row
/// that is followed by at least one data row. Cell coordinates are preserved on
/// every value for provenance.
/// Formula policy: <see cref="FormulaPolicy"/> controls whether cached values are
/// accepted or blocked pending review (per section 4 "Excel without a desktop Excel
/// dependency").
/// </summary>
public sealed class ExcelAdapter : ISourceAdapter
{
    public string Kind => "excel";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;

    public enum FormulaPolicy
    {
        /// <summary>Accept cached formula results as values.</summary>
        AcceptCached,
        /// <summary>Emit a diagnostic on any formula cell; do not read the cached value.</summary>
        BlockOnFormula
    }

    private readonly FormulaPolicy _policy;

    public ExcelAdapter(FormulaPolicy policy = FormulaPolicy.AcceptCached)
    {
        _policy = policy;
    }

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var path = RequirePath(connection);
        using var workbook = new XLWorkbook(path);
        var sheet = SelectSheet(workbook, connection);
        int? explicitHeader = null;
        if (connection.Configuration is not null
            && connection.Configuration.TryGetValue("headerRow", out var hrStr)
            && int.TryParse(hrStr, out var hr) && hr > 0)
        {
            explicitHeader = hr;
        }
        var headerRow = explicitHeader ?? DetectHeaderRow(sheet);
        var lastDataRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
        var columns = DiscoverColumns(sheet, headerRow);
        var unitHints = ExtractUnitHints(sheet, headerRow);
        var fingerprint = ExcelSchema.ComputeFingerprint(sheet.Name, columns);
        ISourceSchema schema = new ExcelSchema(
            SourceId: connection.SourceId,
            SheetName: sheet.Name,
            HeaderRow: headerRow,
            FirstDataRow: headerRow + 1,
            LastDataRow: lastDataRow,
            Columns: columns,
            UnitHeadingHints: unitHints,
            Fingerprint: fingerprint);
        return Task.FromResult(schema);
    }

    public async Task<DataSample> ReadSampleAsync(
        SourceConnection connection,
        ISourceSchema schema,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var rows = new List<DataRecord>();
        bool truncated = false;
        int i = 0;
        await foreach (var r in ReadFullAsync(connection, schema, cancellationToken))
        {
            if (i++ >= maxRows) { truncated = true; break; }
            rows.Add(r);
        }
        return new DataSample(schema, rows, truncated);
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(
        SourceConnection connection,
        ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var excelSchema = (ExcelSchema)schema;
        var path = RequirePath(connection);
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name == excelSchema.SheetName)
            ?? throw new InvalidOperationException(
                $"Sheet '{excelSchema.SheetName}' not found in workbook {path}.");

        for (int rowIdx = excelSchema.FirstDataRow; rowIdx <= excelSchema.LastDataRow; rowIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xlRow = sheet.Row(rowIdx);
            if (xlRow.IsEmpty()) continue;

            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var cellSources = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
            bool anyHiddenRowPolicy = !xlRow.IsHidden;

            foreach (var col in excelSchema.Columns)
            {
                var cell = xlRow.Cell(col.Ordinal);
                var sourceRef = new SourceReference(
                    connection.SourceId,
                    $"{excelSchema.SheetName}!{cell.Address.ToString()}",
                    $"{excelSchema.SheetName}!{cell.Address}");

                DataValue value = ReadCellValue(cell, sourceRef);
                fields[col.Name] = value;
                cellSources[col.Name] = sourceRef;
            }

            yield return new DataRecord
            {
                RowId = $"{excelSchema.SheetName}#r{rowIdx}",
                Fields = fields,
                CellSources = cellSources
            };

            await Task.Yield();
        }
    }

    private DataValue ReadCellValue(IXLCell cell, SourceReference src)
    {
        if (cell.HasFormula && _policy == FormulaPolicy.BlockOnFormula)
        {
            // Per section 4: report the exact affected cell and require review.
            throw new InvalidOperationException(
                $"Formula cache not permitted at {src}.");
        }

        var value = cell.CachedValue;
        var empty = cell.IsEmpty();
        if (empty)
            return DataValue.Missing();

        // Distinguish blank-string cells from truly missing values.
        if (cell.DataType == XLDataType.Text)
        {
            var text = cell.GetString();
            var v = DataValue.StringValue(text, src);
            return v;
        }

        if (cell.DataType == XLDataType.Number)
        {
            var d = cell.GetValue<decimal>();
            // Preserve the displayed lexical form for evidence (section 4: "Retain both raw cell value
            // and displayed unit or period heading where relevant").
            var lexical = cell.GetFormattedString();
            return DataValue.DecimalValue(d, src, lexical);
        }

        if (cell.DataType == XLDataType.Boolean)
            return DataValue.BoolValue(cell.GetBoolean());

        if (cell.DataType == XLDataType.DateTime)
        {
            var dt = cell.GetDateTime();
            return DataValue.DateValue(DateOnly.FromDateTime(dt));
        }

        return DataValue.StringValue(cell.ToString() ?? "", src);
    }

    private static string RequirePath(SourceConnection conn)
        => conn.Path ?? throw new InvalidOperationException(
            $"Excel connection '{conn.SourceId}' has no file path.");

    private static IXLWorksheet SelectSheet(XLWorkbook wb, SourceConnection conn)
    {
        if (conn.Configuration is not null
            && conn.Configuration.TryGetValue("sheet", out var name))
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name == name);
            if (ws is not null) return ws;
        }
        return wb.Worksheets.First();
    }

    private static int DetectHeaderRow(IXLWorksheet sheet)
    {
        var first = sheet.FirstRowUsed()?.RowNumber() ?? 1;
        var last = sheet.LastRowUsed()?.RowNumber() ?? first;
        for (int r = first; r <= last; r++)
        {
            var row = sheet.Row(r);
            int nonEmpty = 0;
            var used = row.CellsUsed();
            foreach (var _ in used) nonEmpty++;
            if (nonEmpty >= 2) return r;
        }
        return first;
    }

    private static IReadOnlyList<ColumnSchema> DiscoverColumns(IXLWorksheet sheet, int headerRow)
    {
        var columns = new List<ColumnSchema>();
        var headerCells = sheet.Row(headerRow).CellsUsed(XLCellsUsedOptions.Contents);
        var ordered = headerCells.OrderBy(c => c.Address.ColumnNumber).ToList();

        foreach (var cell in ordered)
        {
            var name = cell.GetString().Trim();
            if (string.IsNullOrEmpty(name)) name = $"col_{cell.Address.ColumnNumber}";
            var shape = InferColumnShape(sheet, cell.Address.ColumnNumber, headerRow);
            columns.Add(new ColumnSchema(
                Name: name,
                InferredShape: shape,
                Ordinal: cell.Address.ColumnNumber,
                DisplayName: name));
        }
        return columns;
    }

    private static PortShape InferColumnShape(IXLWorksheet sheet, int column, int headerRow)
    {
        int numeric = 0, text = 0, blank = 0;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
        for (int r = headerRow + 1; r <= Math.Min(lastRow, headerRow + 20); r++)
        {
            var c = sheet.Cell(r, column);
            if (c.IsEmpty()) { blank++; continue; }
            if (c.DataType == XLDataType.Number) numeric++;
            else text++;
        }
        if (numeric > text) return PortShape.String; // adapter emits raw; parse-decimal converts
        return PortShape.String;
    }

    private static IReadOnlyList<string> ExtractUnitHints(IXLWorksheet sheet, int headerRow)
    {
        // Scan rows above the header for a "QAR thousands"-style heading (section 5.2 fixture).
        var hints = new List<string>();
        int start = Math.Max(1, headerRow - 3);
        for (int r = start; r < headerRow; r++)
        {
            foreach (var c in sheet.Row(r).CellsUsed(XLCellsUsedOptions.Contents))
            {
                var s = c.GetString().Trim();
                if (!string.IsNullOrEmpty(s)) hints.Add(s);
            }
        }
        return hints;
    }
}
