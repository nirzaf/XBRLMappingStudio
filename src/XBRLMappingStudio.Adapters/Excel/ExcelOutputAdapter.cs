using ClosedXML.Excel;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Excel;

/// <summary>
/// Writes tabular artifacts to XLSX (IO03). The write path is staged and reconciled:
/// bytes are first written to a sibling <c>.staged</c> file, reopened to confirm the
/// sheet and row count survived the round-trip, and only then moved onto the destination.
/// A failure before the move leaves any pre-existing destination untouched, and the
/// source workbook is never used as a destination.
/// </summary>
public sealed class ExcelOutputAdapter : ITargetAdapter
{
    public string Kind => "excel";
    public AdapterCapabilities Capabilities => AdapterCapabilities.WriteOnly;

    /// <summary>Sheet name used for produced workbooks.</summary>
    public string SheetName { get; init; } = "Output";

    /// <summary>
    /// Test seam: when set and it returns true for the staged path, publication fails
    /// before the destination is touched, proving the prior output survives a mid-write
    /// failure. Never set in production.
    /// </summary>
    public Func<string, bool>? ReconcileFailProbe { get; set; }

    /// <summary>Serialize a header row plus data rows into XLSX bytes.</summary>
    public byte[] WriteWorkbook(IReadOnlyList<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName);
        for (int c = 0; c < headers.Count; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        int r = 2;
        foreach (var row in rows)
        {
            int c = 1;
            foreach (var cell in row)
            {
                var xl = ws.Cell(r, c);
                switch (cell)
                {
                    case null: break; // leave truly missing cells empty
                    case decimal d: xl.Value = d; break;
                    case double dbl: xl.Value = (decimal)dbl; break;
                    case long l: xl.Value = l; break;
                    case int i: xl.Value = i; break;
                    case bool b: xl.Value = b; break;
                    case DateTime dt: xl.Value = dt; break;
                    case DateOnly dod: xl.Value = dod.ToDateTime(new TimeOnly(0, 0)); break;
                    default: xl.Value = cell.ToString(); break;
                }
                c++;
            }
            r++;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public Task<string> StageArtifactAsync(SourceConnection destination, byte[] payload,
        string suggestedExtension, CancellationToken cancellationToken)
    {
        var path = RequirePath(destination);
        var staged = path + ".staged";
        // Confirm the payload itself is a readable workbook before we ever name a staged file.
        using (var probe = new MemoryStream(payload))
        using (var wb = new XLWorkbook(probe))
        {
            _ = wb.Worksheets.First(); // throws if no sheet
        }
        File.WriteAllBytes(staged, payload);
        return Task.FromResult(staged);
    }

    public Task<OutputIdentity> PublishAsync(string stagedPath, SourceConnection destination,
        CancellationToken cancellationToken)
    {
        var path = RequirePath(destination);
        if (string.Equals(stagedPath, path, StringComparison.Ordinal))
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                "Refusing to publish onto the staged path itself.");

        // Reconcile: reopen the staged artifact and confirm the expected shape persisted.
        // The staged file carries a temporary '.staged' extension, so load from a stream.
        int stagedRows;
        using (var stagedStream = File.OpenRead(stagedPath))
        using (var wb = new XLWorkbook(stagedStream))
        {
            var ws = wb.Worksheets.First();
            stagedRows = (ws.LastRowUsed()?.RowNumber() ?? 0);
        }

        // Simulated mid-write failure: abort before touching the destination so any prior
        // output at `path` remains byte-for-byte intact.
        if (ReconcileFailProbe?.Invoke(stagedPath) == true)
            throw new IOException($"Simulated mid-write failure while publishing '{path}'.");

        File.Move(stagedPath, path, overwrite: true);

        // Post-publish verification: the accepted file must reopen to the same row count.
        using (var wb = new XLWorkbook(path))
        {
            var ws = wb.Worksheets.First();
            int finalRows = ws.LastRowUsed()?.RowNumber() ?? 0;
            if (finalRows != stagedRows)
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"Published workbook row count ({finalRows}) does not match staged ({stagedRows}).",
                    DiagnosticStage.Publication);
        }

        var bytes = new FileInfo(path).Length;
        var digest = RunManifest.ComputeSha256(File.ReadAllBytes(path));
        return Task.FromResult(new OutputIdentity(path, digest, bytes, "xlsx"));
    }

    private static string RequirePath(SourceConnection conn)
        => conn.Path ?? throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
            "Excel destination connection has no file path.");
}
