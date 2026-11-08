using System.Globalization;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>
/// Cross-format output acceptance for lexical values that are easy to corrupt while
/// serializing records. These checks intentionally reopen each published artifact
/// through its source adapter instead of asserting only on writer bytes.
/// </summary>
public sealed class RecordOutputLexicalAcceptanceTests : IAsyncLifetime
{
    private const string Note = "North & <South> \"quoted\"\nsecond line";
    private const string Account = "00123-04";
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "xms-record-output-lexical-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CsvWriter_ReopensEscapedTextAndLeadingZeroIdentifierExactly()
    {
        var path = Path.Combine(_directory, "records.csv");
        var connection = new SourceConnection("output", "csv", path);
        var writer = new CsvAdapter(new CsvAdapter.Options(EmitBom: false));
        var payload = writer.WriteRecords(new[] { "account", "note", "amount" }, new[] { Record() });
        var staged = await writer.StageArtifactAsync(connection, payload, ".csv", CancellationToken.None);
        await writer.PublishAsync(staged, connection, CancellationToken.None);

        var reader = new CsvAdapter();
        var schema = await reader.InspectSchemaAsync(connection, CancellationToken.None);
        var actual = await ReadSingleAsync(reader, connection, schema);
        Assert.Equal(Account, actual["account"].AsString());
        Assert.Equal(Note, actual["note"].AsString());
        Assert.Equal("12.5000", actual["amount"].AsString());
    }

    [Fact]
    public async Task JsonWriter_ReopensEscapedTextAndLeadingZeroIdentifierExactly()
    {
        var path = Path.Combine(_directory, "records.json");
        var connection = new SourceConnection("output", "json", path);
        var writer = new JsonOutputAdapter();
        var staged = await writer.StageArtifactAsync(connection, writer.Write(new[] { Record() }),
            ".json", CancellationToken.None);
        await writer.PublishAsync(staged, connection, CancellationToken.None);

        var reader = new JsonAdapter();
        var schema = await reader.InspectSchemaAsync(connection, CancellationToken.None);
        var actual = await ReadSingleAsync(reader, connection, schema);
        Assert.Equal(Account, actual["account"].AsString());
        Assert.Equal(Note, actual["note"].AsString());
        Assert.Equal(12.5000m, actual["amount"].AsDecimal());
        Assert.Equal("12.5000", actual["amount"].LexicalSource);
    }

    [Fact]
    public async Task XmlWriter_ReopensEscapedTextAndLeadingZeroIdentifierExactly()
    {
        var path = Path.Combine(_directory, "records.xml");
        var connection = new SourceConnection("output", "xml", path);
        var record = Record(namespacedNote: true);
        var writer = new XmlOutputAdapter();
        var staged = await writer.StageArtifactAsync(connection, writer.Write(new[] { record }),
            ".xml", CancellationToken.None);
        await writer.PublishAsync(staged, connection, CancellationToken.None);

        var reader = new XmlAdapter();
        var schema = await reader.InspectSchemaAsync(connection, CancellationToken.None);
        var actual = await ReadSingleAsync(reader, connection, schema);
        Assert.Equal(Account, actual["row.account"].AsString());
        Assert.Equal(Note, actual["row.[urn:xms:acceptance:v1]note"].AsString());
        Assert.Equal("12.5000", actual["row.amount"].AsString());
    }

    [Fact]
    public async Task ExcelWriter_ReopensEscapedTextAndLeadingZeroIdentifierExactly()
    {
        var path = Path.Combine(_directory, "records.xlsx");
        var connection = new SourceConnection("output", "excel", path,
            new Dictionary<string, string> { ["sheet"] = "Output", ["headerRow"] = "1" });
        var writer = new ExcelOutputAdapter();
        var payload = writer.WriteWorkbook(new[] { "account", "note", "amount" },
            new IEnumerable<object?>[] { new object?[] { Account, Note, "12.5000" } });
        var staged = await writer.StageArtifactAsync(connection, payload, ".xlsx", CancellationToken.None);
        await writer.PublishAsync(staged, connection, CancellationToken.None);

        var reader = new ExcelAdapter();
        var schema = await reader.InspectSchemaAsync(connection, CancellationToken.None);
        var actual = await ReadSingleAsync(reader, connection, schema);
        Assert.Equal(Account, actual["account"].AsString());
        Assert.Equal(Note, actual["note"].AsString());
        Assert.Equal("12.5000", actual["amount"].AsString());
    }

    private static DataRecord Record(bool namespacedNote = false) => new()
    {
        RowId = "lexical-fixture-1",
        Fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
        {
            ["account"] = DataValue.StringValue(Account),
            [namespacedNote ? "{urn:xms:acceptance:v1}note" : "note"] = DataValue.StringValue(Note),
            ["amount"] = DataValue.DecimalValue(decimal.Parse("12.5000", CultureInfo.InvariantCulture))
        }
    };

    private static async Task<DataRecord> ReadSingleAsync(
        ISourceAdapter reader,
        SourceConnection connection,
        ISourceSchema schema)
    {
        DataRecord? result = null;
        await foreach (var record in reader.ReadFullAsync(connection, schema, CancellationToken.None))
        {
            Assert.Null(result);
            result = record;
        }
        return Assert.IsType<DataRecord>(result);
    }
}
