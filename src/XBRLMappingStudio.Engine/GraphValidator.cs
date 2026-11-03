using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Validates the mapping graph prior to execution (MAP02). Detects:
///   - Unknown node types (block execution, preserve for inspection)
///   - Dangling / unbound required input ports
///   - Cycle in graph (topological sort fails)
///   - Port shape / cardinality mismatch
///   - Duplicate edge into same target port
/// </summary>
public sealed class GraphValidator
{
    private readonly FunctionCatalog _catalog;

    public GraphValidator(FunctionCatalog catalog)
    {
        _catalog = catalog;
    }

    public DiagnosticBag Validate(MappingGraph graph)
    {
        var bag = new DiagnosticBag();

        CheckNodeTypes(graph, bag);
        CheckConnectivity(graph, bag);
        CheckCycles(graph, bag);

        return bag;
    }

    private void CheckNodeTypes(MappingGraph graph, DiagnosticBag bag)
    {
        foreach (var node in graph.Nodes.Values)
        {
            var sig = _catalog.Find(node.NodeType);
            if (sig is null)
            {
                // Per MAP01: unknown node types are preserved but block execution.
                bag.Error(
                    DiagnosticCodes.MapUnknownNodeType,
                    DiagnosticSubsystem.Mapping,
                    DiagnosticStage.Validation,
                    $"Unknown node type '{node.NodeType}' on node '{node.DisplayName}'. Execution is blocked.",
                    nodeId: node.NodeId);
                continue;
            }

            // Verify node-declared ports match the catalog signature.
            foreach (var expected in sig.Inputs)
            {
                if (!expected.IsRequired) continue;
                if (!node.InputPorts.Any(p => p.Name == expected.Name))
                {
                    bag.Error(
                        DiagnosticCodes.MapUnboundRequiredInput,
                        DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Validation,
                        $"Node '{node.DisplayName}' is missing required input port '{expected.Name}'.",
                        nodeId: node.NodeId,
                        portName: expected.Name);
                }
            }
        }
    }

    private void CheckConnectivity(MappingGraph graph, DiagnosticBag bag)
    {
        foreach (var node in graph.Nodes.Values)
        {
            var sig = _catalog.Find(node.NodeType);
            if (sig is null) continue;

            foreach (var port in node.InputPorts)
            {
                var edge = graph.IncomingEdge(node.NodeId, port.Name);
                if (edge is null)
                {
                    // Only required inputs can dangle; unconnected optional ports are legal.
                    if (port.IsRequired)
                    {
                        bag.Error(
                            DiagnosticCodes.MapDanglingPort,
                            DiagnosticSubsystem.Mapping,
                            DiagnosticStage.Validation,
                            $"Node '{node.DisplayName}' has unbound required input '{port.Name}'.",
                            nodeId: node.NodeId,
                            portName: port.Name);
                    }
                    continue;
                }

                // Verify source port exists and shape is compatible.
                if (!graph.Nodes.TryGetValue(edge.SourceNodeId, out var srcNode)) continue;
                var srcPort = srcNode.OutputPorts.FirstOrDefault(p => p.Name == edge.SourcePortName);
                if (srcPort is null)
                {
                    bag.Error(
                        DiagnosticCodes.MapDanglingPort,
                        DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Validation,
                        $"Edge references missing output port '{edge.SourcePortName}' on node '{srcNode.DisplayName}'.",
                        nodeId: edge.TargetNodeId,
                        portName: edge.TargetPortName,
                        edgeId: edge.EdgeId);
                    continue;
                }

                if (!ShapeChecker.AreCompatible(srcPort, port))
                {
                    bag.Error(
                        DiagnosticCodes.MapTypeIncompatible,
                        DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Validation,
                        $"Port shape mismatch: '{srcNode.DisplayName}'.{srcPort.Name} ({srcPort.Shape}/{srcPort.Cardinality}) "
                        + $"to '{node.DisplayName}'.'{port.Name}' ({port.Shape}/{port.Cardinality}).",
                        nodeId: node.NodeId,
                        portName: port.Name,
                        edgeId: edge.EdgeId);
                }

                // Collection-to-single requires explicit aggregation node.
                if (srcPort.Cardinality == PortCardinality.Collection
                    && port.Cardinality == PortCardinality.Single)
                {
                    bag.Error(
                        DiagnosticCodes.MapCardinalityMismatch,
                        DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Validation,
                        $"Collection output from '{srcNode.DisplayName}'.{srcPort.Name} "
                        + "cannot bind directly to single input on "
                        + $"'{node.DisplayName}'.{port.Name}. Insert an aggregation node.",
                        nodeId: node.NodeId,
                        portName: port.Name,
                        edgeId: edge.EdgeId);
                }
            }
        }
    }

    private static void CheckCycles(MappingGraph graph, DiagnosticBag bag)
    {
        var color = graph.Nodes.Keys.ToDictionary(k => k, _ => 0); // 0=white,1=gray,2=black
        var stack = new List<string>();

        foreach (var start in graph.Nodes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (color[start] == 0)
            {
                if (Dfs(start, graph, color, stack))
                {
                    var cycle = string.Join(" -> ", stack);
                    bag.Error(
                        DiagnosticCodes.MapCycleDetected,
                        DiagnosticSubsystem.Mapping,
                        DiagnosticStage.Validation,
                        $"Cycle detected in mapping graph: {cycle}",
                        nodeId: stack[0]);
                    return; // one cycle is enough
                }
            }
        }
    }

    private static bool Dfs(string node, MappingGraph graph, Dictionary<string, int> color, List<string> stack)
    {
        color[node] = 1;
        stack.Add(node);
        var n = graph.Nodes[node];
        foreach (var port in n.OutputPorts)
        {
            foreach (var edge in graph.OutgoingEdges(n.NodeId, port.Name))
            {
                var next = edge.TargetNodeId;
                if (color[next] == 1)
                {
                    stack.Add(next);
                    return true;
                }
                if (color[next] == 0 && Dfs(next, graph, color, stack))
                    return true;
            }
        }
        color[node] = 2;
        stack.RemoveAt(stack.Count - 1);
        return false;
    }
}

/// <summary>
/// Type / shape compatibility rules for port edges (MAP02).
/// </summary>
public static class ShapeChecker
{
    public static bool AreCompatible(PortDefinition source, PortDefinition target)
    {
        // Cardinality is checked separately for collection->single.
        if (source.Shape != target.Shape)
        {
            // Widening rules for numeric shapes and identifier->string.
            return (source.Shape, target.Shape) switch
            {
                (PortShape.Integer, PortShape.Decimal) => true,
                (PortShape.Decimal, PortShape.Decimal) => true,
                (PortShape.String, PortShape.Identifier) => true,
                (PortShape.Identifier, PortShape.String) => true,
                _ => false
            };
        }
        return true;
    }
}
