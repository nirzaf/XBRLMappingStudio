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
    public const long DefaultMaximumInputBytes = Options.DefaultMaximumInputBytes;

    public sealed record Options(
        int MaxDepth = 128,
        int MaxRows = 100_000,
        int MaxStringLength = 1_000_000,
        long MaxInputBytes = DefaultMaximumInputBytes)
    {
        public const long DefaultMaximumInputBytes = 64L * 1024 * 1024;
        public const long AbsoluteMaximumInputBytes = 64L * 1024 * 1024;
        public const int DefaultMaximumDocumentCharacters = 16 * 1024 * 1024;
        public const int AbsoluteMaximumDepth = 256;
        public const int AbsoluteMaximumRows = 1_000_000;
        public const int AbsoluteMaximumStringLength = 16 * 1024 * 1024;

        public static Options FromConfiguration(IReadOnlyDictionary<string, string>? cfg)
        {
            if (cfg is null) return new Options();
            int depth = cfg.TryGetValue("maxDepth", out var d) && int.TryParse(d, out var dv) ? dv : 128;
            int rows = cfg.TryGetValue("maxRows", out var r) && int.TryParse(r, out var rv) ? rv : 100_000;
            int stringLength = cfg.TryGetValue("maxStringLength", out var s) && int.TryParse(s, out var sv)
                ? sv : 1_000_000;
            long bytes = cfg.TryGetValue("maxInputBytes", out var b) && long.TryParse(b, out var bv)
                ? bv : DefaultMaximumInputBytes;
            var options = new Options(depth, rows, stringLength, bytes);
            try
            {
                options.Validate();
            }
            catch (ArgumentOutOfRangeException exception)
            {
                var code = exception.ParamName switch
                {
                    nameof(MaxDepth) => DiagnosticCodes.IoDepthExceeded,
                    nameof(MaxRows) => DiagnosticCodes.IoRowBudgetExceeded,
                    nameof(MaxStringLength) => DiagnosticCodes.IoStringBudgetExceeded,
                    _ => DiagnosticCodes.IoInputBudgetExceeded
                };
                throw new AdapterException(code,
                    $"Configured XML source limit '{exception.ParamName}' is outside the supported range.",
                    isInputBudgetExceeded: true);
            }
            return options;
        }

        public void Validate()
        {
            if (MaxDepth is < 0 or > AbsoluteMaximumDepth) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
            if (MaxRows is < 1 or > AbsoluteMaximumRows) throw new ArgumentOutOfRangeException(nameof(MaxRows));
            if (MaxStringLength is < 1 or > AbsoluteMaximumStringLength)
                throw new ArgumentOutOfRangeException(nameof(MaxStringLength));
            if (MaxInputBytes is < 1 or > AbsoluteMaximumInputBytes)
                throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
        }
    }

    private readonly Options _options;

    public XmlAdapter(Options? options = null)
    {
        _options = options ?? new Options();
        _options.Validate();
    }

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

    private List<DataRecord> ReadRecords(SourceConnection connection, CancellationToken ct)
    {
        var path = connection.Path ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            $"XML connection '{connection.SourceId}' has no file path.");

        XDocument doc;
        try
        {
            using var bytes = ReadBytes(path, ct);
            ValidateDocumentBounds(bytes, ct);
            using var parseStream = OpenSnapshot(bytes);
            using var reader = XmlReader.Create(parseStream, CreateReaderSettings());
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

        var rowElements = SelectRowset(doc.Root).ToArray();
        if (rowElements.Length > _options.MaxRows)
            throw BudgetExceeded(DiagnosticCodes.IoRowBudgetExceeded,
                $"XML rowset exceeds the maximum row count of {_options.MaxRows}.");
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

    private void ValidateDocumentBounds(MemoryStream bytes, CancellationToken cancellationToken)
    {
        var rootChildCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var scanStream = OpenSnapshot(bytes);
        using var reader = XmlReader.Create(scanStream, CreateReaderSettings());
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Depth > _options.MaxDepth)
                    throw BudgetExceeded(DiagnosticCodes.IoDepthExceeded,
                        $"XML nesting exceeds the maximum depth of {_options.MaxDepth}.");
                EnsureStringWithinBudget(reader.LocalName);
                EnsureStringWithinBudget(reader.NamespaceURI);
                if (reader.Depth == 1)
                {
                    var key = reader.NamespaceURI + "\0" + reader.LocalName;
                    rootChildCounts.TryGetValue(key, out var count);
                    count++;
                    if (count > _options.MaxRows)
                        throw BudgetExceeded(DiagnosticCodes.IoRowBudgetExceeded,
                            $"XML rowset exceeds the maximum row count of {_options.MaxRows}.");
                    rootChildCounts[key] = count;
                }
                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        EnsureStringWithinBudget(reader.LocalName);
                        EnsureStringWithinBudget(reader.NamespaceURI);
                        EnsureStringWithinBudget(reader.Value);
                    }
                    reader.MoveToElement();
                }
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                     or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                EnsureStringWithinBudget(reader.Value);
            }
        }
    }

    private static XmlReaderSettings CreateReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = Options.DefaultMaximumDocumentCharacters
    };

    private static MemoryStream OpenSnapshot(MemoryStream bytes)
        => new(bytes.GetBuffer(), 0, checked((int)bytes.Length), writable: false);

    private MemoryStream ReadBytes(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        var content = new MemoryStream((int)Math.Min(stream.Length, _options.MaxInputBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (content.Length + read > _options.MaxInputBytes)
                throw BudgetExceeded(DiagnosticCodes.IoInputBudgetExceeded,
                    $"XML input exceeds the maximum size of {_options.MaxInputBytes} bytes.");
            content.Write(buffer, 0, read);
        }
        return content;
    }

    private void EnsureStringWithinBudget(string value)
    {
        if (value.Length > _options.MaxStringLength)
            throw BudgetExceeded(DiagnosticCodes.IoStringBudgetExceeded,
                $"XML name or text value exceeds the maximum length of {_options.MaxStringLength} characters.");
    }

    private static AdapterException BudgetExceeded(string code, string message)
        => new(code, message, isInputBudgetExceeded: true);

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

    private void FlattenElement(string prefix, XElement el,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs,
        string sourceId, int rowIdx)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var nil = (string?)el.Attribute(xsi + "nil");
        if (nil is "true" or "1")
        {
            if (prefix.Length == 0) prefix = "_text";
            var nilSource = new SourceReference(sourceId, $"xml#r{rowIdx + 1}:{prefix}");
            AddField(prefix, DataValue.ExplicitNil(PortShape.String), nilSource, fields, srcs, rowIdx);
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

    private void SetValue(string key, string raw,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs,
        string sourceId, int rowIdx)
    {
        if (key.Length == 0) key = "_text";
        EnsureStringWithinBudget(key);
        EnsureStringWithinBudget(raw);
        var src = new SourceReference(sourceId, $"xml#r{rowIdx + 1}:{key}");
        DataValue value;
        if (string.IsNullOrWhiteSpace(raw))
            value = DataValue.Missing(PortShape.String);
        else if (decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                 CultureInfo.InvariantCulture, out var d))
        {
            // XML is untyped: preserve the existing inference grammar and text fallback
            // (including overflow-as-text), but never accept rounded/underflowed numbers.
            if (!ExactDecimalLexicalParser.TryParseExact(raw, allowExponent: false, out var exact) || exact != d)
                throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                    $"XML number '{raw}' cannot be represented as an exact decimal.");
            value = DataValue.DecimalValue(d, src, raw);
        }
        else
            value = DataValue.StringValue(raw.Trim(), src);
        AddField(key, value, src, fields, srcs, rowIdx);
    }

    private static void AddField(string key, DataValue value, SourceReference source,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs, int rowIdx)
    {
        if (!fields.TryAdd(key, value))
            throw new AdapterException(DiagnosticCodes.IoDuplicateKey,
                $"Multiple XML values resolve to field '{key}' in row {rowIdx + 1}.");
        srcs.Add(key, source);
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
