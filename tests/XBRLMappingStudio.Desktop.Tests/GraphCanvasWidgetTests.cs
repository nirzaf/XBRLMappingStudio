using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using XBRLMappingStudio.Desktop;
using XBRLMappingStudio.Desktop.Controls;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

[assembly: AvaloniaTestApplication(typeof(XBRLMappingStudio.Desktop.Tests.GraphCanvasTestAppBuilder))]

namespace XBRLMappingStudio.Desktop.Tests;

public sealed class GraphCanvasTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Headless widget coverage for actual pointer and keyboard dispatch.</summary>
public sealed class GraphCanvasWidgetTests
{
    [AvaloniaFact]
    public void TypedPortConnectionAndReconnectionUseTheNamedHandles()
    {
        var project = Project();
        var session = new DesignerSession(project);
        var (window, canvas) = Show(session);
        Assert.True(canvas.Focusable);

        window.MouseDown(new Point(180, 46), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(240, 46), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(240, 46), MouseButton.Left, RawInputModifiers.None);

        var connected = Assert.Single(session.Project.Edges);
        Assert.Equal("amount", connected.SourcePortName);
        Assert.Equal("amount", connected.TargetPortName);

        window.MouseDown(new Point(240, 46), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(480, 46), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(480, 46), MouseButton.Left, RawInputModifiers.None);

        var reconnected = Assert.Single(session.Project.Edges);
        Assert.Equal("n2", reconnected.TargetNodeId);
        Assert.Equal("replacement", reconnected.TargetPortName);
        window.Close();
    }

    [AvaloniaFact]
    public void InvalidShapeConnectionIsBlockedAndExplained()
    {
        var project = Project();
        var targetIndex = project.Nodes.FindIndex(node => node.NodeId == "n1");
        project.Nodes[targetIndex] = project.Nodes[targetIndex] with
        {
            InputPorts = new() { new PortDefinition("amount", PortShape.String, PortCardinality.Single) }
        };
        var session = new DesignerSession(project);
        var (window, canvas) = Show(session);
        string? rejection = null;
        canvas.InteractionRejected += message => rejection = message;

        window.MouseDown(new Point(180, 46), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(240, 46), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(240, 46), MouseButton.Left, RawInputModifiers.None);

        Assert.Empty(session.Project.Edges);
        Assert.Contains("Cannot connect Decimal", rejection, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void KeyboardNudgeUndoRedoAndPanZoomAreDispatchable()
    {
        var session = new DesignerSession(Project());
        var digest = session.Project.ComputeSemanticDigest();
        session.Select("n0", additive: false);
        var (window, canvas) = Show(session);
        canvas.Focus();

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(10, session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);
        Assert.Equal(digest, session.Project.ComputeSemanticDigest());
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(0, session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Assert.Equal(10, session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);

        window.MouseDown(new Point(300, 200), MouseButton.Middle, RawInputModifiers.None);
        window.MouseMove(new Point(340, 220), RawInputModifiers.MiddleMouseButton);
        window.MouseUp(new Point(340, 220), MouseButton.Middle, RawInputModifiers.None);
        Assert.Equal(new Vector(40, 20), canvas.PanOffset);

        window.MouseWheel(new Point(300, 150), new Vector(0, 1), RawInputModifiers.None);
        Assert.True(canvas.ZoomFactor > 1);
        canvas.Focus();
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, "Home");
        Assert.Equal(1, canvas.ZoomFactor);
        Assert.Equal(default, canvas.PanOffset);
        window.Close();
    }

    [AvaloniaFact]
    public void ShiftMultiSelectMovesTogetherAndPointerDragIsOneUndoStep()
    {
        var session = new DesignerSession(Project());
        var digest = session.Project.ComputeSemanticDigest();
        var (window, _) = Show(session);

        window.MouseDown(new Point(100, 25), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(new Point(100, 25), MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(new Point(280, 25), MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(new Point(280, 25), MouseButton.Left, RawInputModifiers.Shift);
        Assert.Equal(new[] { "n0", "n1" }, session.SelectedNodeIds.OrderBy(id => id));

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(10, session.Project.Nodes.Single(node => node.NodeId == "n0").Layout!.X);
        Assert.Equal(250, session.Project.Nodes.Single(node => node.NodeId == "n1").Layout!.X);

        window.MouseDown(new Point(100, 25), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(130, 35), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(130, 35), MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(new LayoutPosition(40, 10), session.Project.Nodes.Single(node => node.NodeId == "n0").Layout);
        Assert.Equal(new LayoutPosition(280, 10), session.Project.Nodes.Single(node => node.NodeId == "n1").Layout);
        Assert.Equal(digest, session.Project.ComputeSemanticDigest());

        session.Undo();
        Assert.Equal(new LayoutPosition(10, 0), session.Project.Nodes.Single(node => node.NodeId == "n0").Layout);
        Assert.Equal(new LayoutPosition(250, 0), session.Project.Nodes.Single(node => node.NodeId == "n1").Layout);
        session.Undo();
        Assert.Equal(new LayoutPosition(0, 0), session.Project.Nodes.Single(node => node.NodeId == "n0").Layout);
        Assert.Equal(new LayoutPosition(240, 0), session.Project.Nodes.Single(node => node.NodeId == "n1").Layout);
        Assert.Equal(digest, session.Project.ComputeSemanticDigest());
        window.Close();
    }

    private static (Window Window, GraphCanvas Canvas) Show(DesignerSession session)
    {
        var canvas = new GraphCanvas
        {
            Nodes = session.Nodes,
            Edges = session.Edges,
            Session = session,
            Width = 600,
            Height = 300
        };
        var window = new Window { Width = 600, Height = 300, Content = canvas };
        window.Show();
        canvas.Focus();
        return (window, canvas);
    }

    private static MappingProject Project()
    {
        var profile = SyntheticProfile.Build();
        return new MappingProject
        {
            ProjectId = Id.New(),
            Name = "Graph canvas widget",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest,
            Nodes = new List<MappingNode>
            {
                new()
                {
                    NodeId = "n0", NodeType = "source", DisplayName = "Amount source",
                    OutputPorts = new() { new PortDefinition("amount", PortShape.Decimal, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 0)
                },
                new()
                {
                    NodeId = "n1", NodeType = "transform", DisplayName = "Amount target",
                    InputPorts = new() { new PortDefinition("amount", PortShape.Decimal, PortCardinality.Single) },
                    Layout = new LayoutPosition(240, 0)
                },
                new()
                {
                    NodeId = "n2", NodeType = "transform", DisplayName = "Replacement target",
                    InputPorts = new() { new PortDefinition("replacement", PortShape.Decimal, PortCardinality.Single) },
                    Layout = new LayoutPosition(480, 0)
                }
            }
        };
    }
}
