using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Desktop.ViewModels;

/// <summary>
/// Portable editing session behind the desktop designer (UI02). It owns the
/// <see cref="MappingProject"/> and mirrors its nodes/edges into view-models, while
/// implementing selection (incl. rubber-band multi-select), reconnection, deletion and
/// a snapshot-based undo/redo stack.
///
/// The class deliberately uses only plain doubles for geometry (no Avalonia types) so the
/// whole editing model is unit-testable headlessly. Crucially, moving a node changes only
/// <see cref="MappingNode.Layout"/>, so the project's semantic digest is preserved
/// (BASE03/MAP01 acceptance: layout generation is independent of semantic revision).
/// </summary>
public sealed class DesignerSession
{
    public const double NodeWidth = 180;
    public const double NodeHeight = 64;

    private MappingProject _project;
    private readonly List<NodeViewModel> _nodes = new();
    private readonly List<EdgeViewModel> _edges = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    // Snapshot stacks holding canonical JSON of the project before/after each edit.
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    public DesignerSession(MappingProject project)
    {
        _project = project;
        RebuildViewModels(captureSnapshot: false);
    }

    public MappingProject Project => _project;
    public IReadOnlyList<NodeViewModel> Nodes => _nodes;
    public IReadOnlyList<EdgeViewModel> Edges => _edges;
    public IReadOnlyCollection<string> SelectedNodeIds => _selected;

    /// <summary>Raised after any operation that changes the visible graph.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---- selection --------------------------------------------------------------

    public void Select(string nodeId, bool additive)
    {
        if (!additive) _selected.Clear();
        _selected.Add(nodeId);
        SyncSelectionFlags();
    }

    public void ToggleSelection(string nodeId)
    {
        if (!_selected.Remove(nodeId)) _selected.Add(nodeId);
        SyncSelectionFlags();
    }

    public void ClearSelection()
    {
        _selected.Clear();
        SyncSelectionFlags();
    }

    public void SelectAll()
    {
        _selected.Clear();
        foreach (var n in _nodes) _selected.Add(n.NodeId);
        SyncSelectionFlags();
    }

    /// <summary>Rubber-band multi-select: selects every node whose rectangle intersects
    /// the given drag window (in graph coordinates). Non-destructive unless <paramref
    /// name="additive"/> is false.</summary>
    public int RubberBandSelect(double x, double y, double w, double h, bool additive = false)
    {
        if (!additive) _selected.Clear();
        double l = Math.Min(x, x + w), r = Math.Max(x, x + w);
        double t = Math.Min(y, y + h), b = Math.Max(y, y + h);
        int hits = 0;
        foreach (var n in _nodes)
        {
            bool overlaps = n.X < r && n.X + NodeWidth > l && n.Y < b && n.Y + NodeHeight > t;
            if (overlaps && _selected.Add(n.NodeId)) hits++;
        }
        SyncSelectionFlags();
        return hits;
    }

    // ---- layout (non-semantic) --------------------------------------------------

    /// <summary>Move a node. Mutates only <see cref="MappingNode.Layout"/>, so the
    /// semantic digest is unchanged. Undoable (restores the prior position).</summary>
    public void MoveNode(string nodeId, double x, double y)
    {
        var node = _project.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        var vm = _nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null || vm is null) return;

