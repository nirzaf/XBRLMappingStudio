using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class IndependentFixtureAcceptanceTests
{
    [Fact]
    public async Task HandAuthoredFixturesMatchPinnedManifestAndCrossFormatOracle()
    {
        Assert.Empty(IndependentAcceptanceFixtures.VerifyIntegrity());
        using var manifest = JsonDocument.Parse(IndependentAcceptanceFixtures.ReadText("fixture-manifest.json"));
        var root = manifest.RootElement;
        var profile = SyntheticProfile.Build();
        Assert.Equal(profile.ProfileId, root.GetProperty("profileId").GetString());
        Assert.Equal(profile.Digest, root.GetProperty("profileDigest").GetString());
        Assert.Equal(SyntheticProfile.TaxonomyPackageDigest, root.GetProperty("taxonomyDigest").GetString());

        var expected = root.GetProperty("expectedFacts")[0];
        var expectedConcept = expected.GetProperty("concept").GetString();
        var expectedValue = expected.GetProperty("valueLexical").GetString();
        var expectedRows = root.GetProperty("expectedRowOccurrences").GetInt32();

        using var workspace = new FixtureWorkspace();

        var workbookPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "revenue.xlsx");
        var workbookConnection = new SourceConnection("workbook", "excel", workbookPath,
            new Dictionary<string, string> { ["sheet"] = "Financials", ["headerRow"] = "3" });
        var workbookAdapter = new ExcelAdapter();
        var workbookSchema = await workbookAdapter.InspectSchemaAsync(workbookConnection, CancellationToken.None);
        var workbookRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in workbookAdapter.ReadFullAsync(workbookConnection, workbookSchema, CancellationToken.None))
            workbookRows.Add(row);
        Assert.Equal(expectedRows, workbookRows.Count);
        var revenueRow = Assert.Single(workbookRows, row => row["Line"].AsString() == "Revenue");
        Assert.Equal("Financials!B4", revenueRow.CellSources?.GetValueOrDefault("Current")?.Locator);
        Assert.Equal(1250.0000m, revenueRow["Current"].AsDecimal());

        var csvPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "source.csv");
        var csvConnection = new SourceConnection("csv", "csv", csvPath);
        var csvAdapter = new CsvAdapter();
        var csvSchema = await csvAdapter.InspectSchemaAsync(csvConnection, CancellationToken.None);
        var csvRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in csvAdapter.ReadFullAsync(csvConnection, csvSchema, CancellationToken.None))
            csvRows.Add(row);
        Assert.Equal(expectedRows, csvRows.Count);
        Assert.Equal("1250.0000", Assert.Single(csvRows, row => row["Line"].AsString() == "Revenue")["Current"].AsString());

        var nestedJsonPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "nested.json");
        var jsonSource = new SourceConnection("nested-json", "json", nestedJsonPath);
        var jsonAdapter = new JsonAdapter();
        var jsonSchema = await jsonAdapter.InspectSchemaAsync(jsonSource, CancellationToken.None);
        var nestedJsonRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in jsonAdapter.ReadFullAsync(jsonSource, jsonSchema, CancellationToken.None))
            nestedJsonRows.Add(row);
        var nestedJson = Assert.Single(nestedJsonRows);
        Assert.Equal("ABC123", nestedJson["entity.identifier"].AsString());
        Assert.Equal("1250000.0000", nestedJson["amount.lexical"].AsString());

        var nestedXmlPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "nested.xml");
        var xmlSource = new SourceConnection("nested-xml", "xml", nestedXmlPath);
        var xmlAdapter = new XmlAdapter();
        var xmlSchema = await xmlAdapter.InspectSchemaAsync(xmlSource, CancellationToken.None);
        var nestedXmlRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in xmlAdapter.ReadFullAsync(xmlSource, xmlSchema, CancellationToken.None))
            nestedXmlRows.Add(row);
        var nestedXml = Assert.Single(nestedXmlRows);
        Assert.Equal("ABC123", nestedXml["[urn:xms:acceptance:v1]entity.[urn:xms:acceptance:v1]identifier"].AsString());
        Assert.Equal("1250000.0000", nestedXml["[urn:xms:acceptance:v1]amount.[urn:xms:acceptance:v1]lexical"].AsString());
        var namespaceFields = root.GetProperty("expectedNamespaceFields").EnumerateArray()
            .Select(field => field.GetString()!).ToArray();
        Assert.Equal("1", nestedXml[namespaceFields[0]].AsString());
        Assert.Equal("2", nestedXml[namespaceFields[1]].AsString());

        var databasePath = workspace.PathFor("sqlite-source.db");
        await using (var database = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = IndependentAcceptanceFixtures.ReadText("sqlite-source.sql");
            await command.ExecuteNonQueryAsync();
        }
        var sqliteSource = new SourceConnection("sqlite", "sqlite", databasePath,
            new Dictionary<string, string> { ["table"] = "financials", ["decimalColumns"] = "Current,Prior" });
        var sqliteAdapter = new SqliteSourceAdapter();
        var sqliteSchema = await sqliteAdapter.InspectSchemaAsync(sqliteSource, CancellationToken.None);
        var sqliteRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in sqliteAdapter.ReadFullAsync(sqliteSource, sqliteSchema, CancellationToken.None))
            sqliteRows.Add(row);
        Assert.Equal(expectedRows, sqliteRows.Count);
        Assert.Equal("1250.0000", Assert.Single(sqliteRows, row => row["Line"].AsString() == "Revenue")["Current"].LexicalSource);

        var xbrlJsonPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "revenue-report.json");
        var xbrlXmlPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "revenue-report.xml");
        var xbrlAdapter = new XbrlReportAdapter();
        var reportJson = await ReadXbrlRows(new SourceConnection("xbrl-json", "xbrl-report", xbrlJsonPath));
        var reportXml = await ReadXbrlRows(new SourceConnection("xbrl-xml", "xbrl-report", xbrlXmlPath));
        Assert.Single(reportJson);
        Assert.Single(reportXml);
        Assert.Equal(reportJson[0].SemanticKey(), reportXml[0].SemanticKey());
        Assert.Equal(expectedConcept, reportJson[0].ConceptName);
        Assert.Equal(expectedValue, reportJson[0].Value?.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(expected.GetProperty("decimals").GetInt32(), reportJson[0].Decimals);
        Assert.Equal(expected.GetProperty("period").GetString(), reportJson[0].Period.ToIsoString());

        var reportJsonRows = await ReadXbrlAdapterRows(xbrlAdapter,
            new SourceConnection("xbrl-json", "xbrl-report", xbrlJsonPath));
        Assert.Single(reportJsonRows);
        Assert.Equal(expectedValue, reportJsonRows[0]["value"].LexicalSource);
        Assert.Contains("xbrl-json#f1", reportJsonRows[0].RowId, StringComparison.Ordinal);

        var duplicatePath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "duplicate-key.json");
        var duplicateConnection = new SourceConnection("negative-json", "json", duplicatePath);
        var duplicate = await Assert.ThrowsAsync<AdapterException>(
            () => jsonAdapter.InspectSchemaAsync(duplicateConnection, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoDuplicateKey, duplicate.Code);

        var changedBytes = IndependentAcceptanceFixtures.ReadBytes("source.csv");
        changedBytes[0] ^= 0x01;
        var files = root.GetProperty("files");
        Assert.NotEqual(files.GetProperty("source.csv").GetString(), IndependentAcceptanceFixtures.Digest(changedBytes));
    }

    private sealed class FixtureWorkspace : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "xms-independent-fixtures", Guid.NewGuid().ToString("N"));

        public FixtureWorkspace() => System.IO.Directory.CreateDirectory(Directory);

        public string PathFor(string name) => Path.Combine(Directory, name);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task<IReadOnlyList<XbrlFact>> ReadXbrlRows(SourceConnection connection)
    {
        var facts = connection.Path!.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? XmlSerializer.ReadFacts(await File.ReadAllTextAsync(connection.Path))
            : OimSerializer.ReadFacts(await File.ReadAllTextAsync(connection.Path));
        return facts;
    }

    private static async Task<IReadOnlyList<XBRLMappingStudio.Engine.DataRecord>> ReadXbrlAdapterRows(
        XbrlReportAdapter adapter, SourceConnection connection)
    {
        var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
        var rows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(connection, schema, CancellationToken.None)) rows.Add(row);
        return rows;
    }
}
