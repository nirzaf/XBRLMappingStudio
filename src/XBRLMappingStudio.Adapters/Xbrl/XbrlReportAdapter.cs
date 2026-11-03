using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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
        "concept", "entityScheme", "entityIdentifier", "period",
        "unit", "compoundUnit", "value", "decimals", "lang", "nil", "dimensions"
    };

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var cols = new List<ColumnSchema>();
        for (int i = 0; i < Columns.Length; i++)
            cols.Add(new ColumnSchema(Columns[i], PortShape.String, i, Columns[i]));
        ISourceSchema schema = TabularSchema.FromSource(connection.SourceId, cols);
        return Task.FromResult(schema);
    }

    public Task<DataSample> ReadSampleAsync(SourceConnection connection, ISourceSchema schema,
        int maxRows, CancellationToken cancellationToken)
    {
        var facts = Read(connection);
        bool truncated = facts.Count > maxRows;
        var rows = facts.Take(maxRows).Select((f, i) => ToRecord(connection, f, i)).ToList();
        return Task.FromResult(new DataSample(schema, rows, truncated));
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(SourceConnection connection, ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var facts = Read(connection);
        for (int i = 0; i < facts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToRecord(connection, facts[i], i);
            await Task.Yield();
        }
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
            ["concept"] = DataValue.StringValue(f.ConceptName, src),
            ["entityScheme"] = DataValue.StringValue(f.EntityScheme, src),
            ["entityIdentifier"] = DataValue.StringValue(f.EntityIdentifier, src),
            ["period"] = DataValue.StringValue(f.Period.ToIsoString(), src),
            ["unit"] = f.Unit is not null
                ? DataValue.StringValue(f.Unit.Measure, src)
                : DataValue.Missing(PortShape.String),
            ["compoundUnit"] = f.CompoundUnit is not null
                ? DataValue.StringValue(f.CompoundUnit.CanonicalKey, src)
                : DataValue.Missing(PortShape.String),
            ["value"] = f.IsNil || f.Value is null
                ? DataValue.Missing(PortShape.Decimal)
                : DataValue.DecimalValue(f.Value.Value, src, f.Value.Value.ToString(CultureInfo.InvariantCulture)),
            ["decimals"] = f.Decimals is not null
                ? DataValue.IntegerValue(f.Decimals.Value)
                : DataValue.StringValue("INF", src),
            ["lang"] = string.IsNullOrEmpty(f.Language)
                ? DataValue.Missing(PortShape.String)
                : DataValue.StringValue(f.Language!, src),
            ["nil"] = DataValue.BoolValue(f.IsNil),
            ["dimensions"] = DataValue.StringValue(
                string.Join(";", f.Dimensions.Select(d => d.ToString())), src)
        };
        var srcs = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
        foreach (var k in fields.Keys) srcs[k] = src;

        return new DataRecord
        {
            RowId = $"{connection.SourceId}#f{index + 1}",
            Fields = fields,
            CellSources = srcs
        };
    }
}
