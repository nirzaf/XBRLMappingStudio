using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Xml;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL;

/// <summary>A parsed xBRL-JSON fact together with its exact facts-object member key.</summary>
public sealed record OimFactEntry(string Locator, XbrlFact Fact);

/// <summary>Reads and writes the supported XBRL Open Information Model JSON subset.</summary>
public static class OimSerializer
{
    private const string DocumentType = "https://xbrl.org/2021/xbrl-json";
    private const string Iso4217 = "http://www.xbrl.org/2003/iso4217";
    private const string Xbrli = "http://www.xbrl.org/2003/instance";
    private static readonly HashSet<string> SupportedReportMembers = new(StringComparer.Ordinal)
    {
        "documentInfo", "facts"
    };
    private static readonly HashSet<string> SupportedDocumentInfoMembers = new(StringComparer.Ordinal)
    {
        "documentType", "namespaces", "taxonomy"
    };
    private static readonly HashSet<string> SupportedFactMembers = new(StringComparer.Ordinal)
    {
        "value", "decimals", "dimensions", "links"
    };

    public static string Serialize(FilingProfile profile, IReadOnlyList<XbrlFact> facts)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(facts);
        foreach (var fact in facts)
        {
            var concept = profile.FindConcept(fact.ConceptName)
                ?? throw Unsupported($"Concept '{fact.ConceptName}' is not defined in the filing profile.");
            if (!FactUnitParser.TryValidateForConcept(concept, profile, fact.Unit, fact.CompoundUnit, out var unitError))
                throw Unsupported(unitError);
            if (!fact.ValidateAgainst(profile, concept, out var reason)) throw Unsupported(reason);
        }
        var ns = BuildNamespaces(profile, facts);
        var docInfo = new JsonObject
        {
            ["documentType"] = DocumentType,
            ["namespaces"] = new JsonObject(ns.Output.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
            ["taxonomy"] = new JsonArray(profile.TaxonomyUrls
                .Select(url => (JsonNode?)JsonValue.Create(url)).ToArray())
        };
        var report = new JsonObject
        {
            ["documentInfo"] = docInfo,
            ["facts"] = new JsonObject()
        };
        var factObject = (JsonObject)report["facts"]!;
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fact in facts.OrderBy(f => f.SemanticKey(), StringComparer.Ordinal))
        {
            var key = fact.SemanticKey();
            occurrences.TryGetValue(key, out var occurrence);
            occurrences[key] = ++occurrence;
            var id = CreateFactLocator(key, occurrence);
            factObject[id] = FactToJson(fact, ns);
        }

