using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using System.Diagnostics;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Worker;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class PdfExtractionWorkerTests
{
    [Theory]
    [InlineData("xbrl-studio.dll")]
    [InlineData("XBRLMappingStudio.Desktop.dll")]
    public async Task IsolatedWorker_ExtractsExactBytesAndReturnsPageEvidence(string hostAssembly)
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-pdf-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "synthetic.pdf");
            var sourceBytes = BuildPdf();
            await File.WriteAllBytesAsync(path, sourceBytes);

            var hostPath = Path.Combine(AppContext.BaseDirectory, hostAssembly);
            Assert.True(File.Exists(hostPath), $"Expected worker host at '{hostPath}'.");
            var invocation = new PdfWorkerInvocation(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                new[] { hostPath });

            var result = await new PdfExtractionProcessClient().ExtractFileAsync(
                path,
                deadline: TimeSpan.FromSeconds(30),
                invocation: invocation);

            Assert.Equal(sourceBytes.Length, result.SourceByteLength);
            var page = Assert.Single(result.Pages);
            Assert.Equal(1, page.PageNumber);
            Assert.Contains("Revenue", page.Text, StringComparison.Ordinal);
            Assert.Contains(page.Words, word => word.Text == "2025");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task OversizedPdf_IsRejectedBeforeTheWorkerStarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-pdf-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "too-large.pdf");
            await File.WriteAllBytesAsync(path, BuildPdf());

            var error = await Assert.ThrowsAsync<AdapterException>(() =>
                new PdfExtractionProcessClient().ExtractFileAsync(path,
                    new PdfDigitalExtractionOptions(MaxFileBytes: 32)));

            Assert.Equal(DiagnosticCodes.IoPdfBudgetExceeded, error.Code);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task IsolatedWorker_PreservesMalformedPdfDiagnosticStage()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-pdf-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "invalid.pdf");
            await File.WriteAllTextAsync(path, "not a PDF document");
            var hostPath = Path.Combine(AppContext.BaseDirectory, "xbrl-studio.dll");
            var invocation = new PdfWorkerInvocation(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                new[] { hostPath });

            var error = await Assert.ThrowsAsync<AdapterException>(() =>
                new PdfExtractionProcessClient().ExtractFileAsync(path, invocation: invocation));

            Assert.Equal(DiagnosticCodes.IoPdfInvalid, error.Code);
            Assert.Equal(DiagnosticStage.Introspection, error.Stage);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task WorkerDeadline_KillsAnUnresponsiveParserProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-pdf-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "synthetic.pdf");
            await File.WriteAllBytesAsync(path, BuildPdf());
            var invocation = OperatingSystem.IsWindows()
                ? new PdfWorkerInvocation("powershell.exe",
                    new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" },
                    AppendInternalCommand: false)
                : new PdfWorkerInvocation("/bin/sh", new[] { "-c", "sleep 30" },
                    AppendInternalCommand: false);
            var timer = Stopwatch.StartNew();

            var error = await Assert.ThrowsAsync<AdapterException>(() =>
                new PdfExtractionProcessClient().ExtractFileAsync(path,
                    deadline: TimeSpan.FromMilliseconds(400), invocation: invocation));

            Assert.Equal(DiagnosticCodes.WkrTimeout, error.Code);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"worker deadline took {timer.Elapsed}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static byte[] BuildPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Revenue 2025 QAR 1250000", 12, new PdfPoint(72, 720), font);
        return builder.Build();
    }
}
