using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters;

/// <summary>
/// A flat tabular schema shared by the CSV, JSON and XML read adapters. Column order is
/// fixed at discovery time; the fingerprint covers ordered column names so a header or
/// key rename is a detectable drift (IO01/IO02 contract).
/// </summary>
public sealed record TabularSchema(
    string SourceId,
    IReadOnlyList<ColumnSchema> Columns,
    string Fingerprint) : ISourceSchema
{
    public static string ComputeFingerprint(IEnumerable<ColumnSchema> cols)
    {
        var joined = string.Join('|', cols
            .OrderBy(c => c.Ordinal)
            .Select(c => $"{c.Ordinal}:{c.Name}:{c.InferredShape}"));
        return XBRLMappingStudio.Diagnostics.RunManifest.ComputeSha256(joined);
    }

    public static TabularSchema FromSource(string sourceId, IEnumerable<ColumnSchema> cols)
    {
        var list = cols.ToList();
        return new TabularSchema(sourceId, list, ComputeFingerprint(list));
    }
}
