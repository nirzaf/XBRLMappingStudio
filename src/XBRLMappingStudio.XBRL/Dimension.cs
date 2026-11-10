using System.Text.Json.Serialization;

namespace XBRLMappingStudio.XBRL;

/// <summary>Standard XBRL Dimensions arcrole identifiers used by profile metadata.</summary>
public static class XbrlArcRoles
{
    public const string All = "http://xbrl.org/int/dim/arcrole/all";
    public const string NotAll = "http://xbrl.org/int/dim/arcrole/notAll";
    public const string HypercubeDimension = "http://xbrl.org/int/dim/arcrole/hypercube-dimension";
    public const string DimensionDomain = "http://xbrl.org/int/dim/arcrole/dimension-domain";
    public const string DomainMember = "http://xbrl.org/int/dim/arcrole/domain-member";
    public const string DimensionDefault = "http://xbrl.org/int/dim/arcrole/dimension-default";
}

/// <summary>
/// Versioned metadata for a typed dimension's global typed-domain element.
/// The initial supported datatype subset is deliberately limited to xs:string.
/// </summary>
public sealed record TypedDimensionDomainMetadata(
    string DomainElementQName,
    string DataTypeQName);

/// <summary>
/// A dimensional aspect carried by a fact (XBR04). Explicit dimensions pair a dimension
/// concept with a member concept. Supported typed values are normalized separately from
/// opaque imported XML evidence and require matching selected-profile metadata.
/// </summary>
public sealed record Dimension(
    string Name,
    string MemberName,
    bool IsExplicit = true)
{
    /// <summary>
    /// Canonical XML-infoset JSON for an imported typed member value. Null for explicit
    /// dimensions. It is evidence only and is intentionally not interpreted as taxonomy data.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypedValueJson { get; init; }

    /// <summary>A profile-validated simple typed value, currently used for xs:string domains.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypedValue { get; init; }

    /// <summary>Expanded name evidence for the imported axis, when retained by a report reader.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AxisExpandedName { get; init; }

    /// <summary>Expanded name evidence for an imported explicit member, when present.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemberExpandedName { get; init; }

    /// <summary>OIM-style key: the bracketed dimension qualified name.</summary>
    public string Key => Name;

    public override string ToString() => IsExplicit ? $"{Name}={MemberName}" : $"{Name}=<typed>";
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
