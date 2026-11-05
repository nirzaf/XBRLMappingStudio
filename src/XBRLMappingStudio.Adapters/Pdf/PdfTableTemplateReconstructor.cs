using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Pdf;

/// <summary>A reviewer-declared year heading and its normalized table-column anchor.</summary>
public sealed record PdfPeriodColumnAnchor(string HeaderText, int ReportingYear, double CenterOnColumnAxisNormalized);

public enum PdfTableColumnAxis
{
    Horizontal,
    Vertical
}

/// <summary>
/// Geometry captured from a declared first-page template. It describes layout only;
/// it does not approve a source, interpret numeric text, or select an XBRL concept.
/// </summary>
public sealed record PdfTableTemplate(
    string SeedSourceSha256,
    int SeedPageNumber,
    int SeedPageRotationDegrees,
    PdfTableColumnAxis ColumnAxis,
    bool RowLabelAtLowAxis,
    bool RowsAtLowLineCoordinate,
    string RowLabelHeader,
    double RowLabelHeaderCenterOnColumnAxisNormalized,
    double RowLabelBoundaryNormalized,
    double HeaderLineCoordinateNormalized,
    double LineGroupingToleranceNormalized,
    double AnchorToleranceNormalized,
    IReadOnlyList<PdfPeriodColumnAnchor> PeriodColumns,
    string Revision)
{
    public static PdfTableTemplate CreateFromFirstPage(
        PdfDigitalDocument document,
        int pageNumber,
        string rowLabelHeader,
        IReadOnlyDictionary<string, int> periodHeaders,
        bool rowsAtLowLineCoordinate,
        double lineGroupingToleranceNormalized = 0.008,
        double anchorToleranceNormalized = 0.02)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(periodHeaders);

        var pages = document.Pages;
        if (pages is null)
            throw InvalidTemplate("The declared PDF template document has no page list.");
        if (document.SourceByteLength <= 0 || !IsSha256(document.SourceSha256)
            || pages.Count > PdfDigitalExtractionOptions.AbsoluteMaxPages
            || pageNumber <= 0 || pageNumber > pages.Count)
            throw InvalidTemplate("The declared PDF template page does not exist.");
        if (string.IsNullOrWhiteSpace(rowLabelHeader) || rowLabelHeader.Trim().Any(char.IsWhiteSpace))
            throw InvalidTemplate("A single-word row-label header is required in the declared PDF template.");
        if (periodHeaders.Count is < 2 or > 30)
            throw InvalidTemplate("Between two and thirty explicitly declared year columns are required.");
        if (!double.IsFinite(lineGroupingToleranceNormalized)
            || lineGroupingToleranceNormalized is < 0.001 or > 0.05
            || !double.IsFinite(anchorToleranceNormalized)
            || anchorToleranceNormalized is < 0.001 or > 0.1)
        {
            throw InvalidTemplate("PDF template tolerances are outside the supported bounds.");
        }

        var page = pages[pageNumber - 1];
        ValidatePage(page);
        var rowLabelText = rowLabelHeader.Trim();
        var normalizedHeaders = periodHeaders
            .Select(pair => (Text: pair.Key?.Trim() ?? string.Empty, Year: pair.Value))
            .ToArray();
        if (normalizedHeaders.Any(header => header.Text.Length == 0 || header.Text.Any(char.IsWhiteSpace)
                || header.Year is < 1900 or > 2200)
            || normalizedHeaders.Select(header => header.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedHeaders.Length
            || normalizedHeaders.Select(header => header.Year).Distinct().Count() != normalizedHeaders.Length)
        {
            throw InvalidTemplate("PDF year headers and reporting years must be unique and non-empty.");
        }

        var horizontalCandidates = BuildVisualLines(page, lineGroupingToleranceNormalized, PdfTableColumnAxis.Horizontal)
            .Where(line => HasOneWord(line, rowLabelText)
                && normalizedHeaders.All(header => HasOneWord(line, header.Text)))
            .Select(line => (Axis: PdfTableColumnAxis.Horizontal, Line: line)).ToArray();
        var verticalCandidates = BuildVisualLines(page, lineGroupingToleranceNormalized, PdfTableColumnAxis.Vertical)
            .Where(line => HasOneWord(line, rowLabelText)
                && normalizedHeaders.All(header => HasOneWord(line, header.Text)))
            .Select(line => (Axis: PdfTableColumnAxis.Vertical, Line: line)).ToArray();
        var candidates = horizontalCandidates.Concat(verticalCandidates).ToArray();
        if (candidates.Length != 1)
            throw InvalidTemplate("The first-page template must contain one unambiguous row-label and year-header line.");

        var axis = candidates[0].Axis;
        var headerLine = candidates[0].Line;
        var labelWord = FindWord(headerLine, rowLabelText)!;
        var columns = normalizedHeaders
            .Select(header =>
            {
                var word = FindWord(headerLine, header.Text)!;
                return new PdfPeriodColumnAnchor(header.Text, header.Year,
                    ColumnCenter(word, page, axis));
            })
            .OrderBy(column => column.CenterOnColumnAxisNormalized)
            .ToArray();

        var labelBounds = ColumnBounds(labelWord, page, axis);
        var headerBounds = columns.Select(column => ColumnBounds(
            FindWord(headerLine, column.HeaderText)!, page, axis)).ToArray();
        var earliestHeader = headerBounds.Min(bounds => bounds.Low);
        var latestHeader = headerBounds.Max(bounds => bounds.High);
        bool rowLabelAtLowAxis;
        double rowLabelBoundary;
        if (labelBounds.High < earliestHeader)
        {
            rowLabelAtLowAxis = true;
            rowLabelBoundary = (labelBounds.High + earliestHeader) / 2;
        }
        else if (labelBounds.Low > latestHeader)
        {
            rowLabelAtLowAxis = false;
            rowLabelBoundary = (labelBounds.Low + latestHeader) / 2;
        }
        else
        {
            throw InvalidTemplate("The declared row-label and year columns overlap in the first-page template.");
        }

        if (columns.Length > 1 && columns.Zip(columns.Skip(1),
                (left, right) => right.CenterOnColumnAxisNormalized - left.CenterOnColumnAxisNormalized)
            .Any(distance => distance <= anchorToleranceNormalized * 2))
        {
            throw InvalidTemplate("Declared year columns are too close to distinguish within the configured anchor tolerance.");
        }

        var revisionText = string.Join("|", new[]
        {
            document.SourceSha256,
            pageNumber.ToString(CultureInfo.InvariantCulture),
            page.RotationDegrees.ToString(CultureInfo.InvariantCulture),
            axis.ToString(),
            rowLabelAtLowAxis.ToString(CultureInfo.InvariantCulture),
            rowsAtLowLineCoordinate.ToString(CultureInfo.InvariantCulture),
            rowLabelText,
            ColumnCenter(labelWord, page, axis).ToString("R", CultureInfo.InvariantCulture),
            (rowLabelBoundary / ColumnLength(page, axis)).ToString("R", CultureInfo.InvariantCulture),
            headerLine.CenterLineNormalized.ToString("R", CultureInfo.InvariantCulture),
            lineGroupingToleranceNormalized.ToString("R", CultureInfo.InvariantCulture),
            anchorToleranceNormalized.ToString("R", CultureInfo.InvariantCulture),
            string.Join(";", columns.Select(column =>
                $"{column.HeaderText}:{column.ReportingYear}:{column.CenterOnColumnAxisNormalized.ToString("R", CultureInfo.InvariantCulture)}"))
        });

        return new PdfTableTemplate(
            document.SourceSha256,
            pageNumber,
            page.RotationDegrees,
            axis,
            rowLabelAtLowAxis,
            rowsAtLowLineCoordinate,
            rowLabelText,
            ColumnCenter(labelWord, page, axis),
            rowLabelBoundary / ColumnLength(page, axis),
            headerLine.CenterLineNormalized,
            lineGroupingToleranceNormalized,
            anchorToleranceNormalized,
            columns,
            "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionText))).ToLowerInvariant());
    }

    private static AdapterException InvalidTemplate(string message)
        => new(DiagnosticCodes.IoPdfTemplateInvalid, message, DiagnosticStage.Validation);

    internal static void ValidatePage(PdfDigitalPage page)
    {
        if (page is null || page.PageNumber <= 0 || !double.IsFinite(page.Width) || page.Width <= 0
            || !double.IsFinite(page.Height) || page.Height <= 0
            || page.RotationDegrees is not (0 or 90 or 180 or 270)
            || page.Words is null || page.Words.Count > PdfDigitalExtractionOptions.AbsoluteMaxWords)
        {
            throw InvalidTemplate("The PDF page geometry is invalid or unsupported.");
        }

        foreach (var word in page.Words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Text)
                || !double.IsFinite(word.Left) || !double.IsFinite(word.Right)
                || !double.IsFinite(word.Bottom) || !double.IsFinite(word.Top)
                || word.Right < word.Left || word.Top < word.Bottom
                || word.Left < 0 || word.Right > page.Width
                || word.Bottom < 0 || word.Top > page.Height)
            {
                throw InvalidTemplate("The PDF page contains invalid word geometry.");
            }
        }
    }

    internal static double ColumnCenter(PdfDigitalWord word, PdfDigitalPage page, PdfTableColumnAxis axis)
    {
        var bounds = ColumnBounds(word, page, axis);
        return ((bounds.Low + bounds.High) / 2) / ColumnLength(page, axis);
    }

    internal static (double Low, double High) ColumnBounds(
        PdfDigitalWord word,
        PdfDigitalPage page,
        PdfTableColumnAxis axis)
        => axis == PdfTableColumnAxis.Horizontal
            ? (word.Left, word.Right)
            : (word.Bottom, word.Top);

    internal static double ColumnLength(PdfDigitalPage page, PdfTableColumnAxis axis)
        => axis == PdfTableColumnAxis.Horizontal ? page.Width : page.Height;

    internal static bool IsSha256(string? digest)
        => digest is { Length: 71 }
            && digest.StartsWith("sha256:", StringComparison.Ordinal)
            && digest.AsSpan(7).ToString().All(Uri.IsHexDigit);

    internal static PdfVisualLine[] BuildVisualLines(
        PdfDigitalPage page,
        double tolerance,
        PdfTableColumnAxis columnAxis)
    {
        var ordered = page.Words
            .Select(word => (Word: word,
                LineCoordinate: columnAxis == PdfTableColumnAxis.Horizontal
                    ? ((word.Bottom + word.Top) / 2) / page.Height
                    : ((word.Left + word.Right) / 2) / page.Width,
                ColumnCoordinate: columnAxis == PdfTableColumnAxis.Horizontal
                    ? (word.Left + word.Right) / 2
                    : (word.Bottom + word.Top) / 2))
            .OrderByDescending(item => item.LineCoordinate)
            .ThenBy(item => item.ColumnCoordinate)
            .ToArray();
        var groups = new List<List<(PdfDigitalWord Word, double LineCoordinate)>>();
        foreach (var item in ordered)
        {
            var group = groups.LastOrDefault();
            if (group is null || Math.Abs(group[0].LineCoordinate - item.LineCoordinate) > tolerance)
            {
                groups.Add(new List<(PdfDigitalWord, double)> { (item.Word, item.LineCoordinate) });
            }
            else
            {
                group.Add((item.Word, item.LineCoordinate));
            }
        }

        return groups.Select(group =>
        {
            var words = group.Select(item => item.Word)
                .OrderBy(word => ColumnBounds(word, page, columnAxis).Low).ToArray();
            return new PdfVisualLine(words, group.Average(item => item.LineCoordinate));
        }).ToArray();
    }

    internal static bool HasOneWord(PdfVisualLine line, string text)
        => line.Words.Count(word => StringComparer.OrdinalIgnoreCase.Equals(word.Text.Trim(), text)) == 1;

    internal static PdfDigitalWord? FindWord(PdfVisualLine line, string text)
        => line.Words.SingleOrDefault(word => StringComparer.OrdinalIgnoreCase.Equals(word.Text.Trim(), text));
}

