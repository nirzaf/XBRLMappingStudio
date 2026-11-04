using ClosedXML.Excel;
using XBRLMappingStudio.Adapters.Excel;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>IO03 — XLSX output: staged write is reopened and reconciled before the
/// destination is replaced; a mid-write failure preserves prior output and the source
/// workbook is never overwritten.</summary>
public sealed class ExcelOutputAdapterTests
{
    [Fact]
    public async Task StagePublish_ReopensAndReconciles()
    {
        using var ws = new TempWorkspace();
        var dest = new Adapters.SourceConnection("out", "excel", ws.PathFor("report.xlsx"));
        var ad = new ExcelOutputAdapter();

        var payload = ad.WriteWorkbook(
            new[] { "Name", "Amount" },
            new IEnumerable<object?>[]
            {
                new object?[] { "Revenue", 1250m },
                new object?[] { "Assets", 5000m },
            });

        var staged = await ad.StageArtifactAsync(dest, payload, ".xlsx", default);
        var id = await ad.PublishAsync(staged, dest, default);

        Assert.Equal("xlsx", id.Kind);
        Assert.True(File.Exists(ws.PathFor("report.xlsx")));

        using var wb = new XLWorkbook(ws.PathFor("report.xlsx"));
        var sh = wb.Worksheets.First();
        Assert.Equal("Revenue", sh.Cell(2, 1).GetString());
        Assert.Equal(1250m, sh.Cell(2, 2).GetValue<decimal>());
        Assert.Equal(5000m, sh.Cell(3, 2).GetValue<decimal>());
    }

    [Fact]
    public async Task MidWriteFailure_PreservesPriorOutput()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("report.xlsx");
        var dest = new Adapters.SourceConnection("out", "excel", path);

        // Seed a known accepted output.
        var seed = new ExcelOutputAdapter();
        var seedPayload = seed.WriteWorkbook(new[] { "A" },
            new IEnumerable<object?>[] { new object?[] { "old" } });
        var seedStaged = await seed.StageArtifactAsync(dest, seedPayload, ".xlsx", default);
        await seed.PublishAsync(seedStaged, dest, default);
        var before = File.ReadAllBytes(path);

        // New publication that fails during reconcile, before the move.
        var failing = new ExcelOutputAdapter { ReconcileFailProbe = _ => true };
        var payload2 = failing.WriteWorkbook(new[] { "A" },
            new IEnumerable<object?>[] { new object?[] { "new" } });
        var staged2 = await failing.StageArtifactAsync(dest, payload2, ".xlsx", default);

        await Assert.ThrowsAsync<IOException>(() => failing.PublishAsync(staged2, dest, default));

        var after = File.ReadAllBytes(path);
        Assert.Equal(before, after); // prior accepted output survives untouched
    }

    [Fact]
    public async Task StagePublish_PreservesLeadingZeroText()
    {
        using var ws = new TempWorkspace();
        var dest = new Adapters.SourceConnection("out", "excel", ws.PathFor("leading-zero.xlsx"));
        var adapter = new ExcelOutputAdapter();
        var payload = adapter.WriteWorkbook(
            new[] { "account", "value" },
            new IEnumerable<object?>[] { new object?[] { "00123", "-12.50" } });
        var staged = await adapter.StageArtifactAsync(dest, payload, ".xlsx", default);

        await adapter.PublishAsync(staged, dest, default);

        using var workbook = new XLWorkbook(ws.PathFor("leading-zero.xlsx"));
        var sheet = workbook.Worksheets.First();
        Assert.Equal("00123", sheet.Cell(2, 1).GetString());
        Assert.Equal("-12.50", sheet.Cell(2, 2).GetString());
    }

    [Fact]
    public async Task PublishToDistinctPath_LeavesSourceWorkbookUntouched()
    {
        using var ws = new TempWorkspace();
        var srcPath = ws.PathFor("source.xlsx");
        using (var wb = new XLWorkbook())
        {
            var s = wb.Worksheets.Add("Data");
            s.Cell(1, 1).Value = "keep-me";
            wb.SaveAs(srcPath);
        }
        var srcBefore = File.ReadAllBytes(srcPath);

        var dest = new Adapters.SourceConnection("out", "excel", ws.PathFor("out.xlsx"));
        var ad = new ExcelOutputAdapter();
        var staged = await ad.StageArtifactAsync(dest,
            ad.WriteWorkbook(new[] { "h" }, new IEnumerable<object?>[] { new object?[] { "v" } }),
            ".xlsx", default);
        await ad.PublishAsync(staged, dest, default);

        Assert.Equal(srcBefore, File.ReadAllBytes(srcPath)); // source never overwritten
        Assert.True(File.Exists(ws.PathFor("out.xlsx")));
    }
}
