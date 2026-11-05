using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace XBRLMappingStudio.Diagnostics;

/// <summary>
/// Terminal outcome of a job. Distinguishes confirmed states from ambiguous ones per
/// section 9 (Job lifecycle and publication).
/// </summary>
public enum RunOutcome
{
    /// <summary>All required steps completed and output published.</summary>
    Success,
    /// <summary>Completed with permitted warnings only.</summary>
    SuccessWithWarnings,
    /// <summary>Blocking diagnostic prevented execution.</summary>
    InvalidMapping,
    /// <summary>Source data mismatch or read failure.</summary>
    DataError,
    /// <summary>Profile or configuration incomplete.</summary>
    IncompleteConfiguration,
    /// <summary>Credential or authorization failed.</summary>
    AuthorizationFailure,
    /// <summary>Cooperative cancellation observed.</summary>
    Cancelled,
    /// <summary>Worker crash, timeout or operational failure.</summary>
    OperationalFailure,
    /// <summary>Publication attempted but not confirmed. Requires reconciliation.</summary>
    Unknown
}

/// <summary>
/// Structured identity of a run per BASE04. Records inputs, versions, and digests so
/// that an outcome can be reconciled and, where supported, replayed after a fault.
/// </summary>
public sealed class RunManifest
{
    public required string RunId { get; init; }
    public required string OperationId { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public required string ProjectId { get; init; }
    public int SemanticRevision { get; init; }
    public string ProjectSemanticDigest { get; init; } = "";
    public required string ProfileDigest { get; init; }
    public required string EngineVersion { get; init; }

    /// <summary>Locale, fiscal calendar, and rule digests frozen at start per section 2.</summary>
    public IReadOnlyDictionary<string, string> FrozenParameters { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Source content fingerprint or immutable DB snapshot identifier per input.</summary>
    public IReadOnlyList<InputIdentity> Inputs { get; set; } = Array.Empty<InputIdentity>();

    /// <summary>Digest of each artifact produced.</summary>
    public IReadOnlyList<OutputIdentity> Outputs { get; set; } = Array.Empty<OutputIdentity>();

    /// <summary>Digest of the exact staged bytes accepted by the selected profile worker.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidatedArtifactDigest { get; set; }

    /// <summary>Digest confirmed at the destination after an authorized publication.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublishedArtifactDigest { get; set; }

    public RunOutcome Outcome { get; set; } = RunOutcome.Unknown;

    /// <summary>
    /// True when this run was a bounded preview/draft evaluation rather than a full,
    /// publishable execution. A preview outcome is labelled separately from a full run
    /// even when both finish with <see cref="RunOutcome.SuccessWithWarnings"/>, so a
    /// sampled preview can never be mistaken for something fit to publish (OG01).
    /// </summary>
    public bool IsPreview { get; set; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; set; } = Array.Empty<Diagnostic>();

    public static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record InputIdentity(
    string SourceId,
    string Kind,        // "file", "sqlite-table", "sqlite-preview", "snapshot"
    string? Digest,     // content fingerprint or immutable snapshot id; may exist without replay support
    bool IsReplayable,
    string? OriginalPath,
    string? RowIdentityStrategy = null)
{
    /// <summary>Read consistency mechanism; distinct from content fingerprint and row identity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnapshotStrategy { get; init; }
}

public sealed record OutputIdentity(
    string Path,
    string Digest,
    long Bytes,
    string Kind)        // "xbrl-json", "xlsx", "db-receipt"
{
    /// <summary>Rows affected by a durable database publication receipt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RowsWritten { get; init; }

    /// <summary>Stable operation key stored with a database publication receipt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationKey { get; init; }

    /// <summary>Database table associated with a database publication receipt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Table { get; init; }
}

/// <summary>
/// Mutable collector for diagnostics during a single operation.
/// </summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasBlockingErrors => _items.Any(d => d.IsBlocking);

    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

    public void Info(string code, string subsystem, string stage, string message,
        string? nodeId = null, SourceReference? source = null, string? operationId = null)
        => _items.Add(new Diagnostic
        {
            Code = code,
            Subsystem = subsystem,
            Severity = DiagnosticSeverity.Information,
            Stage = stage,
            IsBlocking = false,
            Message = message,
            NodeId = nodeId,
            Source = source,
            OperationId = operationId
        });

    public void Warn(string code, string subsystem, string stage, string message,
        string? nodeId = null, SourceReference? source = null, string? operationId = null,
        IReadOnlyDictionary<string, string>? details = null)
        => _items.Add(new Diagnostic
        {
            Code = code,
            Subsystem = subsystem,
            Severity = DiagnosticSeverity.Warning,
            Stage = stage,
            IsBlocking = false,
            Message = message,
            NodeId = nodeId,
            Source = source,
            OperationId = operationId,
            Details = details
        });

    public void Error(string code, string subsystem, string stage, string message,
        string? nodeId = null, string? portName = null, SourceReference? source = null,
        string? operationId = null,
        IReadOnlyDictionary<string, string>? details = null,
        string? edgeId = null)
        => _items.Add(new Diagnostic
        {
            Code = code,
            Subsystem = subsystem,
            Severity = DiagnosticSeverity.Error,
            Stage = stage,
            IsBlocking = true,
            Message = message,
            NodeId = nodeId,
            PortName = portName,
            EdgeId = edgeId,
            Source = source,
            OperationId = operationId,
            Details = details
        });

    public IEnumerable<Diagnostic> OfCode(string code) => _items.Where(d => d.Code == code);
}

[JsonSerializable(typeof(RunManifest))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(RunOutcome))]
public partial class DiagnosticsJsonContext : JsonSerializerContext
{
}
