using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Xml;

/// <summary>
/// Generic XML source adapter (IO06). Reading is hardened: DTD processing is prohibited
/// and no external resolver is attached, so DOCTYPE/entity-expansion and external-entity
/// attempts fail closed with <see cref="DiagnosticCodes.IoUnsafeXmlConstruct"/>. Elements
/// and attributes are keyed by their namespace-qualified name so the same local name in
/// two namespaces stays distinct. A rowset is discovered as the most-repeated child
/// element of the document root.
/// </summary>
public sealed class XmlAdapter : ISourceAdapter
{
    public string Kind => "xml";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var records = ReadRecords(connection, cancellationToken);
        var columns = DiscoverColumns(records);
        ISourceSchema schema = TabularSchema.FromSource(connection.SourceId, columns);
        return Task.FromResult(schema);
    }

    public Task<DataSample> ReadSampleAsync(SourceConnection connection, ISourceSchema schema,
        int maxRows, CancellationToken cancellationToken)
    {
        var records = ReadRecords(connection, cancellationToken);
        bool truncated = records.Count > maxRows;
        var sample = truncated ? records.Take(maxRows).ToList() : records;
        return Task.FromResult(new DataSample(schema, sample, truncated));
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(SourceConnection connection, ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var records = ReadRecords(connection, cancellationToken);
        foreach (var r in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return r;
            await Task.Yield();
        }
    }

    private static List<DataRecord> ReadRecords(SourceConnection connection, CancellationToken ct)
    {
        var path = connection.Path ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            $"XML connection '{connection.SourceId}' has no file path.");

        XDocument doc;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 16 * 1024 * 1024
        };
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            // Prohibited DTD / entity expansion / malformed markup all surface here.
            throw new AdapterException(DiagnosticCodes.IoUnsafeXmlConstruct,
                $"XML rejected ({ex.Message}). DTDs and external entities are prohibited.",
                DiagnosticStage.Introspection);
        }

        if (doc.Root is null)
            return new List<DataRecord>();

        var rowElements = SelectRowset(doc.Root);
        var records = new List<DataRecord>();
        int idx = 0;
        foreach (var el in rowElements)
        {
            ct.ThrowIfCancellationRequested();
            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var srcs = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
            FlattenElement("", el, fields, srcs, connection.SourceId, idx);
            records.Add(new DataRecord
            {
                RowId = $"{connection.SourceId}#r{idx + 1}",
                Fields = fields,
                CellSources = srcs
            });
            idx++;
        }
        return records;
    }

    /// <summary>
    /// The rowset is the group of same-named root children with the highest count; ties
    /// break by first appearance. A single-child or heterogeneous root yields one record.
    /// </summary>
    private static IEnumerable<XElement> SelectRowset(XElement root)
    {
        var children = root.Elements().ToList();
        if (children.Count == 0) return new[] { root };

        var groups = children
            .GroupBy(e => Qualified(e.Name))
            .Select((g, i) => new { g.Key, Items = g.ToList(), First = i })
            .OrderByDescending(x => x.Items.Count)
            .ThenBy(x => x.First)
            .First();

        // Only treat it as a repeating rowset when a name actually repeats; otherwise the
        // whole root is one record.
        return groups.Items.Count > 1 ? groups.Items : new[] { root };
    }

    private static void FlattenElement(string prefix, XElement el,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs,
        string sourceId, int rowIdx)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var nil = (string?)el.Attribute(xsi + "nil");
        if (nil is "true" or "1")
        {
            if (prefix.Length == 0) prefix = "_text";
            var nilSource = new SourceReference(sourceId, $"xml#r{rowIdx + 1}:{prefix}");
            fields[prefix] = DataValue.ExplicitNil(PortShape.String);
            srcs[prefix] = nilSource;
            return;
        }

        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            var key = Join(prefix, Qualified(attr.Name));
            SetValue(key, attr.Value, fields, srcs, sourceId, rowIdx);
        }

        var childElements = el.Elements().ToList();
        if (childElements.Count == 0)
        {
            // Leaf element: its text is the value of this path.
            SetValue(prefix, el.Value, fields, srcs, sourceId, rowIdx);
            return;
        }

        // If the same child name repeats, collapse to a rendered list; else recurse.
        var byName = childElements.GroupBy(e => Qualified(e.Name)).ToList();
        foreach (var grp in byName)
        {
            var key = Join(prefix, grp.Key);
            if (grp.Count() > 1)
            {
                var rendered = string.Join("; ", grp.Select(g => g.HasElements ? g.ToString(SaveOptions.DisableFormatting) : g.Value));
                SetValue(key, rendered, fields, srcs, sourceId, rowIdx);
            }
            else
            {
                var only = grp.First();
                if (only.HasElements)
                    FlattenElement(key, only, fields, srcs, sourceId, rowIdx);
                else
                    FlattenElement(key, only, fields, srcs, sourceId, rowIdx);
            }
        }
    }

    private static void SetValue(string key, string raw,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs,
        string sourceId, int rowIdx)
    {
        if (key.Length == 0) key = "_text";
        var src = new SourceReference(sourceId, $"xml#r{rowIdx + 1}:{key}");
        if (string.IsNullOrWhiteSpace(raw))
            fields[key] = DataValue.Missing(PortShape.String);
        else if (decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                 CultureInfo.InvariantCulture, out var d))
            fields[key] = DataValue.DecimalValue(d, src, raw);
        else
            fields[key] = DataValue.StringValue(raw.Trim(), src);
        srcs[key] = src;
    }

    private static string Join(string prefix, string name)
        => prefix.Length == 0 ? name : $"{prefix}.{name}";

    /// <summary>Namespace-qualified local name so identical locals in different namespaces are distinct.</summary>
    private static string Qualified(XName name)
        => name.NamespaceName.Length == 0 ? name.LocalName : $"[{name.NamespaceName}]{name.LocalName}";

    private static IReadOnlyList<ColumnSchema> DiscoverColumns(List<DataRecord> records)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
            foreach (var k in r.Fields.Keys)
                if (seen.Add(k)) names.Add(k);
        var cols = new List<ColumnSchema>();
        for (int i = 0; i < names.Count; i++)
            cols.Add(new ColumnSchema(names[i], PortShape.String, i, names[i]));
        return cols;
    }
}
