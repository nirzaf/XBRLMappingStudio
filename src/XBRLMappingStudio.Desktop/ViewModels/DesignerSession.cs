using XBRLMappingStudio.Engine;
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

    /// <summary>Raised when graph selection changes without changing the project.</summary>
    public event Action? SelectionChanged;

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
            bool overlaps = n.X < r && n.X + NodeWidth > l && n.Y < b && n.Y + n.Height > t;
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

    /// <summary>Moves the selected set as one undoable layout-only keyboard edit.</summary>
    public bool MoveSelectedBy(double dx, double dy)
    {
        if (_selected.Count == 0 || (dx == 0 && dy == 0)) return false;
        var selected = _project.Nodes.Where(node => _selected.Contains(node.NodeId)).ToArray();
        if (selected.Length == 0) return false;

        PushUndo();
        foreach (var node in selected)
        {
            var current = node.Layout ?? new LayoutPosition(0, 0);
            node.Layout = new LayoutPosition(current.X + dx, current.Y + dy);
            var viewModel = _nodes.First(view => view.NodeId == node.NodeId);
            viewModel.X = current.X + dx;
            viewModel.Y = current.Y + dy;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Commits a pointer drag as one layout-only undo operation.</summary>
    public bool MoveNodesTo(IReadOnlyDictionary<string, LayoutPosition> positions)
    {
        var nodes = _project.Nodes.Where(node => positions.ContainsKey(node.NodeId)).ToArray();
        if (!nodes.Any(node => node.Layout is null
                || node.Layout.X != positions[node.NodeId].X
                || node.Layout.Y != positions[node.NodeId].Y)) return false;

        PushUndo();
        foreach (var node in nodes)
        {
            var position = positions[node.NodeId];
            node.Layout = position;
            var view = _nodes.First(candidate => candidate.NodeId == node.NodeId);
            view.X = position.X;
            view.Y = position.Y;
        }
        Changed?.Invoke();
        return true;
    }

    // ---- graph topology (semantic) ----------------------------------------------

    public bool Connect(string sourceNodeId, string targetNodeId,
        string sourcePort = "out", string targetPort = "in")
    {
        return TryConnect(sourceNodeId, targetNodeId, sourcePort, targetPort, out _);
    }

    public bool TryConnect(string sourceNodeId, string targetNodeId,
        string sourcePort, string targetPort, out string? refusalReason)
    {
        refusalReason = null;
        if (!TryValidateConnection(sourceNodeId, targetNodeId, sourcePort, targetPort,
                ignoredEdgeId: null, out refusalReason))
            return false;
        if (_project.Edges.Any(e => e.SourceNodeId == sourceNodeId && e.TargetNodeId == targetNodeId
                                    && e.SourcePortName == sourcePort && e.TargetPortName == targetPort))
            return false; // idempotent

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
        return true;
    }

    /// <summary>Reconnection: retarget an existing edge to a new node, preserving the
    /// edge identity. Semantic change (edge endpoints are in the digest).</summary>
    public bool ReconnectEdge(string edgeId, string newTargetNodeId, string? newTargetPortName = null)
        => TryReconnectEdge(edgeId, newTargetNodeId, newTargetPortName, out _);

    public bool TryReconnectEdge(string edgeId, string newTargetNodeId,
        string? newTargetPortName, out string? refusalReason)
    {
        refusalReason = null;
        var idx = _project.Edges.FindIndex(e => e.EdgeId == edgeId);
        if (idx < 0)
        {
            refusalReason = "The edge no longer exists.";
            return false;
        }
        var old = _project.Edges[idx];
        var targetPort = newTargetPortName ?? old.TargetPortName;
        if (!TryValidateConnection(old.SourceNodeId, newTargetNodeId, old.SourcePortName, targetPort,
                edgeId, out refusalReason)) return false;
        if (old.TargetNodeId == newTargetNodeId && old.TargetPortName == targetPort) return false;

        PushUndo();
        _project.Edges[idx] = old with { TargetNodeId = newTargetNodeId, TargetPortName = targetPort };
        RebuildViewModels(captureSnapshot: false);
        return true;
    }

    /// <summary>Rebinds one input to a different compatible output as a single undoable edit.</summary>
    public bool TryRebindInput(
        string targetNodeId,
        string targetPortName,
        string sourceNodeId,
        string sourcePortName,
        out string? refusalReason)
    {
        refusalReason = null;
        var existingIndex = _project.Edges.FindIndex(edge =>
            StringComparer.Ordinal.Equals(edge.TargetNodeId, targetNodeId)
            && StringComparer.Ordinal.Equals(edge.TargetPortName, targetPortName));
        var existing = existingIndex >= 0 ? _project.Edges[existingIndex] : null;
        if (existing is not null
            && StringComparer.Ordinal.Equals(existing.SourceNodeId, sourceNodeId)
            && StringComparer.Ordinal.Equals(existing.SourcePortName, sourcePortName))
            return false;

        if (!TryValidateConnection(sourceNodeId, targetNodeId, sourcePortName, targetPortName,
                existing?.EdgeId, out refusalReason))
            return false;

        PushUndo();
        if (existing is not null)
        {
            _project.Edges[existingIndex] = existing with
            {
                SourceNodeId = sourceNodeId,
                SourcePortName = sourcePortName
            };
        }
        else
        {
            _project.Edges.Add(new MappingEdge
            {
                EdgeId = "e" + Id.New(),
                SourceNodeId = sourceNodeId,
                SourcePortName = sourcePortName,
                TargetNodeId = targetNodeId,
                TargetPortName = targetPortName
            });
        }
        RebuildViewModels(captureSnapshot: false);
        return true;
    }

    private bool TryValidateConnection(string sourceNodeId, string targetNodeId,
        string sourcePortName, string targetPortName, string? ignoredEdgeId, out string? refusalReason)
    {
        refusalReason = null;
        if (StringComparer.Ordinal.Equals(sourceNodeId, targetNodeId))
        {
            refusalReason = "A node cannot connect to itself.";
            return false;
        }

        var sourceNode = _project.Nodes.FirstOrDefault(node => node.NodeId == sourceNodeId);
        var targetNode = _project.Nodes.FirstOrDefault(node => node.NodeId == targetNodeId);
        if (sourceNode is null || targetNode is null)
        {
            refusalReason = "The connection references a node that is no longer in the graph.";
            return false;
        }

        var sourcePort = sourceNode.OutputPorts.FirstOrDefault(port => port.Name == sourcePortName);
        if (sourcePort is null)
        {
            refusalReason = $"'{sourceNode.DisplayName}' has no output port named '{sourcePortName}'.";
            return false;
        }
        var targetPort = targetNode.InputPorts.FirstOrDefault(port => port.Name == targetPortName);
        if (targetPort is null)
        {
            refusalReason = $"'{targetNode.DisplayName}' has no input port named '{targetPortName}'.";
            return false;
        }

        if (!ShapeChecker.AreCompatible(sourcePort, targetPort))
        {
            refusalReason = $"Cannot connect {sourcePort.Shape} output '{sourcePortName}' to "
                + $"{targetPort.Shape} input '{targetPortName}'.";
            return false;
        }
        if (sourcePort.Cardinality == PortCardinality.Collection
            && targetPort.Cardinality == PortCardinality.Single)
        {
            refusalReason = "A collection output cannot connect directly to a single-value input.";
            return false;
        }
        if (_project.Edges.Any(edge => edge.EdgeId != ignoredEdgeId
                && edge.TargetNodeId == targetNodeId && edge.TargetPortName == targetPortName))
        {
            refusalReason = $"Input port '{targetPortName}' already has a connection.";
            return false;
        }
        if (WouldCreateCycle(sourceNodeId, targetNodeId, ignoredEdgeId))
        {
            refusalReason = "This connection would create a cycle in the mapping graph.";
            return false;
        }
        return true;
    }

    private bool WouldCreateCycle(string sourceNodeId, string targetNodeId, string? ignoredEdgeId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(targetNodeId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (StringComparer.Ordinal.Equals(current, sourceNodeId)) return true;
            foreach (var edge in _project.Edges.Where(edge => edge.EdgeId != ignoredEdgeId
                         && edge.SourceNodeId == current))
                pending.Push(edge.TargetNodeId);
        }
        return false;
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

    /// <summary>Adds a fully declared node in one undoable graph edit.</summary>
    public string AddNode(MappingNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.NodeId)
            || _project.Nodes.Any(existing => StringComparer.Ordinal.Equals(existing.NodeId, node.NodeId)))
            throw new InvalidOperationException("A node ID must be non-empty and unique in the graph.");

        PushUndo();
        _project.Nodes.Add(node.ShallowCopy());
        RebuildViewModels(captureSnapshot: false);
        return node.NodeId;
    }

    /// <summary>Inserts a declared node into one edge as a single undoable edit.</summary>
    public bool TryInsertNodeOnEdge(
        string edgeId,
        MappingNode node,
        string inputPortName,
        string outputPortName,
        out string? refusalReason)
    {
        refusalReason = null;
        ArgumentNullException.ThrowIfNull(node);
        var edgeIndex = _project.Edges.FindIndex(edge =>
            StringComparer.Ordinal.Equals(edge.EdgeId, edgeId));
        if (edgeIndex < 0)
        {
            refusalReason = "The edge no longer exists.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(node.NodeId)
            || _project.Nodes.Any(existing => StringComparer.Ordinal.Equals(existing.NodeId, node.NodeId)))
        {
            refusalReason = "A node ID must be non-empty and unique in the graph.";
            return false;
        }

        var edge = _project.Edges[edgeIndex];
        var source = _project.Nodes.FirstOrDefault(candidate => candidate.NodeId == edge.SourceNodeId);
        var target = _project.Nodes.FirstOrDefault(candidate => candidate.NodeId == edge.TargetNodeId);
        var sourcePort = source?.OutputPorts.FirstOrDefault(port => port.Name == edge.SourcePortName);
        var inputPort = node.InputPorts.FirstOrDefault(port => port.Name == inputPortName);
        var outputPort = node.OutputPorts.FirstOrDefault(port => port.Name == outputPortName);
        var targetPort = target?.InputPorts.FirstOrDefault(port => port.Name == edge.TargetPortName);
        if (source is null || target is null || sourcePort is null || inputPort is null
            || outputPort is null || targetPort is null)
        {
            refusalReason = "The edge or inserted node references a missing port.";
            return false;
        }
        if (!ShapeChecker.AreCompatible(sourcePort, inputPort)
            || !ShapeChecker.AreCompatible(outputPort, targetPort))
        {
            refusalReason = "The inserted node ports are incompatible with the existing edge.";
            return false;
        }
        if ((sourcePort.Cardinality == PortCardinality.Collection
                && inputPort.Cardinality == PortCardinality.Single)
            || (outputPort.Cardinality == PortCardinality.Collection
                && targetPort.Cardinality == PortCardinality.Single))
        {
            refusalReason = "A collection output cannot connect directly to a single-value input.";
            return false;
        }

        PushUndo();
        _project.Edges.RemoveAt(edgeIndex);
        _project.Nodes.Add(node.ShallowCopy());
        _project.Edges.Add(new MappingEdge
        {
            EdgeId = "e" + Id.New(),
            SourceNodeId = edge.SourceNodeId,
            SourcePortName = edge.SourcePortName,
            TargetNodeId = node.NodeId,
            TargetPortName = inputPortName
        });
        _project.Edges.Add(new MappingEdge
        {
            EdgeId = "e" + Id.New(),
            SourceNodeId = node.NodeId,
            SourcePortName = outputPortName,
            TargetNodeId = edge.TargetNodeId,
            TargetPortName = edge.TargetPortName
        });
        _selected.Clear();
        _selected.Add(node.NodeId);
        RebuildViewModels(captureSnapshot: false);
        return true;
    }

    /// <summary>
    /// Replaces one edge with a declared node plus companion nodes/connections as one
    /// validated undoable edit. This is used for operators with explicit side inputs,
    /// such as Ratio and Coalesce with a user-editable Decimal constant.
    /// </summary>
    public bool TryInsertNodeOnEdgeWithCompanions(
        string edgeId,
        MappingNode node,
        string inputPortName,
        string outputPortName,
        IReadOnlyList<MappingNode> companionNodes,
        IReadOnlyList<MappingEdge> companionEdges,
        out string? refusalReason)
    {
        refusalReason = null;
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(companionNodes);
        ArgumentNullException.ThrowIfNull(companionEdges);

        var edgeIndex = _project.Edges.FindIndex(edge =>
            StringComparer.Ordinal.Equals(edge.EdgeId, edgeId));
        if (edgeIndex < 0)
        {
            refusalReason = "The edge no longer exists.";
            return false;
        }
        var replacedEdge = _project.Edges[edgeIndex];
        var addedNodes = new[] { node }.Concat(companionNodes).ToArray();
        var newNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addedNode in addedNodes)
        {
            if (string.IsNullOrWhiteSpace(addedNode.NodeId)
                || _project.Nodes.Any(existing => StringComparer.Ordinal.Equals(existing.NodeId, addedNode.NodeId))
                || !newNodeIds.Add(addedNode.NodeId))
            {
                refusalReason = "Inserted node IDs must be non-empty and unique in the graph.";
                return false;
            }
        }

        var source = _project.Nodes.FirstOrDefault(candidate => candidate.NodeId == replacedEdge.SourceNodeId);
        var target = _project.Nodes.FirstOrDefault(candidate => candidate.NodeId == replacedEdge.TargetNodeId);
        var sourcePort = source?.OutputPorts.FirstOrDefault(port => port.Name == replacedEdge.SourcePortName);
        var inputPort = node.InputPorts.FirstOrDefault(port => port.Name == inputPortName);
        var outputPort = node.OutputPorts.FirstOrDefault(port => port.Name == outputPortName);
        var targetPort = target?.InputPorts.FirstOrDefault(port => port.Name == replacedEdge.TargetPortName);
        if (source is null || target is null || sourcePort is null || inputPort is null
            || outputPort is null || targetPort is null)
        {
            refusalReason = "The edge or inserted node references a missing port.";
            return false;
        }

        var allNodes = _project.Nodes.Concat(addedNodes).ToDictionary(existing => existing.NodeId, StringComparer.Ordinal);
        var replacementEdges = new List<MappingEdge>
        {
            new()
            {
                EdgeId = "e" + Id.New(),
                SourceNodeId = replacedEdge.SourceNodeId,
                SourcePortName = replacedEdge.SourcePortName,
                TargetNodeId = node.NodeId,
                TargetPortName = inputPortName
            },
            new()
            {
                EdgeId = "e" + Id.New(),
                SourceNodeId = node.NodeId,
                SourcePortName = outputPortName,
                TargetNodeId = replacedEdge.TargetNodeId,
                TargetPortName = replacedEdge.TargetPortName
            }
        };
        replacementEdges.AddRange(companionEdges.Select(edge => edge with { EdgeId = "e" + Id.New() }));

        var remainingEdges = _project.Edges.Where(edge => !StringComparer.Ordinal.Equals(edge.EdgeId, edgeId))
            .ToArray();
        var candidateEdges = remainingEdges.Concat(replacementEdges).ToArray();
        var occupiedInputs = new HashSet<(string NodeId, string PortName)>();
        foreach (var candidateEdge in candidateEdges)
        {
            if (!allNodes.TryGetValue(candidateEdge.SourceNodeId, out var candidateSource)
                || !allNodes.TryGetValue(candidateEdge.TargetNodeId, out var candidateTarget))
            {
                refusalReason = "A proposed companion connection references a node outside the graph.";
                return false;
            }

            var candidateSourcePort = candidateSource.OutputPorts.FirstOrDefault(port => port.Name == candidateEdge.SourcePortName);
            var candidateTargetPort = candidateTarget.InputPorts.FirstOrDefault(port => port.Name == candidateEdge.TargetPortName);
            if (candidateSourcePort is null || candidateTargetPort is null)
            {
                refusalReason = "A proposed companion connection references a missing port.";
                return false;
            }
            if (!ShapeChecker.AreCompatible(candidateSourcePort, candidateTargetPort)
                || (candidateSourcePort.Cardinality == PortCardinality.Collection
                    && candidateTargetPort.Cardinality == PortCardinality.Single))
            {
                refusalReason = $"Proposed connection from {candidateSourcePort.Shape} to {candidateTargetPort.Shape} is incompatible.";
                return false;
            }
            if (!occupiedInputs.Add((candidateEdge.TargetNodeId, candidateEdge.TargetPortName)))
            {
                refusalReason = $"Input port '{candidateEdge.TargetPortName}' already has a connection.";
                return false;
            }
        }

        foreach (var addedNode in addedNodes)
        foreach (var requiredPort in addedNode.InputPorts.Where(port => port.IsRequired))
        {
            if (!occupiedInputs.Contains((addedNode.NodeId, requiredPort.Name)))
            {
                refusalReason = $"Required input port '{requiredPort.Name}' on '{addedNode.DisplayName}' is not connected.";
                return false;
            }
        }

        var indegree = allNodes.Keys.ToDictionary(nodeId => nodeId, _ => 0, StringComparer.Ordinal);
        var outgoing = allNodes.Keys.ToDictionary(nodeId => nodeId, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var candidateEdge in candidateEdges)
        {
            indegree[candidateEdge.TargetNodeId]++;
            outgoing[candidateEdge.SourceNodeId].Add(candidateEdge.TargetNodeId);
        }
        var ready = new Queue<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visitedCount = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            visitedCount++;
            foreach (var next in outgoing[current])
                if (--indegree[next] == 0) ready.Enqueue(next);
        }
        if (visitedCount != allNodes.Count)
        {
            refusalReason = "The inserted subgraph would create a cycle.";
            return false;
        }

        PushUndo();
        _project.Edges.RemoveAt(edgeIndex);
        foreach (var addedNode in addedNodes) _project.Nodes.Add(addedNode.ShallowCopy());
        _project.Edges.AddRange(replacementEdges);
        _selected.Clear();
        _selected.Add(node.NodeId);
        RebuildViewModels(captureSnapshot: false);
        return true;
    }

    /// <summary>Replaces project content from an immutable service result in one undo step.</summary>
    public bool ReplaceProject(MappingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!StringComparer.Ordinal.Equals(_project.ProjectId, project.ProjectId))
            throw new InvalidOperationException("A designer session cannot replace its project identity.");

        var serialized = ProjectSerializer.Serialize(project);
        if (StringComparer.Ordinal.Equals(ProjectSerializer.Serialize(_project), serialized)) return false;
        PushUndo();
        _project = ProjectSerializer.Deserialize(serialized);
        RebuildViewModels(captureSnapshot: false);
        return true;
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

    public bool SetBinding(SourceBinding binding, bool clearBindingSchemasFromHistory = false)
    {
        var index = _project.Bindings.FindIndex(current => current.BindingId == binding.BindingId);
        if (index < 0) return false;
        if (Equals(_project.Bindings[index], binding))
        {
            if (clearBindingSchemasFromHistory)
            {
                ClearBindingSchemaFromHistory(_undo, binding.BindingId);
                ClearBindingSchemaFromHistory(_redo, binding.BindingId);
            }
            return false;
        }
        PushUndo();
        _project.Bindings[index] = binding;
        if (clearBindingSchemasFromHistory)
        {
            ClearBindingSchemaFromHistory(_undo, binding.BindingId);
            ClearBindingSchemaFromHistory(_redo, binding.BindingId);
        }
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

    private static void ClearBindingSchemaFromHistory(Stack<string> snapshots, string bindingId)
    {
        if (snapshots.Count == 0) return;
        var ordered = snapshots.ToArray();
        snapshots.Clear();
        foreach (var snapshot in ordered.Reverse())
        {
            var project = ProjectSerializer.Deserialize(snapshot);
            var index = project.Bindings.FindIndex(binding => binding.BindingId == bindingId);
            if (index >= 0 && project.Bindings[index].SchemaFingerprint is not null)
                project.Bindings[index] = project.Bindings[index] with { SchemaFingerprint = null };
            snapshots.Push(ProjectSerializer.Serialize(project));
        }
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
                InputPorts = n.InputPorts.ToArray(),
                OutputPorts = n.OutputPorts.ToArray(),
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
                SourcePortName = e.SourcePortName,
                TargetNodeId = e.TargetNodeId,
                TargetPortName = e.TargetPortName,
                From = from,
                To = to
            });
        }
        if (!captureSnapshot) Changed?.Invoke();
    }

    private void SyncSelectionFlags()
    {
        foreach (var n in _nodes) n.IsSelected = _selected.Contains(n.NodeId);
        SelectionChanged?.Invoke();
    }
}
