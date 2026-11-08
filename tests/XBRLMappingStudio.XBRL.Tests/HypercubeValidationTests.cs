namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>Explicit segment DRS semantics, independently asserted rather than inferred from implementation.</summary>
public sealed class HypercubeValidationTests
{
    private const string Rule = "xbrl:dimension-hypercube-valid";
    private const string R = "urn:test:role:one";
    private const string S = "urn:test:role:two";
    private static string Q(string name) => SyntheticProfile.Qualified(name);
    private static Dimension D(string member = "Member", string axis = "Axis") => new(Q(axis), Q(member));
    private static DefinitionLink Arc(string kind, string parent, string child, string role = R,
        string? target = null, bool? usable = null, bool? closed = null, string? context = null)
        => new(role, kind, Q(parent), Q(child), TargetRoleUri: target, Usable: usable,
            Closed: closed, ContextElement: context);

    private static List<DefinitionLink> Cube(string role = R, string cube = "Cube", string member = "Member",
        string primary = "Revenue", string kind = XbrlArcRoles.All, bool closed = false)
        => [Arc(kind, primary, cube, role, closed: closed, context: "segment"),
            Arc(XbrlArcRoles.HypercubeDimension, cube, "Axis", role),
            Arc(XbrlArcRoles.DimensionDomain, "Axis", cube + "Domain", role, usable: false),
            Arc(XbrlArcRoles.DomainMember, cube + "Domain", member, role)];

    private static FilingProfile Profile(IEnumerable<DefinitionLink> relationships)
    {
        var links = relationships.ToArray();
        var source = SyntheticProfile.Build();
        var names = links.SelectMany(link => new[] { link.ParentQName, link.ChildQName })
            .Concat(new[] { Q("Member"), Q("Other"), Q("Extra"), Q("Axis"), Q("ExtraAxis") })
            .Distinct(StringComparer.Ordinal).Except(source.Concepts.Select(concept => concept.QualifiedName));
        return new FilingProfile
        {
            ProfileId = source.ProfileId, Digest = source.Digest,
            Concepts = source.Concepts.Concat(names.Select(name => new ConceptMetadata(name,
                SyntheticProfile.Namespace, name.Split(':').Last(), name, "string", "duration", false))).ToArray(),
            SupportedUnits = source.SupportedUnits, RequiredChecks = source.RequiredChecks,
            DefinitionLinks = links,
            // The flattened profile restriction is deliberately a superset of every role's domain.
            DimensionDomains = new Dictionary<string, IReadOnlyList<string>>
            {
                [Q("Axis")] = links.Select(link => link.ChildQName).Concat(new[] { Q("Member"), Q("Other") }).Distinct().ToArray(),
                [Q("ExtraAxis")] = [Q("Extra")]
            }
        };
    }

    private static XbrlFact Fact(params Dimension[] dimensions) => new()
    {
        ConceptName = Q("Revenue"), EntityScheme = "http://xbrl.org/entity/", EntityIdentifier = "ABC-Corp",
        Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
        Unit = new XbrlUnit("QAR"), Value = 100m, Decimals = 0, Dimensions = dimensions
    };
    private static RuleOutcome Outcome(IEnumerable<DefinitionLink> links, params Dimension[] dimensions)
        => Assert.Single(new FilingRuleValidator(Profile(links)).ValidateDimensionalAspects(Fact(dimensions)), o => o.RuleId == Rule);
    private static void Expect(RuleStatus expected, IEnumerable<DefinitionLink> links, params Dimension[] dimensions)
        => Assert.Equal(expected, Outcome(links, dimensions).Status);

    [Fact] public void DirectAllRequiresMemberInItsOwnDomain()
    {
        Expect(RuleStatus.Pass, Cube(), D());
        Expect(RuleStatus.Fail, Cube(), D("Other"));
        Expect(RuleStatus.Fail, Cube());
    }

    [Fact] public void NoApplicableCubeDoesNotConstrainPrimaryItem()
        => Expect(RuleStatus.Pass, Cube(primary: "Assets"), D("Other"));

    [Fact] public void AllCubesInOneRoleAreConjoined()
    {
        var links = Cube(); var second = Cube(cube: "SecondCube", member: "Other");
        second[1] = second[1] with { TargetRoleUri = S };
        second[2] = second[2] with { RoleUri = S };
        second[3] = second[3] with { RoleUri = S };
        links.AddRange(second);
        Expect(RuleStatus.Fail, links, D());
        Expect(RuleStatus.Fail, links, D("Other"));
    }

    [Fact] public void DifferentRolesAreAlternativesWithIndependentMemberDomains()
    {
        var links = Cube(); links.AddRange(Cube(S, "SecondCube", "Other"));
        Expect(RuleStatus.Pass, links, D());
        Expect(RuleStatus.Pass, links, D("Other"));
    }

