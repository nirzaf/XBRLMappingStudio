using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Emits and re-reads xBRL 2.1 XML instance documents (XBR05). Facts are grouped into
/// shared <c>xbrli:context</c> and <c>xbrli:unit</c> elements by semantic aspect, so the
/// XML and JSON encodings of the same fact set are semantically equivalent as a multiset.
/// Reading uses a DTD-prohibited reader. Constructs the model cannot represent (tuples,
/// typed dimensions, fractional values, non-dimensional segments) block export with
/// <see cref="DiagnosticCodes.XbrUnsupportedConstruct"/> rather than being silently dropped.
/// </summary>
public static class XmlSerializer
{
    private static readonly XNamespace Xbrli = "http://www.xbrl.org/2003/instance";
    private static readonly XNamespace Link = "http://www.xbrl.org/2003/linkbase";
    private static readonly XNamespace Xbrldi = "http://xbrl.org/2006/xbrldi";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Iso4217 = "http://www.xbrl.org/2003/iso4217";

    public static string Serialize(FilingProfile profile, IReadOnlyList<XbrlFact> facts)
    {
        foreach (var f in facts)
        {
            AssertSupported(f);
            var concept = profile.FindConcept(f.ConceptName)
                ?? throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                    $"Concept '{f.ConceptName}' is not defined in the filing profile.");
            if (!f.ValidateAgainst(concept, out var reason))
                throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct, reason);
        }

        // Deterministic aspect -> id assignment by first appearance in fact order.
        var contextIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var contextOrder = new List<(string Id, XbrlFact Fact)>();
        var unitIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var unitOrder = new List<(string Id, XbrlFact Fact)>();

        foreach (var f in facts)
        {
            var ckey = ContextKey(f);
            if (!contextIds.ContainsKey(ckey))
            {
                var id = "c" + (contextIds.Count + 1);
                contextIds[ckey] = id;
                contextOrder.Add((id, f));
            }
            if (UnitPresent(f))
            {
                var ukey = UnitKey(f);
                if (!unitIds.ContainsKey(ukey))
                {
                    var id = "u" + (unitIds.Count + 1);
                    unitIds[ukey] = id;
                    unitOrder.Add((id, f));
                }
            }
        }

        var namespaceUris = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xbrli"] = Xbrli.NamespaceName,
            ["link"] = Link.NamespaceName,
            ["xbrldi"] = Xbrldi.NamespaceName,
            ["xlink"] = Xlink.NamespaceName,
            ["iso4217"] = Iso4217.NamespaceName
        };
        foreach (var (prefix, uri) in profile.NamespaceUris) AddNamespace(prefix, uri);
        foreach (var c in profile.Concepts)
            AddNamespace(SplitQName(c.QualifiedName).prefix!, c.Namespace);
        foreach (var f in facts)
        {
            _ = ResolveQName(f.ConceptName);
            foreach (var d in f.Dimensions)
            {
                if (!d.IsExplicit)
                    throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                        $"Typed dimension '{d.Name}' requires taxonomy domain metadata and is not exportable in this profile.");
                _ = ResolveQName(d.Name);
                _ = ResolveQName(d.MemberName);
            }
            foreach (var measure in (f.Unit is null ? Enumerable.Empty<string>() : new[] { f.Unit.Measure })
                         .Concat(f.CompoundUnit?.Numerators ?? Array.Empty<string>())
                         .Concat(f.CompoundUnit?.DenominatorList ?? Array.Empty<string>()))
                _ = ResolveQName(MeasureQName(measure, profile));
        }

        var root = new XElement(Xbrli + "xbrl");
        foreach (var (pfx, ns) in namespaceUris.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            root.Add(new XAttribute(XNamespace.Xmlns + pfx, ns));
        foreach (var url in profile.TaxonomyUrls)
            root.Add(new XElement(Link + "schemaRef",
                new XAttribute(Xlink + "type", "simple"), new XAttribute(Xlink + "href", url)));

        foreach (var (id, f) in contextOrder)
            root.Add(BuildContext(id, f, ResolveQName));
        foreach (var (id, f) in unitOrder)
            root.Add(BuildUnit(id, f, profile, ResolveQName));

        foreach (var f in facts)
            root.Add(BuildFact(f, namespaceUris, ResolveQName, contextIds, unitIds));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
            doc.Save(w);
        return sb.ToString();

        void AddNamespace(string prefix, string uri)
        {
            if (namespaceUris.TryGetValue(prefix, out var prior) && prior != uri)
                throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                    $"Namespace prefix '{prefix}' has conflicting URIs.");
            namespaceUris[prefix] = uri;
        }

        string ResolveQName(string qname)
        {
            var (prefix, local) = SplitQName(qname);
            if (prefix is null || !namespaceUris.ContainsKey(prefix))
                throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                    $"QName '{qname}' has no namespace URI in the filing profile.");
            try { XmlConvert.VerifyNCName(local); }
            catch (XmlException ex)
            {
                throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct, ex.Message);
            }
            return qname;
        }
    }

    private static XElement BuildContext(string id, XbrlFact f, Func<string, string> qname)
    {
        var entity = new XElement(Xbrli + "entity",
            new XElement(Xbrli + "identifier",
                new XAttribute("scheme", f.EntityScheme), f.EntityIdentifier));
        if (f.Dimensions.Count > 0)
            entity.Add(new XElement(Xbrli + "segment", f.Dimensions.OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new XElement(Xbrldi + "explicitMember",
                    new XAttribute("dimension", qname(d.Name)), qname(d.MemberName)))));

        var period = f.Period.Kind == PeriodKind.Instant
            ? new XElement(Xbrli + "period", new XElement(Xbrli + "instant", Iso(f.Period.Instant)))
            : new XElement(Xbrli + "period",
                new XElement(Xbrli + "startDate", Iso(f.Period.Start)),
                new XElement(Xbrli + "endDate", Iso(f.Period.End)));

        return new XElement(Xbrli + "context",
            new XAttribute("id", id), entity, period);
    }

    private static XElement BuildUnit(string id, XbrlFact f, FilingProfile profile, Func<string, string> qname)
    {
        if (f.CompoundUnit is not null)
        {
            var num = new XElement(Xbrli + "unitNumerator");
            foreach (var m in f.CompoundUnit.Numerators)
                num.Add(new XElement(Xbrli + "measure", qname(MeasureQName(m, profile))));
            var den = new XElement(Xbrli + "unitDenominator");
            foreach (var m in f.CompoundUnit.DenominatorList)
                den.Add(new XElement(Xbrli + "measure", qname(MeasureQName(m, profile))));
            return new XElement(Xbrli + "unit", new XAttribute("id", id),
                new XElement(Xbrli + "divide", num, den));
        }
        var unit = new XElement(Xbrli + "unit", new XAttribute("id", id),
            new XElement(Xbrli + "measure", qname(MeasureQName(f.Unit!.Measure, profile))));
        if (f.Unit.Power != 1)
            unit.Element(Xbrli + "measure")!.Add(new XAttribute("power", f.Unit.Power));
        return unit;
    }

    private static XElement BuildFact(XbrlFact f, Dictionary<string, string> namespaces, Func<string, string> qname,
        Dictionary<string, string> contextIds, Dictionary<string, string> unitIds)
    {
        var (pfx, local) = SplitQName(qname(f.ConceptName));
        var el = new XElement(XNamespace.Get(namespaces[pfx!]) + local,
            new XAttribute("contextRef", contextIds[ContextKey(f)]));
        if (UnitPresent(f))
            el.Add(new XAttribute("unitRef", unitIds[UnitKey(f)]));
        if (f.Decimals.HasValue) el.Add(new XAttribute("decimals", f.Decimals.Value.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(f.Language))
            el.Add(new XAttribute(XNamespace.Xml + "lang", f.Language.ToLowerInvariant()));
        if (f.IsNil)
            el.Add(new XAttribute(Xbrli + "nil", "true"));
        else if (f.Value is not null)
            el.Value = f.Value.Value.ToString(CultureInfo.InvariantCulture);
        else if (f.TextValue is not null)
            el.Value = f.TextValue;
        return el;
    }

    /// <summary>Re-read facts from a xBRL 2.1 XML instance document.</summary>
    public static IReadOnlyList<XbrlFact> ReadFacts(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true
        };
        XDocument doc;
        try
        {
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);
            doc = XDocument.Load(xr);
        }
        catch (XmlException ex)
        {
            throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                $"XML instance rejected: {ex.Message}");
        }

        var root = doc.Root ?? throw new InvalidOperationException("Missing xbrl root.");
        var contexts = root.Elements(Xbrli + "context")
            .ToDictionary(c => (string)c.Attribute("id")!, c => c);
        var units = root.Elements(Xbrli + "unit")
            .ToDictionary(u => (string)u.Attribute("id")!, u => u);

        var facts = new List<XbrlFact>();
        foreach (var el in root.Elements())
        {
            if (el.Name.Namespace == Xbrli) continue; // skip context/unit/schema elements
            var ctxRef = (string?)el.Attribute("contextRef");
            if (ctxRef is null || !contexts.TryGetValue(ctxRef, out var ctx)) continue;

            var f = ParseFact(el, ctx, units);
            if (f is not null) facts.Add(f);
        }
        return facts;
    }

    private static XbrlFact? ParseFact(XElement el, XElement ctx, Dictionary<string, XElement> units)
    {
        var local = el.Name.LocalName;
        var pfx = el.GetPrefixOfNamespace(el.Name.Namespace) ?? "synthetic";
        var concept = $"{pfx}:{local}";

        var ident = ctx.Element(Xbrli + "entity")?.Element(Xbrli + "identifier");
        var scheme = (string?)ident?.Attribute("scheme") ?? "";
        var identifier = (string?)ident ?? "";

        var dims = new List<Dimension>();
        var entity = ctx.Element(Xbrli + "entity");
        if (entity?.Descendants(Xbrldi + "typedMember").Any() == true)
            throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                "Typed dimensions are not supported by this report model.");
        foreach (var ed in entity?.Elements(Xbrli + "segment").Elements(Xbrldi + "explicitMember") ?? Enumerable.Empty<XElement>())
        {
            var dimName = (string?)ed.Attribute("dimension") ?? "";
            dims.Add(new Dimension(dimName, ed.Value));
        }

        var periodEl = ctx.Element(Xbrli + "period");
        XbrlPeriod period;
        if (periodEl?.Element(Xbrli + "instant") is XElement inst)
            period = XbrlPeriod.OfInstant(DateOnly.Parse(inst.Value, CultureInfo.InvariantCulture));
        else
        {
            var startStr = periodEl?.Element(Xbrli + "startDate")?.Value ?? "";
            var endStr = periodEl?.Element(Xbrli + "endDate")?.Value ?? "";
            period = XbrlPeriod.OfDuration(
                DateOnly.Parse(startStr, CultureInfo.InvariantCulture),
                DateOnly.Parse(endStr, CultureInfo.InvariantCulture));
        }

        XbrlUnit? unit = null;
        CompoundUnit? compound = null;
        var unitRef = (string?)el.Attribute("unitRef");
        if (unitRef is not null && units.TryGetValue(unitRef, out var unitEl))
        {
            var divide = unitEl.Element(Xbrli + "divide");
            if (divide is not null)
            {
                var nums = divide.Element(Xbrli + "unitNumerator")?.Elements(Xbrli + "measure")
                    .Select(InternalMeasure).ToList() ?? new();
                var dens = divide.Element(Xbrli + "unitDenominator")?.Elements(Xbrli + "measure")
                    .Select(InternalMeasure).ToList() ?? new();
                compound = new CompoundUnit(nums, dens);
            }
            else
            {
                var m = unitEl.Element(Xbrli + "measure");
                var power = (int?)m?.Attribute("power") ?? 1;
                unit = new XbrlUnit(m is null ? "" : InternalMeasure(m), power);
            }
        }

        var nilAttr = (string?)el.Attribute(Xbrli + "nil");
        bool isNil = string.Equals(nilAttr, "true", StringComparison.OrdinalIgnoreCase);
        var decimalsStr = (string?)el.Attribute("decimals");
        int? decimals = decimalsStr is null || decimalsStr == "INF" ? null : int.Parse(decimalsStr, CultureInfo.InvariantCulture);
        var lang = (string?)el.Attribute(XNamespace.Xml + "lang");
        decimal? value = null;
        string? textValue = null;
        if (!isNil)
        {
            if (decimal.TryParse(el.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
            else textValue = el.Value;
        }

        return new XbrlFact
        {
            ConceptName = concept,
            EntityScheme = scheme,
            EntityIdentifier = identifier,
            Period = period,
            Unit = unit,
            CompoundUnit = compound,
            Dimensions = dims,
            Value = value,
            TextValue = textValue,
            IsNil = isNil,
            Decimals = decimals,
            Language = lang,
            ContributingSources = Array.Empty<SourceReference>()
        };
    }

    // ---- helpers --------------------------------------------------------------------

    private static string ContextKey(XbrlFact f) => string.Join('\u241F',
        f.EntityScheme, f.EntityIdentifier,
        f.Period.ToIsoString(),
        string.Join(",", f.Dimensions.OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => $"{d.Name}={d.MemberName}")));

    private static bool UnitPresent(XbrlFact f) => f.Unit is not null || f.CompoundUnit is not null;

    private static string UnitKey(XbrlFact f) => f.CompoundUnit is not null
        ? "cu:" + f.CompoundUnit.CanonicalKey
        : $"u:{f.Unit!.Measure}^{f.Unit.Power}";

    private static void AssertSupported(XbrlFact f)
    {
        // The synthetic model cannot express tuples, typed dimensions, fractional values,
        // or non-dimensional segments. Any such attempt is represented here as a concept
        // name that cannot be bound to a namespace prefix, or a conflicting unit form.
        if (f.Unit is not null && f.CompoundUnit is not null)
            throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                $"Fact on '{f.ConceptName}' carries both a simple and a compound unit.");
        if (SplitQName(f.ConceptName).prefix is null)
            throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                $"Concept '{f.ConceptName}' has no namespace prefix; tuples and unqualified constructs are not exportable.");
    }

    private static (string? prefix, string local) SplitQName(string qname)
    {
        var i = qname.IndexOf(':');
        return i <= 0 ? (null, qname) : (qname.Substring(0, i), qname.Substring(i + 1));
    }

    private static string PrefixFor(string qname) => SplitQName(qname).prefix ?? "x";

    private static string MeasureQName(string measure, FilingProfile profile)
    {
        if (measure.Contains(':')) return measure;
        if (profile.SupportedUnits.Contains(measure)) return "iso4217:" + measure;
        if (measure is "shares" or "pure" or "monetary" or "integer" or "percent") return "xbrli:" + measure;
        throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
            $"Unit measure '{measure}' is not qualified by the filing profile.");
    }

    private static string InternalMeasure(XElement element)
    {
        var value = element.Value;
        var separator = value.IndexOf(':');
        if (separator < 1) return value;
        var prefix = value[..separator];
        var ns = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        return ns == Iso4217.NamespaceName || ns == Xbrli.NamespaceName
            ? value[(separator + 1)..]
            : value;
    }

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Carries a stable product code for XBRL export failures (XBR05).</summary>
public sealed class XbrlExportException : Exception
{
    public string Code { get; }
    public XbrlExportException(string code, string message) : base(message) { Code = code; }
}
