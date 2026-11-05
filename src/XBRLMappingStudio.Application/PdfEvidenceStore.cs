using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Application;

/// <summary>A raw text occurrence and its normalized location in one extracted PDF page.</summary>
public sealed record PdfEvidenceCapture(PdfEvidenceRegion Region, string ExtractedText);

public enum PdfEvidenceResolutionState
{
    Current,
    StaleSource,
    InvalidSnapshot
}

/// <summary>
/// Durable pointer to a specific page region in a content-addressed copy of the
/// original PDF. It deliberately does not contain a parsed/reviewed financial value.
/// </summary>
public sealed record PdfEvidenceReference(
    string EvidenceId,
    string SourceSha256,
    int SourceByteLength,
    string OriginalSourcePath,
    string ImmutableDocumentPath,
    int PageNumber,
    int PageRotationDegrees,
    double PageWidth,
    double PageHeight,
    PdfDigitalPageBounds MediaBounds,
    PdfDigitalPageBounds CropBounds,
    PdfEvidenceRegion Region,
    string ExtractedText,
    string ExtractionMethod,
    string? TemplateRevision,
    DateTimeOffset CapturedUtc);

/// <summary>Resolved evidence, with a fail-closed status against both the source and snapshot hashes.</summary>
public sealed record PdfEvidenceResolution(
    PdfEvidenceReference Evidence,
    PdfEvidenceResolutionState State,
    Diagnostic? Diagnostic)
{
    public bool CanUseForMapping => State == PdfEvidenceResolutionState.Current;
}

/// <summary>
/// Persists source regions independently of reviewed values. Original PDFs are kept
/// as private, content-addressed files; SQLite contains only bounded metadata and
/// references. Every resolution rechecks both the immutable copy and the live source.
/// </summary>
public sealed class PdfEvidenceStore
{
    public const string DigitalExtractionMethod = "pdfpig:0.1.16/digital-text-v1";
    private const int MaxCapturesPerCall = 5_000;
    private const int MaxCapturedTextCharacters = 4_096;

    private readonly LocalApplicationDatabase _database;
    private readonly string _managedDirectory;

    public PdfEvidenceStore(LocalApplicationDatabase database, string? managedDirectory = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _managedDirectory = Path.GetFullPath(managedDirectory ?? database.Path + ".pdf-evidence");
        LocalApplicationDatabase.EnsurePrivateDirectory(_managedDirectory);
    }

    /// <summary>
    /// Stores exact source bytes and one or more extracted regions. The file digest is
    /// compared to the extraction result before any evidence reference is committed.
    /// </summary>
    public async Task<IReadOnlyList<PdfEvidenceReference>> CaptureAsync(
        string originalSourcePath,
        PdfDigitalDocument extraction,
        IReadOnlyList<PdfEvidenceCapture> captures,
        string? templateRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalSourcePath);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(captures);
        ValidateExtraction(extraction);
        if (captures.Count is < 1 or > MaxCapturesPerCall)
            throw InvalidEvidence("PDF evidence must contain between one and five thousand regions.");
        if (templateRevision is not null && !IsSha256(templateRevision))
            throw InvalidEvidence("The PDF evidence template revision is invalid.");

        var fullSourcePath = Path.GetFullPath(originalSourcePath);
        var sourceBytes = await ReadBoundedFileAsync(fullSourcePath, cancellationToken).ConfigureAwait(false);
        var sourceDigest = RunManifest.ComputeSha256(sourceBytes);
        if (sourceBytes.Length != extraction.SourceByteLength
            || !StringComparer.Ordinal.Equals(sourceDigest, extraction.SourceSha256))
        {
            throw StaleEvidence("The source PDF changed after extraction; evidence was not saved.");
        }

        var pagesByNumber = extraction.Pages.ToDictionary(page => page.PageNumber);
        foreach (var capture in captures)
            ValidateCapture(capture, pagesByNumber);

        var managedFileName = sourceDigest[7..] + ".pdf";
        var managedPath = Path.Combine(_managedDirectory, managedFileName);
        await EnsureManagedSnapshotAsync(managedPath, sourceBytes, sourceDigest, cancellationToken)
            .ConfigureAwait(false);

