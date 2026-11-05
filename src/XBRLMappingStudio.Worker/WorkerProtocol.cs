using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Worker;

/// <summary>
/// Worker request operation kinds. Kept coarse (per section 2 process protocol) to
/// avoid one call per cell.
/// </summary>
public static class WorkerOperations
{
    public const string Handshake = "handshake";
    public const string LoadTaxonomy = "load-taxonomy";
    public const string SearchConcepts = "search-concepts";
    public const string ValidateReport = "validate-report";
    public const string ConvertXmlToOim = "xml-to-oim";
    public const string Shutdown = "shutdown";
}

/// <summary>
/// Framed request sent over the worker's standard input. Each request carries an
/// operation ID, job ID, profile digest, and a deadline (section 2 Process protocol).
/// </summary>
public sealed record WorkerRequest(
    string Operation,
    string OperationId,
    string JobId,
    string ProfileDigest,
    DateTimeOffset DeadlineUtc,
    JsonElement? Payload)
{
    public string Encode()
    {
        var json = JsonSerializer.Serialize(this, WorkerJson.Options);
        // UTF-8 framed message: newline-delimited.
        return json + "\n";
    }
}

public sealed record WorkerResponse(
    string OperationId,
    WorkerStatus Status,
    JsonElement? Result,
    IReadOnlyList<Diagnostic>? Diagnostics,
    string? Error)
{
    public static WorkerResponse Decode(string line)
        => JsonSerializer.Deserialize<WorkerResponse>(line, WorkerJson.Options)
           ?? throw new InvalidOperationException("Malformed worker response.");
}

public enum WorkerStatus
{
    Success,
    DomainFailure,
    IncompleteValidation,
    Cancelled,
    TimedOut,
    Crash
}

internal static class WorkerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>
/// Protocol version negotiation. Sent as the first framed message.
/// </summary>
public sealed record WorkerHandshake(
    int ProtocolVersion,
    string EngineVersion,
    IReadOnlyList<string> Capabilities)
{
    public bool? ArelleAvailable { get; init; }
    public int? ProfileSchemaVersion { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileVersion { get; init; }
    public string? ProfileDigest { get; init; }
    public string? TaxonomyEntryPoint { get; init; }
    public IReadOnlyList<string> RequiredPlugins { get; init; } = Array.Empty<string>();
}

/// <summary>Versioned evidence returned after the worker loads the selected taxonomy.</summary>
public sealed record WorkerTaxonomyLoadResult(
    string Engine,
    string EngineVersion,
    IReadOnlyList<string> Capabilities,
    bool Completed,
    int? ProfileSchemaVersion,
    string? ProfileId,
    string? ProfileVersion,
    string? ProfileDigest,
    string? TaxonomyEntryPoint,
    IReadOnlyList<string> RequiredPlugins,
    int ConceptCount);

/// <summary>A worker returned a terminal status before its validation handshake completed.</summary>
public sealed class WorkerStartupException : Exception
{
    public WorkerStatus Status { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public WorkerStartupException(WorkerStatus status, string? message,
        IReadOnlyList<Diagnostic>? diagnostics = null)
        : base(message ?? $"Worker startup ended with status '{status}'.")
    {
        Status = status;
        Diagnostics = diagnostics ?? Array.Empty<Diagnostic>();
    }
}

public static class WorkerProtocol
{
    public const int CurrentVersion = 1;
    public const int MinimumVersion = 1;
    public const int MaxMessageBytes = 4 * 1024 * 1024; // 4 MiB bound per section 2

    public static void ValidateByteLimit(string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(message);
        if (bytes > MaxMessageBytes)
            throw new InvalidOperationException(
                $"Worker message exceeds {MaxMessageBytes} bytes ({bytes}).");
    }
}
