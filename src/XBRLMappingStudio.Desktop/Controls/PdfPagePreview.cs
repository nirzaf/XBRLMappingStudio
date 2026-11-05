using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using XBRLMappingStudio.Adapters.Pdf;

namespace XBRLMappingStudio.Desktop.Controls;

/// <summary>
/// Read-only text-position page preview. A selected table cell is drawn over its
/// exact normalized evidence region; text remains separately available in the UI.
/// </summary>
public sealed class PdfPagePreview : Control
{
    public static readonly StyledProperty<PdfDigitalPage?> PageProperty =
        AvaloniaProperty.Register<PdfPagePreview, PdfDigitalPage?>(nameof(Page));

    public static readonly StyledProperty<PdfEvidenceRegion?> HighlightProperty =
        AvaloniaProperty.Register<PdfPagePreview, PdfEvidenceRegion?>(nameof(Highlight));

    public static readonly StyledProperty<double> ZoomFactorProperty =
        AvaloniaProperty.Register<PdfPagePreview, double>(nameof(ZoomFactor), 1.0);

    private const double PageScale = 0.75;
    private const double PageInset = 14;

    static PdfPagePreview()
    {
        AffectsRender<PdfPagePreview>(PageProperty, HighlightProperty, ZoomFactorProperty);
        AffectsMeasure<PdfPagePreview>(PageProperty, ZoomFactorProperty);
    }

    public PdfDigitalPage? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public PdfEvidenceRegion? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public double ZoomFactor
    {
        get => GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, Math.Clamp(value, 0.5, 3.0));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Page is not { Width: > 0, Height: > 0 } page)
            return new Size(300, 220);
        var zoom = double.IsFinite(ZoomFactor) ? Math.Clamp(ZoomFactor, 0.5, 3.0) : 1.0;
        return new Size(page.Width * PageScale * zoom + PageInset * 2,
            page.Height * PageScale * zoom + PageInset * 2);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)), null,
            new Rect(Bounds.Size));
        if (Page is not { Width: > 0, Height: > 0 } page) return;

        var usableWidth = Math.Max(1, Bounds.Width - PageInset * 2);
        var usableHeight = Math.Max(1, Bounds.Height - PageInset * 2);
        var scale = Math.Min(usableWidth / page.Width, usableHeight / page.Height);
        var pageWidth = page.Width * scale;
        var pageHeight = page.Height * scale;
        var left = (Bounds.Width - pageWidth) / 2;
        var top = (Bounds.Height - pageHeight) / 2;
        var pageRect = new Rect(left, top, pageWidth, pageHeight);
        var pageBorder = new Pen(new SolidColorBrush(Color.FromRgb(0x94, 0x9F, 0xAF)), 1);
        context.DrawRectangle(Brushes.White, pageBorder, pageRect);

        var textSize = Math.Clamp(12 * scale, 6, 18);
        var textBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x27, 0x35));
        foreach (var word in page.Words)
        {
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            var x = left + word.Left * scale;
            var y = top + (page.Height - word.Top) * scale;
            var text = new FormattedText(word.Text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Sans-Serif"), textSize, textBrush);
            context.DrawText(text, new Point(x, y));
        }

        if (Highlight is { } region && region.PageNumber == page.PageNumber
            && double.IsFinite(region.LeftNormalized) && double.IsFinite(region.BottomNormalized)
            && double.IsFinite(region.RightNormalized) && double.IsFinite(region.TopNormalized))
        {
            var highlight = new Rect(
                left + region.LeftNormalized * page.Width * scale,
                top + (1 - region.TopNormalized) * page.Height * scale,
                (region.RightNormalized - region.LeftNormalized) * page.Width * scale,
                (region.TopNormalized - region.BottomNormalized) * page.Height * scale);
            var fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xE3, 0x8A)) { Opacity = 0.38 };
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x4D, 0x00)), 2);
            context.DrawRectangle(fill, outline, highlight);
        }
    }
}
