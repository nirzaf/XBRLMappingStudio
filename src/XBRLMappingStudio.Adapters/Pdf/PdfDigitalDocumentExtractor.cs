using System.Security.Cryptography;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Pdf;

/// <summary>
/// Hard limits for the digital-text PDF probe. Callers may lower these values, but
/// cannot raise them above the process-level safety ceilings.
/// </summary>
public sealed record PdfDigitalExtractionOptions(
    int MaxFileBytes = 20 * 1024 * 1024,
    int MaxPages = 200,
    int MaxWords = 50_000,
    int MaxTextCharacters = 1_000_000)
{
    public const int AbsoluteMaxFileBytes = 25 * 1024 * 1024;
    public const int AbsoluteMaxPages = 500;
    public const int AbsoluteMaxWords = 100_000;
    public const int AbsoluteMaxTextCharacters = 2_000_000;

    public PdfDigitalExtractionOptions Validate()
    {
        if (MaxFileBytes is <= 0 or > AbsoluteMaxFileBytes
            || MaxPages is <= 0 or > AbsoluteMaxPages
            || MaxWords is <= 0 or > AbsoluteMaxWords
            || MaxTextCharacters is <= 0 or > AbsoluteMaxTextCharacters)
        {
            throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                "The requested PDF extraction limits are outside the supported safety bounds.",
                DiagnosticStage.Validation);
        }

        return this;
    }
}

/// <summary>Text and positional evidence extracted from one PDF word.</summary>
public sealed record PdfDigitalWord(
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top);

/// <summary>One page, retaining the PDF's one-based page number and page rotation.</summary>
public sealed record PdfDigitalPage(
    int PageNumber,
    int RotationDegrees,
    double Width,
    double Height,
    string Text,
    IReadOnlyList<PdfDigitalWord> Words);

/// <summary>
/// Immutable extraction result. Text order is the parser's content order; positions
/// are retained so later review can detect ambiguous visual reading order. This type
/// does not interpret financial values or create mapping records.
/// </summary>
public sealed record PdfDigitalDocument(
    string SourceSha256,
    int SourceByteLength,
    IReadOnlyList<PdfDigitalPage> Pages);

/// <summary>
/// Reads text and geometry only from digitally generated PDFs. Call this from the
/// supervised PDF worker so the caller can enforce a hard process deadline.
/// </summary>
public static class PdfDigitalDocumentExtractor
{
    public static PdfDigitalDocument Extract(
        ReadOnlyMemory<byte> sourceBytes,
        PdfDigitalExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var limits = (options ?? new PdfDigitalExtractionOptions()).Validate();
        if (sourceBytes.IsEmpty)
        {
            throw InvalidPdf();
        }

        if (sourceBytes.Length > limits.MaxFileBytes)
        {
            throw BudgetExceeded("The PDF exceeds the configured byte limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var document = PdfDocument.Open(sourceBytes, ParsingOptions.LenientParsingOff);
            if (document.IsEncrypted)
                throw EncryptedPdf();
            if (document.NumberOfPages == 0)
            {
                throw InvalidPdf("The PDF contains no pages.");
            }

            if (document.NumberOfPages > limits.MaxPages)
            {
                throw BudgetExceeded("The PDF exceeds the configured page limit.");
            }

            var pages = new List<PdfDigitalPage>(document.NumberOfPages);
            var totalWords = 0;
            var totalCharacters = 0;

            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.GetPage(pageNumber);
                var text = page.Text ?? string.Empty;
                totalCharacters = checked(totalCharacters + text.Length);
                if (totalCharacters > limits.MaxTextCharacters)
                {
                    throw BudgetExceeded("The PDF exceeds the configured extracted-text limit.");
                }

                var words = new List<PdfDigitalWord>();
                foreach (var word in page.GetWords())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(word.Text))
                    {
                        continue;
                    }

                    totalWords++;
                    if (totalWords > limits.MaxWords)
                    {
                        throw BudgetExceeded("The PDF exceeds the configured extracted-word limit.");
                    }

                    words.Add(new PdfDigitalWord(
                        word.Text,
                        word.BoundingBox.Left,
                        word.BoundingBox.Bottom,
                        word.BoundingBox.Right,
                        word.BoundingBox.Top));
                }

                pages.Add(new PdfDigitalPage(
                    pageNumber,
                    page.Rotation.Value,
                    page.Width,
                    page.Height,
                    text,
                    words));
            }

            return new PdfDigitalDocument(
                "sha256:" + Convert.ToHexString(SHA256.HashData(sourceBytes.Span)).ToLowerInvariant(),
                sourceBytes.Length,
                pages);
        }
        catch (AdapterException)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException)
        {
            throw EncryptedPdf();
        }
        catch (OutOfMemoryException) { throw; }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Do not include parser exception text, which can contain document data.
            throw InvalidPdf();
        }
    }

    private static AdapterException InvalidPdf(string message = "The PDF is malformed or unsupported.")
        => new(DiagnosticCodes.IoPdfInvalid, message, DiagnosticStage.Introspection);

    private static AdapterException BudgetExceeded(string message)
        => new(DiagnosticCodes.IoPdfBudgetExceeded, message, DiagnosticStage.Validation);

    private static AdapterException EncryptedPdf()
        => new(DiagnosticCodes.IoPdfEncrypted,
            "Encrypted PDFs are not supported for unattended extraction.",
            DiagnosticStage.Introspection);
}
