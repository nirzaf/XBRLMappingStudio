using System.Globalization;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// A deliberately narrow grouping contract that may be evaluated one complete
/// group at a time. Scalar sink inputs are independent of source row position.
/// No join, row-dependent transform, additional set operator or second sink is
/// admitted. The descriptor is derived from the validated, expanded graph.
/// </summary>
public sealed record PartitionedGroupPlan(
    string BindingId, string GroupNodeId, IReadOnlyList<string> KeyFields,
    IReadOnlySet<string> RequiredFields, IReadOnlySet<string>? RetainedFields = null)
{
    internal static PartitionedGroupPlan? TryCreate(MappingGraph graph)
    {
        var nodes = graph.Nodes.Values.ToArray();
        if (nodes.Any(n => n.NodeType is not (NodeTypeConstants.CollectRows
            or NodeTypeConstants.GroupBy or NodeTypeConstants.FactConstruct
            or NodeTypeConstants.Constant or NodeTypeConstants.RunParameter
            or NodeTypeConstants.BuildInstantDate or NodeTypeConstants.BuildDurationDate)))
            return null;
        var collects = nodes.Where(n => n.NodeType == NodeTypeConstants.CollectRows).ToArray();
        var groups = nodes.Where(n => n.NodeType == NodeTypeConstants.GroupBy).ToArray();
        var sinks = nodes.Where(n => n.NodeType == NodeTypeConstants.FactConstruct).ToArray();
        if (collects.Length != 1 || groups.Length != 1 || sinks.Length != 1) return null;
        var collect = collects[0];
        var group = groups[0];
        var sink = sinks[0];
        var input = graph.IncomingEdge(group.NodeId, "items");
        var value = graph.IncomingEdge(sink.NodeId, "value");
        var values = graph.IncomingEdge(sink.NodeId, "values");
        // Do not admit an ambiguous scalar/collection fallback.
        if (value is not null && values is not null) return null;
        value ??= values;
        if (input?.SourceNodeId != collect.NodeId || input.SourcePortName != "items"
            || value?.SourceNodeId != group.NodeId || value.SourcePortName != "values")
            return null;
        foreach (var edge in graph.Edges.Values)
        {
            if (edge.SourceNodeId == collect.NodeId && edge != input) return null;
            if (edge.SourceNodeId == group.NodeId
                && (edge.TargetNodeId != sink.NodeId || edge.SourcePortName is not ("keys" or "values")))
                return null;
        }
        if (new[] { (collect, "binding"), (collect, "fields"), (group, "keys"), (group, "sumField") }
            .Any(item => item.Item1.Parameters.TryGetValue(item.Item2, out var value)
                && value is JsonElement element && element.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            return null;
        var binding = Parameter(collect, "binding");
        if (binding is null) return null;
        var keys = (Parameter(group, "keys") ?? "").Split(',', StringSplitOptions.TrimEntries);
        var required = keys.Append(Parameter(group, "sumField") ?? "").ToHashSet(StringComparer.Ordinal);
        var retained = Parameter(collect, "fields");
        var retainedFields = string.IsNullOrEmpty(retained) ? null
            : retained.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        if (retainedFields is not null) required.IntersectWith(retainedFields);
        return new(binding, group.NodeId, keys, required, retainedFields);
    }

    private static string? Parameter(MappingNode node, string name)
        => !node.Parameters.TryGetValue(name, out var value) ? null : value switch
        {
            null => null,
            JsonElement element when element.ValueKind is JsonValueKind.String or JsonValueKind.Null => element.GetString(),
            _ => value.ToString()
        };
}

/// <summary>Shared ordinal group identity, independent of delimiter characters.</summary>
public static class GroupIdentity
{
    public static string Encode(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':');
            foreach (var character in value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.Append(character);
            }
        }
        return builder.ToString();
    }
}
