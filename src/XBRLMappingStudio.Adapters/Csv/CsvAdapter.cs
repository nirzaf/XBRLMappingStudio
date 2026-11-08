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

    public const int DefaultMaximumRowBytes = 4 * 1024 * 1024;
    public const long DefaultMaximumFileBytes = 1024L * 1024 * 1024;
    private readonly Options _options;
    private readonly int _maximumRowBytes;
    private readonly long _maximumFileBytes;

    /// <summary>Test seam that aborts before replacement after staged content is parsed.</summary>
    public Func<string, bool>? ReconcileFailProbe { get; set; }

    public CsvAdapter(Options? options = null,
        int maximumRowBytes = DefaultMaximumRowBytes,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        if (maximumRowBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumRowBytes));
        if (maximumFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        _options = options ?? new Options();
        _maximumRowBytes = maximumRowBytes;
        _maximumFileBytes = maximumFileBytes;
    }

    public Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        using var reader = OpenReader(connection, cancellationToken);
        var first = ReadRows(reader, cancellationToken).FirstOrDefault() ?? Array.Empty<string>();
        var columns = BuildColumns(new List<string[]> { first });
        ISourceSchema schema = TabularSchema.FromSource(connection.SourceId, columns);
        return Task.FromResult(schema);
    }

    public Task<DataSample> ReadSampleAsync(SourceConnection connection, ISourceSchema schema,
        int maxRows, CancellationToken cancellationToken)
    {
        if (maxRows < 0) throw new ArgumentOutOfRangeException(nameof(maxRows));
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
        using var reader = OpenReader(connection, ct);
        var cols = schema.Columns;
        int r = -1;
        foreach (var cells in ReadRows(reader, ct))
        {
            r++;
            if (_options.HasHeader && r == 0) continue;
            ct.ThrowIfCancellationRequested();
            if (cells.Length > cols.Count)
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"CSV row {r + 1} has more fields than the inspected schema.");
            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var sources = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
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

    private StreamReader OpenReader(SourceConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = connection.Path ?? throw new InvalidOperationException(
            $"CSV connection '{connection.SourceId}' has no file path.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        try
        {
            if (stream.Length > _maximumFileBytes)
                throw BudgetExceeded($"CSV source exceeds the {_maximumFileBytes} byte file budget.");
            // Preserve the original contract: strip UTF-8 BOM regardless of declared
            // encoding, but do not auto-detect other BOMs or override that encoding.
            Span<byte> prefix = stackalloc byte[3];
            var count = stream.Read(prefix);
            stream.Position = count == 3 && prefix.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            return new StreamReader(stream, new PreamblelessEncoding(_options.Encoding ?? new UTF8Encoding(false)),
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
        }
        catch { stream.Dispose(); throw; }
    }

    // StreamReader otherwise strips the declared encoding's preamble even when
    // BOM detection is disabled. Legacy decoding stripped UTF-8 only.
    private sealed class PreamblelessEncoding(Encoding inner) : Encoding
    {
        public override byte[] GetPreamble() => Array.Empty<byte>();
        public override Decoder GetDecoder() => inner.GetDecoder();
        public override int GetByteCount(char[] chars, int index, int count) => inner.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            => inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => inner.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override int GetMaxByteCount(int charCount) => inner.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => inner.GetMaxCharCount(byteCount);
    }

    private static AdapterException BudgetExceeded(string message)
        => new(DiagnosticCodes.IoNumberOutOfBudget, message, isInputBudgetExceeded: true);

    private string DecodeText(byte[] bytes)
    {
        var enc = _options.Encoding ?? new UTF8Encoding(false);
        // Strip a UTF-8 BOM regardless of declared encoding so headers parse cleanly.
        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
        return enc.GetString(bytes, offset, bytes.Length - offset);
    }

    private List<string[]> ParseRows(string text)
    {
        using var reader = new StringReader(text);
        return ReadRows(reader, CancellationToken.None, enforceBudgets: false).ToList();
    }

    // A single bounded logical record is retained, including quoted multiline fields.
    // Account for raw syntax as well as decoded content so delimiter/quote-only rows
    // cannot evade the allocation bound. Cancellation is checked even inside a field.
    private IEnumerable<string[]> ReadRows(TextReader reader, CancellationToken ct, bool enforceBudgets = true)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        bool inQuotes = false;
        long rowBytes = 0;
        int scanned = 0;
        void Charge(int bytes)
        {
            rowBytes += bytes;
            if (enforceBudgets && rowBytes > _maximumRowBytes)
                throw BudgetExceeded($"CSV record exceeds the {_maximumRowBytes} byte row budget.");
        }
        void EndField() { Charge(32); record.Add(field.ToString()); field.Clear(); }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if ((scanned++ & 4095) == 0 && reader is StreamReader streamReader
                && streamReader.BaseStream.Length > _maximumFileBytes)
                throw BudgetExceeded($"CSV source exceeds the {_maximumFileBytes} byte file budget.");
            int value = reader.Read();
            if (value < 0) break;
            Charge(2);
            char ch = (char)value;
            if (inQuotes)
            {
                if (ch == _options.Quote)
                {
                    if (reader.Peek() == _options.Quote)
                    {
                        reader.Read(); Charge(2); field.Append(_options.Quote);
                    }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == _options.Quote) inQuotes = true;
            else if (ch == _options.Delimiter) EndField();
            else if (ch == '\r') { /* preserve CR swallowing outside quotes */ }
            else if (ch == '\n')
            {
                EndField();
                yield return record.ToArray();
                record.Clear(); rowBytes = 0;
            }
            else field.Append(ch);
        }
        if (inQuotes)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "CSV output ended inside a quoted field.");
        if (field.Length > 0 || record.Count > 0)
        {
            EndField();
            yield return record.ToArray();
        }
    }

    private static IReadOnlyList<ColumnSchema> BuildColumns(List<string[]> rows)
    {
        var first = rows.Count > 0 ? rows[0] : Array.Empty<string>();
        var cols = new List<ColumnSchema>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int c = 0; c < first.Length; c++)
        {
            var name = first[c].Trim();
            if (name.Length == 0) name = $"col_{c + 1}";
            if (!names.Add(name))
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"CSV schema contains duplicate column name '{name}'.");
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

    /// <summary>
    /// Format mapped records using invariant lexical values. Numeric cells retain their
    /// type-based trust so a negative decimal is not rewritten as spreadsheet text; text
    /// cells still pass through the formula-injection guard.
    /// </summary>
    public string FormatRecords(IReadOnlyList<string> headers, IReadOnlyList<DataRecord> records)
    {
        var sb = new StringBuilder();
        if (_options.HasHeader)
            sb.Append(JoinRow(headers.Select(header => (string?)header)));

        foreach (var record in records)
        {
            if (sb.Length > 0) sb.Append('\n');
            var cells = headers.Select(header =>
            {
                if (!record.Fields.TryGetValue(header, out var value))
                    return (Text: (string?)null, IsTrustedNumber: false);
                return ToCsvCell(value);
            });
            sb.Append(JoinTypedRow(cells));
        }
        return sb.ToString();
    }

    /// <summary>Encode mapped records without a preamble; staging owns the BOM policy.</summary>
    public byte[] WriteRecords(IReadOnlyList<string> headers, IReadOnlyList<DataRecord> records)
    {
        var encoding = _options.Encoding ?? Encoding.UTF8;
        var noPreamble = Encoding.GetEncoding(encoding.CodePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return noPreamble.GetBytes(FormatRecords(headers, records));
    }

    private string JoinTypedRow(IEnumerable<(string? Text, bool IsTrustedNumber)> cells)
        => string.Join(_options.Delimiter, cells.Select(cell => Escape(cell.Text, !cell.IsTrustedNumber)));

    private static (string? Text, bool IsTrustedNumber) ToCsvCell(DataValue value)
    {
        if (value.Presence is ValuePresence.Missing or ValuePresence.ExplicitNil)
            return (null, false);
        if (value.Presence == ValuePresence.Blank)
            return (string.Empty, false);

        return value.Raw switch
        {
            decimal number => (number.ToString(CultureInfo.InvariantCulture), true),
            long number => (number.ToString(CultureInfo.InvariantCulture), true),
            int number => (number.ToString(CultureInfo.InvariantCulture), true),
            bool boolean => (boolean ? "true" : "false", true),
            DateOnly date => (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false),
            DateTimeOffset instant => (instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), false),
            IFormattable formattable => (formattable.ToString(null, CultureInfo.InvariantCulture), false),
            var other => (other?.ToString() ?? string.Empty, false)
        };
    }

    private string JoinRow(IEnumerable<string?> cells)
        => string.Join(_options.Delimiter, cells.Select(cell => Escape(cell)));

    private string Escape(string? value, bool protectFormula = true)
    {
        value ??= "";
        // Formula-injection guard (section 4): neutralize leading formula triggers.
        if (protectFormula && !_options.AllowFormulas && value.Length > 0
            && (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@'))
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
        _ = CountRows(payload);
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
        cancellationToken.ThrowIfCancellationRequested();
        var stagedRows = CountRows(File.ReadAllBytes(stagedPath));
        if (ReconcileFailProbe?.Invoke(stagedPath) == true)
            throw new IOException($"Simulated mid-write failure while publishing '{path}'.");
        File.Move(stagedPath, path, overwrite: true);
        var finalRows = CountRows(File.ReadAllBytes(path));
        if (finalRows != stagedRows)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Published CSV row count ({finalRows}) does not match staged ({stagedRows}).",
                DiagnosticStage.Publication);
        var bytes = new FileInfo(path).Length;
        var digest = RunManifest.ComputeSha256(File.ReadAllBytes(path));
        return Task.FromResult(new OutputIdentity(path, digest, bytes, "csv"));
    }

    private int CountRows(byte[] payload)
    {
        var rows = ParseRows(DecodeText(payload)).Count;
        return Math.Max(0, rows - (_options.HasHeader && rows > 0 ? 1 : 0));
    }
}
