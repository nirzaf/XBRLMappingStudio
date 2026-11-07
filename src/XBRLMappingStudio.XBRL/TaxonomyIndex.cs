namespace XBRLMappingStudio.XBRL;

/// <summary>A viewport-sized page of search results (XBR03).</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNext);

/// <summary>A concept's occurrence at one complete path in one presentation role.</summary>
public sealed record PresentationOccurrence(string RoleUri, IReadOnlyList<string> ConceptPath);

/// <summary>
/// A single presentation relationship network (XBR03): the set of parent→child links
/// under one role URI. A child may appear under multiple parents and multiple roles.
/// </summary>
public sealed class PresentationNetwork
{
    public string RoleUri { get; }
    private readonly IReadOnlyList<PresentationLink> _links;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PresentationLink>> _childrenByParent;

    public PresentationNetwork(string roleUri, IReadOnlyList<PresentationLink> links)
    {
        RoleUri = roleUri;
        _links = Array.AsReadOnly(links.ToArray());
        _childrenByParent = _links
            .GroupBy(link => link.ParentQName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PresentationLink>)Array.AsReadOnly(group
                    .OrderBy(link => link.Order)
                    .ThenBy(link => link.ChildQName, StringComparer.Ordinal)
                    .ToArray()),
                StringComparer.Ordinal);
    }

    public IReadOnlyList<PresentationLink> Links => _links;

    public IEnumerable<PresentationLink> ChildrenOf(string parentQName) =>
        _childrenByParent.TryGetValue(parentQName, out var children)
            ? children
            : Array.Empty<PresentationLink>();

    /// <summary>Loads one bounded page of a parent's ordered child relationships.</summary>
    public PagedResult<PresentationLink> ChildrenPage(
        string parentQName,
        int page = 0,
        int pageSize = TaxonomyIndex.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_childrenByParent.TryGetValue(parentQName, out var children))
            return new PagedResult<PresentationLink>(Array.Empty<PresentationLink>(), page, pageSize, 0, false);

        var start = (long)page * pageSize;
        var end = start + pageSize;
        var items = start >= children.Count
            ? Array.Empty<PresentationLink>()
            : children.Skip((int)start).Take(pageSize).ToArray();
        return new PagedResult<PresentationLink>(
            Array.AsReadOnly(items), page, pageSize, children.Count, children.Count > end);
    }

    /// <summary>Concepts that are never a child within this role (the top of the tree).</summary>
    public IEnumerable<string> Roots()
    {
        var children = new HashSet<string>(_links.Select(l => l.ChildQName), StringComparer.Ordinal);
        return _links.Select(l => l.ParentQName)
            .Where(p => !children.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal);
    }
}

/// <summary>A bounded, lazily expandable parent-to-child relationship network.</summary>
public sealed class TaxonomyLinkNetwork<TLink>
{
    private readonly IReadOnlyList<TLink> _links;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TLink>> _childrenByParent;
    private readonly Func<TLink, string> _parent;
    private readonly Func<TLink, string> _child;

    internal TaxonomyLinkNetwork(
        IEnumerable<TLink> links,
        Func<TLink, string> parent,
        Func<TLink, string> child)
    {
        _parent = parent;
        _child = child;
        _links = Array.AsReadOnly(links.ToArray());
        _childrenByParent = _links.GroupBy(parent, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TLink>)Array.AsReadOnly(group.ToArray()),
                StringComparer.Ordinal);
    }

    public IReadOnlyList<TLink> Links => _links;

    public IEnumerable<TLink> ChildrenOf(string parentQName) =>
        _childrenByParent.TryGetValue(parentQName, out var children)
            ? children
            : Array.Empty<TLink>();

    public PagedResult<TLink> ChildrenPage(
        string parentQName,
        int page = 0,
        int pageSize = TaxonomyIndex.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_childrenByParent.TryGetValue(parentQName, out var children))
            return new PagedResult<TLink>(Array.Empty<TLink>(), page, pageSize, 0, false);

        var start = (long)page * pageSize;
        var end = start + pageSize;
        var items = start >= children.Count
            ? Array.Empty<TLink>()
            : children.Skip((int)start).Take(pageSize).ToArray();
        return new PagedResult<TLink>(
            Array.AsReadOnly(items), page, pageSize, children.Count, children.Count > end);
    }

    public IEnumerable<string> Roots()
    {
        var children = new HashSet<string>(_links.Select(_child), StringComparer.Ordinal);
        return _links.Select(_parent)
            .Where(parent => !children.Contains(parent))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(parent => parent, StringComparer.Ordinal);
    }
}

