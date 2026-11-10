using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private const int MaximumTypedDimensionDepth = 128;
    private const int MaximumTypedDimensionValueCharacters = 1_000_000;

    private static readonly XNamespace Xbrli = "http://www.xbrl.org/2003/instance";
    private static readonly XNamespace Link = "http://www.xbrl.org/2003/linkbase";
    private static readonly XNamespace Xbrldi = "http://xbrl.org/2006/xbrldi";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Iso4217 = "http://www.xbrl.org/2003/iso4217";

    /// <summary>
    /// Streams a large xBRL-XML instance while retaining only one fact/context/unit
    /// element at a time. Contexts and units are deliberately emitted as one per
    /// fact, so no report-sized aspect dictionary is needed. Repeated equivalent
    /// contexts and units are valid XBRL and preserve the exact fact semantics.
    /// </summary>
    public static long WriteLargeReport(
        FilingProfile profile,
        IEnumerable<XbrlFact> facts,
        Stream destination,
        long maxOutputBytes,
        int maxNamespaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(destination);
        if (maxOutputBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        if (maxNamespaces < 1) throw new ArgumentOutOfRangeException(nameof(maxNamespaces));

        var namespaces = BuildLargeNamespaces(profile, maxNamespaces);
        string ResolveQName(string qname)
        {
            var (prefix, local) = SplitQName(qname);
            if (prefix is null || !namespaces.ContainsKey(prefix))
                throw Unsupported($"QName '{qname}' has no namespace URI in the filing profile.");
            try
            {
                XmlConvert.VerifyNCName(prefix);
                XmlConvert.VerifyNCName(local);
            }
            catch (XmlException)
            {
                throw Unsupported($"QName '{qname}' is invalid.");
            }
            return qname;
        }

        void ValidateFact(XbrlFact fact)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertSupported(fact);
            var concept = profile.FindConcept(fact.ConceptName)
                ?? throw Unsupported($"Concept '{fact.ConceptName}' is not defined in the filing profile.");
            if (!FactUnitParser.TryValidateForConcept(concept, profile, fact.Unit, fact.CompoundUnit, out var unitError))
                throw Unsupported(unitError);
            if (!fact.ValidateAgainst(profile, concept, out var reason)) throw Unsupported(reason);
            _ = ResolveQName(fact.ConceptName);
            foreach (var dimension in fact.Dimensions)
            {
                _ = ResolveQName(dimension.Name);
                if (dimension.IsExplicit) _ = ResolveQName(dimension.MemberName);
                else
                {
                    if (!profile.TypedDimensionDomains.TryGetValue(dimension.Name, out var typedDomain)
                        || typedDomain.DataTypeQName != "xs:string" || dimension.TypedValue is null)
                        throw Unsupported($"Typed dimension '{dimension.Name}' lacks supported profile metadata or a normalized value.");
                    _ = TypedDomainXName(profile, dimension.Name);
                }
            }
            foreach (var measure in (fact.Unit is null ? Enumerable.Empty<string>() : new[] { fact.Unit.Measure })
                         .Concat(fact.CompoundUnit?.Numerators ?? Array.Empty<string>())
                         .Concat(fact.CompoundUnit?.DenominatorList ?? Array.Empty<string>()))
                _ = ResolveQName(MeasureQName(measure, profile));
        }

        using var bounded = new BoundedXmlWriteStream(destination, maxOutputBytes);
        using (var writer = XmlWriter.Create(bounded, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            Indent = true,
            CloseOutput = false,
            OmitXmlDeclaration = false
        }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("xbrli", "xbrl", Xbrli.NamespaceName);
            foreach (var entry in namespaces.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                // WriteStartElement already binds the instance prefix on the root.
                if (StringComparer.Ordinal.Equals(entry.Key, "xbrli")) continue;
                writer.WriteAttributeString("xmlns", entry.Key, null, entry.Value);
            }
            foreach (var url in profile.TaxonomyUrls)
                new XElement(Link + "schemaRef",
                    new XAttribute(Xlink + "type", "simple"),
                    new XAttribute(Xlink + "href", url)).WriteTo(writer);
            writer.Flush();

            long ordinal = 0;
            // XBRL instance order requires all contexts before units and all units
            // before facts, so the private fact spool is read in three bounded passes.
            foreach (var fact in facts)
            {
                ValidateFact(fact);
                ordinal = checked(ordinal + 1);
                BuildContext("c" + ordinal.ToString(CultureInfo.InvariantCulture), fact, profile, ResolveQName).WriteTo(writer);
                writer.Flush();
            }

            ordinal = 0;
            foreach (var fact in facts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordinal = checked(ordinal + 1);
                if (UnitPresent(fact))
                    BuildUnit("u" + ordinal.ToString(CultureInfo.InvariantCulture), fact, profile, ResolveQName).WriteTo(writer);
                writer.Flush();
            }

            ordinal = 0;
            foreach (var fact in facts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordinal = checked(ordinal + 1);
                var contextId = "c" + ordinal.ToString(CultureInfo.InvariantCulture);
                var unitId = "u" + ordinal.ToString(CultureInfo.InvariantCulture);
                var contextIds = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ContextKey(fact)] = contextId
                };
                var unitIds = UnitPresent(fact)
                    ? new Dictionary<string, string>(StringComparer.Ordinal) { [UnitKey(fact)] = unitId }
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                BuildFact(fact, namespaces, ResolveQName, contextIds, unitIds).WriteTo(writer);
                writer.Flush();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return bounded.BytesWritten;
    }

    private static Dictionary<string, string> BuildLargeNamespaces(FilingProfile profile, int maxNamespaces)
    {
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xbrli"] = Xbrli.NamespaceName,
            ["link"] = Link.NamespaceName,
            ["xbrldi"] = Xbrldi.NamespaceName,
            ["xlink"] = Xlink.NamespaceName,
            ["iso4217"] = Iso4217.NamespaceName,
            ["xsi"] = Xsi.NamespaceName
        };

        void AddNamespace(string prefix, string uri)
        {
            try { XmlConvert.VerifyNCName(prefix); }
            catch (XmlException) { throw Unsupported($"Namespace prefix '{prefix}' is invalid."); }
            if (namespaces.TryGetValue(prefix, out var prior) && !StringComparer.Ordinal.Equals(prior, uri))
                throw Unsupported($"Namespace prefix '{prefix}' has conflicting URIs.");
            namespaces[prefix] = uri;
            if (namespaces.Count > maxNamespaces)
                throw Unsupported($"The report exceeded its {maxNamespaces} namespace budget.");
        }

        foreach (var (prefix, uri) in profile.NamespaceUris) AddNamespace(prefix, uri);
        foreach (var concept in profile.Concepts)
        {
            var (prefix, _) = SplitQName(concept.QualifiedName);
            if (prefix is null) throw Unsupported($"Concept '{concept.QualifiedName}' has no namespace prefix.");
            AddNamespace(prefix, concept.Namespace);
        }
        if (namespaces.Count > maxNamespaces)
            throw Unsupported($"The report exceeded its {maxNamespaces} namespace budget.");
        return namespaces;
    }

    public static string Serialize(FilingProfile profile, IReadOnlyList<XbrlFact> facts)
    {
        foreach (var f in facts)
        {
            AssertSupported(f);
            var concept = profile.FindConcept(f.ConceptName)
                ?? throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                    $"Concept '{f.ConceptName}' is not defined in the filing profile.");
            if (!FactUnitParser.TryValidateForConcept(concept, profile, f.Unit, f.CompoundUnit, out var unitError))
                throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                    unitError);
            if (!f.ValidateAgainst(profile, concept, out var reason))
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
                _ = ResolveQName(d.Name);
                if (d.IsExplicit) _ = ResolveQName(d.MemberName);
                else
                {
                    if (!profile.TypedDimensionDomains.TryGetValue(d.Name, out var typedDomain)
                        || typedDomain.DataTypeQName != "xs:string" || d.TypedValue is null)
                        throw Unsupported($"Typed dimension '{d.Name}' lacks supported profile metadata or a normalized value.");
                    _ = TypedDomainXName(profile, d.Name);
                }
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
            root.Add(BuildContext(id, f, profile, ResolveQName));
        foreach (var (id, f) in unitOrder)
            root.Add(BuildUnit(id, f, profile, ResolveQName));

        foreach (var f in facts)
            root.Add(BuildFact(f, namespaceUris, ResolveQName, contextIds, unitIds));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new Utf8StringWriter(sb),
                   new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
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

    private static XElement BuildContext(string id, XbrlFact f, FilingProfile profile, Func<string, string> qname)
    {
        var entity = new XElement(Xbrli + "entity",
            new XElement(Xbrli + "identifier",
                new XAttribute("scheme", f.EntityScheme), f.EntityIdentifier));
        if (f.Dimensions.Count > 0)
            entity.Add(new XElement(Xbrli + "segment", f.Dimensions.OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => d.IsExplicit
                    ? new XElement(Xbrldi + "explicitMember",
                        new XAttribute("dimension", qname(d.Name)), qname(d.MemberName))
                    : new XElement(Xbrldi + "typedMember",
                        new XAttribute("dimension", qname(d.Name)),
                        new XElement(TypedDomainXName(profile, d.Name), d.TypedValue
                            ?? throw Unsupported($"Typed dimension '{d.Name}' has no normalized value."))))));

        var period = f.Period.Kind == PeriodKind.Instant
            ? new XElement(Xbrli + "period", new XElement(Xbrli + "instant", Iso(f.Period.Instant)))
            : new XElement(Xbrli + "period",
                new XElement(Xbrli + "startDate", Iso(f.Period.Start)),
                // The model and xBRL-JSON use an exclusive end boundary. XBRL 2.1
                // interprets a date-only endDate as the end of that named day, so
                // emit the last included day to preserve the same instant.
                new XElement(Xbrli + "endDate", Iso(f.Period.End?.AddDays(-1))));

        return new XElement(Xbrli + "context",
            new XAttribute("id", id), entity, period);
    }

    private static XName TypedDomainXName(FilingProfile profile, string dimensionName)
    {
        if (!profile.TypedDimensionDomains.TryGetValue(dimensionName, out var metadata))
            throw Unsupported($"Typed dimension '{dimensionName}' has no selected-profile domain metadata.");
        var (prefix, local) = SplitQName(metadata.DomainElementQName);
        if (prefix is null || !profile.NamespaceUris.TryGetValue(prefix, out var uri))
            throw Unsupported($"Typed domain element '{metadata.DomainElementQName}' has no selected-profile namespace.");
        try { XmlConvert.VerifyNCName(local); }
        catch (XmlException) { throw Unsupported($"Typed domain element '{metadata.DomainElementQName}' is invalid."); }
        return XName.Get(local, uri);
    }

    private static XElement BuildUnit(string id, XbrlFact f, FilingProfile profile, Func<string, string> qname)
    {
        if (f.CompoundUnit is not null)
        {
            if (f.CompoundUnit.Numerators.Count == 0
                || f.CompoundUnit.Numerators.Count + f.CompoundUnit.DenominatorList.Count > FactUnitParser.MaximumMeasures)
                throw Unsupported("Compound unit must contain a numerator and stay within the supported measure limit.");
            if (f.CompoundUnit.DenominatorList.Count == 0)
            {
                var product = new XElement(Xbrli + "unit", new XAttribute("id", id));
                foreach (var item in f.CompoundUnit.Numerators.OrderBy(measure => measure, StringComparer.Ordinal))
                    product.Add(new XElement(Xbrli + "measure", qname(MeasureQName(item, profile))));
                return product;
            }
            var num = new XElement(Xbrli + "unitNumerator");
            foreach (var m in f.CompoundUnit.Numerators.OrderBy(measure => measure, StringComparer.Ordinal))
                num.Add(new XElement(Xbrli + "measure", qname(MeasureQName(m, profile))));
            var den = new XElement(Xbrli + "unitDenominator");
            foreach (var m in f.CompoundUnit.DenominatorList.OrderBy(measure => measure, StringComparer.Ordinal))
                den.Add(new XElement(Xbrli + "measure", qname(MeasureQName(m, profile))));
            return new XElement(Xbrli + "unit", new XAttribute("id", id),
                new XElement(Xbrli + "divide", num, den));
        }
        var measureName = qname(MeasureQName(f.Unit!.Measure, profile));
        var unit = new XElement(Xbrli + "unit", new XAttribute("id", id));
        if (f.Unit.Power > 0)
        {
            foreach (var _ in Enumerable.Range(0, f.Unit.Power))
                unit.Add(new XElement(Xbrli + "measure", measureName));
            return unit;
        }

        var numerator = new XElement(Xbrli + "unitNumerator",
            new XElement(Xbrli + "measure", qname("xbrli:pure")));
        var denominator = new XElement(Xbrli + "unitDenominator",
            Enumerable.Range(0, -f.Unit.Power).Select(_ => new XElement(Xbrli + "measure", measureName)));
        unit.Add(new XElement(Xbrli + "divide", numerator, denominator));
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
        => ReadFactsCore(xml, profile: null);

    /// <summary>
    /// Re-read facts while canonicalizing QName aliases against the selected profile.
    /// Prefixes are document-local syntax; the expanded namespace name identifies the
    /// concept, dimensions, members and custom unit measures.
    /// </summary>
    public static IReadOnlyList<XbrlFact> ReadFacts(string xml, FilingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ReadFactsCore(xml, profile);
    }

    private static IReadOnlyList<XbrlFact> ReadFactsCore(string xml, FilingProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(xml);
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

            var f = ParseFact(el, ctx, units, profile);
            if (f is not null) facts.Add(f);
        }
        return facts;
    }

    private static XbrlFact? ParseFact(
        XElement el,
        XElement ctx,
        Dictionary<string, XElement> units,
        FilingProfile? profile)
    {
        EnsureOnlyAttributes(el, "contextRef", "unitRef", "decimals", "id",
            Xsi + "nil",
            XNamespace.Xml + "lang");
        if (el.Elements().Any())
            throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                $"Nested fact content on '{el.Name}' is unsupported by this report model.");

        var local = el.Name.LocalName;
        var pfx = el.GetPrefixOfNamespace(el.Name.Namespace) ?? "synthetic";
        var concept = profile is null
            ? $"{pfx}:{local}"
            : CanonicalConceptName(el.Name.NamespaceName, local, profile);

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
        var dimensionNames = new HashSet<ExpandedConceptName>();
        foreach (var segment in entityElement.Elements(Xbrli + "segment"))
        {
            EnsureOnlyAttributes(segment);
            EnsureNoSignificantText(segment, "Entity segment");
            foreach (var child in segment.Elements())
            {
                if (child.Name == Xbrldi + "typedMember")
                {
                    EnsureOnlyAttributes(child, "dimension");
                    EnsureNoSignificantText(child, "Typed dimension member");
                    var typedDimName = (string?)child.Attribute("dimension") ?? "";
                    var expandedTypedDimension = ResolveExpandedQName(child, typedDimName);
                    var typedValues = child.Elements().ToArray();
                    if (typedValues.Length != 1)
                        throw Unsupported("A typed dimension member must contain exactly one typed value element.");
                    if (dims.Count >= FactDimensionParser.MaximumDimensions)
                        throw Unsupported($"A fact may have at most {FactDimensionParser.MaximumDimensions} taxonomy-defined dimensions.");
                    if (!dimensionNames.Add(expandedTypedDimension))
                        throw Unsupported($"Dimension '{typedDimName}' duplicates another dimension on the fact.");
                    if (profile is not null)
                        typedDimName = CanonicalProfileQName(child, typedDimName, profile);
                    var typedEvidence = typedValues[0];
                    string? normalizedValue = null;
                    if (profile is not null && profile.TypedDimensionDomains.TryGetValue(typedDimName, out _))
                    {
                        if (profile.TypedDimensionDomains[typedDimName].DataTypeQName != "xs:string"
                            || typedEvidence.Name != TypedDomainXName(profile, typedDimName))
                            throw Unsupported($"Typed dimension '{typedDimName}' does not match its supported xs:string domain element.");
                        EnsureOnlyAttributes(typedEvidence);
                        if (typedEvidence.Elements().Any())
                            throw Unsupported($"Typed dimension '{typedDimName}' must contain simple xs:string text.");
                        normalizedValue = typedEvidence.Value;
                        if (normalizedValue.Length > MaximumTypedDimensionValueCharacters)
                            throw Unsupported($"Typed dimension '{typedDimName}' exceeds the {MaximumTypedDimensionValueCharacters}-character value limit.");
                    }
                    dims.Add(new Dimension(typedDimName, "", IsExplicit: false)
                    {
                        TypedValue = normalizedValue,
                        TypedValueJson = CanonicalizeTypedValue(typedEvidence),
                        AxisExpandedName = $"{{{expandedTypedDimension.NamespaceUri}}}{expandedTypedDimension.LocalName}"
                    });
                    continue;
                }
                if (child.Name != Xbrldi + "explicitMember")
                    throw new XbrlExportException(DiagnosticCodes.XbrUnsupportedConstruct,
                        $"Entity segment child '{child.Name}' is unsupported by this report model.");
                EnsureOnlyAttributes(child, "dimension");
                if (child.Elements().Any())
                    throw Unsupported("Explicit dimension members cannot contain nested elements.");
                var dimName = (string?)child.Attribute("dimension") ?? "";
                var memberName = child.Value;
                var expandedDimension = ResolveExpandedQName(child, dimName);
                var expandedMember = ResolveExpandedQName(child, memberName);
                if (dims.Count >= FactDimensionParser.MaximumDimensions)
                    throw Unsupported($"A fact may have at most {FactDimensionParser.MaximumDimensions} taxonomy-defined dimensions.");
                if (!dimensionNames.Add(expandedDimension))
                    throw Unsupported($"Dimension '{dimName}' duplicates another dimension on the fact.");
                if (profile is not null)
                {
                    dimName = CanonicalProfileQName(child, dimName, profile);
                    memberName = CanonicalProfileQName(child, memberName, profile);
                }
                dims.Add(new Dimension(dimName, memberName)
                {
                    AxisExpandedName = $"{{{expandedDimension.NamespaceUri}}}{expandedDimension.LocalName}",
                    MemberExpandedName = $"{{{expandedMember.NamespaceUri}}}{expandedMember.LocalName}"
                });
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
            var inclusiveEnd = DateOnly.Parse(end.Value, CultureInfo.InvariantCulture);
            if (inclusiveEnd == DateOnly.MaxValue)
                throw Unsupported("Duration endDate cannot be converted to the supported exclusive date boundary.");
            period = XbrlPeriod.OfDuration(
                DateOnly.Parse(start.Value, CultureInfo.InvariantCulture),
                inclusiveEnd.AddDays(1));
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
                    || numeratorChildren.Length + denominatorChildren.Length > FactUnitParser.MaximumMeasures
                    || numeratorChildren.Any(child => child.Name != Xbrli + "measure" || child.Elements().Any())
                    || denominatorChildren.Any(child => child.Name != Xbrli + "measure" || child.Elements().Any()))
                    throw Unsupported("A divided unit contains unsupported or missing measures.");
                foreach (var measure in numeratorChildren.Concat(denominatorChildren))
                    EnsureOnlyAttributes(measure);
                var nums = numeratorChildren.Select(MeasureName).ToList();
                var dens = denominatorChildren.Select(MeasureName).ToList();
                if (nums.Intersect(dens, StringComparer.Ordinal).Any())
                    throw Unsupported("A divided unit must be expressed in its simplest form.");
                if (nums.Count == 1 && StringComparer.Ordinal.Equals(nums[0], "pure")
                    && dens.All(measure => StringComparer.Ordinal.Equals(measure, dens[0])))
                    unit = new XbrlUnit(dens[0], -dens.Count);
                else
                    compound = new CompoundUnit(nums, dens);
            }
            else
            {
                if (unitChildren.Length == 0 || unitChildren.Length > FactUnitParser.MaximumMeasures
                    || unitChildren.Any(child => child.Name != Xbrli + "measure" || child.Elements().Any()))
                    throw Unsupported("A product unit must contain one or more supported measures.");
                if (unitChildren.Length == 1)
                {
                    var measure = unitChildren[0];
                    EnsureOnlyAttributes(measure);
                    unit = new XbrlUnit(MeasureName(measure));
                }
                else
                {
                    foreach (var measure in unitChildren) EnsureOnlyAttributes(measure);
                    var measures = unitChildren.Select(MeasureName).ToList();
                    if (measures.All(item => StringComparer.Ordinal.Equals(item, measures[0])))
                        unit = new XbrlUnit(measures[0], measures.Count);
                    else
                        compound = new CompoundUnit(measures);
                }
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
        int? decimals = null;
        if (decimalsStr is not null && decimalsStr != "INF")
        {
            if (!int.TryParse(decimalsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw Unsupported("Fact decimals must be INF or an integer in the supported range [-30, 30].");
            if (parsed is < -30 or > 30)
                throw Unsupported("Fact decimals are outside the supported range [-30, 30].");
            decimals = parsed;
        }
        if (isNil && decimalsStr is not null)
            throw Unsupported("Nil XBRL facts cannot carry a decimals attribute.");
        var lang = (string?)el.Attribute(XNamespace.Xml + "lang");
        if (lang is not null && !Bcp47LanguageTagValidator.IsWellFormed(lang))
            throw Unsupported($"Language '{lang}' is not a well-formed BCP 47 language tag.");
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
                if (!XbrlNumericLexicalParser.TryParseExact(el.Value, out var parsed))
                    throw Unsupported("Numeric fact lexical value is invalid or outside the supported exact decimal range.");
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

        string MeasureName(XElement measure) => profile is null
            ? InternalMeasure(measure)
            : CanonicalProfileQName(measure, measure.Value, profile, normalizeStandardMeasures: true);
    }

    // ---- helpers --------------------------------------------------------------------

    private sealed record CanonicalTypedAttribute(string Name, string Value);

    private sealed record CanonicalTypedContent(
        string Kind,
        string? Value = null,
        string? Target = null,
        string? Data = null,
        CanonicalTypedElement? Element = null);

    private sealed record CanonicalTypedElement(
        string Name,
        IReadOnlyList<CanonicalTypedAttribute> Attributes,
        IReadOnlyList<CanonicalTypedContent> Content);

    private static string CanonicalizeTypedValue(XElement element)
    {
        static string ExpandedName(XName name) => $"{{{name.NamespaceName}}}{name.LocalName}";

        var retainedCharacters = 0;
        void CountCharacters(int count)
        {
            retainedCharacters = checked(retainedCharacters + count);
            if (retainedCharacters > MaximumTypedDimensionValueCharacters)
                throw Unsupported($"Typed dimension value exceeds the {MaximumTypedDimensionValueCharacters}-character evidence limit.");
        }

        CanonicalTypedElement CanonicalElement(XElement value, int depth)
        {
            if (depth > MaximumTypedDimensionDepth)
                throw Unsupported($"Typed dimension value exceeds the {MaximumTypedDimensionDepth}-element nesting limit.");
            var name = ExpandedName(value.Name);
            CountCharacters(name.Length);
            var attributes = new List<CanonicalTypedAttribute>();
            foreach (var attribute in value.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
                         .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                         .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
            {
                CountCharacters(ExpandedName(attribute.Name).Length + attribute.Value.Length);
                attributes.Add(new CanonicalTypedAttribute(ExpandedName(attribute.Name), attribute.Value));
            }

            var content = new List<CanonicalTypedContent>();
            foreach (var node in value.Nodes())
            {
                switch (node)
                {
                    case XElement child:
                        content.Add(new CanonicalTypedContent("element", Element: CanonicalElement(child, depth + 1)));
                        break;
                    case XText text:
                        CountCharacters(text.Value.Length);
                        content.Add(new CanonicalTypedContent("text", text.Value));
                        break;
                    case XComment comment:
                        CountCharacters(comment.Value.Length);
                        content.Add(new CanonicalTypedContent("comment", comment.Value));
                        break;
                    case XProcessingInstruction instruction:
                        CountCharacters(instruction.Target.Length + instruction.Data.Length);
                        content.Add(new CanonicalTypedContent(
                            "processing-instruction", Target: instruction.Target, Data: instruction.Data));
                        break;
                    default:
                        throw Unsupported("Typed dimension value contains an unsupported XML node.");
                }
            }
            return new CanonicalTypedElement(name, attributes, content);
        }

        return JsonSerializer.Serialize(CanonicalElement(element, 1));
    }

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
            parts.Add(dimension.IsExplicit ? "explicit" : "typed");
            parts.Add(dimension.TypedValue ?? "");
            parts.Add(dimension.TypedValueJson ?? "");
        }
        return string.Concat(parts.Select(part => part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));
    }

    private static bool UnitPresent(XbrlFact f) => f.Unit is not null || f.CompoundUnit is not null;

    private static string UnitKey(XbrlFact f)
    {
        if (f.CompoundUnit is not null)
        {
            var denominators = f.CompoundUnit.DenominatorList;
            if (denominators.Count == 0 && f.CompoundUnit.Numerators.Count > 0
                && f.CompoundUnit.Numerators.All(measure => StringComparer.Ordinal.Equals(measure, f.CompoundUnit.Numerators[0])))
                return $"u:{f.CompoundUnit.Numerators[0]}^{f.CompoundUnit.Numerators.Count}";
            if (f.CompoundUnit.Numerators.Count == 1
                && StringComparer.Ordinal.Equals(f.CompoundUnit.Numerators[0], "pure")
                && denominators.Count > 0
                && denominators.All(measure => StringComparer.Ordinal.Equals(measure, denominators[0])))
                return $"u:{denominators[0]}^{-denominators.Count}";
            return "cu:" + f.CompoundUnit.CanonicalKey;
        }
        return $"u:{f.Unit!.Measure}^{f.Unit.Power}";
    }

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
        if (measure is "shares" or "pure") return "xbrli:" + measure;
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

    private static string CanonicalConceptName(
        string namespaceUri, string localName, FilingProfile profile)
    {
        var concept = profile.Concepts.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Namespace, namespaceUri)
            && StringComparer.Ordinal.Equals(candidate.LocalName, localName));
        if (concept is not null) return concept.QualifiedName;
        var prefix = FindProfilePrefix(profile, namespaceUri);
        if (prefix is null)
            throw Unsupported($"Concept namespace '{namespaceUri}' is not declared by the selected filing profile.");
        return $"{prefix}:{localName}";
    }

    private static string CanonicalProfileQName(
        XElement scope, string qname, FilingProfile profile, bool normalizeStandardMeasures = false)
    {
        var expanded = ResolveExpandedQName(scope, qname);
        if (normalizeStandardMeasures && (expanded.NamespaceUri == Iso4217.NamespaceName || expanded.NamespaceUri == Xbrli.NamespaceName))
            return expanded.LocalName;

        var canonicalPrefix = FindProfilePrefix(profile, expanded.NamespaceUri);
        if (canonicalPrefix is null)
            throw Unsupported($"QName '{qname}' uses a namespace that is not declared by the selected filing profile.");
        return $"{canonicalPrefix}:{expanded.LocalName}";
    }

    private static ExpandedConceptName ResolveExpandedQName(XElement scope, string qname)
    {
        var value = qname.Trim();
        var separator = value.IndexOf(':');
        if (separator != value.LastIndexOf(':'))
            throw Unsupported($"QName '{qname}' is invalid.");
        var prefix = separator < 0 ? null : value[..separator];
        var localName = separator < 0 ? value : value[(separator + 1)..];
        try
        {
            XmlConvert.VerifyNCName(localName);
            if (prefix is not null) XmlConvert.VerifyNCName(prefix);
        }
        catch (XmlException)
        {
            throw Unsupported($"QName '{qname}' is invalid.");
        }

        var namespaceUri = prefix is null
            ? scope.GetDefaultNamespace().NamespaceName
            : scope.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        if (string.IsNullOrEmpty(namespaceUri))
            throw Unsupported($"QName '{qname}' has no namespace declaration in the XML instance.");
        return new ExpandedConceptName(namespaceUri, localName);
    }

    private static string? FindProfilePrefix(FilingProfile profile, string namespaceUri)
    {
        var prefixes = profile.NamespaceUris
            .Where(entry => StringComparer.Ordinal.Equals(entry.Value, namespaceUri))
            .Select(entry => entry.Key)
            .Concat(profile.Concepts
                .Where(concept => StringComparer.Ordinal.Equals(concept.Namespace, namespaceUri))
                .Select(concept => SplitQName(concept.QualifiedName).prefix)
                .OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return prefixes.FirstOrDefault();
    }

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class BoundedXmlWriteStream(Stream inner, long maximumBytes) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten += buffer.Length;
        }
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
        private void EnsureCapacity(int count)
        {
            if (count > maximumBytes - BytesWritten)
                throw new InvalidDataException($"The xBRL-XML report exceeded its {maximumBytes} byte budget.");
        }
    }

    private sealed class Utf8StringWriter(StringBuilder builder)
        : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}

/// <summary>Carries a stable product code for XBRL export failures (XBR05).</summary>
public sealed class XbrlExportException : Exception
{
    public string Code { get; }
    public XbrlExportException(string code, string message) : base(message) { Code = code; }
}
