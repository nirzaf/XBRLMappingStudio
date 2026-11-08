using System.Text;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class CsvStreamingTests
{
    [Theory]
    [InlineData("Name,Name")]
    [InlineData("Name, Name ")]
    [InlineData(",col_1")]
    public async Task DuplicateResolvedColumnNamesAreRejected(string header)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("duplicate.csv");
        await File.WriteAllTextAsync(path, header + "\n1,2");
        var error = await Assert.ThrowsAsync<AdapterException>(() => new CsvAdapter()
            .InspectSchemaAsync(new SourceConnection("in", "csv", path), default));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
    }

    [Fact]
    public async Task SourceRowBudgetDoesNotChangeDestinationPayloadContract()
    {
        using var ws = new TempWorkspace();
        var connection = new SourceConnection("out", "csv", ws.PathFor("output.csv"));
        var adapter = new CsvAdapter(new CsvAdapter.Options(EmitBom: false), maximumRowBytes: 32);
        var payload = Encoding.UTF8.GetBytes("Name\n" + new string('x', 1000));
        var staged = await adapter.StageArtifactAsync(connection, payload, ".csv", default);
        await adapter.PublishAsync(staged, connection, default);
        Assert.Equal(payload, await File.ReadAllBytesAsync(connection.Path!));
    }

    [Fact]
    public async Task CancellationDuringQuotedFieldScanStopsBeforeRecordCompletes()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("cancel-field.csv");
        await File.WriteAllTextAsync(path, "\"" + new string('x', 20_000) + "\"");
        using var cts = new CancellationTokenSource();
        int decoded = 0;
        var encoding = new CancellingUtf8Encoding(() =>
        {
            if (++decoded == 2) cts.Cancel();
        });
        var adapter = new CsvAdapter(new CsvAdapter.Options(Encoding: encoding));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.InspectSchemaAsync(new SourceConnection("in", "csv", path), cts.Token));
        Assert.Equal(2, decoded);
    }

    private sealed class CancellingUtf8Encoding(Action onDecode) : UTF8Encoding(false)
    {
        public override Decoder GetDecoder() => new CancellingDecoder(base.GetDecoder(), onDecode);
    }

    private sealed class CancellingDecoder(Decoder inner, Action onDecode) : Decoder
    {
        public override int GetCharCount(byte[] bytes, int index, int count)
            => inner.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            onDecode();
            return inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        }
        public override void Convert(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush,
            out int bytesUsed, out int charsUsed, out bool completed)
        {
            onDecode();
            inner.Convert(bytes, chars, flush, out bytesUsed, out charsUsed, out completed);
        }
    }

    [Fact]
    public async Task DeclaredUtf16DoesNotSilentlyStripItsPreamble()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("utf16.csv");
        await File.WriteAllBytesAsync(path, Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("Name\nété")).ToArray());
        var connection = new SourceConnection("in", "csv", path);
        var adapter = new CsvAdapter(new CsvAdapter.Options(Encoding: Encoding.Unicode));
        var schema = await adapter.InspectSchemaAsync(connection, default);
        Assert.Equal("\uFEFFName", schema.Columns[0].Name);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        Assert.Equal("été", sample.Rows.Single()["\uFEFFName"].AsString());
    }

    [Fact]
    public async Task EncodingAndBlankRecordsKeepLegacySemantics()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("latin.csv");
        await File.WriteAllBytesAsync(path, new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.Latin1.GetBytes("Name,Note\r\nété,x\r\n\n" )).ToArray());
        var connection = new SourceConnection("in", "csv", path);
        var adapter = new CsvAdapter(new CsvAdapter.Options(Encoding: Encoding.Latin1));
        var schema = await adapter.InspectSchemaAsync(connection, default);
        Assert.Equal("Name", schema.Columns[0].Name);
        var sample = await adapter.ReadSampleAsync(connection, schema, 10, default);
        Assert.Equal(2, sample.Rows.Count);
        Assert.Equal("été", sample.Rows[0]["Name"].AsString());
        Assert.True(sample.Rows[1]["Name"].IsMissing);
        Assert.True(sample.Rows[1]["Note"].IsMissing);
    }

    [Fact]
    public async Task WiderRowsAreRejectedRatherThanDroppingCells()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("wide.csv");
        await File.WriteAllTextAsync(path, "Name\na,unmapped\n");
        var adapter = new CsvAdapter();
        var connection = new SourceConnection("in", "csv", path);
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var error = await Assert.ThrowsAsync<AdapterException>(() => adapter.ReadSampleAsync(connection, schema, 1, default));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
    }

    [Fact]
    public async Task SchemaAndSample_DoNotParseMalformedTail()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("sample.csv");
        await File.WriteAllTextAsync(path, "Name,Note\na,first\nb,second\n\"" + new string('x', 100_000));
        var connection = new SourceConnection("in", "csv", path);
        var adapter = new CsvAdapter(maximumRowBytes: 1024);
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        Assert.Single(sample.Rows);
        Assert.True(sample.IsTruncated);
        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var row in adapter.ReadFullAsync(connection, schema, default)) { }
        });
        Assert.True(error.IsInputBudgetExceeded);
    }

    [Theory]
    [InlineData("\"a\r\nb\"", "a\r\nb")]
    [InlineData("\"a\"\"b\"", "a\"b")]
    [InlineData("last", "last")]
    public async Task StreamingPreservesValuesAndLogicalProvenance(string encoded, string expected)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("quoted.csv");
        await File.WriteAllTextAsync(path, "Name,Note\n" + encoded + ",");
        var connection = new SourceConnection("in", "csv", path);
        var adapter = new CsvAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var sample = await adapter.ReadSampleAsync(connection, schema, 2, default);
        var row = Assert.Single(sample.Rows);
        Assert.Equal(expected, row["Name"].AsString());
        Assert.True(row["Note"].IsMissing);
        Assert.Equal("in#r2", row.RowId);
        Assert.Equal("row=2,col=1", row.CellSources!["Name"].Locator);
    }

    [Theory]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData(",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,")]
    public async Task OversizedRecordFailsBeforeMaterializing(string content)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("huge-row.csv");
        await File.WriteAllTextAsync(path, content);
        var adapter = new CsvAdapter(maximumRowBytes: 64);
        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            adapter.InspectSchemaAsync(new SourceConnection("in", "csv", path), default));
        Assert.Equal(DiagnosticCodes.IoNumberOutOfBudget, error.Code);
        Assert.True(error.IsInputBudgetExceeded);
    }

    [Fact]
    public async Task FileBudgetAndPreCancelledScanFailBeforeReading()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("huge-file.csv");
        using (var stream = File.Create(path)) stream.SetLength(CsvAdapter.DefaultMaximumFileBytes + 1);
        var adapter = new CsvAdapter();
        var connection = new SourceConnection("in", "csv", path);
        var error = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(connection, default));
        Assert.True(error.IsInputBudgetExceeded);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.InspectSchemaAsync(connection, cts.Token));
    }

    [Fact]
    public async Task CancelBetweenRowsStopsFullScan()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("cancel.csv");
        await File.WriteAllTextAsync(path, "Name\na\nb\n");
        var adapter = new CsvAdapter();
        var connection = new SourceConnection("in", "csv", path);
        var schema = await adapter.InspectSchemaAsync(connection, default);
        using var cts = new CancellationTokenSource();
        await using var rows = adapter.ReadFullAsync(connection, schema, cts.Token).GetAsyncEnumerator();
        Assert.True(await rows.MoveNextAsync());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rows.MoveNextAsync());
    }

    [Fact]
    public async Task UnterminatedQuotedRecordIsRejected()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("malformed.csv");
        await File.WriteAllTextAsync(path, "Name\n\"unterminated");
        var adapter = new CsvAdapter();
        var connection = new SourceConnection("in", "csv", path);
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var error = await Assert.ThrowsAsync<AdapterException>(() => adapter.ReadSampleAsync(connection, schema, 1, default));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
    }
}
