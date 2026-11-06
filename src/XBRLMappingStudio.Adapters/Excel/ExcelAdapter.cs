using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Excel;

/// <summary>
/// Reads ordinary XLSX workbooks (IO02). Discovery uses an explicitly configured
/// worksheet, named range, A1 region, or the documented first-sheet default when
/// none is configured. A requested worksheet or named range must resolve exactly;
/// it never falls back to another sheet. Cell coordinates are preserved on every
/// value for provenance.
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

    public enum HiddenRowPolicy
    {
        Include,
        Exclude,
        Reject
    }

    private readonly FormulaPolicy _policy;

    public ExcelAdapter(FormulaPolicy policy = FormulaPolicy.AcceptCached)
    {
        _policy = policy;
    }

    public static ExcelAdapter FromConnection(SourceConnection connection)
    {
        var configured = connection.Configuration is not null
            && connection.Configuration.TryGetValue("formulaPolicy", out var value)
            ? value
            : null;
        var policy = configured?.Trim().ToLowerInvariant() switch
        {
            null or "" or "accept-cached" => FormulaPolicy.AcceptCached,
            "block" or "block-on-formula" => FormulaPolicy.BlockOnFormula,
            _ => throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Unsupported Excel formulaPolicy '{configured}'. Use 'accept-cached' or 'block-on-formula'.",
                DiagnosticStage.Validation)
        };
        return new ExcelAdapter(policy);
    }

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var path = RequirePath(connection);
        using var workbook = new XLWorkbook(path);
        return Task.FromResult<ISourceSchema>(CreateSchema(workbook, connection));
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
        if (schema is not ExcelSchema excelSchema)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "Excel source was given an incompatible schema description.", DiagnosticStage.Validation);
        var path = RequirePath(connection);
        using var workbook = new XLWorkbook(path);
        var currentSchema = CreateSchema(workbook, connection);
        if (!StringComparer.Ordinal.Equals(currentSchema.Fingerprint, excelSchema.Fingerprint))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "The Excel source changed after schema inspection. Inspect and remap before reading or publishing.",
                DiagnosticStage.Validation);
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name == excelSchema.SheetName)
            ?? throw new InvalidOperationException(
                $"Sheet '{excelSchema.SheetName}' not found in workbook {path}.");

        for (int rowIdx = excelSchema.FirstDataRow; rowIdx <= excelSchema.LastDataRow; rowIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xlRow = sheet.Row(rowIdx);
            if (xlRow.IsEmpty()) continue;
            if (xlRow.IsHidden)
            {
                if (excelSchema.HiddenRowPolicy == HiddenRowPolicy.Exclude) continue;
                if (excelSchema.HiddenRowPolicy == HiddenRowPolicy.Reject)
                    throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                        $"Hidden data row {rowIdx} is not permitted by the configured Excel source policy.",
                        DiagnosticStage.Validation);
            }

            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var cellSources = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
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

    private static ExcelSchema CreateSchema(XLWorkbook workbook, SourceConnection connection)
    {
        if (HasConfiguredValue(connection, "range") && HasConfiguredValue(connection, "namedRange"))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "Configure either an Excel A1 range or a namedRange, not both.",
                DiagnosticStage.Introspection);

        var sheet = SelectSheet(workbook, connection);
        var region = SelectRegion(workbook, sheet, connection);
        int? explicitHeader = null;
        if (connection.Configuration is not null
            && connection.Configuration.TryGetValue("headerRow", out var headerText))
        {
            if (!int.TryParse(headerText, NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row <= 0)
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"Configured Excel headerRow '{headerText}' must be a positive row number.", DiagnosticStage.Introspection);
            explicitHeader = row;
        }
        var headerRow = explicitHeader ?? DetectHeaderRow(sheet, region);
        if (headerRow < region.FirstRow || headerRow > region.LastRow)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Excel header row {headerRow} is outside the selected source region.", DiagnosticStage.Introspection);

        var lastDataRow = region.LastRow;
        var columns = DiscoverColumns(sheet, headerRow, region.FirstColumn, region.LastColumn);
        if (columns.Count == 0)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Excel worksheet '{sheet.Name}' has no column headers in the selected region.", DiagnosticStage.Introspection);
        var unitHints = ExtractUnitHints(sheet, headerRow, region);
        var hiddenRowPolicy = ResolveHiddenRowPolicy(connection);
        var formulaCacheFingerprint = FormulaCacheFingerprint(sheet, region.FirstRow, lastDataRow, columns);
        // The fingerprint includes headings, the explicit row/column region, formula
        // expressions and cached formula results. A stale cache therefore blocks at
        // schema verification before a report can be published.
        var fingerprint = ExcelSchema.ComputeFingerprint(
            sheet.Name, columns, unitHints, region.Address, hiddenRowPolicy, formulaCacheFingerprint);
        return new ExcelSchema(
            SourceId: connection.SourceId,
            SheetName: sheet.Name,
            HeaderRow: headerRow,
            FirstDataRow: headerRow + 1,
            LastDataRow: lastDataRow,
            Columns: columns,
            UnitHeadingHints: unitHints,
            Fingerprint: fingerprint,
            RegionAddress: region.Address,
            HiddenRowPolicy: hiddenRowPolicy);
    }

    private sealed record ExcelRegion(int FirstRow, int FirstColumn, int LastRow, int LastColumn, string? Address);

    private static ExcelRegion SelectRegion(XLWorkbook workbook, IXLWorksheet sheet, SourceConnection connection)
    {
        if (TryGetConfiguredValue(connection, "namedRange", out var namedRangeName))
        {
            var preferSheetScoped = HasConfiguredValue(connection, "sheet");
            var namedRange = ResolveNamedRange(workbook, sheet, namedRangeName, preferSheetScoped);
            var bounds = namedRange.RangeAddress;
            var namedRangeAddress = $"named-range:{namedRangeName}@{namedRange.Worksheet.Name}!{bounds}";
            return new ExcelRegion(bounds.FirstAddress.RowNumber, bounds.FirstAddress.ColumnNumber,
                bounds.LastAddress.RowNumber, bounds.LastAddress.ColumnNumber, namedRangeAddress);
        }

        if (connection.Configuration is not null
            && connection.Configuration.TryGetValue("range", out var address)
            && !string.IsNullOrWhiteSpace(address))
        {
            if (address.Contains('!') || address.Contains(',') || address.Contains('[') || address.Contains(']'))
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    "Excel range must be one contiguous A1 region within the selected worksheet.",
                    DiagnosticStage.Introspection);
            try
            {
                var range = sheet.Range(address);
                var bounds = range.RangeAddress;
                return new ExcelRegion(bounds.FirstAddress.RowNumber, bounds.FirstAddress.ColumnNumber,
                    bounds.LastAddress.RowNumber, bounds.LastAddress.ColumnNumber, bounds.ToString());
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"Configured Excel range '{address}' is invalid for worksheet '{sheet.Name}'.",
                    DiagnosticStage.Introspection);
            }
        }

        var used = sheet.RangeUsed(XLCellsUsedOptions.Contents);
        if (used is null) return new ExcelRegion(1, 1, 1, 1, null);
        var usedAddress = used.RangeAddress;
        return new ExcelRegion(usedAddress.FirstAddress.RowNumber, usedAddress.FirstAddress.ColumnNumber,
            usedAddress.LastAddress.RowNumber, usedAddress.LastAddress.ColumnNumber, null);
    }

    private static HiddenRowPolicy ResolveHiddenRowPolicy(SourceConnection connection)
    {
        var configured = connection.Configuration is not null
            && connection.Configuration.TryGetValue("hiddenRowPolicy", out var value)
            ? value
            : null;
        return configured?.Trim().ToLowerInvariant() switch
        {
            null or "" or "include" => HiddenRowPolicy.Include,
            "exclude" => HiddenRowPolicy.Exclude,
            "reject" => HiddenRowPolicy.Reject,
            _ => throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Unsupported Excel hiddenRowPolicy '{configured}'. Use 'include', 'exclude' or 'reject'.",
                DiagnosticStage.Introspection)
        };
    }

    private static string FormulaCacheFingerprint(
        IXLWorksheet sheet, int firstDataRow, int lastDataRow, IReadOnlyList<ColumnSchema> columns)
    {
        var builder = new StringBuilder();
        for (var row = firstDataRow; row <= lastDataRow; row++)
        foreach (var column in columns)
        {
            var cell = sheet.Cell(row, column.Ordinal);
            if (!cell.HasFormula) continue;
            AppendPart(builder, cell.Address.ToString() ?? string.Empty);
            AppendPart(builder, cell.FormulaA1 ?? string.Empty);
            var cachedValue = cell.CachedValue;
            var lexicalValue = cachedValue.Type switch
            {
                XLDataType.Text => cachedValue.GetText(),
                XLDataType.Number => cachedValue.GetNumber().ToString("R", CultureInfo.InvariantCulture),
                XLDataType.Boolean => cachedValue.GetBoolean() ? "true" : "false",
                XLDataType.DateTime => cachedValue.GetDateTime().ToString("O", CultureInfo.InvariantCulture),
                XLDataType.TimeSpan => cachedValue.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
                XLDataType.Error => cachedValue.GetError().ToString(),
                _ => string.Empty
            };
            AppendPart(builder, lexicalValue);
        }
        return XBRLMappingStudio.Diagnostics.RunManifest.ComputeSha256(builder.ToString());
    }

    private static void AppendPart(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private DataValue ReadCellValue(IXLCell cell, SourceReference src)
    {
        if (cell.HasFormula && _policy == FormulaPolicy.BlockOnFormula)
        {
            // Per section 4: report the exact affected cell and require review.
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Formula cache not permitted at {src}.", DiagnosticStage.Validation);
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
        if (TryGetConfiguredValue(conn, "namedRange", out var namedRangeName)
            && !HasConfiguredValue(conn, "sheet"))
            return ResolveNamedRange(wb, null, namedRangeName, preferSheetScoped: false).Worksheet;

        if (conn.Configuration is not null
            && conn.Configuration.TryGetValue("sheet", out var name)
            && !string.IsNullOrWhiteSpace(name))
        {
            // An explicitly configured worksheet must exist. Silently reading the first
            // sheet when the operator named a specific one would import the wrong data
            // without warning (section 6: reject an unsupported input rather than lose
            // information), so this is a blocking introspection error.
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name == name);
            if (ws is null)
                throw new AdapterException(DiagnosticCodes.IoSheetMissing,
                    $"Configured worksheet '{name}' was not found in workbook '{conn.Path}'.", DiagnosticStage.Introspection);
            return ws;
        }
        // No worksheet requested: the first sheet is a defined, documented default.
        return wb.Worksheets.First();
    }

    private static IXLRange ResolveNamedRange(
        XLWorkbook workbook,
        IXLWorksheet? selectedSheet,
        string name,
        bool preferSheetScoped)
    {
        IXLDefinedName? definedName = null;
        if (preferSheetScoped && selectedSheet is not null)
            definedName = FindDefinedName(selectedSheet.DefinedNames, name);
        definedName ??= FindDefinedName(workbook.DefinedNames, name);
        if (definedName is null && selectedSheet is not null)
            definedName = FindDefinedName(selectedSheet.DefinedNames, name);

        if (definedName is null && selectedSheet is null)
        {
            var sheetDefinitions = workbook.Worksheets
                .Select(worksheet => FindDefinedName(worksheet.DefinedNames, name))
                .Where(candidate => candidate is not null)
                .Cast<IXLDefinedName>()
                .ToArray();
            if (sheetDefinitions.Length > 1)
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"Excel named range '{name}' exists on multiple worksheets; configure the sheet explicitly.",
                    DiagnosticStage.Introspection);
            definedName = sheetDefinitions.SingleOrDefault();
        }

        if (definedName is null)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Configured Excel named range '{name}' was not found in the workbook or selected worksheet.",
                DiagnosticStage.Introspection);
        if (!definedName.IsValid)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Configured Excel named range '{name}' contains an invalid or external reference.",
                DiagnosticStage.Introspection);

        IXLRange[] ranges;
        try
        {
            ranges = definedName.Ranges.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Configured Excel named range '{name}' is not a supported cell range.",
                DiagnosticStage.Introspection);
        }

        if (ranges.Length != 1)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Configured Excel named range '{name}' must refer to one contiguous cell range.",
                DiagnosticStage.Introspection);
        if (selectedSheet is not null
            && !StringComparer.Ordinal.Equals(ranges[0].Worksheet.Name, selectedSheet.Name))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Configured Excel named range '{name}' refers to worksheet '{ranges[0].Worksheet.Name}', not '{selectedSheet.Name}'.",
                DiagnosticStage.Introspection);
        return ranges[0];
    }

    private static IXLDefinedName? FindDefinedName(IXLDefinedNames definedNames, string name)
    {
        try { return definedNames.DefinedName(name); }
        catch (KeyNotFoundException) { return null; }
    }

    private static bool HasConfiguredValue(SourceConnection connection, string key)
        => TryGetConfiguredValue(connection, key, out _);

    private static bool TryGetConfiguredValue(SourceConnection connection, string key, out string value)
    {
        value = string.Empty;
        if (connection.Configuration is null
            || !connection.Configuration.TryGetValue(key, out var configured)
            || string.IsNullOrWhiteSpace(configured))
            return false;
        value = configured.Trim();
        return true;
    }

    private static int DetectHeaderRow(IXLWorksheet sheet, ExcelRegion region)
    {
        var minimumNonEmpty = Math.Min(2, region.LastColumn - region.FirstColumn + 1);
        for (int r = region.FirstRow; r <= region.LastRow; r++)
        {
            int nonEmpty = 0;
            for (var column = region.FirstColumn; column <= region.LastColumn; column++)
                if (!sheet.Cell(r, column).IsEmpty()) nonEmpty++;
            if (nonEmpty >= minimumNonEmpty) return r;
        }
        return region.FirstRow;
    }

    private static IReadOnlyList<ColumnSchema> DiscoverColumns(
        IXLWorksheet sheet, int headerRow, int firstColumn, int lastColumn)
    {
        var columns = new List<ColumnSchema>();
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            var cell = sheet.Cell(headerRow, column);
            if (cell.IsEmpty()) continue;
            var name = cell.GetString().Trim();
            if (string.IsNullOrEmpty(name)) name = $"col_{column}";
            var shape = InferColumnShape(sheet, column, headerRow);
            columns.Add(new ColumnSchema(
                Name: name,
                InferredShape: shape,
                Ordinal: column,
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

    private static IReadOnlyList<string> ExtractUnitHints(IXLWorksheet sheet, int headerRow, ExcelRegion region)
    {
        // Scan up to three rows above the header for year/unit/scale anchors, but keep
        // the scan inside the explicitly selected region when one is configured.
        var hints = new List<string>();
        int start = Math.Max(region.FirstRow, headerRow - 3);
        for (int r = start; r < headerRow; r++)
        {
            for (var column = region.FirstColumn; column <= region.LastColumn; column++)
            {
                var c = sheet.Cell(r, column);
                if (c.IsEmpty()) continue;
                var s = c.GetString().Trim();
                if (!string.IsNullOrEmpty(s)) hints.Add(s);
            }
        }
        return hints;
    }
}