        return report.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// Writes the same deterministic OIM JSON shape as <see cref="Serialize"/> while
    /// retaining only one fact node at a time. The caller supplies semantic-order
    /// occurrences from its disk-backed index so IDs remain stable without a
    /// row-count-sized in-memory occurrence map.
    /// </summary>
    public static long WriteLargeReport(
        FilingProfile profile,
        IEnumerable<XbrlFact> facts,
        IEnumerable<(XbrlFact Fact, int Occurrence)> semanticOrder,
        Stream destination,
        long maxOutputBytes,
        int maxNamespaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(semanticOrder);
        ArgumentNullException.ThrowIfNull(destination);
        if (maxOutputBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        if (maxNamespaces < 1) throw new ArgumentOutOfRangeException(nameof(maxNamespaces));

        foreach (var fact in facts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concept = profile.FindConcept(fact.ConceptName)
                ?? throw Unsupported($"Concept '{fact.ConceptName}' is not defined in the filing profile.");
            if (!FactUnitParser.TryValidateForConcept(concept, profile, fact.Unit, fact.CompoundUnit, out var unitError))
                throw Unsupported(unitError);
            if (!fact.ValidateAgainst(profile, concept, out var reason)) throw Unsupported(reason);
        }
        var ns = BuildNamespaces(profile, facts, maxNamespaces, cancellationToken);
        using var bounded = new BoundedWriteStream(destination, maxOutputBytes);
        using (var writer = new Utf8JsonWriter(bounded, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("documentInfo");
            writer.WriteStartObject();
            writer.WriteString("documentType", DocumentType);
            writer.WritePropertyName("namespaces");
            writer.WriteStartObject();
            foreach (var entry in ns.Output.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                writer.WriteString(entry.Key, entry.Value);
            writer.WriteEndObject();
            writer.WritePropertyName("taxonomy");
            writer.WriteStartArray();
            foreach (var url in profile.TaxonomyUrls) writer.WriteStringValue(url);
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("facts");
            writer.WriteStartObject();
            foreach (var (fact, occurrence) in semanticOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (occurrence < 1) throw new InvalidDataException("A fact occurrence index must be positive.");
                var id = GetFactLocator(fact, occurrence);
                writer.WritePropertyName(id);
                FactToJson(fact, ns).WriteTo(writer);
                writer.Flush();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }
        return bounded.BytesWritten;
    }

    /// <summary>
    /// Returns the exact facts-object keys Serialize assigns to each input occurrence.
    /// The returned array is aligned with the supplied facts; duplicate semantic facts
    /// receive distinct deterministic keys in stable input order, matching Serialize's
    /// stable semantic sort. No report fields or bytes are changed.
    /// </summary>
    public static IReadOnlyList<string> GetFactLocators(IReadOnlyList<XbrlFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var locators = new string[facts.Count];
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (fact, index) in facts.Select((fact, index) => (fact, index))
            .OrderBy(item => item.fact.SemanticKey(), StringComparer.Ordinal))
        {
            var key = fact.SemanticKey();
            occurrences.TryGetValue(key, out var occurrence);
            occurrences[key] = ++occurrence;
            locators[index] = CreateFactLocator(key, occurrence);
        }
        return locators;
    }

    /// <summary>Builds an exact locator-to-original-fact index for an in-memory report.</summary>
    public static IReadOnlyDictionary<string, XbrlFact> GetFactLocatorMap(IReadOnlyList<XbrlFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var locators = GetFactLocators(facts);
        var map = new Dictionary<string, XbrlFact>(StringComparer.Ordinal);
        for (var index = 0; index < facts.Count; index++)
            map.Add(locators[index], facts[index]);
        return map;
    }

    /// <summary>Returns the deterministic facts-object key for one fact occurrence.</summary>
    public static string GetFactLocator(XbrlFact fact, int occurrence)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (occurrence < 1) throw new ArgumentOutOfRangeException(nameof(occurrence));
        return CreateFactLocator(fact.SemanticKey(), occurrence);
    }

    private static string CreateFactLocator(string semanticKey, int occurrence)
        => "f" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(semanticKey + "\u001f" + occurrence.ToString(CultureInfo.InvariantCulture))))
            .ToLowerInvariant();

    private static JsonObject FactToJson(XbrlFact fact, NamespaceContext ns)
    {
        if (fact.Value is not null && fact.TextValue is not null)
            throw Unsupported("A fact cannot have both a numeric and text value.");
        if (!fact.IsNil && fact.Value is null && fact.TextValue is null)
            throw Unsupported($"Fact '{fact.ConceptName}' has no value.");
        if (fact.Decimals is not null && (fact.IsNil || fact.Value is null))
            throw Unsupported("Decimals can only be written for a non-nil numeric fact.");

        var dimensions = new JsonObject
        {
            ["concept"] = ns.QName(fact.ConceptName),
            ["entity"] = ns.Entity(fact.EntityScheme, fact.EntityIdentifier),
            ["period"] = PeriodToJson(fact.Period)
        };
        if (fact.Unit is not null && fact.CompoundUnit is not null)
            throw Unsupported("A fact cannot have both a simple and a compound unit.");
        if (fact.Unit is not null
            && !(StringComparer.Ordinal.Equals(fact.Unit.Measure, "pure") && fact.Unit.Power == 1))
            dimensions["unit"] = UnitToJson(fact.Unit, ns);
        if (fact.CompoundUnit is not null)
            dimensions["unit"] = CompoundUnitToJson(fact.CompoundUnit, ns);
        if (fact.Language is not null)
            dimensions["language"] = fact.Language.ToLowerInvariant();
        foreach (var dimension in fact.Dimensions)
        {
            var name = ns.QName(dimension.Name);
            if (name.StartsWith("xbrl:", StringComparison.Ordinal))
                throw Unsupported("A taxonomy-defined dimension cannot use the reserved xbrl prefix.");
            if (dimensions.ContainsKey(name))
                throw Unsupported($"Fact '{fact.ConceptName}' repeats dimension '{name}'.");
            if (dimension.IsExplicit)
                dimensions[name] = JsonValue.Create(ns.QName(dimension.MemberName));
            else
            {
                dimensions[name] = JsonValue.Create(dimension.TypedValue
                    ?? throw Unsupported($"Typed dimension '{dimension.Name}' has no supported normalized value."));
            }
        }

        var result = new JsonObject
        {
            ["value"] = fact.IsNil ? null
                : fact.Value is not null
                    ? JsonValue.Create(fact.Value.Value.ToString(CultureInfo.InvariantCulture))
                    : JsonValue.Create(fact.TextValue),
            ["dimensions"] = dimensions
        };
        if (fact.Decimals is not null) result["decimals"] = fact.Decimals.Value;
        return result;
    }

