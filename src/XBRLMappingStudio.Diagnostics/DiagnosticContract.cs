using System.Text.Json.Serialization;

namespace XBRLMappingStudio.Diagnostics;

/// <summary>
/// Severity of a diagnostic. Blocking errors prevent execution or publication.
/// </summary>
public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Execution stage where the diagnostic was emitted.
/// </summary>
public static class DiagnosticStage
{
    public const string Introspection = "Introspection";
    public const string Validation = "Validation";
    public const string Compilation = "Compilation";
    public const string Execution = "Execution";
    public const string Serialization = "Serialization";
    public const string Publication = "Publication";
    public const string Worker = "Worker";
}

/// <summary>
/// Product subsystem that produced the diagnostic.
/// </summary>
public static class DiagnosticSubsystem
{
    public const string Mapping = "Mapping";
    public const string Project = "Project";
    public const string Adapter = "Adapter";
    public const string XBRL = "XBRL";
    public const string Worker = "Worker";
    public const string Publication = "Publication";
    public const string AI = "AI";
}

/// <summary>
/// A reference to a source location that can be traced back for evidence.
/// Format is opaque to the diagnostic system; adapters populate meaningful values.
/// Examples: "Sheet1!B3", "row=7,col=2", "db://table.column#r42".
/// </summary>
public sealed record SourceReference(
    string SourceId,
    string Locator,
    string? DisplayName = null)
{
    public override string ToString() => DisplayName ?? $"{SourceId}:{Locator}";
}

/// <summary>A connected row-phase transformation path associated with one source occurrence.</summary>
public sealed record SourceTransformationPath(
    SourceReference Source,
    IReadOnlyList<string> TransformationIds);

/// <summary>
/// Structured diagnostic per BASE04 contract. Stable product code + evidence locators.
/// The same invalid mapping must produce the same code and binding ID in CLI and desktop.
/// </summary>
public sealed record Diagnostic
{
    private IReadOnlyList<SourceTransformationPath>? _sourceTransformationPaths;

    /// <summary>Stable product code, e.g. "MAP001", "IO012", "XBR003". Never reassigned.</summary>
    public required string Code { get; init; }

    /// <summary>Subsystem that produced this diagnostic.</summary>
    public required string Subsystem { get; init; }

    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>Lifecycle stage: Validation, Execution, Publication, etc.</summary>
    public required string Stage { get; init; }

    /// <summary>If true, execution or publication must be blocked.</summary>
    public bool IsBlocking { get; init; }

    /// <summary>Human-readable message. Actionable, e.g. "Revenue requires a start and end date."</summary>
    public required string Message { get; init; }

    /// <summary>Optional source cell/row reference for evidence navigation.</summary>
    public SourceReference? Source { get; init; }

    /// <summary>Optional connected source paths that contributed to this diagnostic.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SourceTransformationPath>? SourceTransformationPaths
    {
        get => _sourceTransformationPaths is { Count: > 0 } ? _sourceTransformationPaths : null;
        init => _sourceTransformationPaths = value is { Count: > 0 } ? value : null;
    }

    /// <summary>Optional mapping node ID for graph navigation.</summary>
    public string? NodeId { get; init; }

    /// <summary>Optional port name on the referenced node.</summary>
    public string? PortName { get; init; }

    /// <summary>Optional edge ID.</summary>
    public string? EdgeId { get; init; }

    /// <summary>Operation ID for correlating across subsystems.</summary>
    public string? OperationId { get; init; }

    /// <summary>Underlying rule identifier (e.g., Arelle rule code) when mapped through.</summary>
    public string? RuleIdentifier { get; init; }

    /// <summary>Additional structured evidence.</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}
