using XBRLMappingStudio.AI;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI.Tests;

/// <summary>
/// AI03 — provider contract: capability probe first; refusals, unknown ids, schema
/// errors, oversized responses and cancellation must never mutate project state.
/// </summary>
public class ProviderContractTests
{
    private static AiSuggestionService Managed(AiPolicy policy, IChatClientProvider provider) =>
        new(policy, TestProjects.Taxonomy(), provider);

    [Fact]
    public async Task Offline_NullProvider_ProbeReportsUnavailable()
    {
        var probe = await new NullProvider().ProbeAsync(default);
        Assert.False(probe.Available);
    }

    [Fact]
    public async Task ManagedProvider_ProbeSuccess_ProposalsFromAllowedList_AreReturned()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(new[] { new ProposedMapping("revenue", "synthetic:Revenue") }, Refused: false, Truncated: false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(new[] { "revenue" }, default);

        Assert.Equal(1, provider.ProbeCalls);
        Assert.True(batch.UsedProvider);
        Assert.Single(batch.Suggestions);
        Assert.Equal("synthetic:Revenue", batch.Suggestions[0].Draft.Concept);
        Assert.Equal("revenue", batch.Suggestions[0].Draft.SourceField);
        Assert.Null(batch.Suggestions[0].Draft.Unit);
    }

    [Fact]
    public async Task CloudProposal_PreservesStableFieldIdentityAndDeclaredCurrency()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(
                new[] { new ProposedMapping("column-17", "synthetic:Revenue") }, false, false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(
            new[] { new SourceFieldDescriptor("column-17", "Turnover", "QAR") }, default);

        var suggestion = Assert.Single(batch.Suggestions);
        Assert.Equal("Turnover", suggestion.SourceField);
        Assert.Equal("Turnover", suggestion.Draft.SourceField);
        Assert.Equal("QAR", suggestion.Draft.Unit);
        Assert.StartsWith("prv-", suggestion.SuggestionId);
    }

    [Fact]
    public async Task ManagedRequest_OnlySendsLocallyRankedConceptCandidates()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(Array.Empty<ProposedMapping>(), false, false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        await service.SuggestAsync(new[] { new SourceFieldDescriptor("f1", "Revenue") }, default);

        Assert.NotNull(provider.LastRequest);
        Assert.NotEmpty(provider.LastRequest!.AllowedConcepts);
        Assert.All(provider.LastRequest.AllowedConcepts, name =>
            Assert.Contains(name, new[] { "synthetic:Revenue" }));
        Assert.Equal(1000, provider.LastRequest.MaxOutputTokens);
    }

    [Fact]
    public async Task MalformedNullProposal_IsDiscardedWithoutThrowing()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(
                new ProposedMapping[] { new(null!, null!), new("f1", "synthetic:Revenue") }, false, false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(new[] { new SourceFieldDescriptor("f1", "Revenue") }, default);

        Assert.Single(batch.Suggestions);
        Assert.Contains(batch.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.AiInvalidDraft);
    }

    [Fact]
    public async Task CloudProposalForUnknownFieldId_IsDiscarded()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(
                new[] { new ProposedMapping("invented-column", "synthetic:Revenue") }, false, false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(
            new[] { new SourceFieldDescriptor("column-17", "Turnover", "QAR") }, default);

        Assert.Empty(batch.Suggestions);
        Assert.Contains(batch.Diagnostics, d => d.Code == DiagnosticCodes.AiInvalidDraft);
    }

    [Fact]
    public async Task UnknownOrUnTargetableConceptIds_AreDiscardedWithDiagnostic()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(
                new[]
                {
                    new ProposedMapping("f1", "synthetic:DoesNotExist"),
                    new ProposedMapping("f2", "synthetic:FinancialStatements"),
                    new ProposedMapping("f3", "synthetic:Revenue"),
                    new ProposedMapping("f4", "synthetic:Assets")
                },
                Refused: false, Truncated: false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(new[]
        {
            new SourceFieldDescriptor("f1", "revenue"),
            new SourceFieldDescriptor("f2", "gross revenue"),
            new SourceFieldDescriptor("f3", "net revenue"),
            new SourceFieldDescriptor("f4", "revenue")
        }, default);

        // Invented id + abstract id dropped; only the valid one survives.
        Assert.Single(batch.Suggestions);
        Assert.Contains(batch.Diagnostics, d => d.Code == DiagnosticCodes.AiInvalidDraft);
    }

    [Fact]
    public async Task Refusal_FallsBackToLocalRanking_WithoutMarkingProviderUsed()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(Array.Empty<ProposedMapping>(), Refused: true, Truncated: false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(new[] { "Sales" }, default);

        Assert.False(batch.UsedProvider);
        Assert.Contains(batch.Diagnostics, d => d.Code == DiagnosticCodes.AiProviderUnavailable);
        Assert.Contains(batch.Suggestions, s => s.Draft.Concept == "synthetic:Revenue"); // local fallback
    }

