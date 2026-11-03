using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Json;

/// <summary>
/// Generic JSON source adapter (IO05). Accepts a top-level object (single record) or a
/// top-level array (one record per object element). Nested objects are flattened to
/// dotted-path columns. Reading enforces three guide §4 budgets: duplicate keys are
/// rejected, nesting beyond <see cref="Options.MaxDepth"/> is rejected, and any number
/// that cannot be represented as an exact decimal is rejected as out-of-budget.
/// </summary>
public sealed class JsonAdapter : ISourceAdapter
{
    public string Kind => "json";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;

    public sealed record Options(int MaxDepth = 64, int MaxNumberDigits = 28)
    {
        public static Options FromConfiguration(IReadOnlyDictionary<string, string>? cfg)
        {
            if (cfg is null) return new Options();
            int depth = cfg.TryGetValue("maxDepth", out var d) && int.TryParse(d, out var dv) ? dv : 64;
            int digits = cfg.TryGetValue("maxNumberDigits", out var n) && int.TryParse(n, out var nv) ? nv : 28;
            return new Options(depth, digits);
        }
    }

    private readonly Options _options;

    public JsonAdapter(Options? options = null) => _options = options ?? new Options();

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

    // ---- Parsing --------------------------------------------------------------------

    private abstract class JNode { }

    private sealed class JLeaf(string? text, JsonValueKind kind) : JNode
    {
        public string? Text { get; } = text;
        public JsonValueKind Kind { get; } = kind;
    }

    private sealed class JObject : JNode
    {
        public List<(string Key, JNode Value)> Props { get; } = new();
    }

    private sealed class JArray : JNode
    {
        public List<JNode> Items { get; } = new();
    }