    private static string PeriodToJson(XbrlPeriod period)
    {
        const string format = "yyyy-MM-dd'T'HH:mm:ss";
        return period.Kind switch
        {
            // XBRL date-only instants denote the end of that date; OIM expresses the
            // equivalent exclusive boundary at midnight on the following date.
            PeriodKind.Instant when period.Instant is not null
                => AtMidnight(period.Instant.Value.AddDays(1)).ToString(format, CultureInfo.InvariantCulture),
            PeriodKind.Duration when period.Start is not null && period.End is not null
                => AtMidnight(period.Start.Value).ToString(format, CultureInfo.InvariantCulture) + "/"
                    + AtMidnight(period.End.Value).ToString(format, CultureInfo.InvariantCulture),
            _ => throw Unsupported("Fact period is incomplete.")
        };
    }

    private static DateTime AtMidnight(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    private static string UnitToJson(XbrlUnit unit, NamespaceContext ns)
    {
        if (unit.Power is 0 or < -64 or > 64)
            throw Unsupported($"Unit power '{unit.Power}' is outside the supported range.");
        var qname = ns.Measure(unit.Measure);
        return unit.Power > 0
            ? string.Join("*", Enumerable.Repeat(qname, unit.Power))
            : ns.Measure("pure") + "/" + string.Join("*", Enumerable.Repeat(qname, -unit.Power));
    }

    private static string CompoundUnitToJson(CompoundUnit unit, NamespaceContext ns)
    {
        if (unit.Numerators.Count == 0 || unit.Numerators.Count + unit.DenominatorList.Count > 64)
            throw Unsupported("Compound unit must have a numerator and stay within the supported measure limit.");
        if (unit.DenominatorList.Intersect(unit.Numerators, StringComparer.Ordinal).Any())
            throw Unsupported("Compound units must cancel shared measures before division.");
        var numerator = string.Join("*", unit.Numerators.Select(ns.Measure).OrderBy(x => x, StringComparer.Ordinal));
        var denominator = string.Join("*", unit.DenominatorList.Select(ns.Measure).OrderBy(x => x, StringComparer.Ordinal));
        return denominator.Length == 0 ? numerator : numerator + "/" + denominator;
    }

    /// <summary>Read the supported OIM fact subset. Unsupported report constructs fail closed.</summary>
    public static IReadOnlyList<XbrlFact> ReadFacts(string json)
        => ReadFactEntries(json).Select(entry => entry.Fact).ToArray();

    /// <summary>Read facts using selected taxonomy metadata to distinguish numeric pure facts from text.</summary>
    public static IReadOnlyList<XbrlFact> ReadFacts(string json, FilingProfile profile)
        => ReadFactEntries(json, profile).Select(entry => entry.Fact).ToArray();

    /// <summary>
    /// Reads facts while preserving their exact xBRL-JSON object member keys. Worker
    /// findings use these keys as fact locators; they are not derived from report order.
    /// </summary>
    public static IReadOnlyList<OimFactEntry> ReadFactEntries(string json)
        => ReadFactEntriesCore(json, profile: null);

    /// <summary>Read facts using the selected taxonomy's numeric typing and QName metadata.</summary>
    public static IReadOnlyList<OimFactEntry> ReadFactEntries(string json, FilingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ReadFactEntriesCore(json, profile);
    }

    private static IReadOnlyList<OimFactEntry> ReadFactEntriesCore(string json, FilingProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        EnsureUniqueKeys(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("documentInfo", out var info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("documentType", out var documentType)
            || documentType.GetString() != DocumentType)
            throw Unsupported("Document is not an xBRL-JSON 1.0 report.");
        RejectUnsupportedMembers(root, SupportedReportMembers, "report");
        RejectUnsupportedMembers(info, SupportedDocumentInfoMembers, "documentInfo");
        if (!info.TryGetProperty("namespaces", out var namespacesNode)
            || namespacesNode.ValueKind != JsonValueKind.Object)
            throw Unsupported("xBRL-JSON report has no namespace map.");
        if (!info.TryGetProperty("taxonomy", out var taxonomyNode) || taxonomyNode.ValueKind != JsonValueKind.Array)
            throw Unsupported("xBRL-JSON documentInfo.taxonomy must be an array.");
        if (taxonomyNode.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw Unsupported("xBRL-JSON taxonomy references must be strings.");
        var namespaces = namespacesNode.EnumerateObject().ToDictionary(
            p => p.Name, p => p.Value.GetString() ?? throw Unsupported("Namespace URI must be a string."),
            StringComparer.Ordinal);
        if (!root.TryGetProperty("facts", out var factsNode)) return Array.Empty<OimFactEntry>();
        if (factsNode.ValueKind != JsonValueKind.Object)
            throw Unsupported("The xBRL-JSON facts member must be an object.");

        var facts = new List<OimFactEntry>();
        foreach (var property in factsNode.EnumerateObject())
        {
            VerifyNcName(property.Name, "fact identifier");
            var fact = property.Value;
            if (fact.ValueKind != JsonValueKind.Object
                || !fact.TryGetProperty("dimensions", out var dimensions)
                || dimensions.ValueKind != JsonValueKind.Object
                || !fact.TryGetProperty("value", out var value))
                throw Unsupported($"Fact '{property.Name}' is incomplete.");
            RejectUnsupportedMembers(fact, SupportedFactMembers, $"fact '{property.Name}'");
            if (fact.TryGetProperty("links", out _)
                || dimensions.TryGetProperty("noteId", out _))
                throw Unsupported("Fact notes and links are not supported by this mapping model.");

            var concept = RequiredString(dimensions, "concept");
            var expandedConcept = ExpandQName(concept, namespaces);
            var conceptMetadata = profile is null
                ? null
                : profile.Concepts.FirstOrDefault(candidate =>
                    StringComparer.Ordinal.Equals(candidate.Namespace, expandedConcept.Namespace)
                    && StringComparer.Ordinal.Equals(candidate.LocalName, expandedConcept.LocalName));
            var canonicalConcept = conceptMetadata?.QualifiedName
                ?? CanonicalProfileQName(expandedConcept, concept, profile);
            var entity = RequiredString(dimensions, "entity");
            var colon = entity.IndexOf(':');
            if (colon <= 0 || colon == entity.Length - 1)
                throw Unsupported("Entity must be a prefixed SQName.");
            var entityPrefix = entity[..colon];
            if (!namespaces.TryGetValue(entityPrefix, out var entityScheme))
                throw Unsupported($"Entity prefix '{entityPrefix}' is not declared.");
            var entityIdentifier = entity[(colon + 1)..];
            if (entityIdentifier.Any(char.IsWhiteSpace))
                throw Unsupported("Entity identifier contains whitespace and cannot be represented as an SQName.");

            var periodText = RequiredString(dimensions, "period");
            var period = ParsePeriod(periodText);
            var unit = dimensions.TryGetProperty("unit", out var unitValue)
                ? ParseUnit(unitValue.GetString() ?? throw Unsupported("Unit must be a string."), namespaces, profile)
                : (Unit: (XbrlUnit?)null, CompoundUnit: (CompoundUnit?)null);
            var reportedDimensions = new List<Dimension>();
            var reportedDimensionNames = new HashSet<ExpandedConceptName>();
            string? language = null;
            foreach (var dimension in dimensions.EnumerateObject())
            {
                if (dimension.Name is "concept" or "entity" or "period" or "unit") continue;
                if (dimension.Name == "language")
                {
                    var languageTag = dimension.Value.GetString()
                        ?? throw Unsupported("Language dimension must be a string.");
                    if (!Bcp47LanguageTagValidator.IsWellFormed(languageTag))
                        throw Unsupported($"Language '{languageTag}' is not a well-formed BCP 47 language tag.");
                    if (!StringComparer.Ordinal.Equals(languageTag, languageTag.ToLowerInvariant()))
                        throw Unsupported("The xBRL-JSON language dimension must use lowercase BCP 47 tags.");
                    language = languageTag;
                    continue;
                }
                if (dimension.Name == "noteId")
                    throw Unsupported("Note facts are not supported by this mapping model.");
                var expandedDimension = ExpandQName(dimension.Name, namespaces);
                if (reportedDimensionNames.Count >= FactDimensionParser.MaximumDimensions)
                    throw Unsupported($"A fact may have at most {FactDimensionParser.MaximumDimensions} taxonomy-defined dimensions.");
                if (!reportedDimensionNames.Add(new ExpandedConceptName(
                        expandedDimension.Namespace, expandedDimension.LocalName)))
                    throw Unsupported($"Dimension '{dimension.Name}' duplicates another dimension on the fact.");
                var dimensionName = CanonicalProfileQName(expandedDimension, dimension.Name, profile);
                string member;
                bool isExplicit;
                if (dimension.Value.ValueKind == JsonValueKind.Null)
                    throw Unsupported("Nilled typed dimensions are not supported by this mapping model.");
                if (dimension.Value.ValueKind != JsonValueKind.String)
                    throw Unsupported("Dimension values must be strings in the supported report subset.");
                member = dimension.Value.GetString()!;
                if (profile is not null && profile.TypedDimensionDomains.TryGetValue(dimensionName, out var typedDomain))
                {
                    if (!StringComparer.Ordinal.Equals(typedDomain.DataTypeQName, "xs:string"))
                        throw Unsupported($"Typed dimension '{dimension.Name}' uses an unsupported typed value datatype.");
                    if (member.Length > XbrlFact.MaximumTypedDimensionValueCharacters)
                        throw Unsupported($"Typed dimension '{dimension.Name}' exceeds the supported value length.");
                    try { XmlConvert.VerifyXmlChars(member); }
                    catch (XmlException) { throw Unsupported($"Typed dimension '{dimension.Name}' contains invalid XML characters."); }
                    reportedDimensions.Add(new Dimension(dimensionName, "", IsExplicit: false) { TypedValue = member });
                }
                else
                {
                    isExplicit = TryExpandQName(member, namespaces, out var expandedMember);
                    if (!isExplicit)
                        throw Unsupported($"Typed dimension '{dimension.Name}' requires supported xs:string taxonomy domain metadata.");
                    member = CanonicalProfileQName(expandedMember, member, profile);
                    reportedDimensions.Add(new Dimension(dimensionName, member));
                }
            }

            int? decimals = null;
            if (fact.TryGetProperty("decimals", out var decimalsNode))
            {
                if (decimalsNode.ValueKind != JsonValueKind.Number || !decimalsNode.TryGetInt32(out var parsed))
                    throw Unsupported("Fact decimals must be an integer.");
                if (parsed is < -30 or > 30)
                    throw Unsupported("Fact decimals are outside the supported range [-30, 30].");
                decimals = parsed;
            }
            if (value.ValueKind == JsonValueKind.Null && decimals is not null)
                throw Unsupported("Nil xBRL-JSON facts cannot carry a decimals property.");
            var isNumeric = conceptMetadata?.IsNumeric
                ?? (unit.Unit is not null || unit.CompoundUnit is not null || decimals is not null);
            if (isNumeric && unit.Unit is null && unit.CompoundUnit is null)
                unit = (new XbrlUnit("pure"), null);
            if (isNumeric && language is not null)
                throw Unsupported("Numeric xBRL-JSON facts cannot carry a language dimension.");
            decimal? numericValue = null;
            string? textValue = null;
            var isNil = value.ValueKind == JsonValueKind.Null;
            if (!isNil)
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw Unsupported("xBRL-JSON fact values must be strings or null.");
                var lexical = value.GetString()!;
                if (isNumeric)
                {
                    if (!XbrlNumericLexicalParser.TryParseExact(lexical, out var parsed))
                        throw Unsupported($"Numeric fact value '{lexical}' is invalid or outside the supported exact decimal range.");
                    numericValue = parsed;
                }
                else textValue = lexical;
            }

            var parsedFact = new XbrlFact
            {
                ConceptName = canonicalConcept,
                EntityScheme = entityScheme,
                EntityIdentifier = entityIdentifier,
                Period = period,
                Unit = unit.Unit,
                CompoundUnit = unit.Item2,
                Dimensions = reportedDimensions,
                Value = numericValue,
                TextValue = textValue,
                IsNil = isNil,
                Decimals = decimals,
                Language = language,
                ContributingSources = Array.Empty<SourceReference>()
            };
            facts.Add(new OimFactEntry(property.Name, parsedFact));
        }
        return facts;
    }

    private static XbrlPeriod ParsePeriod(string value)
    {
        var bounds = value.Split('/');
        if (bounds.Length is < 1 or > 2)
            throw Unsupported($"Invalid xBRL-JSON period '{value}'.");
        var startOrInstant = ParseBoundary(bounds[0]);
        if (bounds.Length == 1)
        {
            if (startOrInstant == DateOnly.MinValue)
                throw Unsupported("Instant date is outside the supported range.");
            return XbrlPeriod.OfInstant(startOrInstant.AddDays(-1));
        }
        var end = ParseBoundary(bounds[1]);
        if (startOrInstant >= end) throw Unsupported("Duration start must precede its exclusive end.");
        return XbrlPeriod.OfDuration(startOrInstant, end);
    }

    private static DateOnly ParseBoundary(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dateTime)
            || dateTime.TimeOfDay != TimeSpan.Zero)
            throw Unsupported($"Period boundary '{value}' is outside the supported midnight date subset.");
        return DateOnly.FromDateTime(dateTime);
    }

