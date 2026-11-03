using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Serializes facts to xBRL-JSON (OIM) per XBR05. Reopens with <see cref="ReadFacts"/>
/// to verify round-trip equivalence.
/// </summary>
public static class OimSerializer
{
    public static string Serialize(FilingProfile profile, IReadOnlyList<XbrlFact> facts)
    {
        // OIM document root has $schema and facts.
        var oim = new JsonObject
        {
            ["$schema"] = "https://xbrl.org/2021/json/xbrl-json-1.0.json",
            ["report"] = new JsonObject
            {
                ["pii"] = profile.ProfileId
            },
            ["facts"] = new JsonObject()
        };

        var factsNode = (JsonObject)oim["facts"]!;
        int counter = 0;
        foreach (var f in facts)
        {
            counter++;
            var key = $"f{counter:x8}";
            var node = FactToJson(f);
            factsNode[key] = node;
        }

        return oim.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static JsonObject FactToJson(XbrlFact f)
    {
        var node = new JsonObject
        {
            ["value"] = f.IsNil ? JsonValue.Create("null")
                       : f.Value is null ? JsonValue.Create(f.Language ?? "")
                       : JsonValue.Create(f.Value.Value),
            ["concept"] = f.ConceptName,
            ["entity"] = new JsonObject
            {
                ["scheme"] = f.EntityScheme,
                ["identifier"] = f.EntityIdentifier
            },
            ["period"] = PeriodToJson(f.Period),
            ["dimensions"] = DimensionsToJson(f.Dimensions)
        };

        if (f.CompoundUnit is not null)
            node["unit"] = CompoundUnitToJson(f.CompoundUnit);
        else if (f.Unit is not null)
            node["unit"] = f.Unit.Power == 1
                ? JsonValue.Create(f.Unit.Measure)
                : (JsonNode)SimpleUnitWithPowerToJson(f.Unit);
        if (f.Decimals.HasValue)
            node["decimals"] = f.Decimals.Value;
        else
            node["decimals"] = "INF";
        if (!string.IsNullOrEmpty(f.Language))
            node["lang"] = f.Language;
        if (f.IsNil)
        {
            node["nil"] = true;
            node["value"] = null;
        }
        return node;
    }

    private static JsonObject PeriodToJson(XbrlPeriod p) => p.Kind switch
    {
        PeriodKind.Instant => new JsonObject { ["instant"] = p.Instant?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        PeriodKind.Duration => new JsonObject
        {
            ["start"] = p.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end"] = p.End?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        },
        _ => throw new InvalidOperationException()
    };

    private static JsonObject DimensionsToJson(IReadOnlyList<Dimension> dims)
    {
        var obj = new JsonObject();
        foreach (var d in dims)
            obj[d.Name] = d.MemberName;
        return obj;
    }

    private static JsonObject CompoundUnitToJson(CompoundUnit cu)
    {
        var numerators = new JsonArray();
        foreach (var m in cu.Numerators) numerators.Add(JsonValue.Create(m));
        var divide = new JsonObject { ["numerators"] = numerators };
        if (cu.DenominatorList.Count > 0)
        {
            var denominators = new JsonArray();
            foreach (var m in cu.DenominatorList) denominators.Add(JsonValue.Create(m));
            divide["denominators"] = denominators;
        }
        return new JsonObject { ["divide"] = divide };
    }

    private static JsonObject SimpleUnitWithPowerToJson(XbrlUnit u)
    {
        var multiply = new JsonArray
        {
            new JsonObject { ["item"] = u.Measure, ["power"] = u.Power }
        };
        return new JsonObject { ["multiply"] = multiply };
    }

    /// <summary>
    /// Read facts back from an xBRL-JSON document. Used to verify round-trip and
    /// to support reverse (report-to-record) mapping (XBR06).
    /// </summary>
    public static IReadOnlyList<XbrlFact> ReadFacts(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Document root must be an object.");
        var factsNode = node["facts"] as JsonObject
            ?? throw new InvalidOperationException("Missing 'facts' object.");
        var result = new List<XbrlFact>();
        foreach (var (_, value) in factsNode)
        {
            if (value is not JsonObject fobj) continue;
            result.Add(JsonToFact(fobj));
        }
        return result;
    }

    private static XbrlFact JsonToFact(JsonObject obj)
    {
        var concept = obj["concept"]!.GetValue<string>();
        var entity = (JsonObject)obj["entity"]!;
        var scheme = entity["scheme"]!.GetValue<string>();
        var identifier = entity["identifier"]!.GetValue<string>();
        var periodNode = (JsonObject)obj["period"]!;
        XbrlPeriod period;
        if (periodNode["instant"] is JsonNode inst)
        {
            var d = DateOnly.ParseExact(inst.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            period = XbrlPeriod.OfInstant(d);
        }
        else
        {
            var s = DateOnly.ParseExact(periodNode["start"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var e = DateOnly.ParseExact(periodNode["end"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            period = XbrlPeriod.OfDuration(s, e);
        }

        XbrlUnit? unit = null;
        CompoundUnit? compound = null;
        if (obj["unit"] is JsonObject uo)
        {
            if (uo["divide"] is JsonObject div)
            {
                var nums = (div["numerators"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new();
                var dens = (div["denominators"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new();
                compound = new CompoundUnit(nums, dens);
            }
            else if (uo["multiply"] is JsonArray mult)
            {
                var first = mult.OfType<JsonObject>().FirstOrDefault();
                if (first is not null)
                    unit = new XbrlUnit(first["item"]!.GetValue<string>(), first["power"]?.GetValue<int>() ?? 1);
            }
        }
        else if (obj["unit"] is JsonValue uv)
            unit = new XbrlUnit(uv.GetValue<string>());

        var dimensions = new List<Dimension>();
        if (obj["dimensions"] is JsonObject dobj)
            foreach (var (k, v) in dobj)
                if (v is JsonValue dv && dv.GetValueKind() == JsonValueKind.String)
                    dimensions.Add(new Dimension(k, dv.GetValue<string>()));

        decimal? value = null;
        bool isNil = obj["nil"]?.GetValue<bool>() ?? false;
        if (!isNil && obj["value"] is JsonNode vn && vn is not JsonValue jv)
        {
            // fallthrough
        }
        else if (!isNil && obj["value"] is JsonValue jsonValue)
        {
            try { value = jsonValue.GetValue<decimal>(); }
            catch { value = null; }
        }

        int? decimals = obj["decimals"] is JsonNode dn && dn.GetValueKind() == JsonValueKind.Number
            ? dn.GetValue<int>()
            : null;
        var lang = obj["lang"]?.GetValue<string>();

        return new XbrlFact
        {
            ConceptName = concept,
            EntityScheme = scheme,
            EntityIdentifier = identifier,
            Period = period,
            Unit = unit,
            CompoundUnit = compound,
            Dimensions = dimensions,
            Value = value,
            IsNil = isNil,
            Decimals = decimals,
            Language = lang,
            ContributingSources = Array.Empty<SourceReference>()
        };
    }
}