    [Fact]
    public async Task TruncatedOversizedResponse_FallsBackToLocalCandidates()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(new[] { new ProposedMapping("revenue", "synthetic:Revenue") }, Refused: false, Truncated: true)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(new[] { "revenue" }, default);

        Assert.Contains(batch.Suggestions, s => s.Draft.Concept == "synthetic:Revenue");
        Assert.Contains(batch.Diagnostics, d => d.Code == DiagnosticCodes.AiInvalidDraft);
    }

    [Fact]
    public async Task ProbeFailure_FallsBackToLocal_NoCompletionAttempted()
    {
        var provider = new FakeProvider { Probe = new ProbeResult(false, "down") };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);

        var batch = await service.SuggestAsync(new[] { "Sales" }, default);

        Assert.Equal(1, provider.ProbeCalls);
        Assert.Equal(0, provider.CompleteCalls); // never asked for a completion
        Assert.False(batch.UsedProvider);
        Assert.NotEmpty(batch.Suggestions);      // local ranking carried the load
    }

    [Fact]
    public async Task Cancellation_Propagates_LeavingNoPartialBatch()
    {
        var provider = new FakeProvider
        {
            Completer = _ => new ChatResponse(new[] { new ProposedMapping("revenue", "synthetic:Revenue") }, false, false)
        };
        var service = Managed(new AiPolicy(AiMode.ManagedProvider), provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SuggestAsync(new[] { "revenue" }, cts.Token));
    }
}

/// <summary>
/// AI04 — draft semantic validation: injected instructions (disallowed operations),
/// invented members, incompatible ports and missing aspects are all rejected before
/// approval; a well-formed draft passes both structural and filing-rule gates.
/// </summary>
public class DraftValidationTests
{
    private static DraftValidator Validator(IReadOnlySet<string>? members = null) =>
        new(TestProjects.Taxonomy(), TestProjects.Profile(), members);

    private static DraftMapping Good(string id = "m1") => new(id, "Sales", "synthetic:Revenue", "USD", "duration");

    [Fact]
    public void WellFormedDraft_IsAccepted()
    {
        var result = Validator().Validate(new[] { Good() });
        Assert.True(result.Accepted);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void InventedConcept_IsRejected()
    {
        var draft = Good() with { Concept = "synthetic:Cash" };
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.AiInvalidDraft);
    }

    [Fact]
    public void AbstractTarget_IsRejected()
    {
        var draft = Good() with { Concept = "synthetic:FinancialStatements" };
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
    }

    [Fact]
    public void DisallowedOperation_IsRejectedAsUnknownNodeType()
    {
        // A provider-emitted draft smuggling an operation outside the catalog.
        var draft = Good() with { NodeOperation = "runShellCommand" };
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MapUnknownNodeType);
    }

    [Fact]
    public void IncompatiblePortShape_IsRejectedAsTypeMismatch()
    {
        // Source claims to emit Fact-shaped output into the decimal value input.
        var draft = Good() with { SourceShape = "Fact" };
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MapTypeIncompatible);
    }

    [Theory]
    [InlineData(null, "USD", "duration", "concept")]
    [InlineData("synthetic:Revenue", null, "duration", "unit")]
    [InlineData("synthetic:Revenue", "USD", null, "period")]
    public void MissingAspect_SurfacesBeforeApproval(string? concept, string? unit, string? period, string _)
    {
        var draft = new DraftMapping("m1", "Sales", concept, unit, period);
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == DiagnosticCodes.AiInvalidDraft && d.Message.Contains("missing required aspect"));
    }

    [Fact]
    public void UnknownPeriodType_IsRejected()
    {
        var draft = Good() with { PeriodType = "rolling" };
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
    }

    [Fact]
    public void IncompatibleUnit_IsRejectedByFilingRules()
    {
        var draft = Good() with { Unit = "JPY" }; // not in the synthetic profile's unit set
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.XbrUnitMissing);
    }

    [Fact]
    public void PeriodKindMismatch_IsRejectedByFilingRules()
    {
        // Assets is instant-only; a duration draft must fail period-type-match.
        var draft = new DraftMapping("m1", "Assets", "synthetic:Assets", "USD", "duration");
        var result = Validator().Validate(new[] { draft });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.XbrPeriodTypeMismatch);
    }

    [Fact]
    public void InventedDimensionMember_IsRejectedWhenDomainKnown()
    {
        var members = new HashSet<string>(StringComparer.Ordinal) { "EMEA", "APAC" };
        var draft = Good() with { DimensionName = "synthetic:Region", DimensionMember = "Atlantis" };
        var result = Validator(members).Validate(new[] { draft });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.XbrDimensionInvalid);
    }

    [Fact]
    public void SameAspectsOnTwoDrafts_AreRejectedByDuplicateFactRule()
    {
        // Distinct source fields that materialize identical representative facts clash.
        var a = Good("m1");
        var b = Good("m2") with { SourceField = "Turnover" };
        var result = Validator().Validate(new[] { a, b });
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.XbrDuplicateFact);
    }
}
