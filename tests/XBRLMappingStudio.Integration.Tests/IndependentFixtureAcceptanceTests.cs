using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
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

        var mappingOracle = IndependentAcceptanceFixtures.ReadFactOracle("workbookMapping");
        var reportOracle = IndependentAcceptanceFixtures.ReadFactOracle("importedReport");
        Assert.NotNull(mappingOracle.SourceValueLexical);
        Assert.NotNull(mappingOracle.ScaleFactorLexical);
        Assert.Equal(mappingOracle.Value,
            decimal.Parse(mappingOracle.SourceValueLexical!, CultureInfo.InvariantCulture)
            * decimal.Parse(mappingOracle.ScaleFactorLexical!, CultureInfo.InvariantCulture));
        var expectedRows = root.GetProperty("expectedRowOccurrences");

        using var workspace = new FixtureWorkspace();

        var workbookPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "revenue.xlsx");
        var workbookConnection = new SourceConnection("workbook", "excel", workbookPath,
            new Dictionary<string, string> { ["sheet"] = "Financials", ["headerRow"] = "3" });
        var workbookAdapter = new ExcelAdapter();
        var workbookSchema = await workbookAdapter.InspectSchemaAsync(workbookConnection, CancellationToken.None);
        var workbookRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in workbookAdapter.ReadFullAsync(workbookConnection, workbookSchema, CancellationToken.None))
            workbookRows.Add(row);
        Assert.Equal(expectedRows.GetProperty("workbook").GetInt32(), workbookRows.Count);
        var revenueRow = Assert.Single(workbookRows, row => row["Line"].AsString() == "Revenue");
        Assert.Equal(mappingOracle.SourceLocator, revenueRow.CellSources?.GetValueOrDefault(mappingOracle.SourceField!)?.Locator);
        Assert.Equal(mappingOracle.WorkbookDisplayLexical, revenueRow[mappingOracle.SourceField!].LexicalSource);
        Assert.Equal(decimal.Parse(mappingOracle.SourceValueLexical!, CultureInfo.InvariantCulture),
            revenueRow[mappingOracle.SourceField!].AsDecimal());

        var csvPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "source.csv");
        var csvConnection = new SourceConnection("csv", "csv", csvPath);
        var csvAdapter = new CsvAdapter();
        var csvSchema = await csvAdapter.InspectSchemaAsync(csvConnection, CancellationToken.None);
        var csvRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in csvAdapter.ReadFullAsync(csvConnection, csvSchema, CancellationToken.None))
            csvRows.Add(row);
        Assert.Equal(expectedRows.GetProperty("csv").GetInt32(), csvRows.Count);
        Assert.Equal(mappingOracle.SourceValueLexical,
            Assert.Single(csvRows, row => row["Line"].AsString() == "Revenue")[mappingOracle.SourceField!].AsString());

        var nestedJsonPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "nested.json");
        var jsonSource = new SourceConnection("nested-json", "json", nestedJsonPath);
        var jsonAdapter = new JsonAdapter();
        var jsonSchema = await jsonAdapter.InspectSchemaAsync(jsonSource, CancellationToken.None);
        var nestedJsonRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in jsonAdapter.ReadFullAsync(jsonSource, jsonSchema, CancellationToken.None))
            nestedJsonRows.Add(row);
        var nestedJson = Assert.Single(nestedJsonRows);
        Assert.Equal("ABC123", nestedJson["entity.identifier"].AsString());
        Assert.Equal(reportOracle.ValueLexical, nestedJson["amount.lexical"].AsString());
        Assert.Equal(expectedRows.GetProperty("nestedJson").GetInt32(), nestedJsonRows.Count);

        var nestedXmlPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "nested.xml");
        var xmlSource = new SourceConnection("nested-xml", "xml", nestedXmlPath);
        var xmlAdapter = new XmlAdapter();
        var xmlSchema = await xmlAdapter.InspectSchemaAsync(xmlSource, CancellationToken.None);
        var nestedXmlRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in xmlAdapter.ReadFullAsync(xmlSource, xmlSchema, CancellationToken.None))
            nestedXmlRows.Add(row);
        var nestedXml = Assert.Single(nestedXmlRows);
        Assert.Equal("ABC123", nestedXml["[urn:xms:acceptance:v1]entity.[urn:xms:acceptance:v1]identifier"].AsString());
        Assert.Equal(reportOracle.ValueLexical,
            nestedXml["[urn:xms:acceptance:v1]amount.[urn:xms:acceptance:v1]lexical"].AsString());
        Assert.Equal(expectedRows.GetProperty("nestedXml").GetInt32(), nestedXmlRows.Count);
        var ambiguity = Assert.Single(root.GetProperty("ambiguousCases").EnumerateArray());
        Assert.Equal("value", ambiguity.GetProperty("localName").GetString());
        var namespaceFields = ambiguity.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(2, namespaceFields.Length);
        foreach (var field in namespaceFields)
            Assert.Equal(field.GetProperty("valueLexical").GetString(),
                nestedXml[field.GetProperty("path").GetString()!].AsString());

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
        Assert.Equal(expectedRows.GetProperty("sqlite").GetInt32(), sqliteRows.Count);
        Assert.Equal(mappingOracle.SourceValueLexical,
            Assert.Single(sqliteRows, row => row["Line"].AsString() == "Revenue")[mappingOracle.SourceField!].LexicalSource);

        var xbrlJsonPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "revenue-report.json");
        var xbrlXmlPath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "revenue-report.xml");
        using (var sourceJson = JsonDocument.Parse(await File.ReadAllTextAsync(xbrlJsonPath)))
        {
            var sourceFact = Assert.Single(sourceJson.RootElement.GetProperty("facts").EnumerateObject()).Value;
            Assert.Equal(reportOracle.ValueLexical, sourceFact.GetProperty("value").GetString());
        }
        var sourceXml = XDocument.Load(xbrlXmlPath);
        var sourceXmlFact = Assert.Single(sourceXml.Descendants(XName.Get("Revenue", "urn:synthetic:finance:1.0")));
        Assert.Equal(reportOracle.ValueLexical, sourceXmlFact.Value);
        var xbrlAdapter = new XbrlReportAdapter();
        var reportJson = await ReadXbrlRows(new SourceConnection("xbrl-json", "xbrl-report", xbrlJsonPath));
        var reportXml = await ReadXbrlRows(new SourceConnection("xbrl-xml", "xbrl-report", xbrlXmlPath));
        Assert.Equal(expectedRows.GetProperty("importedReportJson").GetInt32(), reportJson.Count);
        Assert.Equal(expectedRows.GetProperty("importedReportXml").GetInt32(), reportXml.Count);
        Assert.Equal(reportJson[0].SemanticKey(), reportXml[0].SemanticKey());
        Assert.Empty(IndependentAcceptanceFixtures.CompareFact(reportOracle, reportJson[0]));
        Assert.Empty(IndependentAcceptanceFixtures.CompareFact(reportOracle, reportXml[0]));

        var reportJsonRows = await ReadXbrlAdapterRows(xbrlAdapter,
            new SourceConnection("xbrl-json", "xbrl-report", xbrlJsonPath));
        Assert.Equal(expectedRows.GetProperty("importedReportJson").GetInt32(), reportJsonRows.Count);
        Assert.Equal(reportOracle.ValueLexical, reportJsonRows[0]["value"].LexicalSource);
        Assert.Contains("xbrl-json#f1", reportJsonRows[0].RowId, StringComparison.Ordinal);
        var reportXmlRows = await ReadXbrlAdapterRows(xbrlAdapter,
            new SourceConnection("xbrl-xml", "xbrl-report", xbrlXmlPath));
        Assert.Equal(expectedRows.GetProperty("importedReportXml").GetInt32(), reportXmlRows.Count);
        Assert.Equal(reportOracle.ValueLexical, reportXmlRows[0]["value"].LexicalSource);

        var duplicatePath = IndependentAcceptanceFixtures.Materialize(workspace.Directory, "duplicate-key.json");
        var duplicateConnection = new SourceConnection("negative-json", "json", duplicatePath);
        var negative = Assert.Single(root.GetProperty("negativeCases").EnumerateArray());
        Assert.Equal("duplicate-key.json", negative.GetProperty("file").GetString());
        var duplicate = await Assert.ThrowsAsync<AdapterException>(
            () => jsonAdapter.InspectSchemaAsync(duplicateConnection, CancellationToken.None));
        Assert.Equal(negative.GetProperty("diagnostic").GetString(), duplicate.Code);
        Assert.Equal(DiagnosticCodes.IoDuplicateKey, duplicate.Code);

        var tamperedFixtureDirectory = workspace.PathFor("tampered-fixtures");
        System.IO.Directory.CreateDirectory(tamperedFixtureDirectory);
        var files = root.GetProperty("files");
        File.WriteAllBytes(Path.Combine(tamperedFixtureDirectory, "fixture-manifest.json"),
            IndependentAcceptanceFixtures.ReadBytes("fixture-manifest.json"));
        foreach (var file in files.EnumerateObject())
            File.WriteAllBytes(Path.Combine(tamperedFixtureDirectory, file.Name),
                IndependentAcceptanceFixtures.ReadBytes(file.Name));
        Assert.Empty(IndependentAcceptanceFixtures.VerifyIntegrity(tamperedFixtureDirectory));

        var changedBytes = File.ReadAllBytes(Path.Combine(tamperedFixtureDirectory, "source.csv"));
        changedBytes[0] ^= 0x01;
        File.WriteAllBytes(Path.Combine(tamperedFixtureDirectory, "source.csv"), changedBytes);
        Assert.Contains(IndependentAcceptanceFixtures.VerifyIntegrity(tamperedFixtureDirectory),
            error => error.Contains("source.csv digest differs", StringComparison.Ordinal));
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