    [Fact] public void NotAllNegatesTheWholeCubePredicate()
    {
        var links = Cube(kind: XbrlArcRoles.NotAll);
        Expect(RuleStatus.Fail, links, D());
        Expect(RuleStatus.Pass, links, D("Other"));
        Expect(RuleStatus.Pass, links);
    }

    [Fact] public void AllAndNotAllInSameRoleExcludeOnlyMatchingSubset()
    {
        var links = Cube();
        links.Add(Arc(XbrlArcRoles.DomainMember, "CubeDomain", "Other"));
        var excluded = Cube(cube: "Excluded", member: "Other", kind: XbrlArcRoles.NotAll);
        excluded[1] = excluded[1] with { TargetRoleUri = S };
        excluded[2] = excluded[2] with { RoleUri = S };
        excluded[3] = excluded[3] with { RoleUri = S };
        links.AddRange(excluded);
        Expect(RuleStatus.Pass, links, D());
        Expect(RuleStatus.Fail, links, D("Other"));
    }

    [Theory] [InlineData(false, RuleStatus.Pass)] [InlineData(true, RuleStatus.Fail)]
    public void ClosedCubeRejectsExtraDimensionButOpenCubeAllowsIt(bool closed, RuleStatus expected)
        => Expect(expected, Cube(closed: closed), D(), D("Extra", "ExtraAxis"));

    [Fact] public void NotAllClosedCubeIsSatisfiedByExtraDimension()
        => Expect(RuleStatus.Pass, Cube(kind: XbrlArcRoles.NotAll, closed: true), D(), D("Extra", "ExtraAxis"));

    [Fact] public void EmptyClosedCubeAcceptsOnlyEmptyDimensionalContext()
    {
        var links = new[] { Arc(XbrlArcRoles.All, "Revenue", "Cube", closed: true, context: "segment") };
        Expect(RuleStatus.Pass, links);
        Expect(RuleStatus.Fail, links, D());
    }

    [Fact] public void PrimaryInheritanceHonorsTargetRoleAndIgnoresUsableFlag()
    {
        var links = Cube(primary: "Ancestor");
        links.Add(Arc(XbrlArcRoles.DomainMember, "Ancestor", "Parent", target: S, usable: false));
        links.Add(Arc(XbrlArcRoles.DomainMember, "Parent", "Revenue", S));
        Expect(RuleStatus.Fail, links, D("Other"));
        Expect(RuleStatus.Pass, links, D());
        links[^1] = links[^1] with { RoleUri = "urn:test:unrelated" };
        Expect(RuleStatus.Pass, links, D("Other"));
    }

    [Fact] public void EveryConsecutiveDimensionalArcHonorsTargetRole()
    {
        const string t = "urn:test:role:three", u = "urn:test:role:four", v = "urn:test:role:five";
        var links = new[]
        {
            Arc(XbrlArcRoles.All, "Revenue", "Cube", target: S, context: "segment"),
            Arc(XbrlArcRoles.HypercubeDimension, "Cube", "Axis", S, target: t),
            Arc(XbrlArcRoles.DimensionDomain, "Axis", "Domain", t, target: u, usable: false),
            Arc(XbrlArcRoles.DomainMember, "Domain", "Branch", u, target: v, usable: false),
            Arc(XbrlArcRoles.DomainMember, "Branch", "Member", v)
        };
        Expect(RuleStatus.Pass, links, D());
        Expect(RuleStatus.Fail, links, D("Other"));
    }

    [Fact] public void UnusableMemberDoesNotPruneUsableDescendants()
    {
        var links = Cube(); links[^1] = links[^1] with { Usable = false };
        links.Add(Arc(XbrlArcRoles.DomainMember, "Member", "Other"));
        Expect(RuleStatus.Fail, links, D());
        Expect(RuleStatus.Pass, links, D("Other"));
    }

    [Fact] public void UnusableOccurrenceOverridesUsableOccurrenceInSameDomain()
    {
        var links = Cube();
        links.Add(Arc(XbrlArcRoles.DomainMember, "CubeDomain", "Branch"));
        links.Add(Arc(XbrlArcRoles.DomainMember, "Branch", "Member", usable: false));
        Expect(RuleStatus.Fail, links, D());
    }

    [Fact] public void DimensionDomainTargetItselfCanBeUsable()
    {
        var links = Cube(); links[2] = links[2] with { Usable = null };
        Expect(RuleStatus.Pass, links, D("CubeDomain"));
    }

    [Fact] public void DefaultIsGlobalButMustBelongToCurrentRoleDomain()
    {
        var links = Cube(); links.Add(Arc(XbrlArcRoles.DimensionDefault, "Axis", "Member", S));
        Expect(RuleStatus.Pass, links);
        links[^1] = links[^1] with { ChildQName = Q("Other") };
        Expect(RuleStatus.Fail, links);
    }

