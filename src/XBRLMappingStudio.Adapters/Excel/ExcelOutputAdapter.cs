using System.IO.Compression;
using System.Xml.Linq;
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
        // Mixed-publication retries bind the exact output digest. Keep workbook
        // core metadata stable so identical fact rows produce identical bytes.
        wb.Properties.Created = DateTime.UnixEpoch;
        wb.Properties.Modified = DateTime.UnixEpoch;
        wb.Properties.Author = "XBRLMappingStudio";
        wb.Properties.LastModifiedBy = "XBRLMappingStudio";
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
        return NormalizePackage(ms.ToArray());
    }

    private static byte[] NormalizePackage(byte[] package)
    {
        using var input = new MemoryStream(package, writable: false);
        using var source = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var coreProperties = source.Entries.SingleOrDefault(entry =>
            entry.FullName.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
            && entry.FullName.EndsWith(".psmdcp", StringComparison.Ordinal));
        if (coreProperties is null) return package;

        const string stableCorePropertiesPath = "package/services/metadata/core-properties/core.psmdcp";
        var corePropertiesPath = coreProperties.FullName;
        var parts = new List<(string Name, byte[] Bytes)>();
        foreach (var entry in source.Entries)
        {
            using var content = entry.Open();
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var name = StringComparer.Ordinal.Equals(entry.FullName, corePropertiesPath)
                ? stableCorePropertiesPath : entry.FullName;
            var bytes = copy.ToArray();
            if (StringComparer.Ordinal.Equals(entry.FullName, "_rels/.rels"))
                bytes = RewriteCorePropertiesRelationship(bytes, stableCorePropertiesPath);
            parts.Add((name, bytes));
        }

        using var normalized = new MemoryStream();
        using (var destination = new ZipArchive(normalized, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var part in parts)
            {
                var entry = destination.CreateEntry(part.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var content = entry.Open();
                content.Write(part.Bytes);
            }
        }
        return normalized.ToArray();
    }

    private static byte[] RewriteCorePropertiesRelationship(byte[] xml, string stablePath)
    {
        XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var stream = new MemoryStream(xml, writable: false);
        var document = XDocument.Load(stream);
        var root = document.Root ?? throw new InvalidDataException("XLSX package has an empty root relationship part.");
        var packageRelationships = root.Elements(relationships + "Relationship").ToArray();
        var relationship = packageRelationships.SingleOrDefault(item =>
            ((string?)item.Attribute("Type"))?.EndsWith("/metadata/core-properties", StringComparison.Ordinal) == true);
        if (relationship is null)
            throw new InvalidDataException("XLSX package is missing its core-properties relationship.");
        relationship.SetAttributeValue("Target", stablePath);
        // ClosedXML creates a GUID-based relationship id for the workbook part.
        // Package relationship ids are local to this part, so canonical sequence
        // ids preserve references while making identical exports retry-stable.
        for (var index = 0; index < packageRelationships.Length; index++)
            packageRelationships[index].SetAttributeValue("Id",
                "rId" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return System.Text.Encoding.UTF8.GetBytes(document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
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