    private static (XbrlUnit? Unit, CompoundUnit? CompoundUnit) ParseUnit(
        string value, IReadOnlyDictionary<string, string> namespaces, FilingProfile? profile)
    {
        var sides = value.Split('/');
        if (sides.Length is < 1 or > 2 || sides.Any(string.IsNullOrEmpty))
            throw Unsupported($"Invalid xBRL-JSON unit '{value}'.");
        var numerators = ParseMeasures(sides[0], namespaces, profile);
        var denominators = sides.Length == 2 ? ParseMeasures(sides[1], namespaces, profile) : new List<string>();
        if (numerators.Count == 0 || numerators.Count + denominators.Count > 64)
            throw Unsupported("Unit exceeds the supported measure limit.");
        if (denominators.Count == 0)
        {
            if (numerators.Count == 1 && StringComparer.Ordinal.Equals(numerators[0], "pure"))
                throw Unsupported("A pure numeric unit must be represented without a unit core dimension.");
            if (numerators.All(m => m == numerators[0])) return (new XbrlUnit(numerators[0], numerators.Count), null);
            return (null, new CompoundUnit(numerators));
        }
        if (denominators.Intersect(numerators, StringComparer.Ordinal).Any())
            throw Unsupported("Compound units must cancel shared measures before division.");
        if (numerators.Count == 1 && StringComparer.Ordinal.Equals(numerators[0], "pure")
            && denominators.All(measure => StringComparer.Ordinal.Equals(measure, denominators[0])))
            return (new XbrlUnit(denominators[0], -denominators.Count), null);
        return (null, new CompoundUnit(numerators, denominators));
    }

