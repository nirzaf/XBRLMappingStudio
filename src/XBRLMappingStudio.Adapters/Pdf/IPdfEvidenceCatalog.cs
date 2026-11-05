namespace XBRLMappingStudio.Adapters.Pdf;

/// <summary>A source-region link which must match a persisted reference exactly.</summary>
public sealed record PdfEvidenceExpectation(
    string EvidenceId,
    PdfEvidenceRegion Region,
    string? TemplateRevision = null,
    string? ExpectedExtractedText = null);

/// <summary>
/// Application-owned evidence lookup used by the PDF source adapter. The adapter
/// batches checks so source and managed-document digests are read once per table.
/// </summary>
public interface IPdfEvidenceCatalog
{
    bool AreCurrent(
        IReadOnlyList<PdfEvidenceExpectation> expectedReferences,
        string currentSourcePath,
        string expectedSourceSha256);
}