/// <summary>
/// Paged, language-aware concept index over a <see cref="FilingProfile"/> (XBR03). The
/// designer binds the taxonomy explorer to <see cref="Search"/>; results are ordered by
/// qualified name so paging is stable and distinct namespaces with an identical label
/// remain distinct rows.
/// </summary>
public sealed class TaxonomyIndex
{
    public const int DefaultPageSize = 50;
    public const int MaximumRelationships = 1_000_000;

    private readonly FilingProfile _profile;
    private readonly IReadOnlyList<PresentationLink> _links;
    private readonly IReadOnlyList<CalculationLink> _calculationLinks;
    private readonly IReadOnlyList<DefinitionLink> _definitionLinks;
    private readonly IReadOnlyDictionary<string, TaxonomyLinkNetwork<CalculationLink>> _calculationNetworks;
    private readonly IReadOnlyDictionary<(string RoleUri, string ArcRoleUri), TaxonomyLinkNetwork<DefinitionLink>> _definitionNetworks;
    private readonly IReadOnlyList<ConceptMetadata> _orderedConcepts;
    private readonly IReadOnlyDictionary<string, ConceptMetadata> _byQualifiedName;
    private readonly IReadOnlyDictionary<ExpandedConceptName, ConceptMetadata> _byExpandedName;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _presentationRolesByConcept;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _calculationRolesByConcept;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _definitionRolesByConcept;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _definitionArcRolesByRole;

