using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Csv;

/// <summary>
/// CSV source and target adapter (IO04). Read honours a declared delimiter, quote
/// character, header policy and encoding; write additionally enforces a BOM policy and
/// a formula-injection guard that prefixes a leading apostrophe to any value that could
/// be interpreted as a spreadsheet formula.
/// </summary>
public sealed class CsvAdapter : ISourceAdapter, ITargetAdapter
{
    public string Kind => "csv";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadWrite;

    public sealed record Options(
        char Delimiter = ',',
        char Quote = '"',
        Encoding? Encoding = null,
        bool HasHeader = true,
        bool EmitBom = true,
        string Culture = "invariant",
        bool AllowFormulas = false)
    {
        public static Options FromConfiguration(IReadOnlyDictionary<string, string>? cfg)
        {
            if (cfg is null) return new Options();
            char delim = cfg.TryGetValue("delimiter", out var d) && d.Length == 1 ? d[0] : ',';
            char quote = cfg.TryGetValue("quote", out var q) && q.Length == 1 ? q[0] : '"';
            bool header = !(cfg.TryGetValue("hasHeader", out var h) && h.Equals("false", StringComparison.OrdinalIgnoreCase));
            bool bom = !(cfg.TryGetValue("bom", out var b) && b.Equals("false", StringComparison.OrdinalIgnoreCase));
            string culture = cfg.TryGetValue("culture", out var c) ? c : "invariant";
            bool allow = cfg.TryGetValue("allowFormulas", out var a) && a.Equals("true", StringComparison.OrdinalIgnoreCase);
            Encoding enc = cfg.TryGetValue("encoding", out var e) ? SafeEncoding(e) : new UTF8Encoding(bom);
            return new Options(delim, quote, enc, header, bom, culture, allow);
        }

        private static Encoding SafeEncoding(string name)
        {
            try { return Encoding.GetEncoding(name); }
            catch (ArgumentException) { return new UTF8Encoding(true); }
        }
    }

    private readonly Options _options;

