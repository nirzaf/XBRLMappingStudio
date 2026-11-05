using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Desktop.Controls;

/// <summary>
/// Graph canvas with typed port connections, target-port reconnection, multi-select,
/// pointer/keyboard movement and pan/zoom. Graph edits are delegated to the portable
/// <see cref="DesignerSession"/> so they remain undoable and testable without Avalonia.
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

    /// <summary>Raised when a graph gesture is refused so the shell can explain why.</summary>
    public event Action<string>? InteractionRejected;

    internal double ZoomFactor => _zoom;
    internal Vector PanOffset => _pan;

    private const double NodeWidth = DesignerSession.NodeWidth;
    private const double PortRadius = 6;
    private const double PortRowSpacing = 18;
    private Vector _pan;
    private double _zoom = 1;
    private bool _spaceDown;

    private enum Mode { None, Pan, DragNode, RubberBand, Connect, Reconnect }
    private Mode _mode;
    private Point _pointerStartScreen;
    private Point _dragStart;
    private Vector _panAtStart;
    private NodeViewModel? _draggedNode;
    private readonly Dictionary<string, Point> _dragOrigins = new(StringComparer.Ordinal);
    private NodeViewModel? _connectSource;
    private PortDefinition? _connectSourcePort;
    private string? _reconnectEdgeId;
    private Point? _rubberEnd;
    private Point? _connectEnd;

    private sealed record PortHit(NodeViewModel Node, PortDefinition Port);

    static GraphCanvas()
    {
        AffectsRender<GraphCanvas>(NodesProperty, EdgesProperty);
    }

    public GraphCanvas() => Focusable = true;

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)), null,
            new Rect(Bounds.Size));

        var transform = Matrix.CreateTranslation(_pan.X, _pan.Y) * Matrix.CreateScale(_zoom, _zoom);
        using (ctx.PushTransform(transform))
        {
            var edgePen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1.5);
            if (Edges is not null)
            {
                foreach (var edge in Edges)
                {
                    if (edge.From is null || edge.To is null) continue;
                    DrawEdge(ctx, edgePen, edge);
                }
            }

            if (_mode is Mode.Connect or Mode.Reconnect
                && _connectSource is not null && _connectSourcePort is not null && _connectEnd is not null)
            {
                var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5)), 1.5)
                { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                DrawBezier(ctx, previewPen,
                    PortRight(_connectSource, _connectSourcePort.Name), _connectEnd.Value);
            }

            if (Nodes is not null)
                foreach (var node in Nodes) DrawNode(ctx, node);

            if (_mode == Mode.RubberBand && _rubberEnd is not null)
            {
                var fill = new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5)) { Opacity = 0.12 };
                var border = new Pen(new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5)), 1);
                ctx.DrawRectangle(fill, border, new Rect(_dragStart, _rubberEnd.Value));
            }
        }
    }

    private static void DrawEdge(DrawingContext ctx, Pen pen, EdgeViewModel edge)
        => DrawBezier(ctx, pen,
            PortRight(edge.From!, edge.SourcePortName), PortLeft(edge.To!, edge.TargetPortName));

    private static void DrawBezier(DrawingContext ctx, Pen pen, Point from, Point to)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(from, false);
            var midX = (from.X + to.X) / 2;
            path.LineTo(new Point(midX, from.Y));
            path.LineTo(new Point(midX, to.Y));
            path.LineTo(to);
        }
        ctx.DrawGeometry(null, pen, geometry);
    }

    private void DrawNode(DrawingContext ctx, NodeViewModel node)
    {
        var fill = node.IsSelected
            ? new SolidColorBrush(Color.FromRgb(0xDD, 0xEB, 0xFF))
            : new SolidColorBrush(Colors.White);
        var border = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1.2);
        var rect = new Rect(node.X, node.Y, NodeWidth, node.Height);
        ctx.DrawRectangle(fill, border, new RoundedRect(rect, 6));

        DrawText(ctx, node.DisplayName, new Point(node.X + 8, node.Y + 6), 13, Colors.Black);
        DrawText(ctx, node.NodeType, new Point(node.X + 8, node.Y + 28), 11,
            Color.FromRgb(0x60, 0x60, 0x60));

        var portBrush = new SolidColorBrush(Color.FromRgb(0x29, 0x62, 0xB5));
        for (var i = 0; i < node.InputPorts.Count; i++)
        {
            var port = node.InputPorts[i];
            var point = PortLeft(node, port.Name);
            ctx.DrawEllipse(portBrush, null, point, PortRadius, PortRadius);
            DrawPortLabel(ctx, port, new Point(point.X + 8, point.Y - 8), rightAligned: false);
        }
        for (var i = 0; i < node.OutputPorts.Count; i++)
        {
            var port = node.OutputPorts[i];
            var point = PortRight(node, port.Name);
            ctx.DrawEllipse(portBrush, null, point, PortRadius, PortRadius);
            DrawPortLabel(ctx, port, new Point(point.X - 8, point.Y - 8), rightAligned: true);
        }
    }

    private static void DrawPortLabel(DrawingContext ctx, PortDefinition port, Point point, bool rightAligned)
    {
        var label = $"{port.Name} · {port.Shape}";
        var formatted = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Sans-Serif"), 9,
            new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45)));
        ctx.DrawText(formatted, rightAligned
            ? new Point(point.X - formatted.Width, point.Y)
            : point);
    }

    private static void DrawText(DrawingContext ctx, string value, Point point, double size, Color color)
    {
        var formatted = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Sans-Serif"), size, new SolidColorBrush(color));
        ctx.DrawText(formatted, point);
    }

    private static Point PortLeft(NodeViewModel node, string name)
    {
        var index = IndexOf(node.InputPorts, name);
        return new Point(node.X, node.Y + PortY(node, index, node.InputPorts.Count));
    }

    private static Point PortRight(NodeViewModel node, string name)
    {
        var index = IndexOf(node.OutputPorts, name);
        return new Point(node.X + NodeWidth, node.Y + PortY(node, index, node.OutputPorts.Count));
    }

    private static int IndexOf(IReadOnlyList<PortDefinition> ports, string name)
    {
        for (var i = 0; i < ports.Count; i++)
            if (StringComparer.Ordinal.Equals(ports[i].Name, name)) return i;
        return -1;
    }

    private static double PortY(NodeViewModel node, int index, int count)
    {
        if (index < 0 || count == 0) return node.Height / 2;
        return Math.Max(46, node.Height / 2 - (count - 1) * PortRowSpacing / 2)
            + index * PortRowSpacing;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var screen = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        Focus();

        if (properties.IsMiddleButtonPressed || _spaceDown)
        {
            _mode = Mode.Pan;
            _pointerStartScreen = screen;
            _panAtStart = _pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;

        var point = ToGraph(screen);
        _pointerStartScreen = screen;
        _dragStart = point;

        var output = HitTestPort(point, output: true);
        if (output is not null)
        {
            BeginConnection(output);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var connectedInput = HitTestPort(point, output: false);
        if (connectedInput is not null)
        {
            var edge = Edges?.FirstOrDefault(candidate => candidate.To?.NodeId == connectedInput.Node.NodeId
                && StringComparer.Ordinal.Equals(candidate.TargetPortName, connectedInput.Port.Name));
            if (edge is not null && edge.From is not null)
            {
                var sourcePort = edge.From.OutputPorts.FirstOrDefault(port =>
                    StringComparer.Ordinal.Equals(port.Name, edge.SourcePortName));
                if (sourcePort is not null)
                {
                    _reconnectEdgeId = edge.EdgeId;
                    _connectSource = edge.From;
                    _connectSourcePort = sourcePort;
                    _connectEnd = point;
                    _mode = Mode.Reconnect;
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
        }

        var node = HitTestNode(point);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (node is not null)
        {
            if (shift) Session?.ToggleSelection(node.NodeId);
            else if (!node.IsSelected) Session?.Select(node.NodeId, additive: false);
            _mode = Mode.DragNode;
            _draggedNode = node;
            _dragOrigins.Clear();
            foreach (var selected in Nodes ?? Array.Empty<NodeViewModel>())
                if (selected.IsSelected) _dragOrigins[selected.NodeId] = new Point(selected.X, selected.Y);
        }
        else
        {
            if (!shift) Session?.ClearSelection();
            _mode = Mode.RubberBand;
            _rubberEnd = point;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    private void BeginConnection(PortHit source)
    {
        _mode = Mode.Connect;
        _connectSource = source.Node;
        _connectSourcePort = source.Port;
        _connectEnd = ToGraph(_pointerStartScreen);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_mode == Mode.None) return;
        var screen = e.GetPosition(this);
        var point = ToGraph(screen);
        var screenDelta = screen - _pointerStartScreen;

        switch (_mode)
        {
            case Mode.Pan:
                _pan = _panAtStart + screenDelta;
                break;
            case Mode.DragNode:
                foreach (var node in Nodes ?? Array.Empty<NodeViewModel>())
                {
                    if (!_dragOrigins.TryGetValue(node.NodeId, out var origin)) continue;
                    node.X = origin.X + screenDelta.X / _zoom;
                    node.Y = origin.Y + screenDelta.Y / _zoom;
                }
                break;
            case Mode.RubberBand:
                _rubberEnd = point;
                break;
            case Mode.Connect:
            case Mode.Reconnect:
                _connectEnd = point;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = ToGraph(e.GetPosition(this));
        switch (_mode)
        {
            case Mode.DragNode when _draggedNode is not null:
                var positions = (Nodes ?? Array.Empty<NodeViewModel>())
                    .Where(node => _dragOrigins.ContainsKey(node.NodeId))
                    .ToDictionary(node => node.NodeId, node => new LayoutPosition(node.X, node.Y),
                        StringComparer.Ordinal);
                Session?.MoveNodesTo(positions);
                break;
            case Mode.RubberBand:
                Session?.RubberBandSelect(_dragStart.X, _dragStart.Y,
                    point.X - _dragStart.X, point.Y - _dragStart.Y,
                    additive: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
            case Mode.Connect when _connectSource is not null && _connectSourcePort is not null:
                var target = HitTestPort(point, output: false);
                if (target is null)
                    Reject("Drop the connection on a named input port.");
                else if (Session is not null && !Session.TryConnect(_connectSource.NodeId, target.Node.NodeId,
                             _connectSourcePort.Name, target.Port.Name, out var connectReason))
                {
                    if (connectReason is not null) Reject(connectReason);
                }
                break;
            case Mode.Reconnect when _reconnectEdgeId is not null:
                var replacement = HitTestPort(point, output: false);
                if (replacement is null)
                    Reject("Drop the edge on a named input port to reconnect it.");
                else if (Session is not null && !Session.TryReconnectEdge(_reconnectEdgeId,
                             replacement.Node.NodeId, replacement.Port.Name, out var reconnectReason))
                {
                    if (reconnectReason is not null) Reject(reconnectReason);
                }
                break;
        }

        ResetPointerGesture();
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var point = e.GetPosition(this);
        _pan = point - (point - _pan) * factor;
        _zoom = Math.Clamp(_zoom * factor, 0.2, 3);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                e.Handled = true;
                break;
            case Key.Escape when _mode != Mode.None:
                foreach (var node in Nodes ?? Array.Empty<NodeViewModel>())
                    if (_dragOrigins.TryGetValue(node.NodeId, out var origin))
                    { node.X = origin.X; node.Y = origin.Y; }
                ResetPointerGesture();
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Home:
                _pan = default;
                _zoom = 1;
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.A when control:
                Session?.SelectAll();
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Z when control && shift:
                Session?.Redo();
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Z when control:
                Session?.Undo();
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Y when control:
                Session?.Redo();
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Delete:
                Session?.DeleteSelected();
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var direction = e.Key switch
                {
                    Key.Left => new Vector(-1, 0),
                    Key.Right => new Vector(1, 0),
                    Key.Up => new Vector(0, -1),
                    _ => new Vector(0, 1)
                };
                if (control)
                    _pan += direction * 32;
                else
                    Session?.MoveSelectedBy(direction.X * (shift ? 50 : 10),
                        direction.Y * (shift ? 50 : 10));
                InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) _spaceDown = false;
    }

    private PortHit? HitTestPort(Point point, bool output)
    {
        foreach (var node in EnumerateFrontToBack())
        {
            var ports = output ? node.OutputPorts : node.InputPorts;
            foreach (var port in ports)
            {
                var center = output ? PortRight(node, port.Name) : PortLeft(node, port.Name);
                if (Math.Abs(point.X - center.X) <= PortRadius + 3
                    && Math.Abs(point.Y - center.Y) <= PortRadius + 3)
                    return new PortHit(node, port);
            }
        }
        return null;
    }

    private NodeViewModel? HitTestNode(Point point)
    {
        foreach (var node in EnumerateFrontToBack())
            if (point.X >= node.X && point.X <= node.X + NodeWidth
                && point.Y >= node.Y && point.Y <= node.Y + node.Height)
                return node;
        return null;
    }

    private IEnumerable<NodeViewModel> EnumerateFrontToBack()
    {
        var nodes = Nodes;
        if (nodes is null) yield break;
        for (var i = nodes.Count - 1; i >= 0; i--) yield return nodes[i];
    }

    private Point ToGraph(Point screen) => (screen - _pan) / _zoom;

    private void Reject(string message) => InteractionRejected?.Invoke(message);

    private void ResetPointerGesture()
    {
        _mode = Mode.None;
        _draggedNode = null;
        _dragOrigins.Clear();
        _connectSource = null;
        _connectSourcePort = null;
        _reconnectEdgeId = null;
        _rubberEnd = null;
        _connectEnd = null;
    }
}
