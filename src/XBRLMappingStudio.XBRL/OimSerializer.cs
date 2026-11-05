using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Xml;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL;

/// <summary>Reads and writes the supported XBRL Open Information Model JSON subset.</summary>
public static class OimSerializer
{
    private const string DocumentType = "https://xbrl.org/2021/xbrl-json";
    private const string Iso4217 = "http://www.xbrl.org/2003/iso4217";
    private const string Xbrli = "http://www.xbrl.org/2003/instance";

    public static string Serialize(FilingProfile profile, IReadOnlyList<XbrlFact> facts)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(facts);
        foreach (var fact in facts)
        {
            var concept = profile.FindConcept(fact.ConceptName)
                ?? throw Unsupported($"Concept '{fact.ConceptName}' is not defined in the filing profile.");
            if (!fact.ValidateAgainst(concept, out var reason)) throw Unsupported(reason);
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
            var id = "f" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(key + "\u001f" + occurrence.ToString(CultureInfo.InvariantCulture))))
                .ToLowerInvariant();
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
            if (!fact.ValidateAgainst(concept, out var reason)) throw Unsupported(reason);
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
                var key = fact.SemanticKey();
                var id = "f" + Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(key + "\u001f" + occurrence.ToString(CultureInfo.InvariantCulture))))
                    .ToLowerInvariant();
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
        if (fact.Unit is not null)
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
            dimensions[name] = dimension.IsExplicit
                ? JsonValue.Create(ns.QName(dimension.MemberName))
                : JsonValue.Create(dimension.MemberName);
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
            : "/" + string.Join("*", Enumerable.Repeat(qname, -unit.Power));
    }

    private static string CompoundUnitToJson(CompoundUnit unit, NamespaceContext ns)
    {
        if (unit.Numerators.Count == 0 || unit.Numerators.Count + unit.DenominatorList.Count > 64)
            throw Unsupported("Compound unit must have a numerator and stay within the supported measure limit.");
        var numerator = string.Join("*", unit.Numerators.Select(ns.Measure).OrderBy(x => x, StringComparer.Ordinal));
        var denominator = string.Join("*", unit.DenominatorList.Select(ns.Measure).OrderBy(x => x, StringComparer.Ordinal));
        return denominator.Length == 0 ? numerator : numerator + "/" + denominator;
    }

    /// <summary>Read the supported OIM fact subset. Unsupported report constructs fail closed.</summary>
    public static IReadOnlyList<XbrlFact> ReadFacts(string json)
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
        if (!root.TryGetProperty("facts", out var factsNode)) return Array.Empty<XbrlFact>();
        if (factsNode.ValueKind != JsonValueKind.Object)
            throw Unsupported("The xBRL-JSON facts member must be an object.");

        var facts = new List<XbrlFact>();
        foreach (var property in factsNode.EnumerateObject())
        {
            VerifyNcName(property.Name, "fact identifier");
            var fact = property.Value;
            if (fact.ValueKind != JsonValueKind.Object
                || !fact.TryGetProperty("dimensions", out var dimensions)
                || dimensions.ValueKind != JsonValueKind.Object
                || !fact.TryGetProperty("value", out var value))
                throw Unsupported($"Fact '{property.Name}' is incomplete.");
            if (fact.TryGetProperty("links", out _)
                || dimensions.TryGetProperty("noteId", out _))
                throw Unsupported("Fact notes and links are not supported by this mapping model.");

            var concept = RequiredString(dimensions, "concept");
            _ = ExpandQName(concept, namespaces);
            var entity = RequiredString(dimensions, "entity");
            var colon = entity.IndexOf(':');
            if (colon <= 0 || colon == entity.Length - 1 || entity.IndexOf(':', colon + 1) >= 0)
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
                ? ParseUnit(unitValue.GetString() ?? throw Unsupported("Unit must be a string."), namespaces)
                : (Unit: (XbrlUnit?)null, CompoundUnit: (CompoundUnit?)null);
            var reportedDimensions = new List<Dimension>();
            string? language = null;
            foreach (var dimension in dimensions.EnumerateObject())
            {
                if (dimension.Name is "concept" or "entity" or "period" or "unit") continue;
                if (dimension.Name == "language")
                {
                    language = dimension.Value.GetString()?.ToLowerInvariant()
                        ?? throw Unsupported("Language dimension must be a string.");
                    continue;
                }
                if (dimension.Name == "noteId")
                    throw Unsupported("Note facts are not supported by this mapping model.");
                _ = ExpandQName(dimension.Name, namespaces);
                string member;
                bool isExplicit;
                if (dimension.Value.ValueKind == JsonValueKind.Null)
                    throw Unsupported("Nilled typed dimensions are not supported by this mapping model.");
                if (dimension.Value.ValueKind != JsonValueKind.String)
                    throw Unsupported("Dimension values must be strings in the supported report subset.");
                member = dimension.Value.GetString()!;
                isExplicit = TryExpandQName(member, namespaces, out _);
                reportedDimensions.Add(new Dimension(dimension.Name, member, isExplicit));
            }

            int? decimals = null;
            if (fact.TryGetProperty("decimals", out var decimalsNode))
            {
                if (decimalsNode.ValueKind != JsonValueKind.Number || !decimalsNode.TryGetInt32(out var parsed))
                    throw Unsupported("Fact decimals must be an integer.");
                decimals = parsed;
            }
            var isNumeric = unit.Unit is not null || unit.CompoundUnit is not null || decimals is not null;
            if (isNumeric && unit.Unit is null && unit.CompoundUnit is null)
                throw Unsupported("A numeric xBRL-JSON fact must have a unit.");
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
                    if (!decimal.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        throw Unsupported($"Numeric fact value '{lexical}' exceeds the supported exact decimal range.");
                    numericValue = parsed;
                }
                else textValue = lexical;
            }

            facts.Add(new XbrlFact
            {
                ConceptName = concept,
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
            });
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
        string value, IReadOnlyDictionary<string, string> namespaces)
    {
        var sides = value.Split('/');
        if (sides.Length is < 1 or > 2 || sides.Any(string.IsNullOrEmpty))
            throw Unsupported($"Invalid xBRL-JSON unit '{value}'.");
        var numerators = ParseMeasures(sides[0], namespaces);
        var denominators = sides.Length == 2 ? ParseMeasures(sides[1], namespaces) : new List<string>();
        if (numerators.Count == 0 || numerators.Count + denominators.Count > 64)
            throw Unsupported("Unit exceeds the supported measure limit.");
        if (denominators.Count == 0)
        {
            if (numerators.All(m => m == numerators[0])) return (new XbrlUnit(numerators[0], numerators.Count), null);
            return (null, new CompoundUnit(numerators));
        }
        return (null, new CompoundUnit(numerators, denominators));
    }

    private static List<string> ParseMeasures(string side, IReadOnlyDictionary<string, string> namespaces)
        => side.Split('*').Select(measure => MeasureFromQName(measure, namespaces)).ToList();

    private static string MeasureFromQName(string value, IReadOnlyDictionary<string, string> namespaces)
    {
        var expanded = ExpandQName(value, namespaces);
        if (expanded.Namespace == Iso4217 || expanded.Namespace == Xbrli) return expanded.LocalName;
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
            if (measure is "shares" or "pure" or "monetary" or "integer" or "percent")
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
            if (identifier.Length == 0 || identifier.Contains(':') || identifier.Any(char.IsWhiteSpace))
                throw Unsupported("Entity identifier cannot be represented as an SQName local part.");
            return prefix + ":" + identifier;
        }

        public string Measure(string value)
        {
            if (value.Contains(':')) return QName(value);
            if (Currencies.Contains(value)) return QName("iso4217:" + value);
            if (value is "shares" or "pure" or "monetary" or "integer" or "percent")
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
