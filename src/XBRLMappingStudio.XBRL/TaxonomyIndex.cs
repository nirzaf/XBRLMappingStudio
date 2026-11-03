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

    public TaxonomyIndex(FilingProfile profile, IEnumerable<PresentationLink>? links = null)
    {
        _profile = profile;
        _links = (links ?? SyntheticProfile.PresentationLinks).ToList();
    }

    public IReadOnlyList<ConceptMetadata> AllConcepts => _profile.Concepts;

    public ConceptMetadata? Get(string qualifiedName) => _profile.FindConcept(qualifiedName);

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
        var matches = _profile.Concepts
            .Where(c => q.Length == 0 || Matches(c, q, language))
            .OrderBy(c => c.Namespace, StringComparer.Ordinal)
            .ThenBy(c => c.LocalName, StringComparer.Ordinal)
            .ToList();

        int total = matches.Count;
        var window = matches.Skip(page * pageSize).Take(pageSize).ToList();
        bool hasNext = (page + 1) * pageSize < total;
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
