using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL;

public enum RuleStatus
{
    /// <summary>The rule ran and passed.</summary>
    Pass,
    /// <summary>The rule ran and found a violation.</summary>
    Fail,
    /// <summary>The rule could not be evaluated (missing information).</summary>
    Incomplete
}

/// <summary>Completion record for one filing rule (XBR07).</summary>
public sealed record RuleOutcome(
    string RuleId,
    RuleStatus Status,
    string? Detail = null,
    Diagnostic? Diagnostic = null);

/// <summary>
/// Aggregate result of running the baseline filing rules against an exported fact set.
/// A required check that could not be evaluated makes <see cref="HasIncomplete"/> true,
/// which the caller maps to an incomplete-configuration run outcome.
/// </summary>
public sealed record FilingValidationResult(IReadOnlyList<RuleOutcome> Outcomes)
{
    public bool HasFailures => Outcomes.Any(o => o.Status == RuleStatus.Fail);
    public bool HasIncomplete => Outcomes.Any(o => o.Status == RuleStatus.Incomplete);
    public bool IsClean => !HasFailures && !HasIncomplete;
    public IReadOnlyList<Diagnostic> Diagnostics =>
        Outcomes.Select(o => o.Diagnostic).OfType<Diagnostic>().ToList();

    public RunOutcome MapToRunOutcome()
    {
        if (HasFailures) return RunOutcome.InvalidMapping;
        // An unevaluable required check means the filing cannot be certified complete.
        if (HasIncomplete) return RunOutcome.IncompleteConfiguration;
        return RunOutcome.Success;
    }
}

/// <summary>
/// Validates an exported fact set against the baseline profile's required rules (XBR07).
/// Each required rule records a completion status; an unevaluable rule yields an
/// <see cref="RuleStatus.Incomplete"/> outcome rather than a silent pass.
/// </summary>
public sealed class FilingRuleValidator
{
    private readonly FilingProfile _profile;
    private readonly IReadOnlySet<string> _knownDimensionMembers;

    public FilingRuleValidator(
        FilingProfile profile,
        IReadOnlySet<string>? knownDimensionMembers = null)
    {
        _profile = profile;
        _knownDimensionMembers = knownDimensionMembers
            ?? new HashSet<string>(StringComparer.Ordinal);
    }

    public FilingValidationResult Validate(IReadOnlyList<XbrlFact> facts)
    {
        var outcomes = new List<RuleOutcome>();
        string op = "filing-validation";

        foreach (var f in facts)
        {
            var concept = _profile.FindConcept(f.ConceptName);

            // Rule: concept-exists + abstract-not-used.
            if (concept is null)
            {
                outcomes.Add(Fail("synthetic:concept-exists", DiagnosticCodes.XbrConceptUnknown,
                    $"Concept '{f.ConceptName}' is not defined in profile '{_profile.ProfileId}'.", op));
                continue;
            }
            outcomes.Add(Pass("synthetic:concept-exists"));
            if (concept.IsAbstract)
                outcomes.Add(Fail("synthetic:abstract-concept-not-used", DiagnosticCodes.XbrConceptUnknown,
                    $"Concept '{f.ConceptName}' is abstract and cannot carry a fact value.", op));
            else
                outcomes.Add(Pass("synthetic:abstract-concept-not-used"));

            // Rule: monetary-unit-required + unit-allowed.
            if (concept.DataType == "monetary" && f.Unit is null && f.CompoundUnit is null)
                outcomes.Add(Fail("synthetic:monetary-unit-required", DiagnosticCodes.XbrUnitMissing,
                    $"Fact on monetary concept '{f.ConceptName}' has no unit.", op));
            else
                outcomes.Add(Pass("synthetic:monetary-unit-required"));

            if (f.Unit is not null && !_profile.SupportedUnits.Contains(f.Unit.Measure))
                outcomes.Add(Fail("synthetic:unit-allowed", DiagnosticCodes.XbrUnitMissing,
                    $"Unit '{f.Unit.Measure}' is not supported by profile '{_profile.ProfileId}'.", op));
            else if (f.CompoundUnit is not null)
                outcomes.Add(CompoundUnitRule(f));
            else
                outcomes.Add(Pass("synthetic:unit-allowed"));

            if (!f.ValidateValueAgainst(concept, out var valueReason)
                && !valueReason.EndsWith("is monetary and requires a unit.", StringComparison.Ordinal))
                outcomes.Add(Fail("synthetic:fact-value-contract", DiagnosticCodes.XbrUnsupportedConstruct,
                    valueReason, op));
            else
                outcomes.Add(Pass("synthetic:fact-value-contract"));

            // Rule: period-type-match.
            var periodOk =
                (concept.PeriodType == "instant" && f.Period.Kind == PeriodKind.Instant) ||
                (concept.PeriodType == "duration" && f.Period.Kind == PeriodKind.Duration);
            outcomes.Add(periodOk
                ? Pass("synthetic:period-type-match")
                : Fail("synthetic:period-type-match", DiagnosticCodes.XbrPeriodTypeMismatch,
                    $"Fact on '{f.ConceptName}' period kind '{f.Period.Kind}' does not match '{concept.PeriodType}'.", op));

            // Rule: dimension-member-valid (incomplete when the member domain is undeclared).
            foreach (var d in f.Dimensions)
                outcomes.Add(DimensionRule(f, d, op));
        }

        // Rule: no-duplicate-facts across the whole set.
        var dupes = facts.GroupBy(f => f.SemanticKey())
            .Where(g => g.Count() > 1)
            .ToList();
        if (dupes.Count > 0)
        {
            var first = dupes[0];
            outcomes.Add(Fail("synthetic:no-duplicate-facts", DiagnosticCodes.XbrDuplicateFact,
                $"Duplicate fact (same aspects) for concept '{first.Key}'.", op));
        }
        else
        {
            outcomes.Add(Pass("synthetic:no-duplicate-facts"));
        }

        var collapsed = Collapse(outcomes);
        // XBR07 check-completeness: any profile-declared required rule that never
        // produced an outcome (empty fact set, or the fact set did not exercise the
        // rule) must appear as an Incomplete outcome so the run cannot resolve to a
        // silent pass. Conditional rules like dimension-member-valid are excluded
        // from RequiredChecks by profile contract, so absence of their outcome is fine.
        var emitted = new HashSet<string>(collapsed.Select(o => o.RuleId), StringComparer.Ordinal);
        foreach (var required in _profile.RequiredChecks.OrderBy(r => r, StringComparer.Ordinal))
        {
            if (emitted.Contains(required)) continue;
            collapsed = collapsed.Append(new RuleOutcome(
                required,
                RuleStatus.Incomplete,
                $"Required check '{required}' declared by profile '{_profile.ProfileId}' did not run; "
                + "the filing cannot be certified complete.",
                new Diagnostic
                {
                    Code = DiagnosticCodes.XbrRequiredCheckMissing,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Warning,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = false,
                    Message = $"Required filing check '{required}' was not executed.",
                    OperationId = op,
                    RuleIdentifier = required
                })).ToList();
        }

        return new FilingValidationResult(collapsed);
    }

