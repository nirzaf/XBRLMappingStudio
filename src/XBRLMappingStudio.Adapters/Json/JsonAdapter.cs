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

    public sealed record Options(
        int MaxDepth = 64,
        int MaxNumberDigits = 28,
        long MaxInputBytes = DefaultMaximumInputBytes,
        int MaxRows = DefaultMaximumRows,
        int MaxStringLength = DefaultMaximumStringLength)
    {
        public const long DefaultMaximumInputBytes = 16L * 1024 * 1024;
        public const long AbsoluteMaximumInputBytes = 64L * 1024 * 1024;
        public const int DefaultMaximumRows = 100_000;
        public const int AbsoluteMaximumRows = 1_000_000;
        public const int DefaultMaximumStringLength = 1_000_000;
        public const int AbsoluteMaximumStringLength = 16 * 1024 * 1024;

        public static Options FromConfiguration(IReadOnlyDictionary<string, string>? cfg)
        {
            if (cfg is null) return new Options();
            int depth = cfg.TryGetValue("maxDepth", out var d) && int.TryParse(d, out var dv) ? dv : 64;
            int digits = cfg.TryGetValue("maxNumberDigits", out var n) && int.TryParse(n, out var nv) ? nv : 28;
            long bytes = cfg.TryGetValue("maxInputBytes", out var b) && long.TryParse(b, out var bv)
                ? bv : DefaultMaximumInputBytes;
            int rows = cfg.TryGetValue("maxRows", out var r) && int.TryParse(r, out var rv)
                ? rv : DefaultMaximumRows;
            int stringLength = cfg.TryGetValue("maxStringLength", out var s) && int.TryParse(s, out var sv)
                ? sv : DefaultMaximumStringLength;
            var options = new Options(depth, digits, bytes, rows, stringLength);
            try
            {
                options.Validate();
            }
            catch (ArgumentOutOfRangeException exception)
            {
                var code = exception.ParamName switch
                {
                    nameof(MaxDepth) => DiagnosticCodes.IoDepthExceeded,
                    nameof(MaxNumberDigits) => DiagnosticCodes.IoNumberOutOfBudget,
                    nameof(MaxRows) => DiagnosticCodes.IoRowBudgetExceeded,
                    nameof(MaxStringLength) => DiagnosticCodes.IoStringBudgetExceeded,
                    _ => DiagnosticCodes.IoInputBudgetExceeded
                };
                throw new AdapterException(code,
                    $"Configured JSON source limit '{exception.ParamName}' is outside the supported range.",
                    isInputBudgetExceeded: true);
            }
            return options;
        }

        public void Validate()
        {
            if (MaxDepth is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
            // Keep caller-selected mantissa budgets compatible with long removable zero
            // tails; the byte ceiling bounds the maximum lexical digit count regardless.
            if (MaxNumberDigits < 1 || MaxNumberDigits > AbsoluteMaximumInputBytes)
                throw new ArgumentOutOfRangeException(nameof(MaxNumberDigits));
            if (MaxInputBytes is < 1 or > AbsoluteMaximumInputBytes)
                throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
            if (MaxRows is < 1 or > AbsoluteMaximumRows) throw new ArgumentOutOfRangeException(nameof(MaxRows));
            if (MaxStringLength is < 1 or > AbsoluteMaximumStringLength)
                throw new ArgumentOutOfRangeException(nameof(MaxStringLength));
        }
    }

    public const long DefaultMaximumInputBytes = Options.DefaultMaximumInputBytes;
    public const int DefaultMaximumRows = Options.DefaultMaximumRows;
    public const int DefaultMaximumStringLength = Options.DefaultMaximumStringLength;

    private readonly Options _options;

    public JsonAdapter(Options? options = null)
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

    // ---- Parsing --------------------------------------------------------------------

    private abstract class JNode { }

    private sealed class JLeaf(string? text, JsonValueKind kind, decimal number = 0m) : JNode
    {
        public string? Text { get; } = text;
        public JsonValueKind Kind { get; } = kind;
        public decimal Number { get; } = number;
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
        var bytes = ReadBytes(connection, ct);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });

        JNode root;
        if (!reader.Read())
            throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "JSON document is empty.");
        root = ReadValue(ref reader, depth: 0, ct);
        if (reader.Read())
            throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "JSON document has trailing content.");

        var records = new List<DataRecord>();
        if (root is JArray arr)
        {
            if (arr.Items.Count > _options.MaxRows)
                throw new AdapterException(DiagnosticCodes.IoRowBudgetExceeded,
                    $"JSON array exceeds the maximum row count of {_options.MaxRows}.",
                    isInputBudgetExceeded: true);
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

    private JNode ReadValue(ref Utf8JsonReader r, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                    cancellationToken.ThrowIfCancellationRequested();
                    if (r.TokenType == JsonTokenType.EndObject) return obj;
                    if (r.TokenType != JsonTokenType.PropertyName)
                        throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "Malformed JSON object.");
                    var name = r.GetString()!;
                    EnsureStringWithinBudget(name);
                    if (!seen.Add(name))
                        throw new AdapterException(DiagnosticCodes.IoDuplicateKey,
                            $"Duplicate JSON key '{name}' at depth {depth}.");
                    if (!r.Read())
                        throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "JSON value missing.");
                    obj.Props.Add((name, ReadValue(ref r, depth + 1, cancellationToken)));
                }
                throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "Unterminated JSON object.");
            }
            case JsonTokenType.StartArray:
            {
                var arr = new JArray();
                while (r.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (r.TokenType == JsonTokenType.EndArray) return arr;
                    if (depth == 0 && arr.Items.Count >= _options.MaxRows)
                        throw new AdapterException(DiagnosticCodes.IoRowBudgetExceeded,
                            $"JSON array exceeds the maximum row count of {_options.MaxRows}.",
                            isInputBudgetExceeded: true);
                    arr.Items.Add(ReadValue(ref r, depth + 1, cancellationToken));
                }
                throw new AdapterException(DiagnosticCodes.IoUnsupportedEncoding, "Unterminated JSON array.");
            }
            case JsonTokenType.String:
            {
                var text = r.GetString();
                EnsureStringWithinBudget(text ?? string.Empty);
                return new JLeaf(text, JsonValueKind.String);
            }
            case JsonTokenType.Number:
            {
                var raw = NumberRaw(r);
                if (SignificantDigits(raw) > _options.MaxNumberDigits
                    || !ExactDecimalLexicalParser.TryParseExact(raw, allowExponent: true, out var exact))
                    throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                        $"JSON number '{raw}' exceeds the exact-decimal budget ({_options.MaxNumberDigits} digits).");
                // Retain the reader's decimal scale where possible, while keeping the
                // original token (not a formatted decimal) as lexical evidence.
                var number = r.TryGetDecimal(out var parsed) && parsed == exact ? parsed : exact;
                return new JLeaf(raw, JsonValueKind.Number, number);
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

    private void EnsureStringWithinBudget(string value)
    {
        if (value.Length > _options.MaxStringLength)
            throw new AdapterException(DiagnosticCodes.IoStringBudgetExceeded,
                $"JSON string or property name exceeds the maximum length of {_options.MaxStringLength} characters.",
                isInputBudgetExceeded: true);
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
            if (ch is 'e' or 'E') break; // the budget counts mantissa digits, including trailing zeros
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
                    if (nested.Props.Count == 0)
                        SetLeaf(path, nested, fields, srcs, sourceId, rowIdx);
                    else
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
            JLeaf { Kind: JsonValueKind.Null } => DataValue.ExplicitNil(PortShape.String),
            JLeaf { Kind: JsonValueKind.Number } l => DataValue.DecimalValue(
                l.Number, src, l.Text),
            JLeaf { Kind: JsonValueKind.True or JsonValueKind.False } l
                => DataValue.BoolValue(l.Kind == JsonValueKind.True),
            JLeaf l => DataValue.StringValue(l.Text ?? "", src),
            JArray a => DataValue.Opaque(PortShape.Record, Render(a), src),
            JObject o => DataValue.Opaque(PortShape.Record, Render(o), src),
            _ => DataValue.Missing()
        };
        if (!fields.TryAdd(path, value))
            throw new AdapterException(DiagnosticCodes.IoDuplicateKey,
                $"Multiple JSON values resolve to field '{path}' in row {rowIdx + 1}.");
        srcs.Add(path, src);
    }

    private static string Render(JNode node) => node switch
    {
        JLeaf l => l.Kind == JsonValueKind.String
            ? JsonSerializer.Serialize(l.Text)
            : (l.Text ?? "null"),
        JArray a => "[" + string.Join(", ", a.Items.Select(Render)) + "]",
        JObject o => "{" + string.Join(", ", o.Props.Select(p =>
            $"{JsonSerializer.Serialize(p.Key)}: {Render(p.Value)}")) + "}",
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

    private byte[] ReadBytes(SourceConnection connection, CancellationToken cancellationToken)
    {
        var path = connection.Path ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            $"JSON connection '{connection.SourceId}' has no file path.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        using var content = new MemoryStream((int)Math.Min(stream.Length, _options.MaxInputBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (content.Length + read > _options.MaxInputBytes)
                throw new AdapterException(DiagnosticCodes.IoInputBudgetExceeded,
                    $"JSON input exceeds the maximum size of {_options.MaxInputBytes} bytes.",
                    isInputBudgetExceeded: true);
            content.Write(buffer, 0, read);
        }
        return content.ToArray();
    }
}
