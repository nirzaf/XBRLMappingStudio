using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Adapters.Xbrl;

/// <summary>
/// Reads an existing xBRL report (xBRL-JSON or xBRL 2.1 XML) and exposes one record per
/// fact (XBR06). This makes report-to-record reverse mapping a first-class source: a
/// downstream graph can map reported facts back into rows. Repeated facts, several units
/// and multiple languages are preserved as distinct records.
/// </summary>
public sealed class XbrlReportAdapter : IProfileAwareSourceAdapter
{
    public string Kind => "xbrl-report";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;
    private const int MaximumTypedDimensionEvidenceCharacters = 6_004_096;
    private const int MaximumTypedDimensionFilterJsonCharacters = 40_000_000;
    private static readonly JsonSerializerOptions DimensionJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] Columns =
    {
        "occurrenceId", "concept", "entityScheme", "entityIdentifier", "period", "periodKind",
        "instantDate", "periodStart", "periodEnd",
        "unit", "compoundUnit", "value", "textValue", "decimals", "lang", "nil", "dimensions", "dimensionsJson"
    };

    private static readonly HashSet<string> SupportedSelectionKeys = new(StringComparer.Ordinal)
    {
        "occurrenceId", "reportDigest", "concept", "entityScheme", "entityIdentifier", "period", "periodKind",
        "instantDate", "periodStart", "periodEnd",
        "unit", "lang", "nil", "dimensionAxis", "dimensionMember", "dimensionFilters", "typedDimensionFilters",
        "maxInputBytes"
    };

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        _ = ParseSelection(connection);
        _ = GetMaximumInputBytes(connection);
        var cols = new List<ColumnSchema>();
        for (int i = 0; i < Columns.Length; i++)
        {
            var shape = Columns[i] switch
            {
                "value" => PortShape.Decimal,
                "nil" => PortShape.Boolean,
                _ => PortShape.String
            };
            cols.Add(new ColumnSchema(Columns[i], shape, i, Columns[i]));
        }
        ISourceSchema schema = TabularSchema.FromSource(connection.SourceId, cols);
        return Task.FromResult(schema);
    }

    public Task<DataSample> ReadSampleAsync(SourceConnection connection, ISourceSchema schema,
        int maxRows, CancellationToken cancellationToken)
        => ReadSampleCore(connection, schema, maxRows, profile: null, cancellationToken);

    public Task<DataSample> ReadSampleAsync(SourceConnection connection, ISourceSchema schema,
        int maxRows, FilingProfile profile, CancellationToken cancellationToken)
        => ReadSampleCore(connection, schema, maxRows, profile, cancellationToken);

    private Task<DataSample> ReadSampleCore(SourceConnection connection, ISourceSchema schema,
        int maxRows, FilingProfile? profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = Read(connection, cancellationToken, profile);
        var facts = Select(connection, report.Facts, report.Digest, profile);
        bool truncated = facts.Count > maxRows;
        var rows = facts.Take(maxRows).Select(item => ToRecord(connection, item.Fact, item.Index)).ToList();
        return Task.FromResult(new DataSample(schema, rows, truncated));
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(SourceConnection connection, ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var record in ReadFullCore(connection, schema, profile: null, cancellationToken))
            yield return record;
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(SourceConnection connection, ISourceSchema schema,
        FilingProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var record in ReadFullCore(connection, schema, profile, cancellationToken))
            yield return record;
    }

    private async IAsyncEnumerable<DataRecord> ReadFullCore(SourceConnection connection, ISourceSchema schema,
        FilingProfile? profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = Read(connection, cancellationToken, profile);
        var facts = Select(connection, report.Facts, report.Digest, profile);
        for (int i = 0; i < facts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToRecord(connection, facts[i].Fact, facts[i].Index);
            await Task.Yield();
        }
    }

    private static IReadOnlyList<(XbrlFact Fact, int Index)> Select(
        SourceConnection connection,
        IReadOnlyList<XbrlFact> facts,
        string reportDigest,
        FilingProfile? profile)
    {
        var selection = ParseSelection(connection);
        var matched = facts.Select((fact, index) => (Fact: fact, Index: index))
            .Where(item => selection.ReportDigest is null
                || StringComparer.Ordinal.Equals(reportDigest, selection.ReportDigest))
            .Where(item => selection.OccurrenceId is null
                || StringComparer.Ordinal.Equals(
                    $"{connection.SourceId}#f{item.Index + 1}", selection.OccurrenceId))
            .Where(item => selection.Concept is null
                || StringComparer.Ordinal.Equals(item.Fact.ConceptName, selection.Concept))
            .Where(item => selection.EntityScheme is null
                || StringComparer.Ordinal.Equals(item.Fact.EntityScheme, selection.EntityScheme))
            .Where(item => selection.EntityIdentifier is null
                || StringComparer.Ordinal.Equals(item.Fact.EntityIdentifier, selection.EntityIdentifier))
            .Where(item => selection.Period is null
                || StringComparer.Ordinal.Equals(item.Fact.Period.ToIsoString(), selection.Period))
            .Where(item => selection.PeriodKind is null
                || StringComparer.Ordinal.Equals(item.Fact.Period.Kind == PeriodKind.Instant ? "instant" : "duration",
                    selection.PeriodKind))
            .Where(item => selection.InstantDate is null || item.Fact.Period.Instant == selection.InstantDate)
            .Where(item => selection.PeriodStart is null || item.Fact.Period.Start == selection.PeriodStart)
            .Where(item => selection.PeriodEnd is null || item.Fact.Period.End == selection.PeriodEnd)
            .Where(item => selection.Unit is null
                || StringComparer.Ordinal.Equals(item.Fact.Unit?.ToString(), selection.Unit)
                || StringComparer.Ordinal.Equals(item.Fact.CompoundUnit?.CanonicalKey, selection.Unit))
            .Where(item => selection.Language is null
                || StringComparer.Ordinal.Equals(item.Fact.Language, selection.Language))
            .Where(item => selection.IsNil is null || item.Fact.IsNil == selection.IsNil)
            .Where(item => selection.Dimensions.All(filter => item.Fact.Dimensions.Any(dimension => dimension.IsExplicit
                && DimensionQNameMatches(filter.Key, dimension.Name, dimension.AxisExpandedName, profile)
                && DimensionQNameMatches(filter.Value, dimension.MemberName, dimension.MemberExpandedName, profile))))
            .Where(item => selection.TypedDimensions.All(filter => item.Fact.Dimensions.Any(dimension => !dimension.IsExplicit
                && DimensionQNameMatches(filter.Key, dimension.Name, dimension.AxisExpandedName, profile)
                && StringComparer.Ordinal.Equals(dimension.TypedValueJson, filter.Value))))
            .ToArray();

        if (selection.HasAspectFilters && matched.Length == 0)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "Configured XBRL report aspect filters matched no fact occurrences.");

        return matched;
    }

    private static ReportSelection ParseSelection(SourceConnection connection)
    {
        var configuration = connection.Configuration;
        if (configuration is null || configuration.Count == 0)
            return ReportSelection.Empty;

        if (configuration.ContainsKey("targetRowId"))
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                "Existing destination target-row selection is not supported for an xBRL report source because the target dataset is not available to the source adapter; no destination rows are created or inferred.");

        var unsupported = configuration.Keys.Where(key => !SupportedSelectionKeys.Contains(key)).ToArray();
        if (unsupported.Length != 0)
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                $"XBRL report selection contains unsupported key(s): {string.Join(", ", unsupported.Order(StringComparer.Ordinal))}.");

        string? Get(string key)
        {
            if (!configuration.TryGetValue(key, out var value)) return null;
            if (string.IsNullOrWhiteSpace(value))
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"XBRL report selection '{key}' must be a non-empty exact value.");
            return value;
        }

        static DateOnly? GetDate(string? value, string key)
        {
            if (value is null) return null;
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"XBRL report selection '{key}' must be an exact ISO date in yyyy-MM-dd form.");
            return parsed;
        }

        var periodKind = Get("periodKind");
        if (periodKind is not null && periodKind is not ("instant" or "duration"))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report selection 'periodKind' must be exactly 'instant' or 'duration'.");
        var instantDate = GetDate(Get("instantDate"), "instantDate");
        var periodStart = GetDate(Get("periodStart"), "periodStart");
        var periodEnd = GetDate(Get("periodEnd"), "periodEnd");
        if (instantDate is not null && (periodStart is not null || periodEnd is not null))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report selection cannot combine 'instantDate' with duration period bounds.");
        if (instantDate is not null && periodKind == "duration"
            || (periodStart is not null || periodEnd is not null) && periodKind == "instant")
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report selection period kind conflicts with its instant or duration date filters.");
        if (periodStart is not null && periodEnd is not null && periodStart >= periodEnd)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report selection 'periodStart' must be before 'periodEnd'.");

        bool? isNil = Get("nil") switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report selection 'nil' must be exactly 'true' or 'false'.")
        };

        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configuration.TryGetValue("dimensionFilters", out var dimensionFilters))
        {
            if (string.IsNullOrWhiteSpace(dimensionFilters))
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    "XBRL report selection 'dimensionFilters' must be a non-empty JSON object of explicit axis-to-member names.");

            try
            {
                using var document = JsonDocument.Parse(dimensionFilters, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                        "XBRL report selection 'dimensionFilters' must be a JSON object of explicit axis-to-member names.");

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var memberValue = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(property.Name) || string.IsNullOrWhiteSpace(memberValue)
                        || !dimensions.TryAdd(property.Name, memberValue))
                        throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                            "XBRL report selection 'dimensionFilters' must contain unique, non-empty explicit axis-to-member names.");
                }
                if (dimensions.Count == 0)
                    throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                        "XBRL report selection 'dimensionFilters' must contain at least one explicit axis-to-member filter.");
            }
            catch (JsonException ex)
            {
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"XBRL report selection 'dimensionFilters' is invalid JSON: {ex.Message}");
            }
        }

        var axis = Get("dimensionAxis");
        var member = Get("dimensionMember");
        if ((axis is null) != (member is null))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report dimension selection requires both 'dimensionAxis' and 'dimensionMember'.");

        if (axis is not null && !dimensions.TryAdd(axis, member!))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report dimension selection cannot repeat an axis in both 'dimensionFilters' and the 'dimensionAxis'/'dimensionMember' pair.");

        var typedDimensions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configuration.TryGetValue("typedDimensionFilters", out var typedDimensionFilters))
        {
            if (string.IsNullOrWhiteSpace(typedDimensionFilters)
                || typedDimensionFilters.Length > MaximumTypedDimensionFilterJsonCharacters)
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"XBRL report selection 'typedDimensionFilters' must be a non-empty JSON object of dimension names to exact opaque typed-value evidence, within {MaximumTypedDimensionFilterJsonCharacters} characters.");

            try
            {
                using var document = JsonDocument.Parse(typedDimensionFilters, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                        "XBRL report selection 'typedDimensionFilters' must be a JSON object.");

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var evidence = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(property.Name) || string.IsNullOrWhiteSpace(evidence)
                        || evidence.Length > MaximumTypedDimensionEvidenceCharacters
                        || !typedDimensions.TryAdd(property.Name, evidence))
                        throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                            $"XBRL report selection 'typedDimensionFilters' must contain unique, non-empty dimension names and exact evidence strings within {MaximumTypedDimensionEvidenceCharacters} characters.");
                    if (typedDimensions.Count > FactDimensionParser.MaximumDimensions)
                        throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                            $"XBRL report selection 'typedDimensionFilters' cannot exceed {FactDimensionParser.MaximumDimensions} axes.");
                }
                if (typedDimensions.Count == 0)
                    throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                        "XBRL report selection 'typedDimensionFilters' must contain at least one filter.");
            }
            catch (JsonException ex)
            {
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"XBRL report selection 'typedDimensionFilters' is invalid JSON: {ex.Message}");
            }
        }

        if (typedDimensions.Keys.Any(dimensions.ContainsKey))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report selection cannot apply explicit-member and typed-value filters to the same dimension axis.");

        var reportDigest = Get("reportDigest");
        if (reportDigest is not null && !IsSha256Digest(reportDigest))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report selection 'reportDigest' must be a lowercase sha256 digest.");

        return new ReportSelection(Get("occurrenceId"), reportDigest, Get("concept"), Get("entityScheme"),
            Get("entityIdentifier"), Get("period"), periodKind,
            instantDate, periodStart, periodEnd, Get("unit"), Get("lang"), isNil, dimensions, typedDimensions);
    }

    private static bool IsSha256Digest(string value)
        => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static (IReadOnlyList<XbrlFact> Facts, string Digest) Read(
        SourceConnection connection, CancellationToken cancellationToken, FilingProfile? profile)
    {
        var path = connection.Path ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            $"XBRL report connection '{connection.SourceId}' has no file path.");
        var input = XbrlReportInputReader.ReadTextWithDigest(path, GetMaximumInputBytes(connection), cancellationToken);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        try
        {
            var facts = ext switch
            {
                // XML parsing retains typed-member XML as opaque evidence with its expanded
                // axis name. FactDimensionParser may promote only the bounded xs:string
                // subset after the graph supplies the resolved profile.
                ".xml" => XmlSerializer.ReadFacts(input.Text),
                _ => profile is null
                    ? OimSerializer.ReadFacts(input.Text)
                    : OimSerializer.ReadFacts(input.Text, profile)
            };
            return (facts, input.Digest);
        }
        catch (Exception ex) when (ex is InvalidOperationException or XbrlExportException or System.Text.Json.JsonException)
        {
            throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding,
                $"Report '{path}' could not be parsed as xBRL-JSON or xBRL-XML: {ex.Message}");
        }
    }

    private static bool DimensionQNameMatches(
        string requested,
        string actual,
        string? actualExpandedName,
        FilingProfile? profile)
    {
        if (StringComparer.Ordinal.Equals(requested, actual)) return true;
        if (profile is null || !TryExpandProfileQName(requested, profile.NamespaceUris, out var requestedExpanded))
            return false;
        if (actualExpandedName is not null)
            return StringComparer.Ordinal.Equals(actualExpandedName, requestedExpanded);
        return TryExpandProfileQName(actual, profile.NamespaceUris, out var actualExpanded)
            && StringComparer.Ordinal.Equals(actualExpanded, requestedExpanded);
    }

    private static bool TryExpandProfileQName(string qname, IReadOnlyDictionary<string, string> namespaceUris,
        out string expandedName)
    {
        expandedName = string.Empty;
        var separator = qname.IndexOf(':');
        if (separator <= 0 || separator != qname.LastIndexOf(':') || separator == qname.Length - 1
            || !namespaceUris.TryGetValue(qname[..separator], out var namespaceUri))
            return false;
        try
        {
            System.Xml.XmlConvert.VerifyNCName(qname[..separator]);
            System.Xml.XmlConvert.VerifyNCName(qname[(separator + 1)..]);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
        expandedName = $"{{{namespaceUri}}}{qname[(separator + 1)..]}";
        return true;
    }

    private static long GetMaximumInputBytes(SourceConnection connection)
    {
        var configured = connection.Configuration is not null
            && connection.Configuration.TryGetValue("maxInputBytes", out var value)
                ? value
                : null;
        return XbrlReportInputReader.ResolveMaximumInputBytes(connection.Path ?? string.Empty, configured);
    }

    private static DataRecord ToRecord(SourceConnection connection, XbrlFact f, int index)
    {
        var src = new SourceReference(connection.SourceId, $"report#f{index + 1}");
        var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
        {
            ["occurrenceId"] = DataValue.StringValue($"{connection.SourceId}#f{index + 1}", src),
            ["concept"] = DataValue.StringValue(f.ConceptName, src),
            ["entityScheme"] = DataValue.StringValue(f.EntityScheme, src),
            ["entityIdentifier"] = DataValue.StringValue(f.EntityIdentifier, src),
            ["period"] = DataValue.StringValue(f.Period.ToIsoString(), src),
            ["periodKind"] = DataValue.StringValue(f.Period.Kind == PeriodKind.Instant ? "instant" : "duration", src),
            ["instantDate"] = f.Period.Instant is { } instant
                ? DataValue.StringValue(instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), src)
                : DataValue.Missing(PortShape.String),
            ["periodStart"] = f.Period.Start is { } start
                ? DataValue.StringValue(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), src)
                : DataValue.Missing(PortShape.String),
            ["periodEnd"] = f.Period.End is { } end
                ? DataValue.StringValue(end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), src)
                : DataValue.Missing(PortShape.String),
            ["unit"] = f.Unit is not null
                ? DataValue.StringValue(f.Unit.ToString(), src)
                : DataValue.Missing(PortShape.String),
            ["compoundUnit"] = f.CompoundUnit is not null
                ? DataValue.StringValue(f.CompoundUnit.CanonicalKey, src)
                : DataValue.Missing(PortShape.String),
            ["value"] = f.IsNil || f.Value is null
                ? DataValue.Missing(PortShape.Decimal)
                : DataValue.DecimalValue(f.Value.Value, src, f.Value.Value.ToString(CultureInfo.InvariantCulture)),
            ["textValue"] = f.TextValue is null
                ? DataValue.Missing(PortShape.String)
                : DataValue.StringValue(f.TextValue, src),
            ["decimals"] = !f.IsNil && (f.Value is not null || f.Unit is not null || f.CompoundUnit is not null)
                ? DataValue.StringValue(f.Decimals?.ToString(CultureInfo.InvariantCulture) ?? "INF", src)
                : DataValue.Missing(PortShape.String),
            ["lang"] = string.IsNullOrEmpty(f.Language)
                ? DataValue.Missing(PortShape.String)
                : DataValue.StringValue(f.Language!, src),
            ["nil"] = DataValue.BoolValue(f.IsNil),
            ["dimensions"] = DataValue.StringValue(
                string.Join(";", f.Dimensions.Select(d => d.ToString())), src),
            ["dimensionsJson"] = DataValue.StringValue(JsonSerializer.Serialize(f.Dimensions
                .OrderBy(dimension => dimension.Name, StringComparer.Ordinal)
                .ThenBy(dimension => dimension.MemberName, StringComparer.Ordinal)
                .Select(dimension => new
                {
                    dimension.Name,
                    dimension.MemberName,
                    dimension.IsExplicit,
                    dimension.TypedValue,
                    dimension.TypedValueJson,
                    dimension.AxisExpandedName,
                    dimension.MemberExpandedName
                }), DimensionJsonOptions), src)
        };
        var srcs = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
        foreach (var k in fields.Keys) srcs[k] = src;

        return new DataRecord
        {
            RowId = $"{connection.SourceId}#f{index + 1}",
            Fields = fields,
            CellSources = srcs,
            HasStableRowIdentity = true
        };
    }

    private sealed record ReportSelection(
        string? OccurrenceId,
        string? ReportDigest,
        string? Concept,
        string? EntityScheme,
        string? EntityIdentifier,
        string? Period,
        string? PeriodKind,
        DateOnly? InstantDate,
        DateOnly? PeriodStart,
        DateOnly? PeriodEnd,
        string? Unit,
        string? Language,
        bool? IsNil,
        IReadOnlyDictionary<string, string> Dimensions,
        IReadOnlyDictionary<string, string> TypedDimensions)
    {
        public bool HasAspectFilters
            => OccurrenceId is not null || Concept is not null || EntityScheme is not null
                || ReportDigest is not null
                || EntityIdentifier is not null || Period is not null || PeriodKind is not null
                || InstantDate is not null || PeriodStart is not null || PeriodEnd is not null
                || Unit is not null || Language is not null || IsNil is not null
                || Dimensions.Count != 0 || TypedDimensions.Count != 0;

        public static ReportSelection Empty { get; }
        = new(null, null, null, null, null, null, null, null, null, null, null, null, null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