public enum PdfTableReconstructionState
{
    Anchored,
    NeedsReview,
    LayoutDrift
}

public sealed record PdfEvidenceRegion(
    int PageNumber,
    double LeftNormalized,
    double BottomNormalized,
    double RightNormalized,
    double TopNormalized);

public sealed record PdfTableCell(int ReportingYear, string RawText, PdfEvidenceRegion Evidence);

public sealed record PdfTableRow(
    int Occurrence,
    string LabelText,
    PdfEvidenceRegion Evidence,
    IReadOnlyList<PdfTableCell> Cells);

public sealed record PdfTableAlternative(
    int PageNumber,
    string Text,
    IReadOnlyList<int> CandidateYears,
    string Reason,
    PdfEvidenceRegion Evidence);

public sealed record PdfTableReconstructionResult(
    PdfTableReconstructionState State,
    string SourceSha256,
    string TemplateRevision,
    IReadOnlyList<PdfTableRow> Rows,
    IReadOnlyList<PdfTableAlternative> Alternatives,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Reconstructs raw table occurrences against an explicit first-page geometry. It
/// preserves header-to-year identity, repeated headers, ambiguous words, and evidence
/// regions. Numeric parsing and approval remain separate operations.
/// </summary>
public static class PdfTableTemplateReconstructor
{
    public static PdfTableReconstructionResult Reconstruct(
        PdfDigitalDocument document,
        PdfTableTemplate template)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        if (document.Pages is null || document.Pages.Count == 0 || template.PeriodColumns is null
            || !IsValidTemplate(template)
            || document.SourceByteLength <= 0 || !PdfTableTemplate.IsSha256(document.SourceSha256)
            || document.Pages.Count > PdfDigitalExtractionOptions.AbsoluteMaxPages)
        {
            return Drift(document, template, "The PDF document or declared year-column template is invalid.");
        }

        var rows = new List<PdfTableRow>();
        var alternatives = new List<PdfTableAlternative>();
        var matchedHeaderCount = 0;
        var occurrence = 0;

        for (var pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            var page = document.Pages[pageIndex];
            try { PdfTableTemplate.ValidatePage(page); }
            catch (AdapterException ex) { return Drift(document, template, ex.Message); }
            if (page.PageNumber != pageIndex + 1)
                return Drift(document, template, "PDF page numbering is not one-based and sequential.");

            var lines = PdfTableTemplate.BuildVisualLines(
                page, template.LineGroupingToleranceNormalized, template.ColumnAxis);
            var repeatedHeaderLines = new List<PdfVisualLine>();
            foreach (var line in lines)
            {
                var expectedHeaderWords = template.PeriodColumns
                    .Select(column => line.Words.Where(word => StringComparer.OrdinalIgnoreCase
                        .Equals(word.Text.Trim(), column.HeaderText)).ToArray())
                    .ToArray();
                var knownHeaderCount = expectedHeaderWords.Count(matches => matches.Length > 0);
                var rowHeaderWords = line.Words.Where(word => StringComparer.OrdinalIgnoreCase
                    .Equals(word.Text.Trim(), template.RowLabelHeader)).ToArray();
                var yearLikeCount = line.Words.Count(word => IsYearLike(word.Text));
                var hasAllHeaders = expectedHeaderWords.All(matches => matches.Length == 1);
                var hasEveryHeaderText = expectedHeaderWords.All(matches => matches.Length > 0);
                var headerOnly = line.Words.All(word =>
                    StringComparer.OrdinalIgnoreCase.Equals(word.Text.Trim(), template.RowLabelHeader)
                    || template.PeriodColumns.Any(column => StringComparer.OrdinalIgnoreCase
                        .Equals(word.Text.Trim(), column.HeaderText)));
                var looksLikeHeader = hasEveryHeaderText
                    || (rowHeaderWords.Length > 0 && (knownHeaderCount > 0 || yearLikeCount >= 2));
                if (!looksLikeHeader)
                    continue;

                if (!hasAllHeaders || rowHeaderWords.Length > 1)
                    return Drift(document, template, "A PDF year header is missing, duplicated, or changed.");

                foreach (var column in template.PeriodColumns)
                {
                    var headerWord = PdfTableTemplate.FindWord(line, column.HeaderText)!;
                    var observed = PdfTableTemplate.ColumnCenter(headerWord, page, template.ColumnAxis);
                    if (Math.Abs(observed - column.CenterOnColumnAxisNormalized) > template.AnchorToleranceNormalized)
                        return Drift(document, template, "A PDF year header moved outside its declared column anchor.");
                }

                if (rowHeaderWords.Length == 1)
                {
                    var observed = PdfTableTemplate.ColumnCenter(rowHeaderWords[0], page, template.ColumnAxis);
                    if (Math.Abs(observed - template.RowLabelHeaderCenterOnColumnAxisNormalized)
                        > template.AnchorToleranceNormalized)
                    {
                        return Drift(document, template, "The PDF row-label header moved outside its declared anchor.");
                    }
                }
                else if (!headerOnly)
                {
                    alternatives.Add(new PdfTableAlternative(page.PageNumber,
                        string.Join(" ", line.Words.Select(word => word.Text)),
                        template.PeriodColumns.Select(column => column.ReportingYear).ToArray(),
                        "Header-like line contains undeclared text and cannot be safely classified.",
                        Evidence(page, line.Words)));
                }

                repeatedHeaderLines.Add(line);
                matchedHeaderCount++;
            }

            var headerLineCoordinate = repeatedHeaderLines.Count == 0
                ? (double?)null
                : repeatedHeaderLines.Max(line => line.CenterLineNormalized);

            foreach (var line in lines)
            {
                if (repeatedHeaderLines.Contains(line)
                    || (headerLineCoordinate.HasValue && (template.RowsAtLowLineCoordinate
                        ? line.CenterLineNormalized >= headerLineCoordinate.Value - template.LineGroupingToleranceNormalized
                        : line.CenterLineNormalized <= headerLineCoordinate.Value + template.LineGroupingToleranceNormalized)))
                    continue;

                var row = ReconstructRow(page, line, template, ref occurrence, alternatives);
                if (row is not null)
                    rows.Add(row);
            }
        }

        if (matchedHeaderCount == 0)
            return Drift(document, template, "No occurrence of the declared PDF year headers was found.");

        var state = alternatives.Count == 0
            ? PdfTableReconstructionState.Anchored
            : PdfTableReconstructionState.NeedsReview;
        var diagnostics = alternatives.Count == 0
            ? Array.Empty<Diagnostic>()
            : new[] { Diagnostic(DiagnosticCodes.IoPdfTemplateAmbiguous,
                "PDF table extraction contains alternatives that require review.",
                DiagnosticSeverity.Warning) };

        return new PdfTableReconstructionResult(state, document.SourceSha256, template.Revision,
            rows, alternatives, diagnostics);
    }

    private static PdfTableRow? ReconstructRow(
        PdfDigitalPage page,
        PdfVisualLine line,
        PdfTableTemplate template,
        ref int occurrence,
        List<PdfTableAlternative> alternatives)
    {
        var axisLength = PdfTableTemplate.ColumnLength(page, template.ColumnAxis);
        var labelBoundary = template.RowLabelBoundaryNormalized * axisLength;
        var labelWords = line.Words.Where(word => template.RowLabelAtLowAxis
                ? PdfTableTemplate.ColumnCenter(word, page, template.ColumnAxis) * axisLength < labelBoundary
                : PdfTableTemplate.ColumnCenter(word, page, template.ColumnAxis) * axisLength > labelBoundary)
            .OrderBy(word => PdfTableTemplate.ColumnBounds(word, page, template.ColumnAxis).Low).ToArray();
        var valueWords = line.Words.Where(word => template.RowLabelAtLowAxis
                ? PdfTableTemplate.ColumnCenter(word, page, template.ColumnAxis) * axisLength >= labelBoundary
                : PdfTableTemplate.ColumnCenter(word, page, template.ColumnAxis) * axisLength <= labelBoundary)
            .OrderBy(word => PdfTableTemplate.ColumnBounds(word, page, template.ColumnAxis).Low).ToArray();
        if (valueWords.Length == 0)
            return null;

        var boundaries = new double[template.PeriodColumns.Count + 1];
        boundaries[0] = template.RowLabelAtLowAxis ? labelBoundary : 0;
        boundaries[^1] = template.RowLabelAtLowAxis ? axisLength : labelBoundary;
        for (var index = 1; index < template.PeriodColumns.Count; index++)
        {
            var left = template.PeriodColumns[index - 1].CenterOnColumnAxisNormalized * axisLength;
            var right = template.PeriodColumns[index].CenterOnColumnAxisNormalized * axisLength;
            boundaries[index] = (left + right) / 2;
        }

        var wordsByColumn = template.PeriodColumns.ToDictionary(column => column.ReportingYear,
            _ => new List<PdfDigitalWord>());
        foreach (var word in labelWords)
        {
            var bounds = PdfTableTemplate.ColumnBounds(word, page, template.ColumnAxis);
            if (template.RowLabelAtLowAxis
                    ? bounds.High >= labelBoundary
                    : bounds.Low <= labelBoundary)
            {
                alternatives.Add(new PdfTableAlternative(page.PageNumber, word.Text,
                    Array.Empty<int>(), "Row-label text overlaps the declared year-column boundary.",
                    Evidence(page, new[] { word })));
            }
        }

        foreach (var word in valueWords)
        {
            var touched = Enumerable.Range(0, template.PeriodColumns.Count)
                .Where(index =>
                {
                    var bounds = PdfTableTemplate.ColumnBounds(word, page, template.ColumnAxis);
                    return bounds.High >= boundaries[index] && bounds.Low <= boundaries[index + 1];
                })
                .ToArray();
            if (touched.Length != 1)
            {
                var candidateYears = touched.Select(index => template.PeriodColumns[index].ReportingYear).ToArray();
                alternatives.Add(new PdfTableAlternative(page.PageNumber, word.Text, candidateYears,
                    touched.Length == 0
                        ? "Word is outside every declared year column."
                        : "Word overlaps more than one declared year column.",
                    Evidence(page, new[] { word })));
                continue;
            }

            wordsByColumn[template.PeriodColumns[touched[0]].ReportingYear].Add(word);
        }

        if (labelWords.Length == 0)
        {
            alternatives.Add(new PdfTableAlternative(page.PageNumber,
                string.Join(" ", valueWords.Select(word => word.Text)),
                wordsByColumn.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key).ToArray(),
                "Value row has no text in the declared row-label column.",
                Evidence(page, line.Words)));
            return null;
        }

        var cells = wordsByColumn.Where(pair => pair.Value.Count > 0)
            .Select(pair => new PdfTableCell(pair.Key,
                string.Join(" ", pair.Value
                    .OrderBy(word => PdfTableTemplate.ColumnBounds(word, page, template.ColumnAxis).Low)
                    .Select(word => word.Text)),
                Evidence(page, pair.Value)))
            .OrderBy(cell => cell.ReportingYear)
            .ToArray();
        if (cells.Length == 0)
            return null;

        occurrence++;
        return new PdfTableRow(occurrence,
            string.Join(" ", labelWords.Select(word => word.Text)),
            Evidence(page, labelWords),
            cells);
    }

    private static PdfTableReconstructionResult Drift(
        PdfDigitalDocument document,
        PdfTableTemplate template,
        string message)
        => new(PdfTableReconstructionState.LayoutDrift, document.SourceSha256, template.Revision,
            Array.Empty<PdfTableRow>(), Array.Empty<PdfTableAlternative>(),
            new[] { Diagnostic(DiagnosticCodes.IoPdfTemplateDrift, message, DiagnosticSeverity.Error) });

    private static bool IsValidTemplate(PdfTableTemplate template)
    {
        if (template.PeriodColumns.Any(column => column is null))
            return false;

        if (string.IsNullOrWhiteSpace(template.RowLabelHeader)
            || template.RowLabelHeader.Any(char.IsWhiteSpace)
            || !PdfTableTemplate.IsSha256(template.SeedSourceSha256)
            || !PdfTableTemplate.IsSha256(template.Revision)
            || template.SeedPageNumber <= 0
            || template.SeedPageRotationDegrees is not (0 or 90 or 180 or 270)
            || template.ColumnAxis is not (PdfTableColumnAxis.Horizontal or PdfTableColumnAxis.Vertical)
            || !double.IsFinite(template.RowLabelHeaderCenterOnColumnAxisNormalized)
            || template.RowLabelHeaderCenterOnColumnAxisNormalized is <= 0 or >= 1
            || !double.IsFinite(template.RowLabelBoundaryNormalized)
            || template.RowLabelBoundaryNormalized is <= 0 or >= 1
            || !double.IsFinite(template.HeaderLineCoordinateNormalized)
            || template.HeaderLineCoordinateNormalized is <= 0 or >= 1
            || !double.IsFinite(template.LineGroupingToleranceNormalized)
            || template.LineGroupingToleranceNormalized is < 0.001 or > 0.05
            || !double.IsFinite(template.AnchorToleranceNormalized)
            || template.AnchorToleranceNormalized is < 0.001 or > 0.1
            || template.PeriodColumns.Count is < 2 or > 30
            || template.PeriodColumns.Select(column => column.ReportingYear).Distinct().Count()
                != template.PeriodColumns.Count
            || template.PeriodColumns.Select(column => column.HeaderText)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != template.PeriodColumns.Count)
        {
            return false;
        }

        if (template.PeriodColumns.Any(column => string.IsNullOrWhiteSpace(column.HeaderText)
            || column.HeaderText.Any(char.IsWhiteSpace)
            || column.ReportingYear is < 1900 or > 2200
            || !double.IsFinite(column.CenterOnColumnAxisNormalized)
            || column.CenterOnColumnAxisNormalized is <= 0 or >= 1))
        {
            return false;
        }

        var centers = template.PeriodColumns.Select(column => column.CenterOnColumnAxisNormalized).ToArray();
        if (centers.Zip(centers.Skip(1), (left, right) => right - left > template.AnchorToleranceNormalized * 2)
            .Any(isWellSeparated => !isWellSeparated))
            return false;

        return template.RowLabelAtLowAxis
            ? template.RowLabelBoundaryNormalized < centers[0]
            : template.RowLabelBoundaryNormalized > centers[^1];
    }

    private static Diagnostic Diagnostic(string code, string message, DiagnosticSeverity severity)
        => new()
        {
            Code = code,
            Subsystem = DiagnosticSubsystem.Adapter,
            Severity = severity,
            Stage = DiagnosticStage.Validation,
            IsBlocking = true,
            Message = message,
        };

    private static PdfEvidenceRegion Evidence(PdfDigitalPage page, IEnumerable<PdfDigitalWord> words)
    {
        var items = words.ToArray();
        return new PdfEvidenceRegion(page.PageNumber,
            items.Min(word => word.Left) / page.Width,
            items.Min(word => word.Bottom) / page.Height,
            items.Max(word => word.Right) / page.Width,
            items.Max(word => word.Top) / page.Height);
    }

    private static bool IsYearLike(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1900 and <= 2200;

}

internal sealed record PdfVisualLine(IReadOnlyList<PdfDigitalWord> Words, double CenterLineNormalized);