    private RuleOutcome CompoundUnitRule(XbrlFact f)
    {
        var cu = f.CompoundUnit!;
        var unsupported = cu.Numerators.Concat(cu.DenominatorList)
            .Where(m => !_profile.SupportedUnits.Contains(m))
            .ToList();
        return unsupported.Count == 0
            ? Pass("synthetic:unit-allowed")
            : Fail("synthetic:unit-allowed", DiagnosticCodes.XbrUnitMissing,
                $"Compound unit '{cu.CanonicalKey}' uses unsupported measures: {string.Join(", ", unsupported)}.",
                "filing-validation");
    }

    private RuleOutcome DimensionRule(XbrlFact f, Dimension d, string op)
    {
        // Explicit dimension whose member domain is unknown cannot be validated offline.
        if (_knownDimensionMembers.Count == 0)
            return new RuleOutcome("synthetic:dimension-member-valid", RuleStatus.Incomplete,
                $"Dimension '{d.Name}' member domain is not declared; check could not be completed.",
                new Diagnostic
                {
                    Code = DiagnosticCodes.XbrRequiredCheckMissing,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Warning,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = false,
                    Message = $"Required dimension check for '{d.Name}' could not be completed.",
                    OperationId = op
                });

        return _knownDimensionMembers.Contains(d.MemberName)
            ? Pass("synthetic:dimension-member-valid")
            : Fail("synthetic:dimension-member-valid", DiagnosticCodes.XbrDimensionInvalid,
                $"Dimension '{d.Name}' member '{d.MemberName}' is not a declared member.", op);
    }

    private static RuleOutcome Pass(string rule) => new(rule, RuleStatus.Pass);

    private static RuleOutcome Fail(string rule, string code, string message, string op) => new(
        rule, RuleStatus.Fail, message,
        new Diagnostic
        {
            Code = code,
            Subsystem = DiagnosticSubsystem.XBRL,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Validation,
            IsBlocking = true,
            Message = message,
            OperationId = op,
            RuleIdentifier = rule
        });

    /// <summary>Keep one instance of each failing/incomplete rule; pass outcomes dedupe too.</summary>
    private static List<RuleOutcome> Collapse(List<RuleOutcome> outcomes)
        => outcomes
            .GroupBy(o => (o.RuleId, o.Status))
            .Select(g => g.First())
            .OrderBy(g => g.RuleId, StringComparer.Ordinal)
            .ThenBy(g => g.Status)
            .ToList();
}
