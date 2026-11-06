using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XBRLMappingStudio.Project;

/// <summary>
/// Computes a semantic digest of executable content, ignoring layout. Two visually
/// different layouts must produce the same digest (MAP01 acceptance).
/// </summary>
public static class SemanticRevisionCalculator
{
    /// <summary>
    /// Stable SHA-256 over canonicalized executable content. Node order and edge order
    /// are normalized so equivalent graphs hash identically.
    /// </summary>
    public static string Compute(MappingProject project)
    {
        var sb = new StringBuilder();
        sb.Append("v1|");
        sb.Append("profile=").Append(project.ProfileId).Append('|');
        sb.Append("digest=").Append(project.ProfileDigest).Append('|');

        // Nodes sorted by NodeId; layout omitted.
        var nodes = project.Nodes.OrderBy(n => n.NodeId, StringComparer.Ordinal).ToList();
        sb.Append("nodes=[");
        foreach (var n in nodes)
        {
            sb.Append(n.NodeId).Append(':').Append(n.NodeType).Append(':');
            AppendSortedDict(sb, n.Parameters);
            sb.Append(":in=");
            foreach (var p in n.InputPorts.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                sb.Append(p.Name).Append('/').Append(p.Shape).Append('/').Append(p.Cardinality)
                  .Append('/').Append(p.IsRequired ? "R" : "O").Append(',');
            }
            sb.Append(":out=");
            foreach (var p in n.OutputPorts.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                sb.Append(p.Name).Append('/').Append(p.Shape).Append('/').Append(p.Cardinality)
                  .Append('/').Append(p.IsRequired ? "R" : "O").Append(',');
            }
            sb.Append(';');
        }
        sb.Append("]|");

        // Edges sorted by EdgeId and endpoints.
        var edges = project.Edges
            .OrderBy(e => e.SourceNodeId, StringComparer.Ordinal)
            .ThenBy(e => e.SourcePortName, StringComparer.Ordinal)
            .ThenBy(e => e.TargetNodeId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetPortName, StringComparer.Ordinal)
            .ToList();
        sb.Append("edges=[");
        foreach (var e in edges)
        {
            sb.Append(e.SourceNodeId).Append('.').Append(e.SourcePortName)
              .Append("->").Append(e.TargetNodeId).Append('.').Append(e.TargetPortName).Append(';');
        }
        sb.Append("]|");

        // Bindings sorted by BindingId.
        var bindings = project.Bindings.OrderBy(b => b.BindingId, StringComparer.Ordinal).ToList();
        sb.Append("bindings=[");
        foreach (var b in bindings)
        {
            sb.Append(b.BindingId).Append(':').Append(b.Kind).Append(':');
            AppendSortedDict(sb, b.Configuration);
            sb.Append(';');
        }
        sb.Append(']');

        // MAP07: reusable function definitions participate in the semantic digest so
        // editing a function yields a new revision while layout-only edits do not.
        sb.Append("|functions=[");
        var functions = project.Functions.OrderBy(f => f.FunctionId, StringComparer.Ordinal)
            .ThenBy(f => f.Revision).ToList();
        if (functions.Count > 0)
            sb.Append("catalog=").Append(project.FunctionCatalogVersion).Append(';');
        foreach (var f in functions)
        {
            sb.Append(f.FunctionId).Append('@').Append(f.Revision).Append(':').Append(f.Name)
                .Append(":catalog=").Append(f.CatalogVersion).Append(":inputs=");
            AppendPorts(sb, f.Inputs);
            sb.Append(":outputs=");
            AppendPorts(sb, f.Outputs);
            sb.Append(":{");
            foreach (var n in f.Nodes.OrderBy(n => n.NodeId, StringComparer.Ordinal))
            {
                sb.Append(n.NodeId).Append('/').Append(n.NodeType).Append('/');
                AppendSortedDict(sb, n.Parameters);
                sb.Append(":in=");
                AppendPorts(sb, n.InputPorts);
                sb.Append(":out=");
                AppendPorts(sb, n.OutputPorts);
                sb.Append(',');
            }
            sb.Append("}:edges={");
            foreach (var e in f.Edges.OrderBy(e => e.SourceNodeId, StringComparer.Ordinal)
                       .ThenBy(e => e.TargetNodeId, StringComparer.Ordinal))
            {
                sb.Append(e.SourceNodeId).Append('.').Append(e.SourcePortName)
                  .Append("->").Append(e.TargetNodeId).Append('.').Append(e.TargetPortName).Append(';');
            }
            sb.Append("};");
        }
        sb.Append(']');

        // A configured destination changes the executable/publication intent and
        // therefore invalidates approvals. Keep the no-destination representation
        // byte-for-byte compatible with the pre-destination v1 semantic digest.
        if (project.Destination is { } destination)
        {
            sb.Append("|destination=[");
            AppendPart(sb, destination.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendPart(sb, destination.Kind ?? "");
            AppendPart(sb, destination.Locator ?? "");
            AppendPart(sb, destination.Table ?? "");
            AppendPart(sb, destination.Format ?? "");
            AppendPart(sb, destination.WriteMode ?? "");
            AppendPart(sb, destination.Compatibility ?? "");
            var businessKey = destination.BusinessKey ?? new List<string>();
            sb.Append(businessKey.Count).Append(':');
            foreach (var key in businessKey)
                AppendPart(sb, key ?? "");
            sb.Append(']');
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Determine whether two revisions differ semantically. Used by the desktop to
    /// decide whether a save bumps SemanticRevision.
    /// </summary>
    public static bool Differs(MappingProject before, MappingProject after)
        => !string.Equals(Compute(before), Compute(after), StringComparison.Ordinal);

    private static void AppendSortedDict(StringBuilder sb, IDictionary<string, object?> dict)
    {
        sb.Append('{');
        foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=');
            sb.Append(RenderValue(kv.Value));
            sb.Append(',');
        }
        sb.Append('}');
    }

    private static void AppendPorts(StringBuilder sb, IEnumerable<PortDefinition> ports)
    {
        foreach (var port in ports.OrderBy(port => port.Name, StringComparer.Ordinal))
        {
            sb.Append(port.Name).Append('/').Append(port.Shape).Append('/').Append(port.Cardinality)
                .Append('/').Append(port.IsRequired ? "R" : "O").Append(',');
        }
    }

    private static void AppendPart(StringBuilder sb, string value)
        => sb.Append(value.Length).Append(':').Append(value);

    private static string RenderValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "1" : "0",
        JsonElement je => RenderJson(je),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string RenderJson(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString() ?? "",
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Null => "null",
        JsonValueKind.Number => je.GetRawText(),
        _ => je.GetRawText()
    };
}
