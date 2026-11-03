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

    private static string RenderValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "1" : "0",
        JsonElement je => je.GetRawText(),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