    public TaxonomyIndex(FilingProfile profile, IEnumerable<PresentationLink>? links = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.Digest))
            throw new ArgumentException("A taxonomy index requires a versioned profile identity.", nameof(profile));
        var concepts = profile.Concepts
            .OrderBy(concept => concept.Namespace, StringComparer.Ordinal)
            .ThenBy(concept => concept.LocalName, StringComparer.Ordinal)
            .ToArray();
        try
        {
            _byExpandedName = concepts.ToDictionary(concept => concept.ExpandedName);
            _byQualifiedName = concepts.ToDictionary(concept => concept.QualifiedName, StringComparer.Ordinal);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                "A taxonomy profile must have unique expanded names and qualified names.", nameof(profile), ex);
        }
        _orderedConcepts = Array.AsReadOnly(concepts);
        var profileLinks = profile.PresentationLinks ?? Array.Empty<PresentationLink>();
        var selectedLinks = links ?? (profileLinks.Count > 0
            ? profileLinks
            : StringComparer.Ordinal.Equals(profile.ProfileId, SyntheticProfile.ProfileId)
                ? SyntheticProfile.PresentationLinks
                : Array.Empty<PresentationLink>());
        var linkArray = selectedLinks.ToArray();
        if (linkArray.Length > MaximumRelationships)
            throw new ArgumentException($"A taxonomy profile cannot index more than {MaximumRelationships} presentation relationships.", nameof(profile));
        var knownQualifiedNames = new HashSet<string>(_byQualifiedName.Keys, StringComparer.Ordinal);
        var uniqueLinks = new HashSet<(string RoleUri, string ParentQName, string ChildQName, decimal Order)>();
        foreach (var link in linkArray)
        {
            if (link is null
                || string.IsNullOrWhiteSpace(link.RoleUri)
                || link.RoleUri.Length > 2048
                || link.RoleUri.Any(char.IsWhiteSpace)
                || link.RoleUri.Any(char.IsControl)
                || !Uri.TryCreate(link.RoleUri, UriKind.Absolute, out var roleUri)
                || (roleUri.Scheme is "http" or "https"
                    && (string.IsNullOrEmpty(roleUri.Host) || roleUri.UserInfo.Length > 0))
                || !knownQualifiedNames.Contains(link.ParentQName)
                || !knownQualifiedNames.Contains(link.ChildQName))
                throw new ArgumentException("Presentation relationships must have an absolute role URI and reference indexed concepts.", nameof(profile));
            if (!uniqueLinks.Add((link.RoleUri, link.ParentQName, link.ChildQName, link.Order)))
                throw new ArgumentException("A taxonomy profile cannot contain duplicate presentation relationships.", nameof(profile));
        }
        _links = Array.AsReadOnly(linkArray);
        _calculationLinks = Array.AsReadOnly((profile.CalculationLinks ?? Array.Empty<CalculationLink>()).ToArray());
        _definitionLinks = Array.AsReadOnly((profile.DefinitionLinks ?? Array.Empty<DefinitionLink>()).ToArray());
        if ((long)_links.Count + _calculationLinks.Count + _definitionLinks.Count > MaximumRelationships)
            throw new ArgumentException($"A taxonomy profile cannot index more than {MaximumRelationships} relationships across linkbases.", nameof(profile));

        var uniqueCalculationLinks = new HashSet<CalculationLink>();
        foreach (var link in _calculationLinks)
        {
            if (link is null
                || !IsValidRoleUri(link.RoleUri)
                || (link.TargetRoleUri is not null && !IsValidRoleUri(link.TargetRoleUri))
                || link.Weight is not (-1m or 1m)
                || !knownQualifiedNames.Contains(link.ParentQName)
                || !knownQualifiedNames.Contains(link.ChildQName))
                throw new ArgumentException("Calculation relationships must have valid role URIs and reference indexed concepts.", nameof(profile));
            if (!uniqueCalculationLinks.Add(link))
                throw new ArgumentException("A taxonomy profile cannot contain duplicate calculation relationships.", nameof(profile));
        }

        var uniqueDefinitionLinks = new HashSet<DefinitionLink>();
        foreach (var link in _definitionLinks)
        {
            if (link is null
                || !IsValidRoleUri(link.RoleUri)
                || !IsValidRoleUri(link.ArcRoleUri)
                || (link.TargetRoleUri is not null && !IsValidRoleUri(link.TargetRoleUri))
                || !knownQualifiedNames.Contains(link.ParentQName)
                || !knownQualifiedNames.Contains(link.ChildQName)
                || (link.ContextElement is not null && link.ContextElement is not ("segment" or "scenario")))
                throw new ArgumentException("Definition relationships must have valid role URIs, supported attributes, and indexed endpoints.", nameof(profile));
            if (!uniqueDefinitionLinks.Add(link))
                throw new ArgumentException("A taxonomy profile cannot contain duplicate definition relationships.", nameof(profile));
        }

        _calculationRolesByConcept = BuildRolesByConcept(
            _calculationLinks, link => link.RoleUri, link => link.ParentQName, link => link.ChildQName);
        _definitionRolesByConcept = BuildRolesByConcept(
            _definitionLinks, link => link.RoleUri, link => link.ParentQName, link => link.ChildQName);
        _definitionArcRolesByRole = _definitionLinks
            .GroupBy(link => link.RoleUri, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)Array.AsReadOnly(group
                    .Select(link => link.ArcRoleUri)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
                StringComparer.Ordinal);
        _calculationNetworks = _calculationLinks
            .GroupBy(link => link.RoleUri, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new TaxonomyLinkNetwork<CalculationLink>(group
                    .OrderBy(link => link.Order)
                    .ThenBy(link => link.ChildQName, StringComparer.Ordinal)
                    .ThenBy(link => link.Weight)
                    .ThenBy(link => link.TargetRoleUri, StringComparer.Ordinal),
                    link => link.ParentQName, link => link.ChildQName),
                StringComparer.Ordinal);
        _definitionNetworks = _definitionLinks
            .GroupBy(link => (link.RoleUri, link.ArcRoleUri))
            .ToDictionary(
                group => group.Key,
                group => new TaxonomyLinkNetwork<DefinitionLink>(group
                    .OrderBy(link => link.Order)
                    .ThenBy(link => link.ChildQName, StringComparer.Ordinal)
                    .ThenBy(link => link.TargetRoleUri, StringComparer.Ordinal)
                    .ThenBy(link => link.Usable)
                    .ThenBy(link => link.Closed)
                    .ThenBy(link => link.ContextElement, StringComparer.Ordinal),
                    link => link.ParentQName, link => link.ChildQName));

        var rolesByConcept = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var link in _links)
        {
            AddRole(link.ParentQName, link.RoleUri);
            AddRole(link.ChildQName, link.RoleUri);
        }
        _presentationRolesByConcept = rolesByConcept.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.Order(StringComparer.Ordinal).ToArray()),
            StringComparer.Ordinal);

        void AddRole(string qualifiedName, string roleUri)
        {
            if (!rolesByConcept.TryGetValue(qualifiedName, out var roles))
                rolesByConcept.Add(qualifiedName, roles = new HashSet<string>(StringComparer.Ordinal));
            roles.Add(roleUri);
        }
    }

    public string ProfileId => _profile.ProfileId;
    public string ProfileDigest => _profile.Digest;
    public string TaxonomyVersion => string.IsNullOrWhiteSpace(_profile.TaxonomyVersion)
        ? _profile.Digest
        : _profile.TaxonomyVersion;
    public IReadOnlySet<string> SupportedUnits => _profile.SupportedUnits;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionDomains => _profile.DimensionDomains;
    public IReadOnlyList<ConceptMetadata> AllConcepts => _orderedConcepts;

    public ConceptMetadata? Get(string qualifiedName) =>
        _byQualifiedName.TryGetValue(qualifiedName, out var concept) ? concept : null;

    public ConceptMetadata? GetByExpandedName(string namespaceUri, string localName) =>
        _byExpandedName.TryGetValue(new ExpandedConceptName(namespaceUri, localName), out var concept)
            ? concept
            : null;

    /// <summary>Presentation roles in which this concept occurs as a parent or child.</summary>
    public IReadOnlyList<string> PresentationRolesFor(string qualifiedName) =>
        _presentationRolesByConcept.TryGetValue(qualifiedName, out var roles)
            ? roles
            : Array.Empty<string>();

    public IReadOnlyList<string> CalculationRolesFor(string qualifiedName) =>
        _calculationRolesByConcept.TryGetValue(qualifiedName, out var roles)
            ? roles
            : Array.Empty<string>();

    public IReadOnlyList<string> DefinitionRolesFor(string qualifiedName) =>
        _definitionRolesByConcept.TryGetValue(qualifiedName, out var roles)
            ? roles
            : Array.Empty<string>();

    public IReadOnlyList<string> DefinitionArcRoles(string roleUri) =>
        _definitionArcRolesByRole.TryGetValue(roleUri, out var arcRoles)
            ? arcRoles
            : Array.Empty<string>();

    public TaxonomyLinkNetwork<CalculationLink> CalculationNetwork(string roleUri) =>
        _calculationNetworks.TryGetValue(roleUri, out var network)
            ? network
            : new TaxonomyLinkNetwork<CalculationLink>(Array.Empty<CalculationLink>(),
                link => link.ParentQName, link => link.ChildQName);

    public TaxonomyLinkNetwork<DefinitionLink> DefinitionNetwork(string roleUri, string arcRoleUri) =>
        _definitionNetworks.TryGetValue((roleUri, arcRoleUri), out var network)
            ? network
            : new TaxonomyLinkNetwork<DefinitionLink>(Array.Empty<DefinitionLink>(),
                link => link.ParentQName, link => link.ChildQName);

    /// <summary>Whether this index is bound to the exact installed profile version.</summary>
    public bool IsCurrentFor(FilingProfile profile) =>
        profile is not null
        && StringComparer.Ordinal.Equals(ProfileId, profile.ProfileId)
        && StringComparer.Ordinal.Equals(ProfileDigest, profile.Digest);

    public IReadOnlyList<string> Roles => _links
        .Select(l => l.RoleUri)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(r => r, StringComparer.Ordinal)
        .ToList();

    public PresentationNetwork Network(string roleUri) => new(
        roleUri,
        _links.Where(l => string.Equals(l.RoleUri, roleUri, StringComparison.Ordinal)).ToList());

    /// <summary>
    /// Finds each bounded root-to-concept path in every loaded presentation role. A
    /// concept can therefore be returned multiple times when it occurs in different
    /// roles or beneath different parents. Cycles and excessive path growth are bounded.
    /// </summary>
    public IReadOnlyList<PresentationOccurrence> PresentationOccurrencesFor(
        string qualifiedName,
        int maximumOccurrences = 512,
        int maximumDepth = 128,
        CancellationToken cancellationToken = default)
    {
        if (maximumOccurrences <= 0) throw new ArgumentOutOfRangeException(nameof(maximumOccurrences));
        if (maximumDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_byQualifiedName.ContainsKey(qualifiedName)) return Array.Empty<PresentationOccurrence>();

        var occurrences = new List<PresentationOccurrence>();
        foreach (var roleUri in PresentationRolesFor(qualifiedName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (occurrences.Count >= maximumOccurrences) return occurrences.AsReadOnly();
            var roleLinks = _links
                .Where(link => StringComparer.Ordinal.Equals(link.RoleUri, roleUri))
                .OrderBy(link => link.Order)
                .ThenBy(link => link.ParentQName, StringComparer.Ordinal)
                .ThenBy(link => link.ChildQName, StringComparer.Ordinal)
                .ToArray();

            var parentsByChild = roleLinks
                .GroupBy(link => link.ChildQName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var childNames = roleLinks.Select(link => link.ChildQName)
                .ToHashSet(StringComparer.Ordinal);
            var reversePath = new List<string>();
            var pathSet = new HashSet<string>(StringComparer.Ordinal);
            VisitParents(qualifiedName);

            void VisitParents(string current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (occurrences.Count >= maximumOccurrences || reversePath.Count >= maximumDepth
                    || !pathSet.Add(current)) return;
                reversePath.Add(current);

                if (!childNames.Contains(current))
                {
                    occurrences.Add(new PresentationOccurrence(
                        roleUri,
                        Array.AsReadOnly(reversePath.AsEnumerable().Reverse().ToArray())));
                }
                else if (parentsByChild.TryGetValue(current, out var parents))
                {
                    foreach (var parent in parents)
                    {
                        VisitParents(parent.ParentQName);
                        if (occurrences.Count >= maximumOccurrences) break;
                    }
                }

                reversePath.RemoveAt(reversePath.Count - 1);
                pathSet.Remove(current);
            }
        }

        return occurrences.AsReadOnly();
    }

    /// <summary>
    /// Substring search across local name, qualified name and the label in the requested
    /// language (falling back to the default label when the language is unsupported).
    /// Deterministically ordered; abstract concepts are included so the caller can render
    /// them, but a helper rejects them as fact targets.
    /// </summary>
    public PagedResult<ConceptMetadata> Search(
        string? query,
        string? language = null,
        int page = 0,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        cancellationToken.ThrowIfCancellationRequested();

        var q = (query ?? "").Trim();
        var start = (long)page * pageSize;
        var end = start + pageSize;
        var window = new List<ConceptMetadata>(Math.Min(pageSize, _orderedConcepts.Count));
        var total = 0;
        var inspected = 0;
        foreach (var concept in _orderedConcepts)
        {
            if ((inspected++ & 0x3f) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (q.Length > 0 && !Matches(concept, q, language)) continue;
            if (total >= start && total < end) window.Add(concept);
            total++;
        }
        bool hasNext = total > end;
        return new PagedResult<ConceptMetadata>(window, page, pageSize, total, hasNext);
    }

    /// <summary>Runs paged search away from the UI thread and observes superseding cancellation.</summary>
    public Task<PagedResult<ConceptMetadata>> SearchAsync(
        string? query,
        string? language = null,
        int page = 0,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Search(query, language, page, pageSize, cancellationToken));

    private static bool Matches(ConceptMetadata c, string q, string? language)
    {
        if (Contains(c.LocalName, q)) return true;
        if (Contains(c.QualifiedName, q)) return true;
        var label = ResolveLabel(c, language);
        return Contains(label, q);
    }

    /// <summary>Label in the requested language, else the default label.</summary>
    public static string ResolveLabel(ConceptMetadata c, string? language)
    {
        if (!string.IsNullOrEmpty(language)
            && c.LabelsByLanguage.TryGetValue(language, out var localized)
            && localized.Length > 0)
            return localized;
        return c.Label;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidRoleUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
            || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme is not ("http" or "https")
            || (!string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildRolesByConcept<TLink>(
        IEnumerable<TLink> links,
        Func<TLink, string> role,
        Func<TLink, string> parent,
        Func<TLink, string> child)
    {
        var roles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            Add(parent(link));
            Add(child(link));

            void Add(string qualifiedName)
            {
                if (!roles.TryGetValue(qualifiedName, out var values))
                    roles.Add(qualifiedName, values = new HashSet<string>(StringComparer.Ordinal));
                values.Add(role(link));
            }
        }

        return roles.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.Order(StringComparer.Ordinal).ToArray()),
            StringComparer.Ordinal);
    }

    /// <summary>True when a concept may carry a fact (concrete + monetary/string typed).</summary>
    public static bool IsFactTargetable(ConceptMetadata c) => !c.IsAbstract;
}
