using System.IO.Compression;
using System.Xml.Linq;
using ClosedXML.Excel;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class ExcelSourceQualificationTests
{
    [Fact]
    public async Task ExplicitRangeRestrictsColumnsAndRowsAndAppliesHiddenRowPolicy()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("qualified.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Financials");
            sheet.Cell("A1").Value = "Ignored title";
            sheet.Cell("B3").Value = "FY2025 QAR thousands";
            sheet.Cell("B4").Value = "Current";
            sheet.Cell("C4").Value = "Prior";
            sheet.Cell("B5").Value = 100;
            sheet.Cell("C5").Value = 80;
            sheet.Cell("B6").Value = 200;
            sheet.Cell("C6").Value = 150;
            sheet.Cell("B7").Value = 999;
            sheet.Cell("C7").Value = 888;
            sheet.Row(5).Hide();
            workbook.SaveAs(path);
        }

        var connection = new SourceConnection("ledger", "excel", path,
            new Dictionary<string, string>
            {
                ["sheet"] = "Financials",
                ["range"] = "B3:C6",
                ["headerRow"] = "4",
                ["hiddenRowPolicy"] = "exclude"
            });
        var adapter = new ExcelAdapter();
        var schema = (ExcelSchema)await adapter.InspectSchemaAsync(connection, CancellationToken.None);
        var rows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var record in adapter.ReadFullAsync(connection, schema, CancellationToken.None)) rows.Add(record);

        Assert.Equal(new[] { "Current", "Prior" }, schema.Columns.Select(column => column.Name));
        Assert.NotNull(schema.RegionAddress);
        Assert.Contains("B3", schema.RegionAddress, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C6", schema.RegionAddress, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExcelAdapter.HiddenRowPolicy.Exclude, schema.HiddenRowPolicy);
        var row = Assert.Single(rows);
        Assert.Equal("Financials#r6", row.RowId);
        Assert.Equal(200m, row["Current"].AsDecimal());
        Assert.Equal(150m, row["Prior"].AsDecimal());

        var rejectConfiguration = connection.Configuration!.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        rejectConfiguration["hiddenRowPolicy"] = "reject";
        var rejecting = connection with { Configuration = rejectConfiguration };
        var rejectSchema = await adapter.InspectSchemaAsync(rejecting, CancellationToken.None);
        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(rejecting, rejectSchema, CancellationToken.None)) { }
        });
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
        Assert.Contains("Hidden data row 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedRangeSelectsItsWorksheetAndBlocksRangeDrift()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("named-template.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var cover = workbook.Worksheets.Add("Cover");
            cover.Cell("A1").Value = "Wrong first-sheet data";
            cover.Cell("A2").Value = "99";
            var sheet = workbook.Worksheets.Add("Financials");
            sheet.Cell("B3").Value = "FY2025 QAR thousands";
            sheet.Cell("B4").Value = "Current";
            sheet.Cell("C4").Value = "Prior";
            sheet.Cell("B5").Value = 100;
            sheet.Cell("C5").Value = 80;
            sheet.Cell("B6").Value = 200;
            sheet.Cell("C6").Value = 150;
            workbook.DefinedNames.Add("AnnualFinancials", sheet.Range("B3:C6"));
            workbook.SaveAs(path);
        }

        var connection = new SourceConnection("ledger", "excel", path,
            new Dictionary<string, string> { ["namedRange"] = "AnnualFinancials", ["headerRow"] = "4" });
        var adapter = new ExcelAdapter();
        var schema = (ExcelSchema)await adapter.InspectSchemaAsync(connection, CancellationToken.None);
        var records = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var record in adapter.ReadFullAsync(connection, schema, CancellationToken.None)) records.Add(record);

        Assert.Equal("Financials", schema.SheetName);
        Assert.Equal(new[] { "Current", "Prior" }, schema.Columns.Select(column => column.Name));
        Assert.Contains("AnnualFinancials", schema.RegionAddress, StringComparison.Ordinal);
        Assert.Equal(new[] { 100m, 200m }, records.Select(record => record["Current"].AsDecimal()));
        Assert.All(records, record => Assert.StartsWith("Financials!", record.CellSources!["Current"].Locator, StringComparison.Ordinal));

        using (var workbook = new XLWorkbook(path))
        {
            workbook.DefinedNames.Delete("AnnualFinancials");
            workbook.DefinedNames.Add("AnnualFinancials", workbook.Worksheet("Financials").Range("B3:C5"));
            workbook.Save();
        }

        var drift = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, CancellationToken.None)) { }
        });
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, drift.Code);

        var missingName = connection with
        {
            Configuration = new Dictionary<string, string> { ["namedRange"] = "MissingTemplate", ["headerRow"] = "1" }
        };
        var missing = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(missingName, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, missing.Code);

        var mismatchedSheet = connection with
        {
            Configuration = new Dictionary<string, string>
            {
                ["namedRange"] = "AnnualFinancials",
                ["sheet"] = "Cover",
                ["headerRow"] = "4"
            }
        };
        var sheetMismatch = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(mismatchedSheet, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, sheetMismatch.Code);

        var conflictingRegion = connection with
        {
            Configuration = new Dictionary<string, string>
            {
                ["namedRange"] = "AnnualFinancials",
                ["range"] = "B3:C6",
                ["headerRow"] = "4"
            }
        };
        var conflict = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(conflictingRegion, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, conflict.Code);
    }

    [Fact]
    public async Task UniqueSheetScopedNamedRangeResolvesAndHonorsItsConfiguredWorksheet()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("sheet-scoped-name.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Financials");
            sheet.Cell("A1").Value = "Current";
            sheet.Cell("A2").Value = 125;
            sheet.DefinedNames.Add("CurrentYear", sheet.Range("A1:A2"));
            workbook.SaveAs(path);
        }

        var adapter = new ExcelAdapter();
        var unresolved = new SourceConnection("ledger", "excel", path,
            new Dictionary<string, string> { ["namedRange"] = "CurrentYear", ["headerRow"] = "1" });
        var inferredSchema = (ExcelSchema)await adapter.InspectSchemaAsync(unresolved, CancellationToken.None);
        Assert.Equal("Financials", inferredSchema.SheetName);
        var inferredRecords = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var record in adapter.ReadFullAsync(unresolved, inferredSchema, CancellationToken.None)) inferredRecords.Add(record);
        Assert.Equal(125m, Assert.Single(inferredRecords)["Current"].AsDecimal());

        var configured = unresolved with
        {
            Configuration = new Dictionary<string, string>
            {
                ["namedRange"] = "CurrentYear",
                ["sheet"] = "Financials",
                ["headerRow"] = "1"
            }
        };
        var schema = (ExcelSchema)await adapter.InspectSchemaAsync(configured, CancellationToken.None);
        var records = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var record in adapter.ReadFullAsync(configured, schema, CancellationToken.None)) records.Add(record);

        Assert.Equal("Financials", schema.SheetName);
        Assert.Equal(125m, Assert.Single(records)["Current"].AsDecimal());
    }

    [Fact]
    public async Task ChangedHeadingOrFormulaCacheInvalidatesInspectedSchema()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("drift.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Financials");
            sheet.Cell("A1").Value = "FY2025 QAR thousands";
            sheet.Cell("A2").Value = "Revenue";
            sheet.Cell("A3").Value = 10;
            sheet.Cell("B2").Value = "Calculated";
            sheet.Cell("B3").FormulaA1 = "1+1";
            workbook.RecalculateAllFormulas();
            workbook.CalculateMode = XLCalculateMode.Manual;
            workbook.SaveAs(path);
        }

        var connection = new SourceConnection("ledger", "excel", path,
            new Dictionary<string, string> { ["sheet"] = "Financials", ["headerRow"] = "2" });
        var adapter = new ExcelAdapter();
        var original = await adapter.InspectSchemaAsync(connection, CancellationToken.None);

        ChangeFormulaCache(path, "B3", "3");
        var cacheChanged = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
        Assert.NotEqual(original.Fingerprint, cacheChanged.Fingerprint);
        var cacheDrift = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, original, CancellationToken.None)) { }
        });
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, cacheDrift.Code);

        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet("Financials").Cell("A1").Value = "FY2026 QAR units";
            workbook.Save();
        }
        var headingChanged = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
        Assert.NotEqual(cacheChanged.Fingerprint, headingChanged.Fingerprint);
        var headingDrift = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, cacheChanged, CancellationToken.None)) { }
        });
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, headingDrift.Code);
    }

    [Fact]
    public async Task ConfiguredFormulaPolicyBlocksCachedValuesAtTheirCell()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor("formula.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Data");
            sheet.Cell("A1").Value = "Amount";
            sheet.Cell("A2").FormulaA1 = "1+1";
            workbook.RecalculateAllFormulas();
            workbook.CalculateMode = XLCalculateMode.Manual;
            workbook.SaveAs(path);
        }

        var connection = new SourceConnection("s", "excel", path,
            new Dictionary<string, string> { ["sheet"] = "Data", ["headerRow"] = "1", ["formulaPolicy"] = "block-on-formula" });
        var adapter = ExcelAdapter.FromConnection(connection);
        var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);

        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, CancellationToken.None)) { }
        });
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
        Assert.Contains("Formula cache not permitted at Data!A2", error.Message, StringComparison.Ordinal);
    }

    private static void ChangeFormulaCache(string workbookPath, string address, string value)
    {
        const string sheetEntryName = "xl/worksheets/sheet1.xml";
        using var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(sheetEntryName)
            ?? throw new InvalidOperationException("The test workbook worksheet XML is missing.");
        XDocument document;
        using (var stream = entry.Open()) document = XDocument.Load(stream);
        var ns = document.Root!.Name.Namespace;
        var cell = document.Descendants(ns + "c")
            .Single(element => StringComparer.Ordinal.Equals((string?)element.Attribute("r"), address));
        var cachedValue = cell.Element(ns + "v");
        if (cachedValue is null) cell.Add(new XElement(ns + "v", value));
        else cachedValue.Value = value;
        entry.Delete();
        var updated = archive.CreateEntry(sheetEntryName, CompressionLevel.Optimal);
        using var updatedStream = updated.Open();
        document.Save(updatedStream);
    }
}
