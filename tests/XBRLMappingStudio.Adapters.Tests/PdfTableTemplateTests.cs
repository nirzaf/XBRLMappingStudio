using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class PdfTableTemplateTests
{
    private static readonly IReadOnlyDictionary<string, int> PeriodHeaders =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["2025"] = 2025,
            ["2026"] = 2026,
        };

    [Fact]
    public void MultiPagePairedYears_KeepHeaderIdentityAndSkipRepeatedHeaders()
    {
        var first = Page(1, 0, Header(900),
            Row(800, "Revenue", ("100", 450), ("200", 800)),
            Row(750, "Expenses", ("60", 450), ("90", 800)));
        var second = Page(2, 0, Header(900),
            Row(800, "Profit", ("40", 450), ("110", 800)));
        var document = Document(first, second);
        var template = PdfTableTemplate.CreateFromFirstPage(document, 1, "Description", PeriodHeaders, true);

        var result = PdfTableTemplateReconstructor.Reconstruct(document, template);

        Assert.Equal(PdfTableReconstructionState.Anchored, result.State);
        Assert.Equal(new[] { "Revenue", "Expenses", "Profit" }, result.Rows.Select(row => row.LabelText));
        Assert.Equal(2025, result.Rows[0].Cells[0].ReportingYear);
        Assert.Equal("100", result.Rows[0].Cells[0].RawText);
        Assert.Equal(2026, result.Rows[0].Cells[1].ReportingYear);
        Assert.Equal("200", result.Rows[0].Cells[1].RawText);
        Assert.Equal(2, result.Rows[2].Evidence.PageNumber);
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public void RotatedTextLine_UsesVerticalAxisWithoutSwappingYearColumns()
    {
        var page = Page(1, 90, VerticalHeader(900), VerticalRow(800, "Revenue", ("100", 450), ("200", 800)));
        var document = Document(page);
        var template = PdfTableTemplate.CreateFromFirstPage(document, 1, "Description", PeriodHeaders, true);

        var result = PdfTableTemplateReconstructor.Reconstruct(document, template);

        Assert.Equal(PdfTableColumnAxis.Vertical, template.ColumnAxis);
        Assert.Equal(PdfTableReconstructionState.Anchored, result.State);
        Assert.Equal("100", Assert.Single(result.Rows).Cells.Single(cell => cell.ReportingYear == 2025).RawText);
        Assert.Equal("200", result.Rows[0].Cells.Single(cell => cell.ReportingYear == 2026).RawText);
    }

    [Fact]
    public void ExplicitHighLineDirection_PreservesRowsOnOppositeSideOfRotatedHeader()
    {
        var page = Page(1, 270, VerticalHeader(500), VerticalRow(700, "Revenue", ("100", 450), ("200", 800)));
        var document = Document(page);
        var template = PdfTableTemplate.CreateFromFirstPage(document, 1, "Description", PeriodHeaders,
            rowsAtLowLineCoordinate: false);

        var result = PdfTableTemplateReconstructor.Reconstruct(document, template);

        Assert.Equal(PdfTableReconstructionState.Anchored, result.State);
        Assert.Equal("Revenue", Assert.Single(result.Rows).LabelText);
        Assert.Equal("100", result.Rows[0].Cells.Single(cell => cell.ReportingYear == 2025).RawText);
    }

    [Fact]
    public void MovedYearColumns_FailClosedInsteadOfSilentlySwappingValues()
    {
        var seed = Document(Page(1, 0, Header(900), Row(800, "Revenue", ("100", 450), ("200", 800))));
        var template = PdfTableTemplate.CreateFromFirstPage(seed, 1, "Description", PeriodHeaders, true);
        var drifted = Document(Page(1, 0, Header(900, year2025X: 540, year2026X: 875),
            Row(800, "Revenue", ("200", 540), ("100", 875))));

        var result = PdfTableTemplateReconstructor.Reconstruct(drifted, template);

        Assert.Equal(PdfTableReconstructionState.LayoutDrift, result.State);
        Assert.Empty(result.Rows);
        Assert.Equal(DiagnosticCodes.IoPdfTemplateDrift, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void ChangedYearHeading_FailsClosed()
    {
        var seed = Document(Page(1, 0, Header(900), Row(800, "Revenue", ("100", 450), ("200", 800))));
        var template = PdfTableTemplate.CreateFromFirstPage(seed, 1, "Description", PeriodHeaders, true);
        var changed = Document(Page(1, 0, new[]
        {
            Word("Description", 40, 160, 890, 910),
            Word("2025", 420, 480, 890, 910),
            Word("2027", 770, 830, 890, 910),
        }, Row(800, "Revenue", ("100", 450), ("200", 800))));

        var result = PdfTableTemplateReconstructor.Reconstruct(changed, template);

        Assert.Equal(PdfTableReconstructionState.LayoutDrift, result.State);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void RepeatedHeaderWithoutRowLabel_DoesNotCreateAnExtraDataRow()
    {
        var first = Page(1, 0, Header(900), Row(800, "Revenue", ("100", 450), ("200", 800)));
        var second = Page(2, 0, new[]
        {
            Word("QAR", 40, 160, 890, 910),
            Word("2025", 420, 480, 890, 910),
            Word("2026", 770, 830, 890, 910),
        }, Row(800, "Profit", ("40", 450), ("110", 800)));
        var document = Document(first, second);
        var template = PdfTableTemplate.CreateFromFirstPage(document, 1, "Description", PeriodHeaders, true);

        var result = PdfTableTemplateReconstructor.Reconstruct(document, template);

        Assert.Equal(PdfTableReconstructionState.NeedsReview, result.State);
        Assert.Equal(new[] { "Revenue", "Profit" }, result.Rows.Select(row => row.LabelText));
        Assert.Contains(result.Alternatives, alternative => alternative.Reason.Contains("Header-like", StringComparison.Ordinal));
    }

    [Fact]
    public void WordOverlappingTwoYearColumns_IsRetainedAsAnExplicitAlternative()
    {
        var seed = Document(Page(1, 0, Header(900), Row(800, "Revenue", ("100", 450), ("200", 800))));
        var template = PdfTableTemplate.CreateFromFirstPage(seed, 1, "Description", PeriodHeaders, true);
        var ambiguous = Document(Page(1, 0, Header(900), new[]
        {
            Word("Revenue", 40, 180, 790, 810),
            Word("120", 600, 650, 790, 810),
        }));

        var result = PdfTableTemplateReconstructor.Reconstruct(ambiguous, template);

        Assert.Equal(PdfTableReconstructionState.NeedsReview, result.State);
        Assert.Empty(result.Rows);
        var alternative = Assert.Single(result.Alternatives);
        Assert.Equal("120", alternative.Text);
        Assert.Equal(new[] { 2025, 2026 }, alternative.CandidateYears);
        Assert.Equal(DiagnosticCodes.IoPdfTemplateAmbiguous, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void TemplateRequiresUnambiguousPairedYearHeadersOnDeclaredFirstPage()
    {
        var document = Document(Page(1, 0, Header(900, year2025X: 450, year2026X: 450)));

        var error = Assert.Throws<AdapterException>(() =>
            PdfTableTemplate.CreateFromFirstPage(document, 1, "Description", PeriodHeaders, true));

        Assert.Equal(DiagnosticCodes.IoPdfTemplateInvalid, error.Code);
    }

    private static PdfDigitalDocument Document(params PdfDigitalPage[] pages)
        => new("sha256:" + new string('a', 64), pages.Sum(page => page.Words.Count), pages);

    private static PdfDigitalPage Page(int number, int rotation, params PdfDigitalWord[][] lines)
        => new(number, rotation, 1000, 1000, string.Empty, lines.SelectMany(line => line).ToArray());

    private static PdfDigitalWord[] Header(
        double y,
        double labelX = 100,
        double year2025X = 450,
        double year2026X = 800)
        => new[]
        {
            Word("Description", labelX - 60, labelX + 60, y - 10, y + 10),
            Word("2025", year2025X - 30, year2025X + 30, y - 10, y + 10),
            Word("2026", year2026X - 30, year2026X + 30, y - 10, y + 10),
        };

    private static PdfDigitalWord[] Row(double y, string label, params (string Text, double X)[] values)
        => new[] { Word(label, 40, 180, y - 10, y + 10) }
            .Concat(values.Select(value => Word(value.Text, value.X - 25, value.X + 25, y - 10, y + 10)))
            .ToArray();

    private static PdfDigitalWord[] VerticalHeader(double x)
        => new[]
        {
            Word("Description", x - 10, x + 10, 40, 160),
            Word("2025", x - 10, x + 10, 420, 480),
            Word("2026", x - 10, x + 10, 770, 830),
        };

    private static PdfDigitalWord[] VerticalRow(double x, string label, params (string Text, double Y)[] values)
        => new[] { Word(label, x - 10, x + 10, 40, 160) }
            .Concat(values.Select(value => Word(value.Text, x - 10, x + 10, value.Y - 25, value.Y + 25)))
            .ToArray();

    private static PdfDigitalWord Word(string text, double left, double right, double bottom, double top)
        => new(text, left, bottom, right, top);
}
