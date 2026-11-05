using System.Text;
using System.Security.Cryptography;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class PdfPigQualificationTests
{
    [Fact]
    public void AuthorOwnedDigitalPdf_ExposesRepeatableTextAndPageCoordinates()
    {
        var pdf = BuildPdf((page, font) =>
        {
            page.AddText("Revenue", 12, new PdfPoint(72, 720), font);
            page.AddText("2025 QAR 1250000", 10, new PdfPoint(72, 700), font);
        });

        var first = ReadSnapshot(pdf);
        var second = ReadSnapshot(pdf);

        Assert.Equal(first, second);
        Assert.Contains(first, letter => letter.Value == "R" && letter.Left >= 72 && letter.Bottom >= 700);
        Assert.Contains("Revenue", ReadWords(pdf));
        Assert.Contains("1250000", ReadWords(pdf));
    }

    [Fact]
    public void RotatedText_ExposesRotatedBaselinesAndStableCoordinates()
    {
        var pdf = BuildRotatedPdf("ROTATED");

        var first = ReadSnapshot(pdf);
        var second = ReadSnapshot(pdf);

        Assert.Equal(first, second);
        Assert.Contains(first, letter => Math.Abs(letter.StartX - letter.EndX) > 0.01
            || Math.Abs(letter.StartY - letter.EndY) > 0.01);
        Assert.Equal("ADEORTT", new string(first.SelectMany(letter => letter.Value)
            .OrderBy(character => character).ToArray()));
    }

    [Fact]
    public void MultiColumnProbe_PreservesDistinctWordCoordinatesInsteadOfAssumingTextFlow()
    {
        var pdf = BuildPdf((page, font) =>
        {
            // Deliberately write the right column first and interleave later lines.
            page.AddText("2026 900", 10, new PdfPoint(340, 720), font);
            page.AddText("Revenue 2025", 10, new PdfPoint(72, 720), font);
            page.AddText("Expenses 2024", 10, new PdfPoint(72, 690), font);
            page.AddText("Costs 2025", 10, new PdfPoint(340, 690), font);
        });

        var words = ReadWordSnapshot(pdf);

        Assert.Contains(words, word => word.Text == "Revenue" && word.Left < 100);
        Assert.Contains(words, word => word.Text == "2026" && word.Left > 300);
        Assert.Contains(words, word => word.Text == "Expenses" && word.Left < 100);
        Assert.Contains(words, word => word.Text == "Costs" && word.Left > 300);
        Assert.Equal(words, ReadWordSnapshot(pdf));
    }

    [Fact]
    public void InvalidPdf_IsRejectedByThePinnedParser()
    {
        var error = Assert.Throws<AdapterException>(() =>
            PdfDigitalDocumentExtractor.Extract(Encoding.ASCII.GetBytes("not a pdf")));
        Assert.Equal(DiagnosticCodes.IoPdfInvalid, error.Code);
    }

    [Fact]
    public void EncryptedPdf_IsRejectedWithAnExplicitDiagnostic()
    {
        var error = Assert.Throws<AdapterException>(() =>
            PdfDigitalDocumentExtractor.Extract(BuildEncryptedPdf()));

        Assert.Equal(DiagnosticCodes.IoPdfEncrypted, error.Code);
    }

    [Fact]
    public void Extractor_PreservesPageNumberAndPageRotation_AndHashesExactSourceBytes()
    {
        var pdf = BuildMultiPagePdf();

        var result = PdfDigitalDocumentExtractor.Extract(pdf);

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Equal(0, result.Pages[0].RotationDegrees);
        Assert.Equal(2, result.Pages[1].PageNumber);
        Assert.Equal(90, result.Pages[1].RotationDegrees);
        Assert.Equal(pdf.Length, result.SourceByteLength);
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(),
            result.SourceSha256);
        Assert.Contains("2026", result.Pages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extractor_RejectsByteAndPageLimitWithoutReturningPartialPages()
    {
        var pdf = BuildMultiPagePdf();

        var byteLimit = Assert.Throws<AdapterException>(() =>
            PdfDigitalDocumentExtractor.Extract(pdf, new PdfDigitalExtractionOptions(MaxFileBytes: 32)));
        var pageLimit = Assert.Throws<AdapterException>(() =>
            PdfDigitalDocumentExtractor.Extract(pdf, new PdfDigitalExtractionOptions(MaxPages: 1)));

        Assert.Equal(DiagnosticCodes.IoPdfBudgetExceeded, byteLimit.Code);
        Assert.Equal(DiagnosticCodes.IoPdfBudgetExceeded, pageLimit.Code);
    }

    [Fact]
    public void HighObjectCountPdf_StaysWithinTheConfiguredByteAndOutputBudgets()
    {
        var pdf = BuildHighObjectCountPdf(2_000);

        var result = PdfDigitalDocumentExtractor.Extract(pdf);

        Assert.True(pdf.Length < new PdfDigitalExtractionOptions().MaxFileBytes);
        Assert.Single(result.Pages);
        Assert.Contains("BOUNDED", result.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extractor_ObservesCancellationBetweenPages()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => PdfDigitalDocumentExtractor.Extract(
            BuildMultiPagePdf(), cancellationToken: cancellation.Token));
    }

    private static IReadOnlyList<LetterSnapshot> ReadSnapshot(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        return page.Letters.Select(letter => new LetterSnapshot(
            letter.Value,
            letter.BoundingBox.Left,
            letter.BoundingBox.Bottom,
            letter.StartBaseLine.X,
            letter.StartBaseLine.Y,
            letter.EndBaseLine.X,
            letter.EndBaseLine.Y)).ToArray();
    }

    private static IReadOnlyList<WordSnapshot> ReadWordSnapshot(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPage(1).GetWords()
            .Select(word => new WordSnapshot(word.Text, word.BoundingBox.Left, word.BoundingBox.Bottom))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadWords(byte[] pdf)
        => ReadWordSnapshot(pdf).Select(word => word.Text).ToArray();

    private static byte[] BuildPdf(Action<PdfPageBuilder, PdfDocumentBuilder.AddedFont> addContent)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        addContent(page, font);
        return builder.Build();
    }

    private static byte[] BuildMultiPagePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(PageSize.A4);
        first.AddText("2025 1250", 10, new PdfPoint(72, 720), font);
        var second = builder.AddPage(PageSize.A4);
        second.SetRotation(new PageRotationDegrees(90));
        second.AddText("2026 1400", 10, new PdfPoint(72, 720), font);
        return builder.Build();
    }

    private static byte[] BuildRotatedPdf(string text)
    {
        var stream = $"BT\n/F1 12 Tf\n0 1 -1 0 500 200 Tm\n({text}) Tj\nET\n";
        var streamLength = Encoding.ASCII.GetByteCount(stream);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {streamLength} >>\nstream\n{stream}endstream"
        };

        return BuildRawPdf(objects);
    }

    private static byte[] BuildEncryptedPdf()
    {
        var stream = "BT\nET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
            "<< /Filter /Standard /V 1 /R 2 /O <0000000000000000000000000000000000000000000000000000000000000000> /U <0000000000000000000000000000000000000000000000000000000000000000> /P -4 >>"
        };

        return BuildRawPdf(objects,
            " /Encrypt 6 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]");
    }

    private static byte[] BuildHighObjectCountPdf(int objectCount)
    {
        var stream = "BT\n/F1 12 Tf\n1 0 0 1 72 720 Tm\n(BOUNDED) Tj\nET\n";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream"
        };
        objects.AddRange(Enumerable.Range(0, objectCount)
            .Select(index => $"<< /Type /Unused /Index {index} >>"));
        return BuildRawPdf(objects.ToArray());
    }

    private static byte[] BuildRawPdf(string[] objects, string trailerEntries = "")
    {
        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length + 1];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index + 1] = Encoding.ASCII.GetByteCount(output.ToString());
            output.Append(index + 1).Append(" 0 obj\n")
                .Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append("xref\n0 ").Append(offsets.Length).Append("\n")
            .Append("0000000000 65535 f \n");
        for (var index = 1; index < offsets.Length; index++)
            output.Append(offsets[index].ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" 00000 n \n");
        output.Append("trailer\n<< /Root 1 0 R /Size ").Append(offsets.Length)
            .Append(trailerEntries).Append(" >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private sealed record LetterSnapshot(
        string Value,
        double Left,
        double Bottom,
        double StartX,
        double StartY,
        double EndX,
        double EndY);

    private sealed record WordSnapshot(string Text, double Left, double Bottom);
}