    private static List<string> ParseMeasures(
        string side, IReadOnlyDictionary<string, string> namespaces, FilingProfile? profile)
        => side.Split('*').Select(measure => MeasureFromQName(measure, namespaces, profile)).ToList();

    private static string MeasureFromQName(
        string value, IReadOnlyDictionary<string, string> namespaces, FilingProfile? profile)
    {
        var expanded = ExpandQName(value, namespaces);
        if (expanded.Namespace == Iso4217 || expanded.Namespace == Xbrli) return expanded.LocalName;
        if (profile is not null)
        {
            var profilePrefix = profile.NamespaceUris.FirstOrDefault(entry =>
                StringComparer.Ordinal.Equals(entry.Value, expanded.Namespace)).Key;
            if (!string.IsNullOrEmpty(profilePrefix)) return $"{profilePrefix}:{expanded.LocalName}";
        }
        return value;
    }

    private static string RequiredString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Unsupported($"Required dimension '{name}' must be a string.");

    private static (string Namespace, string LocalName) ExpandQName(
        string value, IReadOnlyDictionary<string, string> namespaces)
    {
        if (!TryExpandQName(value, namespaces, out var result))
            throw Unsupported($"QName '{value}' is invalid or uses an undeclared prefix.");
        return result;
    }

    private static string CanonicalProfileQName(
        (string Namespace, string LocalName) expanded,
        string original,
        FilingProfile? profile)
    {
        if (profile is null) return original;
        var prefix = profile.NamespaceUris.FirstOrDefault(entry =>
            StringComparer.Ordinal.Equals(entry.Value, expanded.Namespace)).Key;
        return string.IsNullOrEmpty(prefix) ? original : $"{prefix}:{expanded.LocalName}";
    }

