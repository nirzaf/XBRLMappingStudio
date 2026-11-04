using ClosedXML.Excel;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>
/// IO03 — worksheet selection. When a workbook connection explicitly names a worksheet,
/// that sheet must exist: silently reading the first sheet (as a permissive fallback)
/// would import the wrong data without warning. When no sheet is named, the first sheet
/// is used as the documented default.
/// </summary>
public sealed class ExcelWorksheetSelectionTests
{
    private static string BuildWorkbook(TempWorkspace ws)
    {
        var path = ws.PathFor("book.xlsx"); // ClosedXML requires the .xlsx extension.
        using var wb = new XLWorkbook();
        var summary = wb.Worksheets.Add("Summary");
        summary.Cell(1, 1).Value = "S1";
        summary.Cell(1, 2).Value = "S2";
        summary.Cell(2, 1).Value = 1;
        summary.Cell(2, 2).Value = 2;

        var fin = wb.Worksheets.Add("Financials");
        fin.Cell(1, 1).Value = "Current";
        fin.Cell(1, 2).Value = "Prior";
        fin.Cell(2, 1).Value = 10;
        fin.Cell(2, 2).Value = 20;

        wb.SaveAs(path);
        return path;
    }

    private static SourceConnection Conn(string path, string? sheet)
    {
        var cfg = new Dictionary<string, string> { ["headerRow"] = "1" };
        if (sheet is not null) cfg["sheet"] = sheet;
        return new SourceConnection("b1", "excel", path, cfg);
    }

    [Fact]
    public async Task ConfiguredSheetPresent_IsUsed()
    {
        using var ws = new TempWorkspace();
        var path = BuildWorkbook(ws);
        var adapter = new ExcelAdapter();

        var schema = await adapter.InspectSchemaAsync(Conn(path, "Financials"), CancellationToken.None);

        Assert.Contains(schema.Columns, c => c.Name == "Current");
        Assert.Contains(schema.Columns, c => c.Name == "Prior");
        Assert.DoesNotContain(schema.Columns, c => c.Name == "S1");
    }

    [Fact]
    public async Task ConfiguredSheetMissing_IsRejected()
    {
        using var ws = new TempWorkspace();
        var path = BuildWorkbook(ws);
        var adapter = new ExcelAdapter();

        var ex = await Assert.ThrowsAsync<AdapterException>(() =>
            adapter.InspectSchemaAsync(Conn(path, "NoSuchSheet"), CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoSheetMissing, ex.Code);
    }

    [Fact]
    public async Task NoSheetConfigured_DefaultsToFirstSheet()
    {
        using var ws = new TempWorkspace();
        var path = BuildWorkbook(ws);
        var adapter = new ExcelAdapter();

        var schema = await adapter.InspectSchemaAsync(Conn(path, sheet: null), CancellationToken.None);

        // "Summary" is the first worksheet.
        Assert.Contains(schema.Columns, c => c.Name == "S1");
        Assert.Contains(schema.Columns, c => c.Name == "S2");
    }
}