    public CsvAdapter(Options? options = null) => _options = options ?? new Options();

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var text = ReadText(connection);
        var rows = ParseRows(text);
        var columns = BuildColumns(rows);
        ISourceSchema schema = TabularSchema.FromSource(connection.SourceId, columns);
        return Task.FromResult(schema);
    }

    public Task<DataSample> ReadSampleAsync(SourceConnection connection, ISourceSchema schema,
        int maxRows, CancellationToken cancellationToken)
    {
        var rows = new List<DataRecord>();
        bool truncated = false;
        int i = 0;
        foreach (var r in Enumerate(connection, schema, cancellationToken))
        {
            if (i++ >= maxRows) { truncated = true; break; }
            rows.Add(r);
        }
        return Task.FromResult(new DataSample(schema, rows, truncated));
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(SourceConnection connection, ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var r in Enumerate(connection, schema, cancellationToken))
        {
            yield return r;
            await Task.Yield();
        }
    }

    private IEnumerable<DataRecord> Enumerate(SourceConnection connection, ISourceSchema schema, CancellationToken ct)
    {
        var text = ReadText(connection);
        var parsed = ParseRows(text);
        var cols = schema.Columns;
        int start = _options.HasHeader ? 1 : 0;
        for (int r = start; r < parsed.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var sources = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
            var cells = parsed[r];
            for (int c = 0; c < cols.Count; c++)
            {
                var value = c < cells.Length ? cells[c] : "";
                var src = new SourceReference(connection.SourceId, $"row={r + 1},col={c + 1}");
                fields[cols[c].Name] = value.Length == 0
                    ? DataValue.Missing(PortShape.String)
                    : DataValue.StringValue(value, src);
                sources[cols[c].Name] = src;
            }
            yield return new DataRecord
            {
                RowId = $"{connection.SourceId}#r{r + 1}",
                Fields = fields,
                CellSources = sources
            };
        }
    }

    private string ReadText(SourceConnection connection)
    {
        var path = connection.Path ?? throw new InvalidOperationException(
            $"CSV connection '{connection.SourceId}' has no file path.");
        var bytes = File.ReadAllBytes(path);
        var enc = _options.Encoding ?? new UTF8Encoding(false);
        // Strip a UTF-8 BOM regardless of declared encoding so headers parse cleanly.
        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
        return enc.GetString(bytes, offset, bytes.Length - offset);
    }

    private List<string[]> ParseRows(string text)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var record = new List<string>();
        bool inQuotes = false;

        void EndField() { record.Add(field.ToString()); field.Clear(); }
        void EndRecord() { EndField(); rows.Add(record.ToArray()); record.Clear(); }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == _options.Quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == _options.Quote) { field.Append(_options.Quote); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == _options.Quote) inQuotes = true;
            else if (ch == _options.Delimiter) EndField();
            else if (ch == '\r') { /* swallow, handled with \n */ }
            else if (ch == '\n') EndRecord();
            else field.Append(ch);
        }
        // Trailing field/record (handles empty trailing field and final line w/o newline).
        if (field.Length > 0 || record.Count > 0) EndRecord();
        return rows;
    }

    private static IReadOnlyList<ColumnSchema> BuildColumns(List<string[]> rows)
    {
        var first = rows.Count > 0 ? rows[0] : Array.Empty<string>();
        var cols = new List<ColumnSchema>();
        for (int c = 0; c < first.Length; c++)
        {
            var name = first[c].Trim();
            if (name.Length == 0) name = $"col_{c + 1}";
            cols.Add(new ColumnSchema(name, PortShape.String, c, name));
        }
        return cols;
    }

    // ---- Write path (IO04) ----------------------------------------------------------

    public string Format(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> records)
    {
        var sb = new StringBuilder();
        if (_options.HasHeader)
            sb.Append(JoinRow(headers.Select(h => (string?)h)));
        foreach (var rec in records)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(JoinRow(rec));
        }
        return sb.ToString();
    }

    private string JoinRow(IEnumerable<string?> cells)
        => string.Join(_options.Delimiter, cells.Select(Escape));

    private string Escape(string? value)
    {
        value ??= "";
        // Formula-injection guard (section 4): neutralize leading formula triggers.
        if (!_options.AllowFormulas && value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@'))
            value = "'" + value;

        bool needsQuote = value.Contains(_options.Delimiter)
                          || value.Contains(_options.Quote)
                          || value.Contains('\n')
                          || value.Contains('\r');
        if (needsQuote)
            return $"{_options.Quote}{value.Replace(_options.Quote.ToString(), new string(_options.Quote, 2))}{_options.Quote}";
        return value;
    }

    public Task<string> StageArtifactAsync(SourceConnection destination, byte[] payload,
        string suggestedExtension, CancellationToken cancellationToken)
    {
        var path = destination.Path ?? throw new InvalidOperationException("CSV destination has no path.");
        var staged = path + ".staged";
        var enc = _options.Encoding ?? new UTF8Encoding(_options.EmitBom);
        using (var fs = new FileStream(staged, FileMode.Create, FileAccess.Write))
        {
            if (_options.EmitBom && enc.CodePage == Encoding.UTF8.CodePage)
                fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
            fs.Write(payload, 0, payload.Length);
        }
        return Task.FromResult(staged);
    }

    public Task<OutputIdentity> PublishAsync(string stagedPath, SourceConnection destination,
        CancellationToken cancellationToken)
    {
        var path = destination.Path ?? throw new InvalidOperationException("CSV destination has no path.");
        File.Move(stagedPath, path, overwrite: true);
        var bytes = new FileInfo(path).Length;
        var digest = RunManifest.ComputeSha256(File.ReadAllBytes(path));
        return Task.FromResult(new OutputIdentity(path, digest, bytes, "csv"));
    }
}
