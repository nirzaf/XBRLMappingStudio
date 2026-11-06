using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR07 — the filing-rule validator runs required checks against a baseline profile and
/// records a completion status per rule. A pass fixture is clean; failure fixtures produce
/// the specific stable diagnostic; an unevaluable dimension check maps to an incomplete
/// configuration rather than a silent pass.
/// </summary>
public sealed class FilingRuleValidatorTests
{
    private static FilingProfile Profile => SyntheticProfile.Build();

    private static IReadOnlyList<XbrlFact> Load(string fixture)
        => OimSerializer.ReadFacts(FixturePaths.Read(fixture));

    [Fact]
    public void ExpectedPass_IsClean_AndMapsToSuccess()
    {
        var validator = new FilingRuleValidator(Profile);
        var result = validator.Validate(Load("expected-pass.xbrl.json"));
        Assert.True(result.IsClean, $"expected clean but got: {string.Join(", ", result.Outcomes.Where(o => o.Status != RuleStatus.Pass).Select(o => o.RuleId))}");
        Assert.Equal(RunOutcome.Success, result.MapToRunOutcome());
    }

    [Fact]
    public void DuplicateFact_FailsWithStableCode()
    {
        var validator = new FilingRuleValidator(Profile);
        var result = validator.Validate(Load("duplicate-fact.xbrl.json"));
        Assert.True(result.HasFailures);
        Assert.Equal(RunOutcome.InvalidMapping, result.MapToRunOutcome());
        var dup = Assert.Single(result.Outcomes, o => o.RuleId == "synthetic:no-duplicate-facts");
        Assert.Equal(RuleStatus.Fail, dup.Status);
        Assert.Equal(DiagnosticCodes.XbrDuplicateFact, dup.Diagnostic!.Code);
    }

    [Fact]
    public void FailingRule_PeriodMismatchAndUnsupportedUnit()
    {
        var validator = new FilingRuleValidator(Profile);
        var result = validator.Validate(Load("failing-rule.xbrl.json"));
        Assert.True(result.HasFailures);
        Assert.Equal(RunOutcome.InvalidMapping, result.MapToRunOutcome());

        Assert.Contains(result.Outcomes, o =>
            o.RuleId == "synthetic:period-type-match" && o.Status == RuleStatus.Fail
            && o.Diagnostic!.Code == DiagnosticCodes.XbrPeriodTypeMismatch);
        Assert.Contains(result.Outcomes, o =>
            o.RuleId == "synthetic:unit-allowed" && o.Status == RuleStatus.Fail);
    }

    [Fact]
    public void InvalidDimension_WithKnownMembers_Fails()
    {
        // Declare a member domain that does NOT include the fixture's Atlantis member.
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            SyntheticProfile.Qualified("EMEA"),
            SyntheticProfile.Qualified("APAC")
        };
        var validator = new FilingRuleValidator(Profile, known);
        var result = validator.Validate(Load("invalid-dimension.xbrl.json"));

