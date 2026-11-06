using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

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
    /// <summary>Exact profile requirements expected in a validate-report completion response.</summary>
    public ValidationRequirements? ValidationRequirements { get; init; }

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
    public IReadOnlyList<string> RequiredChecks { get; init; } = Array.Empty<string>();
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
    int ConceptCount)
{
    public IReadOnlyList<string> RequiredChecks { get; init; } = Array.Empty<string>();
}

/// <summary>Mandatory validation identifiers sent with each report request.</summary>
public sealed record ValidationRequirements(
    string ProfileId,
    string ProfileDigest,
    IReadOnlyList<string> RequiredRules,
    IReadOnlyList<string> RequiredPlugins);

public enum ValidationCheckStatus
{
    Pass,
    Fail,
    Skipped,
    Unavailable
}

public sealed record ValidationCheckCompletion(
    string Identifier,
    ValidationCheckStatus Status,
    string? Detail = null);

/// <summary>
/// Per-profile evidence returned by validate-report. Identifiers must match the exact
/// requirements sent for the selected profile; absence never implies a pass.
/// </summary>
public sealed record ValidationCompletionReport(
    string ProfileId,
    string ProfileDigest,
    IReadOnlyList<ValidationCheckCompletion> RequiredRules,
    IReadOnlyList<ValidationCheckCompletion> RequiredPlugins);

public enum ValidationCompletionState
{
    Complete,
    Failed,
    Incomplete
}

public sealed record ValidationCompletionAssessment(
    ValidationCompletionState State,
    string? Detail = null,
    string? Identifier = null,
    bool IsPlugin = false,
    bool UsedLocalSyntheticRuleEvidence = false);

/// <summary>Strictly validates completion evidence against the selected profile.</summary>
public static class ValidationCompletionReportValidator
{
    public static ValidationCompletionAssessment Evaluate(
        JsonElement? workerResult,
        FilingProfile profile,
        IReadOnlyList<XbrlFact>? localSyntheticFacts = null,
        FilingValidationResult? localSyntheticValidation = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (workerResult is not { ValueKind: JsonValueKind.Object } result
            || !result.TryGetProperty("validationCompletion", out var completionElement)
            || completionElement.ValueKind != JsonValueKind.Object)
            return new ValidationCompletionAssessment(ValidationCompletionState.Incomplete,
                "Validation worker omitted its required completion report.");

        ValidationCompletionReport? report;
        try
        {
            report = completionElement.Deserialize<ValidationCompletionReport>(WorkerJson.Options);
        }
        catch (JsonException)
        {
            report = null;
        }
        if (report is null || report.RequiredRules is null || report.RequiredPlugins is null)
            return new ValidationCompletionAssessment(ValidationCompletionState.Incomplete,
                "Validation worker returned a malformed completion report.");
        if (!StringComparer.Ordinal.Equals(profile.ProfileId, report.ProfileId)
            || !StringComparer.Ordinal.Equals(profile.Digest, report.ProfileDigest))
            return new ValidationCompletionAssessment(ValidationCompletionState.Incomplete,
                "Validation completion report does not match the selected profile identity.");

        if (!HasExactIdentifiers(profile.RequiredChecks, report.RequiredRules))
            return EvaluateItems(profile.RequiredChecks, report.RequiredRules, "required rule", isPlugin: false);

        ValidationCompletionAssessment rules;
        var canUseSyntheticRules = (localSyntheticFacts is not null || localSyntheticValidation is not null)
            && StringComparer.Ordinal.Equals(profile.ProfileId, SyntheticProfile.ProfileId)
            && report.RequiredRules.Count > 0
            && report.RequiredRules.All(item => item.Status == ValidationCheckStatus.Unavailable);
        if (canUseSyntheticRules)
        {
            var knownDimensionMembers = profile.DimensionDomains.Values
                .SelectMany(members => members).ToHashSet(StringComparer.Ordinal);
            var localOutcomes = localSyntheticValidation
                ?? new FilingRuleValidator(profile, knownDimensionMembers).Validate(localSyntheticFacts!);
            var byRule = localOutcomes.Outcomes.GroupBy(outcome => outcome.RuleId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(outcome => outcome.Status switch
                {
                    RuleStatus.Fail => 0,
                    RuleStatus.Incomplete => 1,
                    _ => 2
                }).First(), StringComparer.Ordinal);
            var localRules = profile.RequiredChecks.Order(StringComparer.Ordinal)
                .Select(identifier => byRule.TryGetValue(identifier, out var outcome)
                    ? new ValidationCheckCompletion(identifier, outcome.Status switch
                    {
                        RuleStatus.Pass => ValidationCheckStatus.Pass,
                        RuleStatus.Fail => ValidationCheckStatus.Fail,
                        _ => ValidationCheckStatus.Unavailable
                    }, outcome.Detail)
                    : new ValidationCheckCompletion(identifier, ValidationCheckStatus.Unavailable,
                        "The local synthetic rule validator did not produce a completion outcome."))
                .ToArray();
            rules = EvaluateItems(profile.RequiredChecks, localRules, "required rule", isPlugin: false)
                with { UsedLocalSyntheticRuleEvidence = true };
        }
        else
        {
            rules = EvaluateItems(profile.RequiredChecks, report.RequiredRules, "required rule", isPlugin: false);
        }
        if (rules.State != ValidationCompletionState.Complete) return rules;
        var plugins = EvaluateItems(profile.RequiredPlugins, report.RequiredPlugins, "required plugin", isPlugin: true);
        return plugins with { UsedLocalSyntheticRuleEvidence = rules.UsedLocalSyntheticRuleEvidence };
    }