        var capturedUtc = DateTimeOffset.UtcNow;
        var references = new List<PdfEvidenceReference>(captures.Count);
        using (var connection = _database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var documentCommand = connection.CreateCommand())
            {
                documentCommand.Transaction = transaction;
                documentCommand.CommandText = """
                    INSERT INTO pdf_source_documents(
                        source_sha256, source_byte_length, managed_file_name, extraction_method, created_utc)
                    VALUES($digest, $length, $file, $method, $created)
                    ON CONFLICT(source_sha256) DO NOTHING;
                    """;
                documentCommand.Parameters.AddWithValue("$digest", sourceDigest);
                documentCommand.Parameters.AddWithValue("$length", sourceBytes.Length);
                documentCommand.Parameters.AddWithValue("$file", managedFileName);
                documentCommand.Parameters.AddWithValue("$method", DigitalExtractionMethod);
                documentCommand.Parameters.AddWithValue("$created", capturedUtc.ToString("O", CultureInfo.InvariantCulture));
                documentCommand.ExecuteNonQuery();
            }

            foreach (var capture in captures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pagesByNumber[capture.Region.PageNumber];
                var evidenceId = Guid.NewGuid().ToString("N");
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO pdf_source_evidence(
                        evidence_id, source_sha256, original_path, page_number, page_rotation_degrees,
                        page_width, page_height,
                        media_left, media_bottom, media_right, media_top,
                        crop_left, crop_bottom, crop_right, crop_top,
                        left_normalized, bottom_normalized, right_normalized, top_normalized,
                        extracted_text, template_revision, extraction_method, captured_utc)
                    VALUES(
                        $id, $digest, $path, $page, $rotation,
                        $width, $height,
                        $media_left, $media_bottom, $media_right, $media_top,
                        $crop_left, $crop_bottom, $crop_right, $crop_top,
                        $left, $bottom, $right, $top,
                        $text, $revision, $method, $captured);
                    """;
                command.Parameters.AddWithValue("$id", evidenceId);
                command.Parameters.AddWithValue("$digest", sourceDigest);
                command.Parameters.AddWithValue("$path", fullSourcePath);
                command.Parameters.AddWithValue("$page", page.PageNumber);
                command.Parameters.AddWithValue("$rotation", page.RotationDegrees);
                command.Parameters.AddWithValue("$width", page.Width);
                command.Parameters.AddWithValue("$height", page.Height);
                AddBounds(command, "$media", page.MediaBounds!);
                AddBounds(command, "$crop", page.CropBounds!);
                command.Parameters.AddWithValue("$left", capture.Region.LeftNormalized);
                command.Parameters.AddWithValue("$bottom", capture.Region.BottomNormalized);
                command.Parameters.AddWithValue("$right", capture.Region.RightNormalized);
                command.Parameters.AddWithValue("$top", capture.Region.TopNormalized);
                command.Parameters.AddWithValue("$text", capture.ExtractedText);
                command.Parameters.AddWithValue("$revision", (object?)templateRevision ?? DBNull.Value);
                command.Parameters.AddWithValue("$method", DigitalExtractionMethod);
                command.Parameters.AddWithValue("$captured", capturedUtc.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();

                references.Add(new PdfEvidenceReference(
                    evidenceId, sourceDigest, sourceBytes.Length, fullSourcePath, managedPath,
                    page.PageNumber, page.RotationDegrees, page.Width, page.Height,
                    page.MediaBounds!, page.CropBounds!, capture.Region, capture.ExtractedText,
                    DigitalExtractionMethod, templateRevision, capturedUtc));
            }

            transaction.Commit();
        }

        return references;
    }

    /// <summary>
    /// Returns the evidence location and checks that the retained source bytes still
    /// match the extraction digest. A changed or missing live source blocks mapping.
    /// </summary>
    public PdfEvidenceResolution Resolve(string evidenceId, string? currentSourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        if (currentSourcePath is not null && string.IsNullOrWhiteSpace(currentSourcePath))
            throw new ArgumentException("A supplied current source path cannot be empty.", nameof(currentSourcePath));

        var stored = ReadReference(evidenceId);
        if (!IsValidStoredReference(stored.Evidence))
            return InvalidSnapshot(stored.Evidence);
        var expectedFileName = stored.Evidence.SourceSha256[7..] + ".pdf";
        if (!StringComparer.Ordinal.Equals(stored.ManagedFileName, expectedFileName))
            return InvalidSnapshot(stored.Evidence);

        var managedDigest = TryComputeFileDigest(stored.Evidence.ImmutableDocumentPath);
        if (!StringComparer.Ordinal.Equals(managedDigest, stored.Evidence.SourceSha256))
            return InvalidSnapshot(stored.Evidence);

        var sourcePath = currentSourcePath is null
            ? stored.Evidence.OriginalSourcePath
            : Path.GetFullPath(currentSourcePath);
        var currentDigest = TryComputeFileDigest(sourcePath);
        if (!StringComparer.Ordinal.Equals(currentDigest, stored.Evidence.SourceSha256))
        {
            return new PdfEvidenceResolution(stored.Evidence, PdfEvidenceResolutionState.StaleSource,
                MakeDiagnostic(DiagnosticCodes.IoPdfEvidenceStale,
                    "The source PDF changed or is unavailable; this evidence cannot authorize mapping."));
        }

        return new PdfEvidenceResolution(stored.Evidence, PdfEvidenceResolutionState.Current, null);
    }

    private StoredReference ReadReference(string evidenceId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.evidence_id, e.source_sha256, d.source_byte_length, e.original_path,
                   d.managed_file_name, e.page_number, e.page_rotation_degrees,
                   e.page_width, e.page_height,
                   e.media_left, e.media_bottom, e.media_right, e.media_top,
                   e.crop_left, e.crop_bottom, e.crop_right, e.crop_top,
                   e.left_normalized, e.bottom_normalized, e.right_normalized, e.top_normalized,
                   e.extracted_text, e.extraction_method, e.template_revision, e.captured_utc
            FROM pdf_source_evidence e
            JOIN pdf_source_documents d ON d.source_sha256=e.source_sha256
            WHERE e.evidence_id=$id;
            """;
        command.Parameters.AddWithValue("$id", evidenceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("The requested PDF evidence reference does not exist.");

        var digest = reader.GetString(1);
        var managedPath = IsSha256(digest)
            ? Path.Combine(_managedDirectory, digest[7..] + ".pdf")
            : string.Empty;
        var region = new PdfEvidenceRegion(reader.GetInt32(5), reader.GetDouble(17), reader.GetDouble(18),
            reader.GetDouble(19), reader.GetDouble(20));
        var evidence = new PdfEvidenceReference(
            reader.GetString(0), digest, reader.GetInt32(2), reader.GetString(3), managedPath,
            reader.GetInt32(5), reader.GetInt32(6), reader.GetDouble(7), reader.GetDouble(8),
            new PdfDigitalPageBounds(reader.GetDouble(9), reader.GetDouble(10), reader.GetDouble(11), reader.GetDouble(12)),
            new PdfDigitalPageBounds(reader.GetDouble(13), reader.GetDouble(14), reader.GetDouble(15), reader.GetDouble(16)),
            region, reader.GetString(21), reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            DateTimeOffset.Parse(reader.GetString(24), CultureInfo.InvariantCulture, DateTimeStyles.None));
        return new StoredReference(evidence, reader.GetString(4));
    }

    private PdfEvidenceResolution InvalidSnapshot(PdfEvidenceReference evidence)
        => new(evidence, PdfEvidenceResolutionState.InvalidSnapshot,
            MakeDiagnostic(DiagnosticCodes.IoPdfEvidenceSnapshotInvalid,
                "The retained PDF snapshot or evidence metadata is missing, invalid or no longer matches its recorded digest."));

    private static bool IsValidStoredReference(PdfEvidenceReference evidence)
        => IsSha256(evidence.SourceSha256)
            && evidence.SourceByteLength is > 0 and <= PdfDigitalExtractionOptions.AbsoluteMaxFileBytes
            && evidence.PageNumber > 0
            && evidence.PageRotationDegrees is 0 or 90 or 180 or 270
            && double.IsFinite(evidence.PageWidth) && evidence.PageWidth > 0
            && double.IsFinite(evidence.PageHeight) && evidence.PageHeight > 0
            && ValidBounds(evidence.MediaBounds) && ValidBounds(evidence.CropBounds)
            && evidence.Region.PageNumber == evidence.PageNumber
            && double.IsFinite(evidence.Region.LeftNormalized) && evidence.Region.LeftNormalized >= 0
            && double.IsFinite(evidence.Region.BottomNormalized) && evidence.Region.BottomNormalized >= 0
            && double.IsFinite(evidence.Region.RightNormalized) && evidence.Region.RightNormalized <= 1
            && double.IsFinite(evidence.Region.TopNormalized) && evidence.Region.TopNormalized <= 1
            && evidence.Region.LeftNormalized < evidence.Region.RightNormalized
            && evidence.Region.BottomNormalized < evidence.Region.TopNormalized
            && evidence.ExtractedText.Length is > 0 and <= MaxCapturedTextCharacters
            && !evidence.ExtractedText.Any(char.IsControl)
            && StringComparer.Ordinal.Equals(evidence.ExtractionMethod, DigitalExtractionMethod)
            && (evidence.TemplateRevision is null || IsSha256(evidence.TemplateRevision));

    private static Diagnostic MakeDiagnostic(string code, string message)
        => new()
        {
            Code = code,
            Subsystem = DiagnosticSubsystem.Adapter,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Validation,
            IsBlocking = true,
            Message = message
        };

    private static void ValidateExtraction(PdfDigitalDocument extraction)
    {
        if (!IsSha256(extraction.SourceSha256) || extraction.SourceByteLength is <= 0 or > PdfDigitalExtractionOptions.AbsoluteMaxFileBytes
            || extraction.Pages is null || extraction.Pages.Count is < 1 or > PdfDigitalExtractionOptions.AbsoluteMaxPages)
            throw InvalidEvidence("The extracted PDF metadata is incomplete or outside supported bounds.");

        for (var index = 0; index < extraction.Pages.Count; index++)
        {
            var page = extraction.Pages[index];
            if (page is null || page.PageNumber != index + 1
                || page.RotationDegrees is not (0 or 90 or 180 or 270)
                || !double.IsFinite(page.Width) || page.Width <= 0
                || !double.IsFinite(page.Height) || page.Height <= 0
                || page.Words is null || page.Words.Count > PdfDigitalExtractionOptions.AbsoluteMaxWords
                || !ValidBounds(page.MediaBounds) || !ValidBounds(page.CropBounds))
                throw InvalidEvidence("The extracted PDF page geometry is incomplete or invalid.");
        }
    }

    private static void ValidateCapture(PdfEvidenceCapture capture, IReadOnlyDictionary<int, PdfDigitalPage> pages)
    {
        if (capture is null || capture.Region is null || capture.ExtractedText is null
            || capture.ExtractedText.Length is 0 or > MaxCapturedTextCharacters
            || capture.ExtractedText.Any(char.IsControl))
            throw InvalidEvidence("A PDF evidence region has missing or oversized extracted text.");

        var region = capture.Region;
        if (region.PageNumber <= 0 || !pages.TryGetValue(region.PageNumber, out var page)
            || !double.IsFinite(region.LeftNormalized) || !double.IsFinite(region.BottomNormalized)
            || !double.IsFinite(region.RightNormalized) || !double.IsFinite(region.TopNormalized)
            || region.LeftNormalized < 0 || region.BottomNormalized < 0
            || region.RightNormalized > 1 || region.TopNormalized > 1
            || region.LeftNormalized >= region.RightNormalized || region.BottomNormalized >= region.TopNormalized)
            throw InvalidEvidence("A PDF evidence region is outside its page or has invalid normalized coordinates.");

        var left = region.LeftNormalized * page.Width;
        var right = region.RightNormalized * page.Width;
        var bottom = region.BottomNormalized * page.Height;
        var top = region.TopNormalized * page.Height;
        var observedCharacters = page.Words
            .Where(word => word is not null && ValidWord(word, page)
                && (word.Left + word.Right) / 2 >= left && (word.Left + word.Right) / 2 <= right
                && (word.Bottom + word.Top) / 2 >= bottom && (word.Bottom + word.Top) / 2 <= top)
            .SelectMany(word => word.Text.Where(character => !char.IsWhiteSpace(character)))
            .OrderBy(character => character)
            .ToArray();
        var capturedCharacters = capture.ExtractedText.Where(character => !char.IsWhiteSpace(character))
            .OrderBy(character => character)
            .ToArray();
        if (observedCharacters.Length == 0 || !observedCharacters.SequenceEqual(capturedCharacters))
            throw InvalidEvidence("The PDF evidence text does not match the words inside its declared page region.");
    }

    private static bool ValidWord(PdfDigitalWord word, PdfDigitalPage page)
        => !string.IsNullOrWhiteSpace(word.Text)
            && double.IsFinite(word.Left) && double.IsFinite(word.Bottom)
            && double.IsFinite(word.Right) && double.IsFinite(word.Top)
            && word.Left <= word.Right && word.Bottom <= word.Top
            && word.Left >= 0 && word.Right <= page.Width
            && word.Bottom >= 0 && word.Top <= page.Height;

    private static bool ValidBounds(PdfDigitalPageBounds? bounds)
        => bounds is not null
            && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Bottom)
            && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Top)
            && bounds.Left < bounds.Right && bounds.Bottom < bounds.Top;

    private static void AddBounds(SqliteCommand command, string prefix, PdfDigitalPageBounds bounds)
    {
        command.Parameters.AddWithValue(prefix + "_left", bounds.Left);
        command.Parameters.AddWithValue(prefix + "_bottom", bounds.Bottom);
        command.Parameters.AddWithValue(prefix + "_right", bounds.Right);
        command.Parameters.AddWithValue(prefix + "_top", bounds.Top);
    }

    private async Task EnsureManagedSnapshotAsync(
        string managedPath,
        byte[] sourceBytes,
        string digest,
        CancellationToken cancellationToken)
    {
        if (File.Exists(managedPath))
        {
            if (!StringComparer.Ordinal.Equals(TryComputeFileDigest(managedPath), digest))
                throw new AdapterException(DiagnosticCodes.IoPdfEvidenceSnapshotInvalid,
                    "A content-addressed PDF snapshot exists with a different digest.", DiagnosticStage.Validation);
            return;
        }

        var temporaryPath = Path.Combine(_managedDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporaryPath, options))
            {
                await output.WriteAsync(sourceBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            try { File.Move(temporaryPath, managedPath); }
            catch (IOException) when (File.Exists(managedPath))
            {
                if (!StringComparer.Ordinal.Equals(TryComputeFileDigest(managedPath), digest)) throw;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        if (input.Length is <= 0 or > PdfDigitalExtractionOptions.AbsoluteMaxFileBytes)
            throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                "The PDF source exceeds the evidence store byte limit.", DiagnosticStage.Validation);

        using var output = new MemoryStream((int)input.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > PdfDigitalExtractionOptions.AbsoluteMaxFileBytes)
                throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                    "The PDF source exceeds the evidence store byte limit.", DiagnosticStage.Validation);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string? TryComputeFileDigest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > PdfDigitalExtractionOptions.AbsoluteMaxFileBytes)
                return null;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > PdfDigitalExtractionOptions.AbsoluteMaxFileBytes) return null;
                hash.AppendData(buffer, 0, read);
            }
            return total == info.Length
                ? "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static AdapterException InvalidEvidence(string message)
        => new(DiagnosticCodes.IoPdfEvidenceReferenceInvalid, message, DiagnosticStage.Validation);

    private static AdapterException StaleEvidence(string message)
        => new(DiagnosticCodes.IoPdfEvidenceStale, message, DiagnosticStage.Validation);

    private static bool IsSha256(string? digest)
        => digest is { Length: 71 }
            && digest.StartsWith("sha256:", StringComparison.Ordinal)
            && digest.AsSpan(7).ToString().All(Uri.IsHexDigit);

    private sealed record StoredReference(PdfEvidenceReference Evidence, string ManagedFileName);
}