        // Layout-only edit: capture a snapshot so undo returns the position, but the
        // executable digest is provably unaffected.
        PushUndo();
        node.Layout = new LayoutPosition(x, y);
        vm.X = x;
        vm.Y = y;
        Changed?.Invoke();
    }

    // ---- graph topology (semantic) ----------------------------------------------

    public void Connect(string sourceNodeId, string targetNodeId,
        string sourcePort = "out", string targetPort = "in")
    {
        if (sourceNodeId == targetNodeId) return;
        if (_project.Edges.Any(e => e.SourceNodeId == sourceNodeId && e.TargetNodeId == targetNodeId
                                    && e.SourcePortName == sourcePort && e.TargetPortName == targetPort))
            return; // idempotent

        PushUndo();
        _project.Edges.Add(new MappingEdge
        {
            EdgeId = "e" + Id.New(),
            SourceNodeId = sourceNodeId,
            SourcePortName = sourcePort,
            TargetNodeId = targetNodeId,
            TargetPortName = targetPort
        });
        RebuildViewModels(captureSnapshot: false);
    }

    /// <summary>Reconnection: retarget an existing edge to a new node, preserving the
    /// edge identity. Semantic change (edge endpoints are in the digest).</summary>
    public bool ReconnectEdge(string edgeId, string newTargetNodeId)
    {
        var idx = _project.Edges.FindIndex(e => e.EdgeId == edgeId);
        var targetExists = _project.Nodes.Any(n => n.NodeId == newTargetNodeId);
        if (idx < 0 || !targetExists) return false;
        var old = _project.Edges[idx];
        if (old.SourceNodeId == newTargetNodeId) return false; // no self-loop

        PushUndo();
        _project.Edges[idx] = old with { TargetNodeId = newTargetNodeId };
        RebuildViewModels(captureSnapshot: false);
        return true;
    }

    public void RemoveEdge(string edgeId)
    {
        var idx = _project.Edges.FindIndex(e => e.EdgeId == edgeId);
        if (idx < 0) return;
        PushUndo();
        _project.Edges.RemoveAt(idx);
        RebuildViewModels(captureSnapshot: false);
    }

    /// <summary>Delete every selected node and its incident edges (single undo step).</summary>
    public int DeleteSelected()
    {
        if (_selected.Count == 0) return 0;
        PushUndo();
        var doomed = new HashSet<string>(_selected, StringComparer.Ordinal);
        _project.Nodes.RemoveAll(n => doomed.Contains(n.NodeId));
        _project.Edges.RemoveAll(e => doomed.Contains(e.SourceNodeId) || doomed.Contains(e.TargetNodeId));
        _selected.Clear();
        RebuildViewModels(captureSnapshot: false);
        return doomed.Count;
    }

    public string AddNode(string nodeType, string displayName, double x, double y)
    {
        PushUndo();
        var nodeId = "n" + Id.New();
        _project.Nodes.Add(new MappingNode
        {
            NodeId = nodeId,
            NodeType = nodeType,
            DisplayName = displayName,
            Layout = new LayoutPosition(x, y)
        });
        RebuildViewModels(captureSnapshot: false);
        return nodeId;
    }

    public bool SetNodeParameter(string nodeId, string name, object? value)
    {
        var node = _project.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null || (node.Parameters.TryGetValue(name, out var existing) && Equals(existing, value))) return false;
        PushUndo();
        node.Parameters[name] = value;
        RebuildViewModels(captureSnapshot: false);
        return true;
    }

    public bool SetNodeParameters(string nodeId, IReadOnlyDictionary<string, object?> values)
    {
        return SetNodeParameterValues(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            [nodeId] = values
        });
    }

    public bool SetNodeParameterValues(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> updates)
    {
        var nodes = updates.ToDictionary(
            pair => pair.Key,
            pair => _project.Nodes.FirstOrDefault(node => node.NodeId == pair.Key),
            StringComparer.Ordinal);
        if (nodes.Values.Any(node => node is null)
            || updates.All(update => update.Value.All(pair =>
                nodes[update.Key]!.Parameters.TryGetValue(pair.Key, out var current)
                && Equals(current, pair.Value)))) return false;
        PushUndo();
        foreach (var (nodeId, values) in updates)
            foreach (var pair in values)
                nodes[nodeId]!.Parameters[pair.Key] = pair.Value;
        RebuildViewModels(captureSnapshot: false);
        return true;
    }

    public bool SetBinding(SourceBinding binding)
    {
        var index = _project.Bindings.FindIndex(current => current.BindingId == binding.BindingId);
        if (index < 0 || Equals(_project.Bindings[index], binding)) return false;
        PushUndo();
        _project.Bindings[index] = binding;
        Changed?.Invoke();
        return true;
    }

    public bool SetDestination(DestinationPlan? destination)
    {
        var snapshot = destination is null
            ? null
            : destination with
            {
                BusinessKey = destination.BusinessKey is null
                    ? null!
                    : new List<string>(destination.BusinessKey)
            };
        if (DestinationPlansEqual(_project.Destination, snapshot)) return false;
        PushUndo();
        _project.Destination = snapshot;
        Changed?.Invoke();
        return true;
    }

    private static bool DestinationPlansEqual(DestinationPlan? left, DestinationPlan? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        var leftKey = left.BusinessKey;
        var rightKey = right.BusinessKey;
        var keysEqual = leftKey is null || rightKey is null
            ? leftKey is null && rightKey is null
            : leftKey.SequenceEqual(rightKey, StringComparer.Ordinal);

        return left.Version == right.Version
            && StringComparer.Ordinal.Equals(left.Kind, right.Kind)
            && StringComparer.Ordinal.Equals(left.Locator, right.Locator)
            && StringComparer.Ordinal.Equals(left.Table, right.Table)
            && StringComparer.Ordinal.Equals(left.Format, right.Format)
            && keysEqual
            && StringComparer.Ordinal.Equals(left.WriteMode, right.WriteMode)
            && StringComparer.Ordinal.Equals(left.Compatibility, right.Compatibility);
    }

    // ---- undo / redo ------------------------------------------------------------

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(ProjectSerializer.Serialize(_project));
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(ProjectSerializer.Serialize(_project));
        Restore(_redo.Pop());
    }

    private void Restore(string json)
    {
        _project = ProjectSerializer.Deserialize(json);
        _selected.Clear();
        RebuildViewModels(captureSnapshot: false);
        Changed?.Invoke();
    }

    private void PushUndo()
    {
        _undo.Push(ProjectSerializer.Serialize(_project));
        _redo.Clear();
    }

    // ---- view-model projection --------------------------------------------------

    private void RebuildViewModels(bool captureSnapshot)
    {
        _nodes.Clear();
        _edges.Clear();
        // Preserve layout positions from the project; default to a grid when absent.
        for (int i = 0; i < _project.Nodes.Count; i++)
        {
            var n = _project.Nodes[i];
            _nodes.Add(new NodeViewModel
            {
                NodeId = n.NodeId,
                NodeType = n.NodeType,
                DisplayName = n.DisplayName,
                X = n.Layout?.X ?? (40 + (i % 3) * 240),
                Y = n.Layout?.Y ?? (40 + (i / 3) * 120),
                IsSelected = _selected.Contains(n.NodeId)
            });
        }
        foreach (var e in _project.Edges)
        {
            var from = _nodes.FirstOrDefault(n => n.NodeId == e.SourceNodeId);
            var to = _nodes.FirstOrDefault(n => n.NodeId == e.TargetNodeId);
            _edges.Add(new EdgeViewModel
            {
                EdgeId = e.EdgeId,
                SourceNodeId = e.SourceNodeId,
                TargetNodeId = e.TargetNodeId,
                From = from,
                To = to
            });
        }
        if (!captureSnapshot) Changed?.Invoke();
    }

    private void SyncSelectionFlags()
    {
        foreach (var n in _nodes) n.IsSelected = _selected.Contains(n.NodeId);
    }
}