        Assert.True(result.HasFailures);
        var dim = Assert.Single(result.Outcomes, o => o.RuleId == "synthetic:dimension-member-valid");
        Assert.Equal(RuleStatus.Fail, dim.Status);
        Assert.Equal(DiagnosticCodes.XbrDimensionInvalid, dim.Diagnostic!.Code);
    }

    [Fact]
    public void InvalidDimension_WithoutDeclaredMembers_IsIncompleteNotPass()
    {
        // No member domain → the required dimension check cannot be completed offline.
        var validator = new FilingRuleValidator(Profile);
        var result = validator.Validate(Load("invalid-dimension.xbrl.json"));

        Assert.False(result.HasFailures);
        Assert.True(result.HasIncomplete);
        Assert.Equal(RunOutcome.IncompleteConfiguration, result.MapToRunOutcome());
        var dim = Assert.Single(result.Outcomes, o => o.RuleId == "synthetic:dimension-member-valid");
        Assert.Equal(RuleStatus.Incomplete, dim.Status);
        Assert.Equal(DiagnosticCodes.XbrRequiredCheckMissing, dim.Diagnostic!.Code);
    }

    [Fact]
    public void ValidDimension_WithKnownMember_Passes()
    {
        // Build an in-memory fact whose member is declared; must pass the dimension rule.
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 100m,
            Decimals = 0,
            Dimensions = new[] { new Dimension(SyntheticProfile.Qualified("Region"), SyntheticProfile.Qualified("EMEA")) }
        };
        var known = new HashSet<string>(StringComparer.Ordinal) { SyntheticProfile.Qualified("EMEA") };
        var result = new FilingRuleValidator(Profile, known).Validate(new[] { fact });
        Assert.DoesNotContain(result.Outcomes, o => o.RuleId == "synthetic:dimension-member-valid" && o.Status != RuleStatus.Pass);
    }

    [Fact]
    public void AbstractConceptCarryingFact_Fails()
    {
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("FinancialStatements"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Value = 1m,
            Decimals = 0
        };
        var result = new FilingRuleValidator(Profile).Validate(new[] { fact });
        Assert.Contains(result.Outcomes, o =>
            o.RuleId == "synthetic:abstract-concept-not-used" && o.Status == RuleStatus.Fail);
    }

    // XBR07 check-completeness: an empty fact set cannot exercise per-fact required
    // rules, so every declared RequiredCheck without an outcome is recorded as
    // Incomplete. The run maps to IncompleteConfiguration rather than a silent Success.
    [Fact]
    public void EmptyFactSet_RecordsIncompleteForEveryUnconditionalRequiredCheck()
    {
        var result = new FilingRuleValidator(Profile).Validate(Array.Empty<XbrlFact>());
        Assert.True(result.HasIncomplete);
        Assert.False(result.IsClean);
        Assert.Equal(RunOutcome.IncompleteConfiguration, result.MapToRunOutcome());
        // Only the set-level no-duplicate-facts rule can pass without any fact.
        foreach (var required in SyntheticProfile.RequiredChecks)
        {
            if (required == "synthetic:no-duplicate-facts") continue;
            var outcome = Assert.Single(result.Outcomes, o => o.RuleId == required);
            Assert.Equal(RuleStatus.Incomplete, outcome.Status);
            Assert.Equal(DiagnosticCodes.XbrRequiredCheckMissing, outcome.Diagnostic!.Code);
        }
    }

    // A full pass run exercises every declared required check; nothing is added.
    [Fact]
    public void ExpectedPass_RecordsEveryRequiredCheck_NoIncompleteAppended()
    {
        var result = new FilingRuleValidator(Profile).Validate(Load("expected-pass.xbrl.json"));
        var emitted = result.Outcomes.Select(o => o.RuleId).ToHashSet(StringComparer.Ordinal);
        foreach (var required in SyntheticProfile.RequiredChecks)
            Assert.Contains(required, emitted);
        Assert.DoesNotContain(result.Outcomes, o => o.Status == RuleStatus.Incomplete);
    }

    [Theory]
    [InlineData("expected-pass.xbrl.json")]
    [InlineData("duplicate-fact.xbrl.json")]
    [InlineData("failing-rule.xbrl.json")]
    [InlineData("invalid-dimension.xbrl.json")]
    public void StreamingValidation_MatchesInMemoryOutcomes(string fixture)
    {
        var facts = Load(fixture);
        var duplicateKey = facts.GroupBy(fact => fact.SemanticKey(), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        var validator = new FilingRuleValidator(Profile);

        var expected = validator.Validate(facts);
        var actual = validator.ValidateStreaming(facts.Select(fact => fact), duplicateKey);

        Assert.Equal(expected.Outcomes, actual.Outcomes);
    }

    [Fact]
    public void StreamingValidation_ObservesCancellationBetweenFacts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new FilingRuleValidator(Profile)
            .ValidateStreaming(Load("expected-pass.xbrl.json"), null, cancellation.Token));
    }
}