    [Fact] public void ExplicitDefaultIsRejectedEvenWhenCubeOtherwiseMatches()
    {
        var links = Cube(); links.Add(Arc(XbrlArcRoles.DimensionDefault, "Axis", "Member"));
        var outcomes = new FilingRuleValidator(Profile(links)).ValidateDimensionalAspects(Fact(D()));
        Assert.Contains(outcomes, o => o.RuleId == "synthetic:dimension-member-valid" && o.Status == RuleStatus.Fail);
    }

    [Fact] public void ConflictingDefaultsAreIncomplete()
    {
        var links = Cube();
        links.Add(Arc(XbrlArcRoles.DimensionDefault, "Axis", "Member"));
        links.Add(Arc(XbrlArcRoles.DimensionDefault, "Axis", "Other", S));
        Expect(RuleStatus.Incomplete, links);
    }

    [Theory] [InlineData("scenario")] [InlineData(null)]
    public void UnsupportedOrMissingContextMetadataIsIncomplete(string? context)
    {
        var links = Cube(); links[0] = links[0] with { ContextElement = context };
        Expect(RuleStatus.Incomplete, links, D());
    }

    [Fact] public void UnsupportedAlternativeCannotBeHiddenByPassingRole()
    {
        var links = Cube(); var unsupported = Cube(S, "Second");
        unsupported[0] = unsupported[0] with { ContextElement = "scenario" };
        links.AddRange(unsupported);
        Expect(RuleStatus.Incomplete, links, D());
    }

    [Fact] public void UnrelatedUnsupportedCubeDoesNotContaminateFact()
    {
        var links = Cube(); var unrelated = Cube(S, "Second", primary: "Assets");
        unrelated[0] = unrelated[0] with { ContextElement = "scenario" }; links.AddRange(unrelated);
        Expect(RuleStatus.Pass, links, D());
    }

    [Fact] public void MissingDomainCannotBecomePassingNotAll()
    {
        var links = Cube(kind: XbrlArcRoles.NotAll); links.RemoveAt(2);
        Expect(RuleStatus.Incomplete, links, D());
    }

    [Fact] public void DirectedMemberCycleIsIncomplete()
    {
        var links = Cube(); links.Add(Arc(XbrlArcRoles.DomainMember, "Member", "CubeDomain"));
        Expect(RuleStatus.Incomplete, links, D());
    }

    [Fact] public void DirectedPrimaryInheritanceCycleIsIncomplete()
    {
        var links = Cube();
        links.Add(Arc(XbrlArcRoles.DomainMember, "Revenue", "Ancestor"));
        links.Add(Arc(XbrlArcRoles.DomainMember, "Ancestor", "Revenue"));
        Expect(RuleStatus.Incomplete, links, D());
    }

    [Fact] public void SharedAcyclicDescendantIsNotMistakenForCycle()
    {
        var links = Cube();
        links.Add(Arc(XbrlArcRoles.DomainMember, "CubeDomain", "Branch"));
        links.Add(Arc(XbrlArcRoles.DomainMember, "Branch", "Member"));
        Expect(RuleStatus.Pass, links, D());
    }

    [Fact] public void RepeatedAxisFailsAndTypedDimensionIsIncomplete()
    {
        Expect(RuleStatus.Fail, Cube(), D(), D("Other"));
        Expect(RuleStatus.Incomplete, Cube(), D() with { IsExplicit = false });
    }

    [Fact] public void BudgetExhaustionIsIncompleteRatherThanSuccessOrUnboundedTraversal()
    {
        var validator = new FilingRuleValidator(Profile(Cube()), maximumDimensionalRelationshipVisits: 1);
        var outcome = Assert.Single(validator.ValidateDimensionalAspects(Fact(D())), o => o.RuleId == Rule);
        Assert.Equal(RuleStatus.Incomplete, outcome.Status);
        Assert.Contains("budget", outcome.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void CancellationPropagatesInsteadOfReturningAnIncompleteResult()
    {
        var validator = new FilingRuleValidator(Profile(Cube()));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => validator.ValidateDimensionalAspects(Fact(D()), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => validator.ValidateStreaming([Fact(D())], null, cancellation.Token));
    }

    [Fact] public void StreamingAndMaterializedValidationProduceIdenticalOutcomes()
    {
        var validator = new FilingRuleValidator(Profile(Cube()));
        var facts = new[] { Fact(D()), Fact(D("Other")), Fact() };
        var materialized = validator.Validate(facts);
        var streamed = validator.ValidateStreaming(facts, null);
        Assert.Equal(materialized.Outcomes, streamed.Outcomes);
        Assert.Equal(materialized.MapToRunOutcome(), streamed.MapToRunOutcome());
        Assert.Contains(streamed.Outcomes, o => o.RuleId == Rule && o.Status == RuleStatus.Fail);
    }
}
