using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
public sealed class XbrlReportAdapter : ISourceAdapter
{
    public string Kind => "xbrl-report";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;

    private static readonly string[] Columns =
    {
        "occurrenceId", "concept", "entityScheme", "entityIdentifier", "period",
        "unit", "compoundUnit", "value", "textValue", "decimals", "lang", "nil", "dimensions", "dimensionsJson"
    };

    private static readonly HashSet<string> SupportedSelectionKeys = new(StringComparer.Ordinal)
    {
        "concept", "period", "unit", "dimensionAxis", "dimensionMember"
    };

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        _ = ParseSelection(connection);
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = Select(connection, Read(connection));
        bool truncated = facts.Count > maxRows;
        var rows = facts.Take(maxRows).Select(item => ToRecord(connection, item.Fact, item.Index)).ToList();
        return Task.FromResult(new DataSample(schema, rows, truncated));
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(SourceConnection connection, ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = Select(connection, Read(connection));
        for (int i = 0; i < facts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToRecord(connection, facts[i].Fact, facts[i].Index);
            await Task.Yield();
        }
    }

    private static IReadOnlyList<(XbrlFact Fact, int Index)> Select(
        SourceConnection connection,
        IReadOnlyList<XbrlFact> facts)
    {
        var selection = ParseSelection(connection);
        var matched = facts.Select((fact, index) => (Fact: fact, Index: index))
            .Where(item => selection.Concept is null
                || StringComparer.Ordinal.Equals(item.Fact.ConceptName, selection.Concept))
            .Where(item => selection.Period is null
                || StringComparer.Ordinal.Equals(item.Fact.Period.ToIsoString(), selection.Period))
            .Where(item => selection.Unit is null
                || StringComparer.Ordinal.Equals(item.Fact.Unit?.ToString(), selection.Unit)
                || StringComparer.Ordinal.Equals(item.Fact.CompoundUnit?.CanonicalKey, selection.Unit))
            .Where(item => selection.DimensionAxis is null
                || item.Fact.Dimensions.Any(dimension => dimension.IsExplicit
                    && StringComparer.Ordinal.Equals(dimension.Name, selection.DimensionAxis)
                    && StringComparer.Ordinal.Equals(dimension.MemberName, selection.DimensionMember)))
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

        var axis = Get("dimensionAxis");
        var member = Get("dimensionMember");
        if ((axis is null) != (member is null))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "XBRL report dimension selection requires both 'dimensionAxis' and 'dimensionMember'.");

        return new ReportSelection(Get("concept"), Get("period"), Get("unit"), axis, member);
    }

    private static IReadOnlyList<XbrlFact> Read(SourceConnection connection)
    {
        var path = connection.Path ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            $"XBRL report connection '{connection.SourceId}' has no file path.");
        var text = File.ReadAllText(path, Encoding.UTF8);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".xml" => XmlSerializer.ReadFacts(text),
                _ => OimSerializer.ReadFacts(text)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or XbrlExportException or System.Text.Json.JsonException)
        {
            throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding,
                $"Report '{path}' could not be parsed as xBRL-JSON or xBRL-XML: {ex.Message}");
        }
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
            ["decimals"] = DataValue.StringValue(f.Decimals?.ToString(CultureInfo.InvariantCulture) ?? "INF", src),
            ["lang"] = string.IsNullOrEmpty(f.Language)
                ? DataValue.Missing(PortShape.String)
                : DataValue.StringValue(f.Language!, src),
            ["nil"] = DataValue.BoolValue(f.IsNil),
            ["dimensions"] = DataValue.StringValue(
                string.Join(";", f.Dimensions.Select(d => d.ToString())), src),
            ["dimensionsJson"] = DataValue.StringValue(JsonSerializer.Serialize(f.Dimensions
                .OrderBy(dimension => dimension.Name, StringComparer.Ordinal)
                .ThenBy(dimension => dimension.MemberName, StringComparer.Ordinal)), src)
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
        string? Concept,
        string? Period,
        string? Unit,
        string? DimensionAxis,
        string? DimensionMember)
    {
        public bool HasAspectFilters
            => Concept is not null || Period is not null || Unit is not null || DimensionAxis is not null;

        public static ReportSelection Empty { get; } = new(null, null, null, null, null);
    }
}
