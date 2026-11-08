using System.Globalization;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Expands revision-pinned declarative functions into a graph before graph validation.
/// Function bodies contain typed input and output boundary markers; all
/// executable work stays in the ordinary node catalog.
/// </summary>
internal static class FunctionExpander
{
    private const string InputMarker = "functionInput";
    private const string OutputMarker = "functionOutput";
    private const int MaxExpandedNodes = 100_000;
    private const int MaxFunctionDepth = 64;

    public static MappingGraph Expand(MappingGraph graph, string catalogVersion, DiagnosticBag diagnostics)
        => new Expansion(graph, catalogVersion, diagnostics).Expand();

    private sealed class Expansion
    {
        private readonly MappingGraph _source;
        private readonly string _catalogVersion;
        private readonly DiagnosticBag _diagnostics;
        private readonly Dictionary<(string FunctionId, int Revision), FunctionDefinition> _definitions = new();
        private readonly Dictionary<(string FunctionId, int Revision), FunctionBody> _expanded = new();
        private int _expandedNodeCount;

        public Expansion(MappingGraph source, string catalogVersion, DiagnosticBag diagnostics)
        {
            _source = source;
            _catalogVersion = catalogVersion;
            _diagnostics = diagnostics;

            foreach (var definition in source.Source.Functions
                         .OrderBy(item => item.FunctionId, StringComparer.Ordinal)
                         .ThenBy(item => item.Revision))
            {
                if (string.IsNullOrWhiteSpace(definition.FunctionId)
                    || string.IsNullOrWhiteSpace(definition.Name)
                    || definition.Revision < 1)
                {
                    Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                        $"Function definition '{definition.Name}' has an empty ID or non-positive revision.");
                    continue;
                }

                var key = (definition.FunctionId, definition.Revision);
                if (!_definitions.TryAdd(key, definition))
                    Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                        $"Function '{key.FunctionId}@{key.Revision}' is defined more than once.");
            }
        }

        public MappingGraph Expand()
        {
            var calls = _source.Nodes.Values
                .Where(node => node.NodeType == NodeTypeConstants.FunctionCall)
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .ToList();
            if (calls.Count == 0) return _source;
            if (_diagnostics.HasBlockingErrors) return _source;
            if (HasCycle(_source.Nodes.Values, _source.Edges.Values))
            {
                Error(DiagnosticCodes.MapFunctionDefinitionInvalid, "Function calls cannot participate in a cyclic graph.");
                return _source;
            }

            if (!FunctionCatalogVersions.IsCompatible(_source.Source.FunctionCatalogVersion, _catalogVersion))
            {
                Error(DiagnosticCodes.MapFunctionCatalogMismatch,
                    $"Project function catalog '{_source.Source.FunctionCatalogVersion}' is incompatible with "
                    + $"the available catalog '{_catalogVersion}'.");
                return _source;
            }

            var nodes = _source.Nodes.Values
                .Where(node => node.NodeType != NodeTypeConstants.FunctionCall)
                .Select(node => node.ShallowCopy())
                .ToList();
            var edges = _source.Edges.Values.OrderBy(edge => edge.EdgeId, StringComparer.Ordinal).ToList();

            foreach (var call in calls)
            {
                if (_diagnostics.HasBlockingErrors) break;
                if (!TryGetKey(call, out var key)) continue;
                var body = Resolve(key, new List<(string FunctionId, int Revision)>(), call.NodeId);
                if (body is not null && ValidateCallNode(call, body))
                    InlineCall(nodes, edges, call, body, call.NodeId + "::");
            }

            if (_diagnostics.HasBlockingErrors) return _source;

            var expanded = new MappingProject
            {
                SchemaVersion = _source.Source.SchemaVersion,
                ProjectId = _source.Source.ProjectId,
                Name = _source.Source.Name,
                ProfileId = _source.Source.ProfileId,
                ProfileDigest = _source.Source.ProfileDigest,
                FunctionCatalogVersion = _source.Source.FunctionCatalogVersion,
                SemanticRevision = _source.Source.SemanticRevision,
                LayoutGeneration = _source.Source.LayoutGeneration,
                Metadata = _source.Source.Metadata,
                Nodes = nodes,
                Edges = edges,
                Bindings = _source.Source.Bindings,
                Functions = _source.Source.Functions,
                Destination = _source.Source.Destination,
                Approvals = _source.Source.Approvals
            };
            return new MappingGraph(expanded);
        }

        private FunctionBody? Resolve(
            (string FunctionId, int Revision) key,
            List<(string FunctionId, int Revision)> stack,
            string callNodeId)
        {
            if (stack.Contains(key))
            {
                var cycle = string.Join(" -> ", stack.Append(key)
                    .Select(item => $"{item.FunctionId}@{item.Revision}"));
                Error(DiagnosticCodes.MapFunctionCycleDetected,
                    $"Recursive function reference detected: {cycle}.", callNodeId);
                return null;
            }

            if (stack.Count >= MaxFunctionDepth)
            {
                Error(DiagnosticCodes.MapFunctionExpansionLimitExceeded,
                    $"Function nesting exceeded the maximum depth of {MaxFunctionDepth}.", callNodeId);
                return null;
            }

            if (_expanded.TryGetValue(key, out var cached)) return cached;
            if (!_definitions.TryGetValue(key, out var definition))
            {
                Error(DiagnosticCodes.MapFunctionRevisionMissing,
                    $"Function '{key.FunctionId}@{key.Revision}' referenced by '{callNodeId}' is not defined "
                    + "at that pinned revision.", callNodeId);
                return null;
            }

            if (!FunctionCatalogVersions.IsCompatible(definition.CatalogVersion, _catalogVersion))
            {
                Error(DiagnosticCodes.MapFunctionCatalogMismatch,
                    $"Function '{key.FunctionId}@{key.Revision}' was created for catalog "
                    + $"'{definition.CatalogVersion}', but '{_catalogVersion}' is available.", callNodeId);
                return null;
            }

            if (definition.Nodes.Count > MaxExpandedNodes)
            {
                Error(DiagnosticCodes.MapFunctionExpansionLimitExceeded,
                    $"Function '{key.FunctionId}@{key.Revision}' exceeds the maximum of {MaxExpandedNodes} nodes.",
                    callNodeId);
                return null;
            }

            if (!ValidateDefinition(definition)) return null;

            stack.Add(key);
            var nodes = definition.Nodes.Select(node => node.ShallowCopy()).ToList();
            var edges = definition.Edges.OrderBy(edge => edge.EdgeId, StringComparer.Ordinal).ToList();

            foreach (var nestedCall in nodes
                         .Where(node => node.NodeType == NodeTypeConstants.FunctionCall)
                         .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                         .ToList())
            {
                if (!TryGetKey(nestedCall, out var nestedKey)) continue;
                var nestedBody = Resolve(nestedKey, stack, nestedCall.NodeId);
                if (nestedBody is not null && ValidateCallNode(nestedCall, nestedBody))
                    InlineCall(nodes, edges, nestedCall, nestedBody, nestedCall.NodeId + "::");
                if (_diagnostics.HasBlockingErrors) break;
            }

            stack.RemoveAt(stack.Count - 1);
            if (_diagnostics.HasBlockingErrors) return null;
            if (!ValidateDefinition(definition with { Nodes = nodes, Edges = edges })) return null;

            var body = new FunctionBody(nodes, edges, definition.Inputs, definition.Outputs);
            _expanded.Add(key, body);
            return body;
        }

        private bool ValidateDefinition(FunctionDefinition definition)
        {
            var valid = true;
            void Invalid(string message)
            {
                Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                    $"Function '{definition.FunctionId}@{definition.Revision}' is invalid: {message}.");
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(definition.CatalogVersion))
                Invalid("catalog version is empty");
            if (!FunctionSignatures.AreValid(definition.Inputs)
                || !FunctionSignatures.AreValid(definition.Outputs) || definition.Outputs.Count == 0)
                Invalid("signatures require unique named typed ports and at least one output");
            if (!valid) return false;
            if (definition.Nodes.Any(node => !FunctionSignatures.AreValid(node.InputPorts)
                || !FunctionSignatures.AreValid(node.OutputPorts)))
            {
                Invalid("body ports must have unique names and valid shapes and cardinalities");
                return false;
            }

            var nodeIds = definition.Nodes.Select(node => node.NodeId).ToArray();
            if (nodeIds.Any(string.IsNullOrWhiteSpace) || nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Length)
                Invalid("node IDs must be non-empty and unique");
            var edgeIds = definition.Edges.Select(edge => edge.EdgeId).ToArray();
            if (edgeIds.Any(string.IsNullOrWhiteSpace) || edgeIds.Distinct(StringComparer.Ordinal).Count() != edgeIds.Length)
                Invalid("edge IDs must be non-empty and unique");
            if (nodeIds.Any(string.IsNullOrWhiteSpace)
                || nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Length
                || edgeIds.Any(string.IsNullOrWhiteSpace)
                || edgeIds.Distinct(StringComparer.Ordinal).Count() != edgeIds.Length)
                return false;

            if (HasCycle(definition.Nodes, definition.Edges))
            {
                Invalid("body graph contains a cycle");
                return false;
            }

            var inputMarkers = definition.Nodes.Where(node => node.NodeType == InputMarker).ToArray();
            var outputMarkers = definition.Nodes.Where(node => node.NodeType == OutputMarker).ToArray();
            if (inputMarkers.Length != 1 || outputMarkers.Length != 1)
            {
                Invalid("exactly one function input marker and one function output marker are required");
                return false;
            }

            var input = inputMarkers[0];
            var output = outputMarkers[0];
            if (input.InputPorts.Count != 0
                || !FunctionSignatures.Matches(definition.Inputs, input.OutputPorts, allowSinglePortAlias: true))
                Invalid("the input marker must expose the declared input signature");
            if (output.OutputPorts.Count != 0
                || !FunctionSignatures.Matches(definition.Outputs, output.InputPorts, allowSinglePortAlias: true))
                Invalid("the output marker must consume the declared output signature");

            var nodeMap = definition.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            foreach (var edge in definition.Edges)
            {
                if (!nodeMap.TryGetValue(edge.SourceNodeId, out var sourceNode)
                    || !nodeMap.TryGetValue(edge.TargetNodeId, out var targetNode))
                {
                    Invalid($"edge '{edge.EdgeId}' references an unknown node");
                    continue;
                }
                var sourcePort = sourceNode.OutputPorts.SingleOrDefault(port => port.Name == edge.SourcePortName);
                var targetPort = targetNode.InputPorts.SingleOrDefault(port => port.Name == edge.TargetPortName);
                if (sourcePort is null || targetPort is null)
                    Invalid($"edge '{edge.EdgeId}' references an unknown port");
                else if (!ShapeChecker.AreCompatible(sourcePort, targetPort)
                    || sourcePort.Cardinality == PortCardinality.Collection && targetPort.Cardinality == PortCardinality.Single)
                    Invalid($"edge '{edge.EdgeId}' has incompatible port types");
            }
            if (definition.Edges.GroupBy(edge => (edge.TargetNodeId, edge.TargetPortName))
                .Any(group => group.Count() > 1))
                Invalid("an input port cannot receive multiple edges");

            if (definition.Edges.Any(edge => edge.TargetNodeId == input.NodeId))
                Invalid("the input marker cannot receive an edge");
            if (definition.Edges.Any(edge => edge.SourceNodeId == output.NodeId))
                Invalid("the output marker cannot drive an edge");
            foreach (var port in output.InputPorts)
                if (definition.Edges.Count(edge => edge.TargetNodeId == output.NodeId && edge.TargetPortName == port.Name) != 1)
                    Invalid($"output '{port.Name}' must have exactly one incoming value");

            return valid;
        }

        private bool ValidateCallNode(MappingNode call, FunctionBody body)
        {
            var valid = FunctionSignatures.Matches(body.Inputs, call.InputPorts)
                && FunctionSignatures.Matches(body.Outputs, call.OutputPorts);
            if (!valid)
                Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                    $"Function call '{call.DisplayName}' does not match its pinned function signature.", call.NodeId);
            return valid;
        }

        private bool TryGetKey(MappingNode call, out (string FunctionId, int Revision) key)
        {
            key = default;
            var functionId = ReadString(call, "functionId");
            var revisionText = ReadString(call, "revision") ?? "1";
            if (string.IsNullOrWhiteSpace(functionId)
                || !int.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                || revision < 1)
            {
                Error(DiagnosticCodes.MapFunctionRevisionMissing,
                    $"Function call '{call.DisplayName}' has an invalid function ID or pinned revision.", call.NodeId);
                return false;
            }

            key = (functionId, revision);
            return true;
        }

        private bool InlineCall(
            List<MappingNode> nodes,
            List<MappingEdge> edges,
            MappingNode call,
            FunctionBody body,
            string prefix)
        {
            var input = body.Nodes.Single(node => node.NodeType == InputMarker);
            var output = body.Nodes.Single(node => node.NodeType == OutputMarker);
            var inputId = prefix + input.NodeId;
            var outputId = prefix + output.NodeId;
            var copiedNodeCount = body.Nodes.Count(node => node.NodeType != InputMarker && node.NodeType != OutputMarker);
            if (_expandedNodeCount + copiedNodeCount > MaxExpandedNodes)
            {
                Error(DiagnosticCodes.MapFunctionExpansionLimitExceeded,
                    $"Function inlining exceeded the maximum of {MaxExpandedNodes} expanded nodes.", call.NodeId);
                return false;
            }

            var copiedNodes = body.Nodes
                .Where(node => node.NodeType != InputMarker && node.NodeType != OutputMarker)
                .Select(node => node.ShallowCopy() with { NodeId = prefix + node.NodeId })
                .ToList();
            var copiedEdges = body.Edges.Select(edge => new MappingEdge
            {
                EdgeId = prefix + edge.EdgeId,
                SourceNodeId = prefix + edge.SourceNodeId,
                SourcePortName = edge.SourcePortName,
                TargetNodeId = prefix + edge.TargetNodeId,
                TargetPortName = edge.TargetPortName
            }).ToList();

            var addedNodeIds = copiedNodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
            if (nodes.Any(node => addedNodeIds.Contains(node.NodeId)))
            {
                Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                    $"Inlining function at '{call.NodeId}' would create duplicate node IDs.", call.NodeId);
                return false;
            }

            var callEdges = edges.Where(edge => edge.SourceNodeId == call.NodeId || edge.TargetNodeId == call.NodeId).ToList();
            if (callEdges.Any(edge => edge.TargetNodeId == call.NodeId && !body.Inputs.Any(port => port.Name == edge.TargetPortName))
                || callEdges.Any(edge => edge.SourceNodeId == call.NodeId && !body.Outputs.Any(port => port.Name == edge.SourcePortName))
                || callEdges.Where(edge => edge.TargetNodeId == call.NodeId)
                    .GroupBy(edge => edge.TargetPortName).Any(group => group.Count() > 1))
            {
                Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                    $"Function call '{call.DisplayName}' has unknown ports or duplicate input edges.", call.NodeId);
                return false;
            }
            var incoming = callEdges.Where(edge => edge.TargetNodeId == call.NodeId)
                .ToDictionary(edge => edge.TargetPortName, StringComparer.Ordinal);
            foreach (var port in body.Inputs)
            {
                if (port.IsRequired && !incoming.ContainsKey(port.Name))
                {
                    Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                        $"Function call '{call.DisplayName}' requires input '{port.Name}'.", call.NodeId);
                    return false;
                }
                // Boundary edges disappear during inlining, so validate their types first.
                if (incoming.TryGetValue(port.Name, out var edge))
                {
                    var source = nodes.FirstOrDefault(node => node.NodeId == edge.SourceNodeId);
                    var sourcePort = source?.OutputPorts.FirstOrDefault(candidate => candidate.Name == edge.SourcePortName);
                    // Other top-level calls are removed from nodes before expansion.
                    sourcePort ??= _source.Nodes.GetValueOrDefault(edge.SourceNodeId)?.OutputPorts
                        .FirstOrDefault(candidate => candidate.Name == edge.SourcePortName);
                    if (sourcePort is null || !ShapeChecker.AreCompatible(sourcePort, port)
                        || sourcePort.Cardinality == PortCardinality.Collection && port.Cardinality == PortCardinality.Single)
                    {
                        Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                            $"Function call '{call.DisplayName}' input '{port.Name}' has an incompatible source.", call.NodeId);
                        return false;
                    }
                }
            }

            foreach (var edge in callEdges.Where(edge => edge.SourceNodeId == call.NodeId))
            {
                var target = nodes.FirstOrDefault(node => node.NodeId == edge.TargetNodeId)
                    ?? _source.Nodes.GetValueOrDefault(edge.TargetNodeId);
                var targetPort = target?.InputPorts.FirstOrDefault(port => port.Name == edge.TargetPortName);
                var sourcePort = body.Outputs.Single(port => port.Name == edge.SourcePortName);
                if (edge.TargetNodeId == call.NodeId || targetPort is null
                    || !ShapeChecker.AreCompatible(sourcePort, targetPort)
                    || sourcePort.Cardinality == PortCardinality.Collection && targetPort.Cardinality == PortCardinality.Single)
                {
                    Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                        $"Function call '{call.DisplayName}' output '{sourcePort.Name}' has an incompatible target or self-reference.", call.NodeId);
                    return false;
                }
            }

            string PublicInput(string markerPort) => body.Inputs.Count == 1
                ? body.Inputs[0].Name : markerPort;
            string MarkerOutput(string publicPort) => body.Outputs.Count == 1
                ? output.InputPorts[0].Name : publicPort;
            (string Node, string Port)? SourceOf(MappingEdge edge)
            {
                if (edge.SourceNodeId != inputId) return (edge.SourceNodeId, edge.SourcePortName);
                return incoming.TryGetValue(PublicInput(edge.SourcePortName), out var external)
                    ? (external.SourceNodeId, external.SourcePortName) : null;
            }

            var rewrittenEdges = new List<MappingEdge>();
            foreach (var edge in copiedEdges.Where(edge => edge.TargetNodeId != outputId))
            {
                var source = SourceOf(edge);
                if (source is null) continue;
                rewrittenEdges.Add(edge with { SourceNodeId = source.Value.Node, SourcePortName = source.Value.Port });
            }
            foreach (var edge in callEdges.Where(edge => edge.SourceNodeId == call.NodeId))
            {
                var outputEdge = copiedEdges.Single(candidate => candidate.TargetNodeId == outputId
                    && candidate.TargetPortName == MarkerOutput(edge.SourcePortName));
                var source = SourceOf(outputEdge);
                if (source is null) continue;
                rewrittenEdges.Add(edge with { SourceNodeId = source.Value.Node, SourcePortName = source.Value.Port });
            }

            var callEdgeIds = callEdges.Select(edge => edge.EdgeId).ToHashSet(StringComparer.Ordinal);
            var resultingEdgeIds = rewrittenEdges.Select(edge => edge.EdgeId).ToHashSet(StringComparer.Ordinal);
            if (resultingEdgeIds.Count != rewrittenEdges.Count
                || edges.Any(edge => !callEdgeIds.Contains(edge.EdgeId) && resultingEdgeIds.Contains(edge.EdgeId)))
            {
                Error(DiagnosticCodes.MapFunctionDefinitionInvalid,
                    $"Inlining function at '{call.NodeId}' would create duplicate edge IDs.", call.NodeId);
                return false;
            }

            nodes.RemoveAll(node => node.NodeId == call.NodeId);
            nodes.AddRange(copiedNodes);
            edges.RemoveAll(edge => callEdgeIds.Contains(edge.EdgeId));
            edges.AddRange(rewrittenEdges);
            _expandedNodeCount += copiedNodes.Count;
            return true;
        }

        // Check before removing boundary nodes: pass-through calls must not erase cycles.
        private static bool HasCycle(IEnumerable<MappingNode> nodes, IEnumerable<MappingEdge> edges)
        {
            var indegree = nodes.ToDictionary(node => node.NodeId, _ => 0, StringComparer.Ordinal);
            var outgoing = indegree.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                if (!indegree.ContainsKey(edge.SourceNodeId) || !indegree.ContainsKey(edge.TargetNodeId)) continue;
                indegree[edge.TargetNodeId]++;
                outgoing[edge.SourceNodeId].Add(edge.TargetNodeId);
            }
            var ready = new Queue<string>(indegree.Where(item => item.Value == 0).Select(item => item.Key));
            var visited = 0;
            while (ready.TryDequeue(out var id))
            {
                visited++;
                foreach (var target in outgoing[id])
                    if (--indegree[target] == 0) ready.Enqueue(target);
            }
            return visited != indegree.Count;
        }

        private static string? ReadString(MappingNode node, string name)
        {
            if (!node.Parameters.TryGetValue(name, out var value) || value is null) return null;
            return value is System.Text.Json.JsonElement element
                ? element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() : element.GetRawText()
                : value.ToString();
        }

        private void Error(string code, string message, string? nodeId = null)
            => _diagnostics.Error(code, DiagnosticSubsystem.Mapping, DiagnosticStage.Compilation,
                message, nodeId: nodeId);
    }

    private sealed record FunctionBody(IReadOnlyList<MappingNode> Nodes, IReadOnlyList<MappingEdge> Edges,
        IReadOnlyList<PortDefinition> Inputs, IReadOnlyList<PortDefinition> Outputs);
}
