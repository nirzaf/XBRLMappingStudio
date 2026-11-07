namespace XBRLMappingStudio.XBRL;

/// <summary>
/// A dimensional aspect carried by a fact (XBR04). Explicit dimensions pair a dimension
/// concept with a member concept; the synthetic profile exercises explicit dimensions
/// only (typed dimensions are out of scope for the offline slice).
/// </summary>
public sealed record Dimension(
    string Name,
    string MemberName,
    bool IsExplicit = true)
{
    /// <summary>OIM-style key: the bracketed dimension qualified name.</summary>
    public string Key => Name;

    public override string ToString() => $"{Name}={MemberName}";
}

/// <summary>
/// A compound unit formed by multiplying measures and dividing by others (XBR04),
/// e.g. <c>USDPerShare = USD / shares</c>. Numerators always carry power +1; each
/// denominator is divided once.
/// </summary>
public sealed record CompoundUnit(
    IReadOnlyList<string> Numerators,
    IReadOnlyList<string>? Denominators = null)
{
    public IReadOnlyList<string> DenominatorList => Denominators ?? System.Array.Empty<string>();

    /// <summary>Stable rendering used for digests and the OIM divide form.</summary>
    public string CanonicalKey
    {
        get
        {
            var n = string.Join("*", Numerators.OrderBy(x => x, System.StringComparer.Ordinal));
            var d = DenominatorList.Count == 0
                ? ""
                : "/" + string.Join("*", DenominatorList.OrderBy(x => x, System.StringComparer.Ordinal));
            return n + d;
        }
    }

    public override string ToString() => CanonicalKey;
}

/// <summary>
/// One edge of a presentation relationship (XBR03). A parent concept lists children in
/// document order within a role URI; the same child may appear under several roles.
/// </summary>
public sealed record PresentationLink(
    string RoleUri,
    string ParentQName,
    string ChildQName,
    decimal Order = 0);

/// <summary>One ordered summation-item arc in a calculation linkbase.</summary>
public sealed record CalculationLink(
    string RoleUri,
    string ParentQName,
    string ChildQName,
    decimal Weight,
    decimal Order = 0,
    string? TargetRoleUri = null);

/// <summary>
/// One ordered dimensional or other definition arc. Arc-specific XBRL attributes are
/// retained so indexing does not collapse relationships that share concept endpoints.
/// </summary>
public sealed record DefinitionLink(
    string RoleUri,
    string ArcRoleUri,
    string ParentQName,
    string ChildQName,
    decimal Order = 0,
    string? TargetRoleUri = null,
    bool? Usable = null,
    bool? Closed = null,
    string? ContextElement = null);
