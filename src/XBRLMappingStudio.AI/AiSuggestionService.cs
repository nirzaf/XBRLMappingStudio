using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI;

/// <summary>One field-level suggestion with the evidence basis for it (AI02/AI03).</summary>
public sealed record Suggestion(
    string SuggestionId,
    string SourceField,
    DraftMapping Draft,
    string Basis,
    int Score);

/// <summary>A full suggestion round plus any subsystem diagnostics raised producing it.</summary>
public sealed record SuggestionBatch(
    IReadOnlyList<Suggestion> Suggestions,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool UsedProvider);

/// <summary>
/// Orchestrates offline ranking and (optionally) a remote provider (AI01, AI03).
/// The zero-outbound guarantee is structural: under <see cref="AiMode.Offline"/> the
/// configured provider object is discarded entirely and replaced by
/// <see cref="NullProvider"/>, so no code path can reach a transport. The service
/// never mutates project state — hostile provider output is reported through
/// diagnostics and dropped here, before any session could apply it.
/// </summary>
public sealed class AiSuggestionService
{
    private readonly AiPolicy _policy;
    private readonly IChatClientProvider _provider;
    private readonly LocalRetrievalRanker _ranker;
    private readonly TaxonomyIndex _taxonomy;

    /// <summary>Provider actually used for outbound decisions (NullProvider when offline).</summary>
    public IChatClientProvider ActiveProvider => _provider;

    public AiSuggestionService(
        AiPolicy policy,
        TaxonomyIndex taxonomy,
        IChatClientProvider? provider = null,
        LocalRetrievalRanker? ranker = null)
    {
        _policy = policy;
        _taxonomy = taxonomy;
        // Offline discards any supplied provider: outbound is impossible by construction.
        _provider = policy.AllowsOutbound && provider is not null
            ? provider
            : new NullProvider();
        _ranker = ranker ?? new LocalRetrievalRanker(taxonomy);
    }

    /// <summary>Capability probe (AI03). Offline always reports unavailable without touching a socket.</summary>
    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
        => _provider.ProbeAsync(cancellationToken);

    /// <summary>
    /// Produce suggestions for the requested source fields. Offline: pure-local ranking,
    /// zero provider invocations. Online: probe first; refusal, unavailable provider,
    /// unknown concept ids, schema errors and oversized (truncated) responses are all
    /// converted to diagnostics and never leak into the returned drafts' acceptance path.
    /// </summary>
    public async Task<SuggestionBatch> SuggestAsync(
        IReadOnlyList<string> sourceFields,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var suggestions = new List<Suggestion>();
        bool usedProvider = false;

        if (!_policy.AllowsOutbound)
        {
            foreach (var field in sourceFields)
                suggestions.AddRange(RankLocally(field));
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        ProbeResult probe;
        try
        {
            probe = await _provider.ProbeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;  // cancellation is terminal; nothing has mutated
        }

        if (!probe.Available)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiProviderUnavailable,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = $"Provider '{_provider.Name}' probe failed: {probe.Detail}. Falling back to local ranking.",
            });
            foreach (var field in sourceFields)
                suggestions.AddRange(RankLocally(field));
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        ChatResponse response;
        try
        {
            response = await _provider.CompleteAsync(
                new ChatRequest(
                    SystemPrompt: "Propose XBRL concept mappings. Respond with qualified names only.",
                    UserPrompt: string.Join("\n", sourceFields),
                    AllowedConcepts: _taxonomy.AllConcepts
                        .Where(TaxonomyIndex.IsFactTargetable)
                        .Select(c => c.QualifiedName)
                        .OrderBy(q => q, StringComparer.Ordinal)
                        .ToList()),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (response.Refused)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiProviderUnavailable,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = $"Provider '{_provider.Name}' refused the request; no proposals were applied.",
            });
            foreach (var field in sourceFields)
                suggestions.AddRange(RankLocally(field));
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        if (response.Truncated)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiInvalidDraft,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = "Provider response exceeded the output budget and was discarded.",
            });
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: true);
        }

        usedProvider = true;
        foreach (var proposed in response.ProposedMappings)
        {
            // Unknown ids / schema errors are dropped with a diagnostic; they never reach the session.
            if (_taxonomy.Get(proposed) is null || !TaxonomyIndex.IsFactTargetable(_taxonomy.Get(proposed)!))
            {
                diagnostics.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.AiInvalidDraft,
                    Subsystem = DiagnosticSubsystem.AI,
                    Stage = DiagnosticStage.Validation,
                    Severity = DiagnosticSeverity.Warning,
                    IsBlocking = false,
                    Message = $"Provider proposed unknown or un-targetable concept '{proposed}'; discarded.",
                });
                continue;
            }

            var concept = _taxonomy.Get(proposed)!;
            suggestions.Add(new Suggestion(
                SuggestionId: $"prv-{DeterministicId(proposed)}",
                SourceField: proposed,
                Draft: new DraftMapping(
                    MappingId: $"prv-{DeterministicId(proposed)}",
                    SourceField: proposed,
                    Concept: proposed,
                    Unit: concept.DataType == "monetary" ? "USD" : null,
                    PeriodType: concept.PeriodType),
                Basis: "provider",
                Score: 0));
        }

        return new SuggestionBatch(suggestions, diagnostics, usedProvider);
    }

    private IEnumerable<Suggestion> RankLocally(string field)
    {
        foreach (var candidate in _ranker.Rank(field))
        {
            var concept = _taxonomy.Get(candidate.QualifiedName)!;
            yield return new Suggestion(
                SuggestionId: $"loc-{field}-{candidate.QualifiedName}",
                SourceField: field,
                Draft: new DraftMapping(
                    MappingId: $"loc-{field}-{candidate.QualifiedName}",
                    SourceField: field,
                    Concept: candidate.QualifiedName,
                    Unit: concept.DataType == "monetary" ? "USD" : null,
                    PeriodType: concept.PeriodType),
                Basis: candidate.Basis,
                Score: candidate.Score);
        }
    }

    /// <summary>Order-stable id so audit replays and repeat sessions line up (AI02/AI06).</summary>
    private static string DeterministicId(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
}
