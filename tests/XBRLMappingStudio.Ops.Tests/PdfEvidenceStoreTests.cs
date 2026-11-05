using System.Globalization;
using System.Text;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class PdfEvidenceStoreTests
{
    [Fact]
    public async Task CapturesImmutableEvidenceWithRotatedCroppedPageGeometry()
    {
        using var workspace = new TemporaryDirectory();
        var sourcePath = Path.Combine(workspace.Path, "cropped-rotated.pdf");
        var bytes = BuildCroppedRotatedPdf("1250000");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var extraction = PdfDigitalDocumentExtractor.Extract(bytes);
        var page = Assert.Single(extraction.Pages);
        var words = page.Words.Where(candidate => candidate.Text.Length == 1).ToArray();
        var region = Region(page, words);
        var database = new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db"));
        var store = new PdfEvidenceStore(database);

        var evidence = Assert.Single(await store.CaptureAsync(sourcePath, extraction,
            new[] { new PdfEvidenceCapture(region, "1250000") }, templateRevision: RunManifest.ComputeSha256("template-v1")));
        var resolved = store.Resolve(evidence.EvidenceId);

        Assert.Equal(PdfEvidenceResolutionState.Current, resolved.State);
        Assert.True(resolved.CanUseForMapping);
        Assert.Equal(1, resolved.Evidence.PageNumber);
        Assert.Equal(90, resolved.Evidence.PageRotationDegrees);
        Assert.Equal(new PdfDigitalPageBounds(0, 0, 612, 792), resolved.Evidence.MediaBounds);
        Assert.Equal(new PdfDigitalPageBounds(50, 100, 450, 700), resolved.Evidence.CropBounds);
        Assert.Equal(region, resolved.Evidence.Region);
        Assert.Equal("1250000", resolved.Evidence.ExtractedText);
        Assert.Equal(PdfEvidenceStore.DigitalExtractionMethod, resolved.Evidence.ExtractionMethod);
        Assert.Equal(RunManifest.ComputeSha256(bytes), resolved.Evidence.SourceSha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(resolved.Evidence.ImmutableDocumentPath));
    }

    [Fact]
    public async Task ChangedLivePdfReturnsBlockingStaleEvidenceButPreservesOriginalSnapshot()
    {
        using var workspace = new TemporaryDirectory();
        var sourcePath = Path.Combine(workspace.Path, "source.pdf");
        var originalBytes = BuildCroppedRotatedPdf("1250000");
        await File.WriteAllBytesAsync(sourcePath, originalBytes);
        var extraction = PdfDigitalDocumentExtractor.Extract(originalBytes);
        var page = Assert.Single(extraction.Pages);
        var words = page.Words.Where(candidate => candidate.Text.Length == 1).ToArray();
        var database = new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db"));
        var store = new PdfEvidenceStore(database);
        var reference = Assert.Single(await store.CaptureAsync(sourcePath, extraction,
            new[] { new PdfEvidenceCapture(Region(page, words), "1250000") }));

        await File.WriteAllBytesAsync(sourcePath, BuildCroppedRotatedPdf("999"));
        var resolution = store.Resolve(reference.EvidenceId);

        Assert.Equal(PdfEvidenceResolutionState.StaleSource, resolution.State);
        Assert.False(resolution.CanUseForMapping);
        Assert.Equal(DiagnosticCodes.IoPdfEvidenceStale, resolution.Diagnostic!.Code);
        Assert.True(resolution.Diagnostic.IsBlocking);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(reference.ImmutableDocumentPath));
    }

    [Fact]
    public async Task RefusesCaptureWhenSourceChangedAfterExtraction()
    {
        using var workspace = new TemporaryDirectory();
        var sourcePath = Path.Combine(workspace.Path, "source.pdf");
        var extractedBytes = BuildCroppedRotatedPdf("1250000");
        await File.WriteAllBytesAsync(sourcePath, extractedBytes);
        var extraction = PdfDigitalDocumentExtractor.Extract(extractedBytes);
        await File.WriteAllBytesAsync(sourcePath, BuildCroppedRotatedPdf("999"));
        var page = Assert.Single(extraction.Pages);
        var words = page.Words.Where(candidate => candidate.Text.Length == 1).ToArray();
        var store = new PdfEvidenceStore(new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db")));

        var error = await Assert.ThrowsAsync<AdapterException>(() => store.CaptureAsync(sourcePath, extraction,
            new[] { new PdfEvidenceCapture(Region(page, words), "1250000") }));

        Assert.Equal(DiagnosticCodes.IoPdfEvidenceStale, error.Code);
    }

    [Fact]
    public async Task RefusesTextThatDoesNotMatchTheDeclaredSourceRegion()
    {
        using var workspace = new TemporaryDirectory();
        var sourcePath = Path.Combine(workspace.Path, "source.pdf");
        var bytes = BuildCroppedRotatedPdf("1250000");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var extraction = PdfDigitalDocumentExtractor.Extract(bytes);
        var page = Assert.Single(extraction.Pages);
        var words = page.Words.Where(candidate => candidate.Text.Length == 1).ToArray();
        var store = new PdfEvidenceStore(new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db")));

        var error = await Assert.ThrowsAsync<AdapterException>(() => store.CaptureAsync(sourcePath, extraction,
            new[] { new PdfEvidenceCapture(Region(page, words), "1250001") }));

        Assert.Equal(DiagnosticCodes.IoPdfEvidenceReferenceInvalid, error.Code);
    }

    [Fact]
    public async Task TamperedManagedSnapshotFailsClosed()
    {
        using var workspace = new TemporaryDirectory();
        var sourcePath = Path.Combine(workspace.Path, "source.pdf");
        var bytes = BuildCroppedRotatedPdf("1250000");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var extraction = PdfDigitalDocumentExtractor.Extract(bytes);
        var page = Assert.Single(extraction.Pages);
        var words = page.Words.Where(candidate => candidate.Text.Length == 1).ToArray();
        var store = new PdfEvidenceStore(new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db")));
        var reference = Assert.Single(await store.CaptureAsync(sourcePath, extraction,
            new[] { new PdfEvidenceCapture(Region(page, words), "1250000") }));
        await File.WriteAllBytesAsync(reference.ImmutableDocumentPath, BuildCroppedRotatedPdf("999"));

        var resolution = store.Resolve(reference.EvidenceId);

        Assert.Equal(PdfEvidenceResolutionState.InvalidSnapshot, resolution.State);
        Assert.False(resolution.CanUseForMapping);
        Assert.Equal(DiagnosticCodes.IoPdfEvidenceSnapshotInvalid, resolution.Diagnostic!.Code);
        Assert.True(resolution.Diagnostic.IsBlocking);
    }

    [Fact]
    public async Task BatchEvidenceReconciliationChecksSourceRegionTextAndTemplateRevision()
    {
        using var workspace = new TemporaryDirectory();
        var sourcePath = Path.Combine(workspace.Path, "source.pdf");
        var bytes = BuildCroppedRotatedPdf("1250000");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var extraction = PdfDigitalDocumentExtractor.Extract(bytes);
        var page = Assert.Single(extraction.Pages);
        var region = Region(page, page.Words.Where(candidate => candidate.Text.Length == 1).ToArray());
        var templateRevision = RunManifest.ComputeSha256("template-v1");
        var store = new PdfEvidenceStore(new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db")));
        var reference = Assert.Single(await store.CaptureAsync(sourcePath, extraction,
            new[] { new PdfEvidenceCapture(region, "1250000") }, templateRevision));
        var expectation = new PdfEvidenceExpectation(reference.EvidenceId, region, templateRevision, "1250000");

        Assert.True(store.AreCurrent(new[] { expectation }, sourcePath, reference.SourceSha256));
        Assert.False(store.AreCurrent(new[] { expectation with { ExpectedExtractedText = "1250001" } },
            sourcePath, reference.SourceSha256));
        Assert.False(store.AreCurrent(new[] { expectation with { TemplateRevision = RunManifest.ComputeSha256("other-template") } },
            sourcePath, reference.SourceSha256));
        Assert.False(store.AreCurrent(new[] { expectation with { Region = region with { RightNormalized = 0.95 } } },
            sourcePath, reference.SourceSha256));
        Assert.False(store.AreCurrent(new[] { expectation }, sourcePath,
            "sha256:" + new string('0', 64)));
    }

    private static PdfEvidenceRegion Region(PdfDigitalPage page, PdfDigitalWord word)
        => Region(page, new[] { word });

    private static PdfEvidenceRegion Region(PdfDigitalPage page, IReadOnlyList<PdfDigitalWord> words)
        => new(page.PageNumber, words.Min(word => word.Left) / page.Width,
            words.Min(word => word.Bottom) / page.Height,
            words.Max(word => word.Right) / page.Width,
            words.Max(word => word.Top) / page.Height);

    private static byte[] BuildCroppedRotatedPdf(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var stream = $"BT\n/F1 12 Tf\n1 0 0 1 100 200 Tm\n({escaped}) Tj\nET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [50 100 450 700] /Rotate 90 /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream).ToString(CultureInfo.InvariantCulture)} >>\nstream\n{stream}endstream"
        };

        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length + 1];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index + 1] = Encoding.ASCII.GetByteCount(output.ToString());
            output.Append(index + 1).Append(" 0 obj\n")
                .Append(objects[index]).Append("\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append("xref\n0 ").Append(offsets.Length).Append("\n0000000000 65535 f \n");
        for (var index = 1; index < offsets.Length; index++)
            output.Append(offsets[index].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        output.Append("trailer\n<< /Root 1 0 R /Size ").Append(offsets.Length)
            .Append(" >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "xms-pdf-evidence-tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
