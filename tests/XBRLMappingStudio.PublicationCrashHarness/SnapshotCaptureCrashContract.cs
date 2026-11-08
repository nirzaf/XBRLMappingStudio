using XBRLMappingStudio.Adapters;

namespace XBRLMappingStudio.PublicationCrashHarness;

/// <summary>Shared subprocess fixture protocol for retained SQLite snapshot capture.</summary>
public static class SnapshotCaptureCrashContract
{
    public const string SourceId = "snapshot-crash-source";
    public const string Table = "financials";
    public const string DecimalColumns = "amount";
    public const string StagingCreated = "staging-created";
    public const string MidCopy = "mid-copy";
    public const string AfterCopy = "after-copy";
    public const string MetadataCreated = "metadata-created";
    public const string MetadataFlushed = "metadata-flushed";
    public const string BeforePublish = "before-publish";
    public const string AfterPublish = "after-publish";
    public const int ReleaseTimeoutSeconds = 60;

    /// <summary>Path is the private staging directory, or the destination after publication.</summary>
    public sealed record ReadySignal(string Boundary, string Path);

    public static bool IsSupportedBoundary(string boundary)
        => boundary is StagingCreated or MidCopy or AfterCopy or MetadataCreated
            or MetadataFlushed or BeforePublish or AfterPublish;

    public static SourceConnection CreateSource(string sourceDatabase)
        => new(SourceId, "sqlite", Path.GetFullPath(sourceDatabase),
            new Dictionary<string, string>
            {
                ["table"] = Table,
                ["decimalColumns"] = DecimalColumns
            });
}
