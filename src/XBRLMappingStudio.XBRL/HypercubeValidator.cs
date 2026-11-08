namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Bounded evaluation of the explicit, segment-only subset of XBRL Dimensions.
/// Relationships must be the effective arcs from the selected, verified profile.
/// This is not a replacement for DTS/schema validation or a filing authority's rules.
/// </summary>
internal sealed class HypercubeValidator
{
    internal const string RuleId = "xbrl:dimension-hypercube-valid";
    private readonly FilingProfile _profile;
    private readonly int _maximumVisits;
    private readonly bool _overBudget;
    private readonly bool _hasHypercubes;
    private readonly Dictionary<(string ArcRole, string Role, string Parent), DefinitionLink[]> _outgoing = new();
    private readonly Dictionary<string, DefinitionLink[]> _incomingMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DefinitionLink[]> _hasHypercubesByPrimary = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _defaults = new(StringComparer.Ordinal);

    internal HypercubeValidator(FilingProfile profile, int maximumVisits)
    {
        _profile = profile;
        _maximumVisits = maximumVisits;
        var links = profile.DefinitionLinks ?? Array.Empty<DefinitionLink>();
        _overBudget = links.Count > TaxonomyIndex.MaximumRelationships;
        if (_overBudget) return;
        _outgoing = links.GroupBy(link => (link.ArcRoleUri, link.RoleUri, link.ParentQName))
            .ToDictionary(group => group.Key, group => group.ToArray());
        _incomingMembers = links.Where(link => link.ArcRoleUri == XbrlArcRoles.DomainMember)
            .GroupBy(link => link.ChildQName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        _hasHypercubesByPrimary = links.Where(link => link.ArcRoleUri is XbrlArcRoles.All or XbrlArcRoles.NotAll)
            .GroupBy(link => link.ParentQName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        _hasHypercubes = _hasHypercubesByPrimary.Count > 0;
        _defaults = links.Where(link => link.ArcRoleUri == XbrlArcRoles.DimensionDefault)
            .GroupBy(link => link.ParentQName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(link => link.ChildQName).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    internal (RuleStatus Status, string? Detail)? Validate(XbrlFact fact, CancellationToken cancellationToken)
    {
        if (_overBudget) return (RuleStatus.Incomplete, "Dimensional relationship metadata exceeds the supported profile limit.");
        if (!_hasHypercubes) return null;
        var budget = new VisitBudget(_maximumVisits, cancellationToken);
        try
        {
            var applicable = new HashSet<DefinitionLink>();
            Walk(new State(fact.ConceptName, null), state =>
            {
                if (_hasHypercubesByPrimary.TryGetValue(state.Concept, out var cubes))
                    foreach (var cube in cubes)
                    {
                        budget.Visit();
                        if (state.Role is null || state.Role == cube.RoleUri) applicable.Add(cube);
                    }
                if (!_incomingMembers.TryGetValue(state.Concept, out var incoming)) return [];
                var parents = new List<State>();
                foreach (var link in incoming)
                {
                    budget.Visit();
                    if (state.Role is null || state.Role == NextRole(link))
                        parents.Add(new State(link.ParentQName, link.RoleUri));
                }
                return parents;
            }, budget);
            if (applicable.Count == 0) return (RuleStatus.Pass, null);
            if (fact.Dimensions.Any(dimension => !dimension.IsExplicit))
                return (RuleStatus.Incomplete, "Typed dimensions are not supported by the local hypercube evaluator.");
            if (fact.Dimensions.Select(dimension => dimension.Name).Distinct(StringComparer.Ordinal).Count()
                != fact.Dimensions.Count)
                return (RuleStatus.Fail, "A fact cannot report the same dimension more than once.");

            // A primary item is dimensionally valid if every cube in at least one
            // applicable ELR is satisfied. notAll negates the entire cube predicate.
            // Inspect every applicable cube before accepting any alternative: missing
            // or unsupported metadata must not become a successful notAll/OR result.
            var anyRoleValid = false;
            string? incomplete = null;
            foreach (var role in applicable.GroupBy(link => link.RoleUri, StringComparer.Ordinal))
            {
                var roleValid = true;
                foreach (var cube in role)
                {
                    var result = ValidateCube(fact, cube, budget);
                    if (result.Status == RuleStatus.Incomplete) incomplete ??= result.Detail;
                    var cubeValid = result.Status == RuleStatus.Pass;
                    roleValid &= cube.ArcRoleUri == XbrlArcRoles.NotAll ? !cubeValid : cubeValid;
                }
                anyRoleValid |= roleValid;
            }
            if (incomplete is not null) return (RuleStatus.Incomplete, incomplete);
            return anyRoleValid
                ? (RuleStatus.Pass, null)
                : (RuleStatus.Fail, $"Fact on '{fact.ConceptName}' does not satisfy the applicable dimensional hypercubes in any definition role.");
        }
        catch (IncompleteNetworkException ex)
        {
            return (RuleStatus.Incomplete, ex.Message);
        }
    }

    private (RuleStatus Status, string? Detail) ValidateCube(XbrlFact fact, DefinitionLink cube, VisitBudget budget)
    {
        budget.Visit();
        if (cube.ContextElement != "segment")
            return (RuleStatus.Incomplete, $"Hypercube '{cube.ChildQName}' requires a supported explicit segment context; scenario or missing context metadata is unsupported.");
        var dimensions = Outgoing(XbrlArcRoles.HypercubeDimension, NextRole(cube), cube.ChildQName);
        var cubeAxes = new HashSet<string>(StringComparer.Ordinal);
        var valid = true;
        foreach (var relationship in dimensions)
        {
            budget.Visit();
            var axis = relationship.ChildQName;
            cubeAxes.Add(axis);
            if (!_profile.DimensionDomains.TryGetValue(axis, out var profileDomain) || profileDomain is null || profileDomain.Count == 0)
                return (RuleStatus.Incomplete, $"Dimension '{axis}' lacks declared explicit-domain metadata.");
            if (_defaults.TryGetValue(axis, out var defaults) && defaults.Length != 1)
                return (RuleStatus.Incomplete, $"Dimension '{axis}' has conflicting default members.");
            var member = fact.Dimensions.FirstOrDefault(dimension => dimension.Name == axis)?.MemberName
                ?? defaults?.SingleOrDefault();
            // Resolve even absent members: missing domain metadata is not evidence
            // that a negated hypercube is valid.
            var domainArcs = Outgoing(XbrlArcRoles.DimensionDomain, NextRole(relationship), axis);
            if (domainArcs.Length == 0)
                return (RuleStatus.Incomplete, $"Dimension '{axis}' has no dimension-domain relationships in role '{NextRole(relationship)}'.");
            var usable = new HashSet<string>(StringComparer.Ordinal);
            var unusable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var domain in domainArcs)
            {
                budget.Visit();
                AddMember(domain);
                Walk(new State(domain.ChildQName, NextRole(domain)), state =>
                {
                    var members = Outgoing(XbrlArcRoles.DomainMember, state.Role!, state.Concept);
                    var children = new List<State>();
                    foreach (var link in members)
                    {
                        budget.Visit();
                        AddMember(link);
                        children.Add(new State(link.ChildQName, NextRole(link)));
                    }
                    return children;
                }, budget);
            }
            // Unusable arcs exclude that member, not its usable descendants. A
            // member reached through both usable and unusable arcs is unusable.
            valid &= member is not null && usable.Contains(member) && !unusable.Contains(member);

            void AddMember(DefinitionLink link)
                => (link.Usable == false ? unusable : usable).Add(link.ChildQName);
        }
        if (cube.Closed == true && fact.Dimensions.Any(dimension => !cubeAxes.Contains(dimension.Name)))
            valid = false;
        return (valid ? RuleStatus.Pass : RuleStatus.Fail, null);
    }

    private DefinitionLink[] Outgoing(string arcRole, string role, string parent)
        => _outgoing.GetValueOrDefault((arcRole, role, parent)) ?? [];

    private static string NextRole(DefinitionLink link) => link.TargetRoleUri ?? link.RoleUri;
    private readonly record struct State(string Concept, string? Role);

    // Iterative DFS bounds stack/retained state and rejects directed cycles instead
    // of silently truncating an invalid network. No state is retained per input row.
    private static void Walk(State root, Func<State, IReadOnlyList<State>> next, VisitBudget budget)
    {
        var done = new HashSet<State>();
        var active = new HashSet<State>();
        var pending = new Stack<(State State, bool Exit)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            budget.Visit();
            if (item.Exit)
            {
                active.Remove(item.State);
                done.Add(item.State);
                continue;
            }
            if (active.Contains(item.State))
                throw new IncompleteNetworkException("A directed cycle prevents complete dimensional relationship evaluation.");
            if (done.Contains(item.State)) continue;
            active.Add(item.State);
            pending.Push((item.State, true));
            var children = next(item.State);
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], false));
        }
    }

    private sealed class VisitBudget(int maximumVisits, CancellationToken cancellationToken)
    {
        private int _remaining = maximumVisits;
        internal void Visit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (--_remaining < 0)
                throw new IncompleteNetworkException("Dimensional relationship traversal exceeded its per-fact resource budget.");
        }
    }

    private sealed class IncompleteNetworkException(string message) : Exception(message);
}
