using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using XBRLMappingStudio.Desktop.ViewModels;

namespace XBRLMappingStudio.Desktop.Controls;

/// <summary>
/// Graph canvas (UI02). Draws nodes and edges with pan/zoom, node drag, rubber-band
/// multi-select and port-to-port connection. Editing intent is delegated to the portable
/// <see cref="DesignerSession"/> so the behaviour is identical whether driven by mouse,
/// keyboard or tests. Nodify.Avalonia is unavailable in this environment (see the plan's
/// risk table), so the canvas is drawn directly.
/// </summary>
public sealed class GraphCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<NodeViewModel>?> NodesProperty =
        AvaloniaProperty.Register<GraphCanvas, IReadOnlyList<NodeViewModel>?>(nameof(Nodes));

    public static readonly StyledProperty<IReadOnlyList<EdgeViewModel>?> EdgesProperty =
        AvaloniaProperty.Register<GraphCanvas, IReadOnlyList<EdgeViewModel>?>(nameof(Edges));

    public static readonly StyledProperty<DesignerSession?> SessionProperty =
        AvaloniaProperty.Register<GraphCanvas, DesignerSession?>(nameof(Session));

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

    public DesignerSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    private const double NodeWidth = DesignerSession.NodeWidth;
    private const double NodeHeight = DesignerSession.NodeHeight;
    private const double PortRadius = 6;

    private Vector _pan = default;
    private double _zoom = 1.0;

    private enum Mode { None, Pan, DragNode, RubberBand, Connect }
    private Mode _mode = Mode.None;
    private Point _dragStart;
    private NodeViewModel? _draggedNode;
    private NodeViewModel? _connectSource;
    private Point? _rubberEnd;
    private Point? _connectEnd;

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
                    DrawEdge(ctx, edgePen, e.From, e.To);
                }
            }

            // In-progress connection preview.
            if (_mode == Mode.Connect && _connectSource is not null && _connectEnd is not null)
            {
                var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5)), 1.5)
                { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                var from = PortRight(_connectSource);
                DrawBezier(ctx, previewPen, from, _connectEnd.Value);
            }

            if (Nodes != null)
            {
                foreach (var n in Nodes)
                    DrawNode(ctx, n);
            }

            // Rubber-band selection rectangle.
            if (_mode == Mode.RubberBand && _rubberEnd is not null)
            {
                var fill = new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5)) { Opacity = 0.12 };
                var border = new Pen(new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5)), 1);
                var r = new Rect(_dragStart, _rubberEnd.Value);
                ctx.DrawRectangle(fill, border, r);
            }
        }
    }

    private static void DrawEdge(DrawingContext ctx, Pen pen, NodeViewModel from, NodeViewModel to)
        => DrawBezier(ctx, pen, PortRight(from), PortLeft(to));

    private static void DrawBezier(DrawingContext ctx, Pen pen, Point p1, Point p2)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(p1, false);
            var midX = (p1.X + p2.X) / 2;
            g.LineTo(new Point(midX, p1.Y));
            g.LineTo(new Point(midX, p2.Y));
            g.LineTo(p2);
        }
        ctx.DrawGeometry(null, pen, geometry);
    }

    private void DrawNode(DrawingContext ctx, NodeViewModel n)
    {
        var fill = n.IsSelected
            ? new SolidColorBrush(Color.FromRgb(0xDD, 0xEB, 0xFF))
            : new SolidColorBrush(Colors.White);
        var border = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1.2);
        var rect = new Rect(n.X, n.Y, NodeWidth, NodeHeight);
        ctx.DrawRectangle(fill, border, new RoundedRect(rect, 6));

        var ft = new FormattedText(
            n.DisplayName, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Sans-Serif"), 13,
            new SolidColorBrush(Colors.Black));
        ctx.DrawText(ft, new Point(n.X + 8, n.Y + 6));

        var sub = new FormattedText(
            n.NodeType, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Sans-Serif"), 11,
            new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)));
        ctx.DrawText(sub, new Point(n.X + 8, n.Y + 28));

        // Port handles.
        var portBrush = new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5));
        ctx.DrawEllipse(portBrush, null, PortLeft(n), PortRadius, PortRadius);
        ctx.DrawEllipse(portBrush, null, PortRight(n), PortRadius, PortRadius);
    }

    private static Point PortLeft(NodeViewModel n) => new(n.X, n.Y + NodeHeight / 2);
    private static Point PortRight(NodeViewModel n) => new(n.X + NodeWidth, n.Y + NodeHeight / 2);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = ToGraph(e.GetPosition(this));
        _dragStart = p;

        // Port-to-port connection starts if pressed near a node's output port.
        var portNode = HitTestOutputPort(p);
        if (portNode is not null)
        {
            _mode = Mode.Connect;
            _connectSource = portNode;
            _connectEnd = p;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        var node = HitTestNode(p);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (node is not null)
        {
            if (shift) Session?.ToggleSelection(node.NodeId);
            else if (!node.IsSelected) Session?.Select(node.NodeId, additive: false);
            _mode = Mode.DragNode;
            _draggedNode = node;
        }
        else
        {
            if (!shift) Session?.ClearSelection();
            _mode = Mode.RubberBand;
            _rubberEnd = p;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_mode == Mode.None) return;
        var p = ToGraph(e.GetPosition(this));
        var deltaScreen = e.GetPosition(this) - ToScreen(_dragStart);

        switch (_mode)
        {
            case Mode.DragNode when _draggedNode is not null:
                // Move every selected node together (multi-drag).
                foreach (var n in Nodes ?? Array.Empty<NodeViewModel>())
                {
                    if (!n.IsSelected) continue;
                    var nx = n.X + deltaScreen.X / _zoom;
                    var ny = n.Y + deltaScreen.Y / _zoom;
                    if (Session is not null) Session.MoveNode(n.NodeId, nx, ny);
                    else { n.X = nx; n.Y = ny; }
                }
                _dragStart = p;
                break;
            case Mode.RubberBand:
                _rubberEnd = p;
                break;
            case Mode.Connect:
                _connectEnd = p;
                break;
            default:
                _pan += e.GetPosition(this) - ToScreen(_dragStart);
                _dragStart = p;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = ToGraph(e.GetPosition(this));
        switch (_mode)
        {
            case Mode.RubberBand:
                Session?.RubberBandSelect(_dragStart.X, _dragStart.Y,
                    p.X - _dragStart.X, p.Y - _dragStart.Y,
                    additive: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
            case Mode.Connect when _connectSource is not null:
                var target = HitTestNode(p);
                if (target is not null && target.NodeId != _connectSource.NodeId)
                    Session?.Connect(_connectSource.NodeId, target.NodeId);
                break;
        }
        _mode = Mode.None;
        _draggedNode = null;
        _connectSource = null;
        _rubberEnd = null;
        _connectEnd = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var p = e.GetPosition(this);
        _pan = p - (p - _pan) * factor;
        _zoom *= factor;
        _zoom = Math.Clamp(_zoom, 0.2, 3.0);
        InvalidateVisual();
    }

    private Point ToGraph(Point screen) => (screen - _pan) / _zoom;
    private Point ToScreen(Point graph) => graph * _zoom + _pan;

    private NodeViewModel? HitTestNode(Point graph)
    {
        foreach (var n in EnumerateFrontToBack())
        {
            if (graph.X >= n.X && graph.X <= n.X + NodeWidth
                && graph.Y >= n.Y && graph.Y <= n.Y + NodeHeight)
                return n;
        }
        return null;
    }

    private NodeViewModel? HitTestOutputPort(Point graph)
    {
        foreach (var n in EnumerateFrontToBack())
        {
            var port = PortRight(n);
            if (Math.Abs(graph.X - port.X) <= PortRadius + 2 && Math.Abs(graph.Y - port.Y) <= PortRadius + 2)
                return n;
        }
        return null;
    }

    // Later nodes draw on top, so hit-test them first.
    private IEnumerable<NodeViewModel> EnumerateFrontToBack()
    {
        var list = Nodes;
        if (list is null) yield break;
        for (int i = list.Count - 1; i >= 0; i--) yield return list[i];
    }
}
