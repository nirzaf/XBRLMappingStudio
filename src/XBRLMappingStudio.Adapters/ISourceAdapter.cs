using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters;

/// <summary>
/// Declared capabilities of a source or target adapter (IO01). The designer marks
/// unsupported operations before a user starts mapping.
/// </summary>
public sealed record AdapterCapabilities(
    bool SupportsSchemaInspection,
    bool SupportsSampling,
    bool SupportsReading,
    bool SupportsStagedWrite,
    bool SupportsPublication,
    bool SupportsLineage)
{
    public static AdapterCapabilities ReadOnly =>
        new(true, true, true, false, false, true);

    public static AdapterCapabilities WriteOnly =>
        new(false, false, false, true, true, true);

    public static AdapterCapabilities ReadWrite =>
        new(true, true, true, true, true, true);

    /// <summary>True when the adapter can serve as a target sink.</summary>
    public bool AllowsWriteTarget => SupportsStagedWrite || SupportsPublication;
}

public enum AdapterDirection
{
    Read,
    Write
}

/// <summary>
/// Connection information for one source. Adapters resolve environment-specific
/// credentials at execution; projects store only portable locators.
/// </summary>
public sealed record SourceConnection(
    string SourceId,
    string Kind,               // "excel", "csv", "db", ...
    string? Path,              // workspace-relative or absolute
    IReadOnlyDictionary<string, string>? Configuration = null);

/// <summary>
/// Result of schema introspection: sheet/table + ordered columns with inferred types.
/// A stable fingerprint of this shape detects schema drift (IO01).
/// </summary>
public interface ISourceSchema
{
    string SourceId { get; }
    string Fingerprint { get; }
    IReadOnlyList<ColumnSchema> Columns { get; }
}

public sealed record ColumnSchema(
    string Name,
    PortShape InferredShape,
    int Ordinal,
    string? DisplayName = null);

/// <summary>
/// Small sample returned by preview requests. Adapters must not stream full sources
/// through this path.
/// </summary>
public sealed record DataSample(
    ISourceSchema Schema,
    IReadOnlyList<DataRecord> Rows,
    bool IsTruncated);

/// <summary>
/// Contract implemented by every source adapter (IO01). The engine calls these methods
/// through <see cref="SourceBinding"/> resolution.
/// </summary>
public interface ISourceAdapter
{
    string Kind { get; }
    AdapterCapabilities Capabilities { get; }

    Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken);

    Task<DataSample> ReadSampleAsync(
        SourceConnection connection,
        ISourceSchema schema,
        int maxRows,
        CancellationToken cancellationToken);

    IAsyncEnumerable<DataRecord> ReadFullAsync(
        SourceConnection connection,
        ISourceSchema schema,
        CancellationToken cancellationToken);
}

public interface ITargetAdapter
{
    string Kind { get; }
    AdapterCapabilities Capabilities { get; }

    /// <summary>Stage a candidate artifact. The engine calls this before publication.</summary>
    Task<string> StageArtifactAsync(
        SourceConnection destination,
        byte[] payload,
        string suggestedExtension,
        CancellationToken cancellationToken);

    /// <summary>Atomically replace the destination with the staged artifact.</summary>
    Task<OutputIdentity> PublishAsync(
        string stagedPath,
        SourceConnection destination,
        CancellationToken cancellationToken);
}