    private static bool TryExpandQName(
        string value, IReadOnlyDictionary<string, string> namespaces,
        out (string Namespace, string LocalName) result)
    {
        var separator = value.IndexOf(':');
        result = default;
        if (separator <= 0 || separator != value.LastIndexOf(':')) return false;
        var prefix = value[..separator];
        var local = value[(separator + 1)..];
        if (!namespaces.TryGetValue(prefix, out var uri) || !IsNcName(prefix) || !IsNcName(local)) return false;
        result = (uri, local);
        return true;
    }

    private static void VerifyNcName(string value, string label)
    {
        if (!IsNcName(value)) throw Unsupported($"Invalid {label} '{value}'.");
    }

    private static bool IsNcName(string value)
    {
        try { XmlConvert.VerifyNCName(value); return true; }
        catch (XmlException) { return false; }
    }

    private static void EnsureUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Unsupported($"JSON object repeats key '{property.Name}'.");
                EnsureUniqueKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray()) EnsureUniqueKeys(value);
        }
    }

    private static void RejectUnsupportedMembers(
        JsonElement value,
        HashSet<string> supportedMembers,
        string location)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!supportedMembers.Contains(property.Name))
                throw Unsupported(
                    $"xBRL-JSON {location} property '{property.Name}' is not supported by this mapping model; import refused to avoid silently dropping report data.");
        }
    }

    private static NamespaceContext BuildNamespaces(FilingProfile profile, IEnumerable<XbrlFact> facts,
        int maxNamespaces = int.MaxValue, CancellationToken cancellationToken = default)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceUris = new Dictionary<string, string>(StringComparer.Ordinal);
        var outputForSource = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliasForUri = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string prefix, string uri)
        {
            VerifyNcName(prefix, "namespace prefix");
            if (!Uri.TryCreate(uri, UriKind.Absolute, out _)) throw Unsupported($"Namespace '{uri}' is not an absolute URI.");
            if (sourceUris.TryGetValue(prefix, out var prior) && prior != uri)
                throw Unsupported($"Namespace prefix '{prefix}' maps to more than one URI.");
            sourceUris[prefix] = uri;
            if (aliasForUri.TryGetValue(uri, out var existing))
            {
                outputForSource[prefix] = existing;
                return;
            }
            if (output.TryGetValue(prefix, out prior) && prior != uri)
                throw Unsupported($"Namespace prefix '{prefix}' conflicts with a reserved prefix.");
            output[prefix] = uri;
            outputForSource[prefix] = prefix;
            aliasForUri[uri] = prefix;
            if (output.Count > maxNamespaces)
                throw Unsupported($"The report exceeded its {maxNamespaces} namespace budget.");
        }

        Add("xbrl", "https://xbrl.org/2021");
        Add("xbrli", Xbrli);
        Add("iso4217", Iso4217);
        Add("xs", "http://www.w3.org/2001/XMLSchema");
        foreach (var entry in profile.NamespaceUris) Add(entry.Key, entry.Value);
        foreach (var concept in profile.Concepts)
        {
            var (prefix, _) = SplitQName(concept.QualifiedName);
            Add(prefix, concept.Namespace);
        }

        string QName(string qname)
        {
            var (prefix, local) = SplitQName(qname);
            if (!sourceUris.ContainsKey(prefix))
                throw Unsupported($"QName '{qname}' has no namespace URI in the filing profile.");
            VerifyNcName(local, "QName local name");
            return outputForSource[prefix] + ":" + local;
        }

        foreach (var fact in facts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = QName(fact.ConceptName);
            foreach (var dimension in fact.Dimensions)
            {
                _ = QName(dimension.Name);
                if (dimension.IsExplicit) _ = QName(dimension.MemberName);
                else if (!profile.TypedDimensionDomains.TryGetValue(dimension.Name, out var typedDomain)
                         || !StringComparer.Ordinal.Equals(typedDomain.DataTypeQName, "xs:string")
                         || dimension.TypedValue is null)
                    throw Unsupported($"Typed dimension '{dimension.Name}' lacks supported profile metadata or a normalized value.");
            }
            if (fact.Unit is not null) _ = Measure(fact.Unit.Measure);
            if (fact.CompoundUnit is not null)
                foreach (var measure in fact.CompoundUnit.Numerators.Concat(fact.CompoundUnit.DenominatorList))
                    _ = Measure(measure);
        }

        var entityPrefixByScheme = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scheme in facts.Select(f => f.EntityScheme).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entityPrefixByScheme.Count >= maxNamespaces)
                throw Unsupported($"The report exceeded its {maxNamespaces} entity namespace budget.");
            if (!Uri.TryCreate(scheme, UriKind.Absolute, out _)) throw Unsupported($"Entity scheme '{scheme}' is not an absolute URI.");
            if (!aliasForUri.TryGetValue(scheme, out var prefix))
            {
                var basePrefix = "entity";
                prefix = basePrefix;
                for (var suffix = 2; output.ContainsKey(prefix); suffix++) prefix = basePrefix + suffix;
                Add(prefix, scheme);
            }
            entityPrefixByScheme[scheme] = prefix;
        }
        return new NamespaceContext(output, outputForSource, entityPrefixByScheme, profile.SupportedUnits);

        string Measure(string measure)
        {
            if (measure.Contains(':')) return QName(measure);
            if (profile.SupportedUnits.Contains(measure)) return QName("iso4217:" + measure);
            if (measure is "shares" or "pure")
                return QName("xbrli:" + measure);
            throw Unsupported($"Unit measure '{measure}' must be a qualified name or a supported currency.");
        }
    }

    private static (string Prefix, string Local) SplitQName(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator != value.LastIndexOf(':'))
            throw Unsupported($"'{value}' is not a qualified name.");
        return (value[..separator], value[(separator + 1)..]);
    }

    private static XbrlExportException Unsupported(string message)
        => new(DiagnosticCodes.XbrUnsupportedConstruct, message);

    private sealed record NamespaceContext(
        IReadOnlyDictionary<string, string> Output,
        IReadOnlyDictionary<string, string> OutputForSource,
        IReadOnlyDictionary<string, string> EntityPrefixes,
        IReadOnlySet<string> Currencies)
    {
        public string QName(string value)
        {
            var (prefix, local) = SplitQName(value);
            if (!OutputForSource.TryGetValue(prefix, out var resolved))
                throw Unsupported($"QName '{value}' has an undeclared namespace prefix.");
            VerifyNcName(local, "QName local name");
            return resolved + ":" + local;
        }

        public string Entity(string scheme, string identifier)
        {
            if (!EntityPrefixes.TryGetValue(scheme, out var prefix))
                throw Unsupported($"Entity scheme '{scheme}' is not declared.");
            if (identifier.Length == 0 || identifier.Any(char.IsWhiteSpace))
                throw Unsupported("Entity identifier cannot be represented as an SQName local part.");
            return prefix + ":" + identifier;
        }

        public string Measure(string value)
        {
            if (value.Contains(':')) return QName(value);
            if (Currencies.Contains(value)) return QName("iso4217:" + value);
            if (value is "shares" or "pure")
                return QName("xbrli:" + value);
            throw Unsupported($"Unit measure '{value}' must be a qualified name or a supported currency.");
        }
    }

    private sealed class BoundedWriteStream(Stream inner, long maximumBytes) : Stream
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
        protected override void Dispose(bool disposing)
        {
            // The caller owns the underlying artifact stream.
            base.Dispose(disposing);
        }
        private void EnsureCapacity(int count)
        {
            if (count > maximumBytes - BytesWritten)
                throw new InvalidDataException($"The xBRL-JSON report exceeded its {maximumBytes} byte budget.");
        }
    }
}
