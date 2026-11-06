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
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
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
            el.Add(new XAttribute(Xsi + "nil", "true"));
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
        if (root.Name != Xbrli + "xbrl")
            throw Unsupported("XML document root is not xbrli:xbrl.");
        var contexts = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var context in root.Elements(Xbrli + "context"))
        {
            var id = (string?)context.Attribute("id");
            if (string.IsNullOrEmpty(id) || !contexts.TryAdd(id, context))
                throw Unsupported("XML instance contains a context with a missing or duplicate id.");
        }
        var units = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var unit in root.Elements(Xbrli + "unit"))
        {
            var id = (string?)unit.Attribute("id");
            if (string.IsNullOrEmpty(id) || !units.TryAdd(id, unit))
                throw Unsupported("XML instance contains a unit with a missing or duplicate id.");
        }

        var facts = new List<XbrlFact>();
        foreach (var el in root.Elements())
        {
            if (el.Name == Xbrli + "context" || el.Name == Xbrli + "unit"
                || el.Name == Link + "schemaRef") continue;
            if (el.Name.Namespace == Xbrli || el.Name.Namespace == Link
                || el.Name.Namespace == Xbrldi || el.Name.Namespace == Xlink)
                throw Unsupported($"Root element '{el.Name}' is unsupported by this report model.");
            var ctxRef = (string?)el.Attribute("contextRef");
            if (string.IsNullOrEmpty(ctxRef) || !contexts.TryGetValue(ctxRef, out var ctx))
                throw Unsupported($"Root element '{el.Name}' is not a supported fact with a known contextRef.");

            var f = ParseFact(el, ctx, units);
            if (f is not null) facts.Add(f);
        }
        return facts;
    }

    private static XbrlFact? ParseFact(XElement el, XElement ctx, Dictionary<string, XElement> units)
    {
        EnsureOnlyAttributes(el, "contextRef", "unitRef", "decimals", "id",
            Xsi + "nil",
            XNamespace.Xml + "lang");
        if (el.Elements().Any())
            throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                $"Nested fact content on '{el.Name}' is unsupported by this report model.");

        var local = el.Name.LocalName;
        var pfx = el.GetPrefixOfNamespace(el.Name.Namespace) ?? "synthetic";
        var concept = $"{pfx}:{local}";

        var contextChildren = ctx.Elements().ToArray();
        EnsureOnlyAttributes(ctx, "id");
        EnsureNoSignificantText(ctx, "Context");
        if (contextChildren.Any(child => child.Name == Xbrli + "scenario"))
            throw Unsupported("Context scenario content is unsupported by this report model.");
        if (contextChildren.Length != 2
            || contextChildren.Count(child => child.Name == Xbrli + "entity") != 1
            || contextChildren.Count(child => child.Name == Xbrli + "period") != 1)
            throw Unsupported("Context contains missing or unmodeled children.");

        var entityElement = contextChildren.Single(child => child.Name == Xbrli + "entity");
        EnsureOnlyAttributes(entityElement);
        EnsureNoSignificantText(entityElement, "Entity");
        var entityChildren = entityElement.Elements().ToArray();
        var identifiers = entityChildren.Where(child => child.Name == Xbrli + "identifier").ToArray();
        if (identifiers.Length != 1 || entityChildren.Any(child => child.Name != Xbrli + "identifier"
                && child.Name != Xbrli + "segment"))
            throw Unsupported("Context entity contains missing identifier or unmodeled children.");
        var ident = identifiers[0];
        EnsureOnlyAttributes(ident, "scheme");
        if (ident.Elements().Any())
            throw Unsupported("Entity identifier cannot contain nested elements.");
        var scheme = (string?)ident?.Attribute("scheme") ?? "";
        var identifier = (string?)ident ?? "";

        var dims = new List<Dimension>();
        if (entityElement.Descendants(Xbrldi + "typedMember").Any())
            throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                "Typed dimensions are not supported by this report model.");
        foreach (var segment in entityElement.Elements(Xbrli + "segment"))
        {
            EnsureOnlyAttributes(segment);
            EnsureNoSignificantText(segment, "Entity segment");
            foreach (var child in segment.Elements())
            {
                if (child.Name != Xbrldi + "explicitMember")
                    throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                        $"Entity segment child '{child.Name}' is unsupported by this report model.");
                EnsureOnlyAttributes(child, "dimension");
                if (child.Elements().Any())
                    throw Unsupported("Explicit dimension members cannot contain nested elements.");
                var dimName = (string?)child.Attribute("dimension") ?? "";
                dims.Add(new Dimension(dimName, child.Value));
            }
        }

        var periodEl = contextChildren.Single(child => child.Name == Xbrli + "period");
        EnsureOnlyAttributes(periodEl);
        EnsureNoSignificantText(periodEl, "Period");
        var periodChildren = periodEl.Elements().ToArray();
        XbrlPeriod period;
        if (periodChildren.Length == 1 && periodChildren[0].Name == Xbrli + "instant")
        {
            var inst = periodChildren[0];
            EnsureOnlyAttributes(inst);
            if (inst.Elements().Any()) throw Unsupported("Instant values cannot contain nested elements.");
            period = XbrlPeriod.OfInstant(DateOnly.Parse(inst.Value, CultureInfo.InvariantCulture));
        }
        else if (periodChildren.Length == 2
            && periodChildren.Count(child => child.Name == Xbrli + "startDate") == 1
            && periodChildren.Count(child => child.Name == Xbrli + "endDate") == 1)
        {
            var start = periodChildren.Single(child => child.Name == Xbrli + "startDate");
            var end = periodChildren.Single(child => child.Name == Xbrli + "endDate");
            EnsureOnlyAttributes(start);
            EnsureOnlyAttributes(end);
            if (start.Elements().Any() || end.Elements().Any())
                throw Unsupported("Duration boundaries cannot contain nested elements.");
            period = XbrlPeriod.OfDuration(
                DateOnly.Parse(start.Value, CultureInfo.InvariantCulture),
                DateOnly.Parse(end.Value, CultureInfo.InvariantCulture));
        }
        else throw Unsupported("Context period has an unsupported structure.");

        XbrlUnit? unit = null;
        CompoundUnit? compound = null;
        var unitRef = (string?)el.Attribute("unitRef");
        if (unitRef is not null)
        {
            if (!units.TryGetValue(unitRef, out var unitEl))
                throw Unsupported($"Fact references unknown unit '{unitRef}'.");
            EnsureOnlyAttributes(unitEl, "id");
            EnsureNoSignificantText(unitEl, "Unit");
            var unitChildren = unitEl.Elements().ToArray();
            var divideElements = unitChildren.Where(child => child.Name == Xbrli + "divide").ToArray();
            if (divideElements.Length > 1)
                throw Unsupported("Unit contains more than one divide structure.");
            var divide = divideElements.SingleOrDefault();
            if (divide is not null)
            {
                EnsureOnlyAttributes(divide);
                EnsureNoSignificantText(divide, "Divided unit");
                if (unitChildren.Length != 1)
                    throw Unsupported("A divided unit cannot also contain simple measures or extra children.");
                var divideChildren = divide.Elements().ToArray();
                if (divideChildren.Length != 2
                    || divideChildren[0].Name != Xbrli + "unitNumerator"
                    || divideChildren[1].Name != Xbrli + "unitDenominator")
                    throw Unsupported("A divided unit must contain one numerator followed by one denominator.");
                var numeratorChildren = divideChildren[0].Elements().ToArray();
                var denominatorChildren = divideChildren[1].Elements().ToArray();
                EnsureOnlyAttributes(divideChildren[0]);
                EnsureOnlyAttributes(divideChildren[1]);
                EnsureNoSignificantText(divideChildren[0], "Unit numerator");
                EnsureNoSignificantText(divideChildren[1], "Unit denominator");
                if (numeratorChildren.Length == 0 || denominatorChildren.Length == 0
                    || numeratorChildren.Any(child => child.Name != Xbrli + "measure" || child.Elements().Any())
                    || denominatorChildren.Any(child => child.Name != Xbrli + "measure" || child.Elements().Any()))
                    throw Unsupported("A divided unit contains unsupported or missing measures.");
                foreach (var measure in numeratorChildren.Concat(denominatorChildren))
                    EnsureOnlyAttributes(measure);
                var nums = numeratorChildren.Select(InternalMeasure).ToList();
                var dens = denominatorChildren.Select(InternalMeasure).ToList();
                compound = new CompoundUnit(nums, dens);
            }
            else
            {
                if (unitChildren.Length != 1 || unitChildren[0].Name != Xbrli + "measure"
                    || unitChildren[0].Elements().Any())
                    throw Unsupported("A simple unit must contain exactly one measure.");
                var measure = unitChildren[0];
                EnsureOnlyAttributes(measure, "power");
                var power = (int?)measure.Attribute("power") ?? 1;
                unit = new XbrlUnit(InternalMeasure(measure), power);
            }
        }

        var xsiNil = (string?)el.Attribute(Xsi + "nil");
        bool isNil = xsiNil?.Trim() switch
        {
            null or "false" or "0" => false,
            "true" or "1" => true,
            _ => throw Unsupported("Fact nil attribute is not a valid XML Schema boolean.")
        };
        if (isNil && !string.IsNullOrWhiteSpace(el.Value))
            throw Unsupported("A nil fact cannot also carry text content.");
        var decimalsStr = (string?)el.Attribute("decimals");
        int? decimals = decimalsStr is null || decimalsStr == "INF" ? null : int.Parse(decimalsStr, CultureInfo.InvariantCulture);
        var lang = (string?)el.Attribute(XNamespace.Xml + "lang");
        decimal? value = null;
        string? textValue = null;
        if (!isNil)
        {
            // Without a taxonomy schema, unitRef/decimals are the available numeric
            // markers. A unitless lexical such as "123" remains text instead of being
            // silently retyped as a decimal. Numeric lexical overflow is rejected.
            var isNumeric = unitRef is not null || decimalsStr is not null;
            if (isNumeric)
            {
                if (!decimal.TryParse(el.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    throw Unsupported("Numeric fact lexical value is outside the supported exact decimal range.");
                value = parsed;
            }
            else
            {
                textValue = el.Value;
            }
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

    // Length-prefix every aspect so legal characters within xs:token identifiers
    // cannot become context separators or cause two distinct contexts to merge.
    private static string ContextKey(XbrlFact f)
    {
        var parts = new List<string>
        {
            "context-v2",
            f.EntityScheme,
            f.EntityIdentifier,
            f.Period.ToIsoString(),
            f.Dimensions.Count.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var dimension in f.Dimensions.OrderBy(dimension => dimension.Name, StringComparer.Ordinal))
        {
            parts.Add(dimension.Name);
            parts.Add(dimension.MemberName);
        }
        return string.Concat(parts.Select(part => part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));
    }

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

    private static void EnsureOnlyAttributes(XElement element, params XName[] allowed)
    {
        var allowedSet = allowed.ToHashSet();
        if (element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && !allowedSet.Contains(attribute.Name)))
            throw Unsupported($"Element '{element.Name}' contains an unsupported attribute.");
    }

    private static void EnsureNoSignificantText(XElement element, string label)
    {
        if (element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
            throw Unsupported($"{label} contains unsupported text content.");
    }

    private static XbrlExportException Unsupported(string message)
        => new(DiagnosticCodes.XbrUnsupportedConstruct, message);

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
