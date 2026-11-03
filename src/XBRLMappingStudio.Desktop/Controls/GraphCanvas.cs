using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using XBRLMappingStudio.Desktop.ViewModels;

namespace XBRLMappingStudio.Desktop.Controls;

/// <summary>
/// Graph canvas prototype (UI02). Draws nodes and edges with pan/zoom, node drag,
/// and selection. Kept intentionally small: Nodify.Avalonia is not available in this
/// environment (see risk mitigation table), so the canvas is drawn directly.
/// </summary>
public sealed class GraphCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<NodeViewModel>?> NodesProperty =
        AvaloniaProperty.Register<GraphCanvas, IReadOnlyList<NodeViewModel>?>(nameof(Nodes));

    public static readonly StyledProperty<IReadOnlyList<EdgeViewModel>?> EdgesProperty =
        AvaloniaProperty.Register<GraphCanvas, IReadOnlyList<EdgeViewModel>?>(nameof(Edges));

    public IReadOnlyList<NodeViewModel>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IReadOnlyList<EdgeViewModel>? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    private const double NodeWidth = 180;
    private const double NodeHeight = 64;

    private Vector _pan = default;
    private double _zoom = 1.0;
    private Point? _dragStart;
    private NodeViewModel? _draggedNode;

    static GraphCanvas()
    {
        AffectsRender<GraphCanvas>(NodesProperty, EdgesProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        var background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        ctx.DrawRectangle(background, null, new Rect(Bounds.Size));

        var transform = Matrix.CreateTranslation(_pan.X, _pan.Y) * Matrix.CreateScale(_zoom, _zoom);
        using (ctx.PushTransform(transform))
        {
            var edgePen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1.5);
            if (Edges != null)
            {
                foreach (var e in Edges)
                {
                    if (e.From is null || e.To is null) continue;
                    var p1 = new Point(e.From.X + NodeWidth, e.From.Y + NodeHeight / 2);
                    var p2 = new Point(e.To.X, e.To.Y + NodeHeight / 2);
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(p1, false);
                        var midX = (p1.X + p2.X) / 2;
                        g.LineTo(new Point(midX, p1.Y));
                        g.LineTo(new Point(midX, p2.Y));
                        g.LineTo(p2);
                    }
                    ctx.DrawGeometry(null, edgePen, geometry);
                }
            }

            if (Nodes != null)
            {
                foreach (var n in Nodes)
                {
                    var fill = n.IsSelected
                        ? new SolidColorBrush(Color.FromRgb(0xDD, 0xEB, 0xFF))
                        : new SolidColorBrush(Colors.White);
                    var border = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1.2);
                    var rect = new Rect(n.X, n.Y, NodeWidth, NodeHeight);
                    ctx.DrawRectangle(fill, border, new RoundedRect(rect, 6));

                    var ft = new FormattedText(
                        n.DisplayName,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Sans-Serif"),
                        13,
                        new SolidColorBrush(Colors.Black));
                    ctx.DrawText(ft, new Point(n.X + 8, n.Y + 6));

                    var sub = new FormattedText(
                        n.NodeType,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Sans-Serif"),
                        11,
                        new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)));
                    ctx.DrawText(sub, new Point(n.X + 8, n.Y + 28));
                }
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        _dragStart = p;
        _draggedNode = HitTestNode(p);
        if (_draggedNode is not null)
        {
            _draggedNode.IsSelected = true;
            foreach (var n in Nodes ?? Array.Empty<NodeViewModel>())
                if (!ReferenceEquals(n, _draggedNode)) n.IsSelected = false;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is null) return;
        var p = e.GetPosition(this);
        var delta = p - _dragStart.Value;
        if (_draggedNode is not null)
        {
            _draggedNode.X += delta.X / _zoom;
            _draggedNode.Y += delta.Y / _zoom;
        }
        else
        {
            _pan += delta;
        }
        _dragStart = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        _draggedNode = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var p = e.GetPosition(this);
        // Zoom about the cursor.
        _pan = p - (p - _pan) * factor;
        _zoom *= factor;
        _zoom = Math.Clamp(_zoom, 0.2, 3.0);
        InvalidateVisual();
    }

    private NodeViewModel? HitTestNode(Point p)
    {
        if (Nodes is null) return null;
        var local = (p - _pan) / _zoom;
        foreach (var n in Nodes)
        {
            if (local.X >= n.X && local.X <= n.X + NodeWidth
                && local.Y >= n.Y && local.Y <= n.Y + NodeHeight)
                return n;
        }
        return null;
    }
}
