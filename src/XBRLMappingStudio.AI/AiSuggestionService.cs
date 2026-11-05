using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI;

/// <summary>One field-level suggestion with the evidence basis for it (AI02/AI03).</summary>
public sealed record Suggestion(
    string SuggestionId,
    string SourceField,
    DraftMapping Draft,
    string Basis,
    int Score)
{
    public TaxonomyCandidateEvidence? TaxonomyEvidence { get; init; }
}

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
    private const int MaxSourceFields = 100;
    private const int MaxSourceNameLength = 256;
    private const int MaxUnitLength = 64;
    private const int MaxCandidateConceptsPerField = 10;
    private const int MaxProviderRequestBytes = 1_048_576;
    private const int MaxProviderResponseBytes = 1_048_576;

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
        ArgumentNullException.ThrowIfNull(taxonomy);
        if (ranker is not null && !ranker.IsCurrentFor(taxonomy))
            throw new ArgumentException("The retrieval ranker must use the exact selected taxonomy profile and digest.", nameof(ranker));
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
    public Task<SuggestionBatch> SuggestAsync(
        IReadOnlyList<string> sourceFields,
        CancellationToken cancellationToken)
        => SuggestAsync(sourceFields.Select(field => new SourceFieldDescriptor(field, field)).ToArray(), cancellationToken);

    public async Task<SuggestionBatch> SuggestAsync(
        IReadOnlyList<SourceFieldDescriptor> sourceFields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceFields);
        var diagnostics = new List<Diagnostic>();
        var suggestions = new List<Suggestion>();
        bool usedProvider = false;

        if (sourceFields.Count > MaxSourceFields
            || sourceFields.Any(field => field is null
                || string.IsNullOrWhiteSpace(field.Id) || string.IsNullOrWhiteSpace(field.Name)
                || field.Name.Length > MaxSourceNameLength || field.Unit?.Length > MaxUnitLength
                || field.PeriodType?.Length > MaxUnitLength
                || (field.PeriodType is not null and not ("instant" or "duration")))
            || sourceFields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != sourceFields.Count)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiInvalidDraft,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = $"Source fields must have non-empty, unique identities, names up to {MaxSourceNameLength} characters, units and periods up to {MaxUnitLength} characters, a supported period when supplied, and no more than {MaxSourceFields} entries; no suggestions were produced."
            });
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        var rankedCandidatesByField = RankCandidates(sourceFields, diagnostics);
        if (rankedCandidatesByField.Values.All(candidates => candidates.Count == 0))
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);

        if (!_policy.AllowsOutbound)
        {
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        var allowedConcepts = rankedCandidatesByField.Values
            .SelectMany(candidates => candidates)
            .Select(candidate => candidate.QualifiedName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var request = new ChatRequest(
            SystemPrompt: "Map each source field by its exact id. Return JSON objects with sourceFieldId and concept only. Select a concept only from the candidate list for that exact sourceFieldId; omit fields with no suitable candidate. Never infer or invent a currency or unit.",
            UserPrompt: JsonSerializer.Serialize(new { fields = sourceFields }),
            AllowedConcepts: allowedConcepts,
            SourceFields: sourceFields,
            MaxOutputTokens: 1000,
            CandidatesBySourceField: rankedCandidatesByField);
        if (JsonSerializer.SerializeToUtf8Bytes(request).Length > MaxProviderRequestBytes)
        {
            diagnostics.Add(InvalidProposal("Ranked taxonomy evidence exceeded the provider request budget; the mapping request was not sent and local candidates are shown instead."));
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
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
        catch (Exception ex)
        {
            diagnostics.Add(ProviderFailure($"Provider '{_provider.Name}' probe failed ({ex.GetType().Name}); falling back to local ranking."));
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        if (probe is null || !probe.Available)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiProviderUnavailable,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = $"Provider '{_provider.Name}' probe failed: {probe?.Detail ?? "no response"}. Falling back to local ranking.",
            });
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        ChatResponse response;
        try
        {
            response = await _provider.CompleteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add(ProviderFailure($"Provider '{_provider.Name}' request failed ({ex.GetType().Name}); falling back to local ranking."));
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        if (response is null)
        {
            diagnostics.Add(ProviderFailure($"Provider '{_provider.Name}' returned no response; falling back to local ranking."));
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
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
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: false);
        }

        var proposals = response.ProposedMappings ?? Array.Empty<ProposedMapping>();
        var estimatedResponseBytes = proposals.Sum(proposal => proposal is null
            ? 1
            : (proposal.SourceFieldId?.Length ?? 0) + (proposal.Concept?.Length ?? 0) + 8);
        if (response.Truncated || response.InboundBytes > MaxProviderResponseBytes
            || estimatedResponseBytes > MaxProviderResponseBytes || proposals.Count > MaxSourceFields * 4)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiInvalidDraft,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = "Provider response exceeded the output budget and was discarded; local candidates are shown instead.",
            });
            AddLocalSuggestions(sourceFields, rankedCandidatesByField, suggestions);
            return new SuggestionBatch(suggestions, diagnostics, UsedProvider: true);
        }

        usedProvider = true;
        var fieldsById = sourceFields.ToDictionary(field => field.Id, StringComparer.Ordinal);
        var candidateByFieldAndName = rankedCandidatesByField.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToDictionary(candidate => candidate.QualifiedName, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var validProposals = new List<ProposedMapping>();
        foreach (var proposal in proposals)
        {
            if (proposal is null || string.IsNullOrWhiteSpace(proposal.SourceFieldId)
                || string.IsNullOrWhiteSpace(proposal.Concept))
            {
                diagnostics.Add(InvalidProposal("Provider returned a proposal with a missing source id or concept; discarded."));
                continue;
            }
            validProposals.Add(proposal);
        }
        foreach (var duplicate in validProposals.GroupBy(mapping => mapping.SourceFieldId, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiInvalidDraft,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = $"Provider returned multiple proposals for source field '{duplicate.Key}'; all were discarded."
            });
        }
        var duplicateIds = validProposals.GroupBy(mapping => mapping.SourceFieldId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var proposed in validProposals)
        {
            if (!fieldsById.TryGetValue(proposed.SourceFieldId, out var sourceField))
            {
                diagnostics.Add(InvalidProposal($"Provider proposed unknown source field id '{proposed.SourceFieldId}'; discarded."));
                continue;
            }
            if (duplicateIds.Contains(proposed.SourceFieldId)) continue;
            // A concept allowed for a different field is still out of scope for this proposal.
            if (!candidateByFieldAndName.TryGetValue(proposed.SourceFieldId, out var fieldCandidates)
                || !fieldCandidates.TryGetValue(proposed.Concept, out var candidate)
                || !TaxonomyIndex.IsFactTargetable(_taxonomy.Get(proposed.Concept)!))
            {
                diagnostics.Add(InvalidProposal($"Provider proposed a concept outside this field's ranked candidates or an un-targetable concept '{proposed.Concept}'; discarded."));
                continue;
            }

            var concept = _taxonomy.Get(proposed.Concept)!;
            suggestions.Add(new Suggestion(
                SuggestionId: $"prv-{DeterministicId(sourceField.Id + "\0" + proposed.Concept)}",
                SourceField: sourceField.Name,
                Draft: new DraftMapping(
                    MappingId: $"prv-{DeterministicId(sourceField.Id + "\0" + proposed.Concept)}",
                    SourceField: sourceField.Name,
                    Concept: proposed.Concept,
                    Unit: concept.DataType == "monetary" ? sourceField.Unit : null,
                    PeriodType: concept.PeriodType),
                Basis: "provider via " + candidate.Basis,
                Score: candidate.Score)
            {
                TaxonomyEvidence = candidate.Evidence
            });
        }

        return new SuggestionBatch(suggestions, diagnostics, usedProvider);
    }

    private Dictionary<string, IReadOnlyList<RankedCandidate>> RankCandidates(
        IReadOnlyList<SourceFieldDescriptor> fields,
        ICollection<Diagnostic> diagnostics)
    {
        var result = new Dictionary<string, IReadOnlyList<RankedCandidate>>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var candidates = _ranker.Rank(field, MaxCandidateConceptsPerField);
            result.Add(field.Id, candidates);
            if (candidates.Count == 0)
                diagnostics.Add(InvalidProposal(
                    "No target concept in the selected taxonomy matched this field and its supplied period/unit context; it remains unassigned."));
        }
        return result;
    }

    private IEnumerable<Suggestion> RankLocally(SourceFieldDescriptor field, IReadOnlyList<RankedCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var concept = _taxonomy.Get(candidate.QualifiedName)!;
            var id = DeterministicId(field.Id + "\0" + candidate.QualifiedName);
            yield return new Suggestion(
                SuggestionId: $"loc-{id}",
                SourceField: field.Name,
                Draft: new DraftMapping(
                    MappingId: $"loc-{id}",
                    SourceField: field.Name,
                    Concept: candidate.QualifiedName,
                    Unit: concept.DataType == "monetary" ? field.Unit : null,
                    PeriodType: concept.PeriodType),
                Basis: candidate.Basis,
                Score: candidate.Score)
            {
                TaxonomyEvidence = candidate.Evidence
            };
        }
    }

    private void AddLocalSuggestions(
        IEnumerable<SourceFieldDescriptor> fields,
        IReadOnlyDictionary<string, IReadOnlyList<RankedCandidate>> rankedCandidatesByField,
        ICollection<Suggestion> suggestions)
    {
        foreach (var field in fields)
            foreach (var suggestion in RankLocally(field, rankedCandidatesByField[field.Id]))
                suggestions.Add(suggestion);
    }

    /// <summary>Order-stable id so audit replays and repeat sessions line up (AI02/AI06).</summary>
    private static string DeterministicId(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    private static Diagnostic InvalidProposal(string message) => new()
    {
        Code = DiagnosticCodes.AiInvalidDraft,
        Subsystem = DiagnosticSubsystem.AI,
        Stage = DiagnosticStage.Validation,
        Severity = DiagnosticSeverity.Warning,
        IsBlocking = false,
        Message = message
    };

    private static Diagnostic ProviderFailure(string message) => new()
    {
        Code = DiagnosticCodes.AiProviderUnavailable,
        Subsystem = DiagnosticSubsystem.AI,
        Stage = DiagnosticStage.Validation,
        Severity = DiagnosticSeverity.Warning,
        IsBlocking = false,
        Message = message
    };
}
