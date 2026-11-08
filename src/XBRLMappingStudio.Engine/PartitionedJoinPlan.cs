using System.Text.Json;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Two directly collected sources joined and grouped by the same single field.
/// A complete key partition can use the ordinary engine without splitting any
/// downstream aggregate, changing null semantics or reconstructing provenance.
/// </summary>
public sealed record PartitionedJoinPlan(
    string LeftBindingId, string RightBindingId, string JoinNodeId, string GroupNodeId,
    string KeyField, string Cardinality, string DuplicatePolicy, string ManyToManyPolicy,
    IReadOnlySet<string>? LeftFields, IReadOnlySet<string>? RightFields)
{
    internal static PartitionedJoinPlan? TryCreate(MappingGraph graph)
    {
        var nodes = graph.Nodes.Values.ToArray();
        if (nodes.Any(n => n.NodeType is not (NodeTypeConstants.CollectRows or NodeTypeConstants.Join
            or NodeTypeConstants.GroupBy or NodeTypeConstants.FactConstruct or NodeTypeConstants.Constant
            or NodeTypeConstants.RunParameter or NodeTypeConstants.BuildInstantDate or NodeTypeConstants.BuildDurationDate)))
            return null;
        var collects = nodes.Where(n => n.NodeType == NodeTypeConstants.CollectRows).ToArray();
        var joins = nodes.Where(n => n.NodeType == NodeTypeConstants.Join).ToArray();
        var groups = nodes.Where(n => n.NodeType == NodeTypeConstants.GroupBy).ToArray();
        var sinks = nodes.Where(n => n.NodeType == NodeTypeConstants.FactConstruct).ToArray();
        if (collects.Length != 2 || joins.Length != 1 || groups.Length != 1 || sinks.Length != 1) return null;
        var join = joins[0];
        var group = groups[0];
        var sink = sinks[0];
        // Scalar text fallbacks must not emit a fact for an empty join partition.
        if (graph.IncomingEdge(sink.NodeId, "textValue") is not null
            || graph.IncomingEdge(sink.NodeId, "textValues") is not null) return null;
        var leftEdge = graph.IncomingEdge(join.NodeId, "left");
        var rightEdge = graph.IncomingEdge(join.NodeId, "right");
        var left = collects.SingleOrDefault(n => n.NodeId == leftEdge?.SourceNodeId);
        var right = collects.SingleOrDefault(n => n.NodeId == rightEdge?.SourceNodeId);
        if (left is null || right is null || left == right
            || leftEdge!.SourcePortName != "items" || rightEdge!.SourcePortName != "items") return null;
        var groupInput = graph.IncomingEdge(group.NodeId, "items");
        var value = graph.IncomingEdge(sink.NodeId, "value");
        var values = graph.IncomingEdge(sink.NodeId, "values");
        if (value is not null && values is not null) return null;
        value ??= values;
        if (groupInput?.SourceNodeId != join.NodeId || groupInput.SourcePortName != "rows"
            || value?.SourceNodeId != group.NodeId || value.SourcePortName != "values") return null;
        foreach (var edge in graph.Edges.Values)
        {
            if (edge.SourceNodeId == left.NodeId && edge != leftEdge) return null;
            if (edge.SourceNodeId == right.NodeId && edge != rightEdge) return null;
            if (edge.SourceNodeId == join.NodeId && edge != groupInput) return null;
            if (edge.SourceNodeId == group.NodeId
                && (edge.TargetNodeId != sink.NodeId || edge.SourcePortName is not ("keys" or "values"))) return null;
        }
        // Match PlanCompiler's string parameter semantics without accepting a
        // structured JSON value as a different string interpretation.
        foreach (var node in new[] { left, right, join, group })
            if (node.Parameters.Values.Any(v => v is JsonElement e
                && e.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))) return null;
        var leftBinding = Parameter(left, "binding");
        var rightBinding = Parameter(right, "binding");
        var key = Parameter(join, "leftKey");
        if (string.IsNullOrEmpty(leftBinding) || string.IsNullOrEmpty(rightBinding) || leftBinding == rightBinding
            || string.IsNullOrEmpty(key) || key != Parameter(join, "rightKey")
            || key != Parameter(group, "keys")?.Trim() || key.Contains(',')) return null;
        var leftFields = Fields(left);
        var rightFields = Fields(right);
        if ((leftFields is not null && !leftFields.Contains(key))
            || (rightFields is not null && !rightFields.Contains(key))) return null;
        var cardinality = Parameter(join, "cardinality") ?? "Inner";
        var duplicate = Parameter(join, "duplicateKey") ?? "All";
        var many = Parameter(join, "manyToMany") ?? "Allow";
        var conflict = Parameter(join, "fieldConflict") ?? "Right";
        if (cardinality is not ("Inner" or "Left" or "Right" or "Full")
            || duplicate is not ("All" or "First" or "Fail") || many is not ("Allow" or "Fail")
            || conflict is not ("Left" or "Right" or "Fail")) return null;
        return new(leftBinding, rightBinding, join.NodeId, group.NodeId, key,
            cardinality, duplicate, many, leftFields, rightFields);
    }

    private static IReadOnlySet<string>? Fields(MappingNode node)
        => string.IsNullOrEmpty(Parameter(node, "fields")) ? null
            : Parameter(node, "fields")!.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    private static string? Parameter(MappingNode node, string name)
        => !node.Parameters.TryGetValue(name, out var value) ? null : value switch
        {
            null => null,
            JsonElement e when e.ValueKind is JsonValueKind.String or JsonValueKind.Null => e.GetString(),
            _ => value.ToString()
        };
}
