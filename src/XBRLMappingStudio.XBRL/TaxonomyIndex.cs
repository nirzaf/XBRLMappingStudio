namespace XBRLMappingStudio.XBRL;

/// <summary>A viewport-sized page of search results (XBR03).</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNext);

/// <summary>
/// A single presentation relationship network (XBR03): the set of parent→child links
/// under one role URI. A child may appear under multiple parents and multiple roles.
/// </summary>
public sealed class PresentationNetwork
{
    public string RoleUri { get; }
    private readonly IReadOnlyList<PresentationLink> _links;

    public PresentationNetwork(string roleUri, IReadOnlyList<PresentationLink> links)
    {
        RoleUri = roleUri;
        _links = links;
    }

    public IReadOnlyList<PresentationLink> Links => _links;

    public IEnumerable<PresentationLink> ChildrenOf(string parentQName) => _links
        .Where(l => string.Equals(l.ParentQName, parentQName, StringComparison.Ordinal))
        .OrderBy(l => l.Order)
        .ThenBy(l => l.ChildQName, StringComparer.Ordinal);

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

/// <summary>
/// Paged, language-aware concept index over a <see cref="FilingProfile"/> (XBR03). The
/// designer binds the taxonomy explorer to <see cref="Search"/>; results are ordered by
/// qualified name so paging is stable and distinct namespaces with an identical label
/// remain distinct rows.
/// </summary>
public sealed class TaxonomyIndex
{
    public const int DefaultPageSize = 50;

    private readonly FilingProfile _profile;
    private readonly IReadOnlyList<PresentationLink> _links;
    private readonly IReadOnlyList<ConceptMetadata> _orderedConcepts;
    private readonly IReadOnlyDictionary<string, ConceptMetadata> _byQualifiedName;
    private readonly IReadOnlyDictionary<ExpandedConceptName, ConceptMetadata> _byExpandedName;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _presentationRolesByConcept;

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
        _links = (links ?? (StringComparer.Ordinal.Equals(profile.ProfileId, SyntheticProfile.ProfileId)
            ? SyntheticProfile.PresentationLinks
            : Array.Empty<PresentationLink>())).ToList();
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
    /// Substring search across local name, qualified name and the label in the requested
    /// language (falling back to the default label when the language is unsupported).
    /// Deterministically ordered; abstract concepts are included so the caller can render
    /// them, but a helper rejects them as fact targets.
    /// </summary>
    public PagedResult<ConceptMetadata> Search(
        string? query,
        string? language = null,
        int page = 0,
        int pageSize = DefaultPageSize)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

        var q = (query ?? "").Trim();
        var start = (long)page * pageSize;
        var end = start + pageSize;
        var window = new List<ConceptMetadata>(Math.Min(pageSize, _orderedConcepts.Count));
        var total = 0;
        foreach (var concept in _orderedConcepts)
        {
            if (q.Length > 0 && !Matches(concept, q, language)) continue;
            if (total >= start && total < end) window.Add(concept);
            total++;
        }
        bool hasNext = total > end;
        return new PagedResult<ConceptMetadata>(window, page, pageSize, total, hasNext);
    }

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

    /// <summary>True when a concept may carry a fact (concrete + monetary/string typed).</summary>
    public static bool IsFactTargetable(ConceptMetadata c) => !c.IsAbstract;
}