    private List<DataRecord> ReadRecords(SourceConnection connection, CancellationToken ct)
    {
        var bytes = ReadBytes(connection);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });

        JNode root;
        if (!reader.Read())
            throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "JSON document is empty.");
        root = ReadValue(ref reader, depth: 0);
        if (reader.Read())
            throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "JSON document has trailing content.");

        var records = new List<DataRecord>();
        if (root is JArray arr)
        {
            int idx = 0;
            foreach (var item in arr.Items)
            {
                ct.ThrowIfCancellationRequested();
                var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
                var srcs = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
                if (item is JObject o)
                    Flatten("", o, fields, srcs, connection.SourceId, idx);
                else
                    SetLeaf("value", item, fields, srcs, connection.SourceId, idx);
                records.Add(Record($"{connection.SourceId}#r{idx + 1}", fields, srcs));
                idx++;
            }
        }
        else if (root is JObject top)
        {
            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var srcs = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
            Flatten("", top, fields, srcs, connection.SourceId, 0);
            records.Add(Record($"{connection.SourceId}#r1", fields, srcs));
        }
        else
        {
            // A bare scalar is treated as a single-record, single-column source.
            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var srcs = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
            SetLeaf("value", root, fields, srcs, connection.SourceId, 0);
            records.Add(Record($"{connection.SourceId}#r1", fields, srcs));
        }
        return records;
    }

    private JNode ReadValue(ref Utf8JsonReader r, int depth)
    {
        if (depth > _options.MaxDepth)
            throw new AdapterException(DiagnosticCodes.IoDepthExceeded,
                $"JSON nesting exceeds the maximum depth of {_options.MaxDepth}.");

        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var obj = new JObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndObject) return obj;
                    if (r.TokenType != JsonTokenType.PropertyName)
                        throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "Malformed JSON object.");
                    var name = r.GetString()!;
                    if (!seen.Add(name))
                        throw new AdapterException(DiagnosticCodes.IoDuplicateKey,
                            $"Duplicate JSON key '{name}' at depth {depth}.");
                    if (!r.Read())
                        throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "JSON value missing.");
                    obj.Props.Add((name, ReadValue(ref r, depth + 1)));
                }
                throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "Unterminated JSON object.");
            }
            case JsonTokenType.StartArray:
            {
                var arr = new JArray();
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndArray) return arr;
                    arr.Items.Add(ReadValue(ref r, depth + 1));
                }
                throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "Unterminated JSON array.");
            }
            case JsonTokenType.String:
                return new JLeaf(r.GetString(), JsonValueKind.String);
            case JsonTokenType.Number:
            {
                var raw = NumberRaw(r);
                if (!r.TryGetDecimal(out var dec) || SignificantDigits(raw) > _options.MaxNumberDigits)
                    throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                        $"JSON number '{raw}' exceeds the exact-decimal budget ({_options.MaxNumberDigits} digits).");
                return new JLeaf(dec.ToString(CultureInfo.InvariantCulture), JsonValueKind.Number);
            }
            case JsonTokenType.True:
                return new JLeaf("true", JsonValueKind.True);
            case JsonTokenType.False:
                return new JLeaf("false", JsonValueKind.False);
            case JsonTokenType.Null:
                return new JLeaf(null, JsonValueKind.Null);
            default:
                throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding,
                    $"Unsupported JSON token '{r.TokenType}'.");
        }
    }

    private static string NumberRaw(Utf8JsonReader r)
    {
        if (r.HasValueSequence)
        {
            var sb = new StringBuilder();
            foreach (var seg in r.ValueSequence)
                sb.Append(Encoding.UTF8.GetString(seg.Span));
            return sb.ToString();
        }
        return Encoding.UTF8.GetString(r.ValueSpan);
    }

    private static int SignificantDigits(string raw)
    {
        int count = 0;
        bool leadingDone = false;
        foreach (var ch in raw)
        {
            if (ch is 'e' or 'E') break; // exponent restarts the budget in practice
            if (char.IsAsciiDigit(ch))
            {
                if (ch != '0' || leadingDone) { leadingDone = true; count++; }
            }
        }
        return count;
    }

    private void Flatten(string prefix, JObject obj,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs,
        string sourceId, int rowIdx)
    {
        foreach (var (key, value) in obj.Props)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            switch (value)
            {
                case JObject nested:
                    Flatten(path, nested, fields, srcs, sourceId, rowIdx);
                    break;
                case JArray arr:
                    SetLeaf(path, arr, fields, srcs, sourceId, rowIdx);
                    break;
                default:
                    SetLeaf(path, value, fields, srcs, sourceId, rowIdx);
                    break;
            }
        }
    }

    private static void SetLeaf(string path, JNode node,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs,
        string sourceId, int rowIdx)
    {
        var src = new SourceReference(sourceId, $"json#r{rowIdx + 1}:{path}");
        DataValue value = node switch
        {
            JLeaf { Kind: JsonValueKind.Null } => DataValue.Missing(),
            JLeaf { Kind: JsonValueKind.Number } l => DataValue.DecimalValue(
                decimal.Parse(l.Text!, CultureInfo.InvariantCulture), src, l.Text),
            JLeaf { Kind: JsonValueKind.True or JsonValueKind.False } l
                => DataValue.BoolValue(l.Kind == JsonValueKind.True),
            JLeaf l => DataValue.StringValue(l.Text ?? "", src),
            JArray a => DataValue.StringValue(Render(a), src),
            JObject o => DataValue.StringValue(Render(o), src),
            _ => DataValue.Missing()
        };
        fields[path] = value;
        srcs[path] = src;
    }

    private static string Render(JNode node) => node switch
    {
        JLeaf l => l.Kind == JsonValueKind.String ? $"\"{l.Text}\"" : (l.Text ?? "null"),
        JArray a => "[" + string.Join(", ", a.Items.Select(Render)) + "]",
        JObject o => "{" + string.Join(", ", o.Props.Select(p => $"\"{p.Key}\": {Render(p.Value)}")) + "}",
        _ => ""
    };

    private static DataRecord Record(string rowId,
        Dictionary<string, DataValue> fields, Dictionary<string, SourceReference> srcs)
        => new DataRecord { RowId = rowId, Fields = fields, CellSources = srcs };

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

    private static byte[] ReadBytes(SourceConnection connection)
    {
        var path = connection.Path ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            $"JSON connection '{connection.SourceId}' has no file path.");
        return File.ReadAllBytes(path);
    }
}