    private static bool HasExactIdentifiers(
        IReadOnlySet<string> expected,
        IReadOnlyList<ValidationCheckCompletion> actual)
        => actual.All(item => item is not null && !string.IsNullOrWhiteSpace(item.Identifier))
            && actual.Select(item => item.Identifier).Distinct(StringComparer.Ordinal).Count() == actual.Count
            && expected.Order(StringComparer.Ordinal)
                .SequenceEqual(actual.Select(item => item.Identifier).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static ValidationCompletionAssessment EvaluateItems(
        IReadOnlySet<string> expected,
        IReadOnlyList<ValidationCheckCompletion> actual,
        string kind,
        bool isPlugin)
    {
        if (actual.Any(item => item is null || string.IsNullOrWhiteSpace(item.Identifier))
            || actual.Select(item => item.Identifier).Distinct(StringComparer.Ordinal).Count() != actual.Count)
            return new ValidationCompletionAssessment(ValidationCompletionState.Incomplete,
                $"Validation completion report contains invalid or duplicate {kind} identifiers.", IsPlugin: isPlugin);

        var expectedIds = expected.Order(StringComparer.Ordinal).ToArray();
        var actualIds = actual.Select(item => item.Identifier).Order(StringComparer.Ordinal).ToArray();
        if (!expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal))
        {
            var missing = expectedIds.Except(actualIds, StringComparer.Ordinal).FirstOrDefault();
            return new ValidationCompletionAssessment(ValidationCompletionState.Incomplete,
                missing is null
                    ? $"Validation completion report has unexpected {kind} identifiers."
                    : $"Validation completion report omitted required {kind} '{missing}'.",
                missing, isPlugin);
        }

        var failed = actual.FirstOrDefault(item => item.Status == ValidationCheckStatus.Fail);
        if (failed is not null)
            return new ValidationCompletionAssessment(ValidationCompletionState.Failed,
                failed.Detail ?? $"Required {kind} '{failed.Identifier}' failed.", failed.Identifier, isPlugin);
        var incomplete = actual.FirstOrDefault(item => item.Status is ValidationCheckStatus.Skipped or ValidationCheckStatus.Unavailable
            || !Enum.IsDefined(item.Status));
        if (incomplete is not null)
            return new ValidationCompletionAssessment(ValidationCompletionState.Incomplete,
                incomplete.Detail ?? $"Required {kind} '{incomplete.Identifier}' was {incomplete.Status.ToString().ToLowerInvariant()}.",
                incomplete.Identifier, isPlugin);
        return new ValidationCompletionAssessment(ValidationCompletionState.Complete);
    }
}

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
    // Version 2 adds exact required-check/plugin requirements and mandatory
    // validation-completion evidence to validate-report.
    public const int CurrentVersion = 2;
    public const int MinimumVersion = 2;
    public const int MaxMessageBytes = 4 * 1024 * 1024; // 4 MiB bound per section 2

    public static void ValidateByteLimit(string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(message);
        if (bytes > MaxMessageBytes)
            throw new InvalidOperationException(
                $"Worker message exceeds {MaxMessageBytes} bytes ({bytes}).");
    }
}
