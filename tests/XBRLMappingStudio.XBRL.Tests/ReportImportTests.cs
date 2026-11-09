using System.Text.Json;
using System.Xml.Linq;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR06 — a report is a first-class source: <see cref="XbrlReportAdapter"/> yields one
/// record per fact and preserves repeated facts, several units, and multiple languages as
/// distinct rows. Both xBRL-JSON and xBRL-XML inputs are supported.
/// </summary>
public sealed class ReportImportTests : IDisposable
{
    private static FilingProfile Profile => TestFilingProfiles.WithNumericCompoundUnitConcept();
    private readonly string _dir;

    public ReportImportTests()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-xbrl-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static IReadOnlyList<XbrlFact> Report() => new[]
    {
        Revenue("QAR", 1_000_000m),
        Revenue("USD", 300_000m),
        Text("ABC Corp", "en"),
        Text("ABC Corp", "fr"),
        Text("شركة ABC", "ar")
    };

    private static XbrlFact Revenue(string unit, decimal value, XbrlPeriod? period = null) => new()
    {
        ConceptName = SyntheticProfile.Qualified("Revenue"),
        EntityScheme = "http://xbrl.org/entity/",
        EntityIdentifier = "ABC-Corp",
        Period = period ?? XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
        Unit = new XbrlUnit(unit),
        Value = value,
        Decimals = -3
    };

    private static XbrlFact WithDimension(XbrlFact fact, XbrlPeriod period, string axis, string member)
        => new()
        {
            ConceptName = fact.ConceptName,
            EntityScheme = fact.EntityScheme,
            EntityIdentifier = fact.EntityIdentifier,
            Period = period,
            Unit = fact.Unit,
            Value = fact.Value,
            Decimals = fact.Decimals,
            Dimensions = new[] { new Dimension(axis, member) }
        };

    private static XbrlFact Text(string value, string language) => new()
    {
        ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
        EntityScheme = "http://xbrl.org/entity/",
        EntityIdentifier = "ABC-Corp",
        Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
        TextValue = value,
        Language = language
    };

    private string Write(string name, string content)
    {
        var p = System.IO.Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static async Task<List<DataRecord>> Drain(XbrlReportAdapter adapter, SourceConnection conn)
    {
        var schema = await adapter.InspectSchemaAsync(conn, CancellationToken.None);
        var rows = new List<DataRecord>();
        await foreach (var r in adapter.ReadFullAsync(conn, schema, CancellationToken.None))
            rows.Add(r);
        return rows;
    }

    [Fact]
    public async Task JsonReport_YieldsOneRecordPerFact()
    {
        var path = Write("report.json", OimSerializer.Serialize(Profile, Report()));
        var adapter = new XbrlReportAdapter();
        var conn = new SourceConnection("src", adapter.Kind, path);
        var rows = await Drain(adapter, conn);

        Assert.Equal(5, rows.Count);
        Assert.Equal(2, rows.Count(r => r.Fields["concept"].AsString() == SyntheticProfile.Qualified("Revenue")));
        Assert.Equal(3, rows.Count(r => r.Fields["concept"].AsString() == SyntheticProfile.Qualified("EntityRegistrantName")));

        // Two distinct units preserved across records.
        Assert.Contains(rows, r => r.Fields["unit"].AsString() == "QAR");
        Assert.Contains(rows, r => r.Fields["unit"].AsString() == "USD");

        // Three distinct languages preserved.
        var langs = rows.Select(r => r.Fields["lang"].AsString()).Distinct().ToList();
        Assert.Contains("en", langs);
        Assert.Contains("fr", langs);
        Assert.Contains("ar", langs);
    }

    [Fact]
    public async Task JsonReport_RejectsInputAboveConfiguredByteBudget()
    {
        var path = Write("over-budget.json", OimSerializer.Serialize(Profile, Report()));
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["maxInputBytes"] = "8" });
        var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);

        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, CancellationToken.None)) { }
        });

        Assert.Equal(DiagnosticCodes.IoInputBudgetExceeded, error.Code);
        Assert.True(error.IsInputBudgetExceeded);
        Assert.Contains("8 bytes", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not-a-number")]
    [InlineData("67108865")]
    public async Task InvalidReportInputByteBudget_FailsDuringSchemaInspection(string budget)
    {
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, Write("budget.json", "{}"),
            new Dictionary<string, string> { ["maxInputBytes"] = budget });

        var error = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(connection, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.IoInputBudgetExceeded, error.Code);
        Assert.True(error.IsInputBudgetExceeded);
    }

    [Fact]
    public async Task XmlReport_YieldsSameRecordsAsJson()
    {
        var facts = Report();
        var jsonPath = Write("report.json", OimSerializer.Serialize(Profile, facts));
        var xmlPath = Write("report.xml", XmlSerializer.Serialize(Profile, facts));

        var adapter = new XbrlReportAdapter();
        var jsonRows = await Drain(adapter, new SourceConnection("j", adapter.Kind, jsonPath));
        var xmlRows = await Drain(adapter, new SourceConnection("x", adapter.Kind, xmlPath));

        // Same multiset of facts regardless of encoding (compare on the value+unit+lang projection).
        static string[] Proj(IEnumerable<DataRecord> rs) => rs
            .Select(r => $"{r.Fields["concept"].AsString()}|{r.Fields["unit"].AsString()}|" +
                $"{(r.Fields["value"].IsMissing ? "" : r.Fields["value"].AsDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture))}|" +
                $"{r.Fields["textValue"].AsString()}|{r.Fields["lang"].AsString()}")
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(Proj(jsonRows), Proj(xmlRows));
    }

    [Fact]
    public async Task RepeatedFact_PreservedAsDistinctRows()
    {
        // Two identical facts (same aspects) remain two records — report import is lossless.
        var facts = new[] { Revenue("QAR", 1_000_000m), Revenue("QAR", 1_000_000m) };
        var path = Write("dup.json", OimSerializer.Serialize(Profile, facts));
        var adapter = new XbrlReportAdapter();
        var rows = await Drain(adapter, new SourceConnection("src", adapter.Kind, path));
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].Fields["value"].AsDecimal(), rows[1].Fields["value"].AsDecimal());
    }

    [Fact]
    public async Task AspectSelection_PreservesRepeatOccurrencesAndOriginalRowIds()
    {
        var axis = SyntheticProfile.Qualified("RegionAxis");
        var north = SyntheticProfile.Qualified("NorthMember");
        var south = SyntheticProfile.Qualified("SouthMember");
        var selectedPeriod = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var alternatePeriod = XbrlPeriod.OfDuration(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        var facts = new[]
        {
            WithDimension(Revenue("QAR", 1_000_000m), selectedPeriod, axis, north),
            WithDimension(Revenue("QAR", 1_000_000m), selectedPeriod, axis, north),
            WithDimension(Revenue("QAR", 1_000_000m, alternatePeriod), alternatePeriod, axis, north),
            WithDimension(Revenue("USD", 1_000_000m), selectedPeriod, axis, north),
            WithDimension(Revenue("QAR", 1_000_000m), selectedPeriod, axis, south)
        };
        var path = Write("aspect-selection.json", OimSerializer.Serialize(Profile, facts));
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, path, new Dictionary<string, string>
        {
            ["concept"] = SyntheticProfile.Qualified("Revenue"),
            ["period"] = selectedPeriod.ToIsoString(),
            ["unit"] = "QAR",
            ["dimensionAxis"] = axis,
            ["dimensionMember"] = north
        });

        var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
        var rows = await Drain(adapter, connection);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "report#f1", "report#f2" }, rows.Select(row => row.RowId));
        Assert.All(rows, row => Assert.True(row.HasStableRowIdentity));
        Assert.Equal(rows.Select(row => row.RowId), rows.Select(row => row["occurrenceId"].AsString()));
        Assert.All(rows, row => Assert.Equal(selectedPeriod.ToIsoString(), row["period"].AsString()));
        Assert.All(rows, row => Assert.Equal("QAR", row["unit"].AsString()));
        Assert.All(rows, row => Assert.Equal(axis + "=" + north, row["dimensions"].AsString()));
        Assert.Equal(PortShape.Decimal, Assert.Single(schema.Columns, column => column.Name == "value").InferredShape);
        Assert.Equal(PortShape.Boolean, Assert.Single(schema.Columns, column => column.Name == "nil").InferredShape);
        Assert.All(rows, row => Assert.Contains(row.RowId, row["value"].Source?.Locator, StringComparison.Ordinal));
        using var dimensionDocument = JsonDocument.Parse(rows[0]["dimensionsJson"].AsString());
        var dimension = Assert.Single(dimensionDocument.RootElement.EnumerateArray());
        Assert.Equal(axis, dimension.GetProperty("Name").GetString());
        Assert.Equal(north, dimension.GetProperty("MemberName").GetString());
    }

    [Fact]
    public async Task OccurrenceSelection_SelectsOneRepeatedFactByStableSourceIdentity()
    {
        var path = Write("occurrence-selection.json", OimSerializer.Serialize(Profile, new[]
        {
            Revenue("QAR", 1_000_000m),
            Revenue("QAR", 1_000_000m)
        }));
        var adapter = new XbrlReportAdapter();
        var rows = await Drain(adapter, new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["occurrenceId"] = "report#f2" }));

        var row = Assert.Single(rows);
        Assert.Equal("report#f2", row.RowId);
        Assert.Equal("report#f2", row["occurrenceId"].AsString());
        Assert.True(row.HasStableRowIdentity);
    }

    [Fact]
    public async Task MultipleDimensionFilters_MatchConjunctivelyAndPreserveRepeatedOccurrences()
    {
        var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var regionAxis = SyntheticProfile.Qualified("RegionAxis");
        var north = SyntheticProfile.Qualified("NorthMember");
        var south = SyntheticProfile.Qualified("SouthMember");
        var departmentAxis = SyntheticProfile.Qualified("DepartmentAxis");
        var finance = SyntheticProfile.Qualified("FinanceMember");
        var operations = SyntheticProfile.Qualified("OperationsMember");
        static XbrlFact WithDimensions(XbrlFact fact, params Dimension[] dimensions) => new()
        {
            ConceptName = fact.ConceptName,
            EntityScheme = fact.EntityScheme,
            EntityIdentifier = fact.EntityIdentifier,
            Period = fact.Period,
            Unit = fact.Unit,
            CompoundUnit = fact.CompoundUnit,
            Value = fact.Value,
            TextValue = fact.TextValue,
            IsNil = fact.IsNil,
            Decimals = fact.Decimals,
            Language = fact.Language,
            Dimensions = dimensions
        };

        var path = Write("multi-dimension-selection.json", OimSerializer.Serialize(Profile, new[]
        {
            WithDimensions(Revenue("QAR", 100m), new Dimension(regionAxis, north), new Dimension(departmentAxis, operations)),
            WithDimensions(Revenue("QAR", 100m), new Dimension(regionAxis, north), new Dimension(departmentAxis, operations)),
            WithDimensions(Revenue("QAR", 100m), new Dimension(regionAxis, south), new Dimension(departmentAxis, operations)),
            WithDimensions(Revenue("QAR", 100m), new Dimension(regionAxis, north), new Dimension(departmentAxis, finance))
        }));
        var adapter = new XbrlReportAdapter();
        var rows = await Drain(adapter, new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string>
            {
                ["period"] = period.ToIsoString(),
                ["dimensionFilters"] = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [regionAxis] = north,
                    [departmentAxis] = operations
                })
            }));

        Assert.Equal(new[] { "report#f2", "report#f3" }, rows.Select(row => row.RowId));
        Assert.All(rows, row => Assert.Equal(period.ToIsoString(), row["period"].AsString()));
        Assert.All(rows, row =>
        {
            using var dimensions = JsonDocument.Parse(row["dimensionsJson"].AsString());
            Assert.Equal(2, dimensions.RootElement.GetArrayLength());
            Assert.Contains(dimensions.RootElement.EnumerateArray(), item =>
                item.GetProperty("Name").GetString() == regionAxis
                && item.GetProperty("MemberName").GetString() == north);
            Assert.Contains(dimensions.RootElement.EnumerateArray(), item =>
                item.GetProperty("Name").GetString() == departmentAxis
                && item.GetProperty("MemberName").GetString() == operations);
        });
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"synthetic:RegionAxis\":\"synthetic:North\",\"synthetic:RegionAxis\":\"synthetic:South\"}")]
    public async Task InvalidDimensionFilterJson_FailsClosed(string dimensionFilters)
    {
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, Write("invalid-dimensions.json", "{}"),
            new Dictionary<string, string> { ["dimensionFilters"] = dimensionFilters });

        var error = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(connection, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
        Assert.Contains("dimensionFilters", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DimensionFilters_CannotRepeatAxisAcrossSelectorForms()
    {
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, Write("duplicate-dimension.json", "{}"),
            new Dictionary<string, string>
            {
                ["dimensionAxis"] = "synthetic:RegionAxis",
                ["dimensionMember"] = "synthetic:North",
                ["dimensionFilters"] = "{\"synthetic:RegionAxis\":\"synthetic:North\"}"
            });

        var error = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(connection, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
        Assert.Contains("cannot repeat an axis", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AspectSelection_NoMatchesAndIncompleteDimensionPairFailClosed()
    {
        var path = Write("no-selection-match.json", OimSerializer.Serialize(Profile, Report()));
        var adapter = new XbrlReportAdapter();
        var noMatch = new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["concept"] = "synthetic:Unknown" });
        var schema = await adapter.InspectSchemaAsync(noMatch, CancellationToken.None);

        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(noMatch, schema, CancellationToken.None)) { }
        });
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
        Assert.Contains("matched no fact occurrences", error.Message, StringComparison.Ordinal);

        var incomplete = new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["dimensionAxis"] = "synthetic:RegionAxis" });
        var invalid = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(incomplete, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, invalid.Code);
        Assert.Contains("requires both", invalid.Message, StringComparison.Ordinal);

        var unknown = new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["periodType"] = "duration" });
        var unsupported = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(unknown, CancellationToken.None));
        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, unsupported.Code);
        Assert.Contains("unsupported key", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingTargetRowSelection_FailsClosedUntilDestinationRowsAreBound()
    {
        var path = Write("target-row.json", OimSerializer.Serialize(Profile, Report()));
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["targetRowId"] = "facts:existing-row-1" });

        var error = await Assert.ThrowsAsync<AdapterException>(
            () => adapter.InspectSchemaAsync(connection, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
        Assert.Contains("target-row selection is not supported", error.Message, StringComparison.Ordinal);
        Assert.Contains("no destination rows are created", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnitSelection_KeepsPoweredAndCompoundUnitsDistinct()
    {
        var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var powered = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = period,
            Unit = new XbrlUnit("QAR", 2),
            Value = 100m,
            Decimals = 0
        };
        var compound = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = period,
            CompoundUnit = new CompoundUnit(new[] { "USD" }, new[] { "shares" }),
            Value = 10m,
            Decimals = 0
        };
        var path = Write("unit-selection.xml", XmlSerializer.Serialize(Profile, new[] { powered, compound }));
        var adapter = new XbrlReportAdapter();

        var poweredRows = await Drain(adapter, new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["unit"] = "QAR^2" }));
        var compoundRows = await Drain(adapter, new SourceConnection("report", adapter.Kind, path,
            new Dictionary<string, string> { ["unit"] = "USD/shares" }));

        var poweredRow = Assert.Single(poweredRows);
        Assert.Equal("QAR^2", poweredRow["unit"].AsString());
        Assert.True(poweredRow["compoundUnit"].IsMissing);
        var compoundRow = Assert.Single(compoundRows);
        Assert.True(compoundRow["unit"].IsMissing);
        Assert.Equal("USD/shares", compoundRow["compoundUnit"].AsString());
    }

    [Fact]
    public async Task TypedDimensionInReportIsReportedAsUnsupported()
    {
        var xml = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { Revenue("QAR", 1m) }));
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        XNamespace xbrldi = "http://xbrl.org/2006/xbrldi";
        XNamespace synthetic = SyntheticProfile.Namespace;
        xml.Root!.Element(xbrli + "context")!.Element(xbrli + "entity")!
            .Add(new XElement(xbrli + "segment",
                new XElement(xbrldi + "typedMember",
                    new XAttribute("dimension", "synthetic:TypedAxis"),
                    new XElement(synthetic + "TypedMember", "typed value"))));
        var path = Write("typed-report.xml", xml.ToString());
        var adapter = new XbrlReportAdapter();
        var connection = new SourceConnection("report", adapter.Kind, path);
        var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);

        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, CancellationToken.None)) { }
        });

        Assert.Equal(DiagnosticCodes.IoUnsupportedEncoding, error.Code);
        Assert.Contains("typed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TextFact_ImportsWithoutChangingItToNumericOrBlank()
    {
        var path = Write("text-report.json", FixturePaths.Read("xbrl-international-simple-report.json"));
        var adapter = new XbrlReportAdapter();
        var rows = await Drain(adapter, new SourceConnection("src", adapter.Kind, path));
        var row = Assert.Single(rows, r => r.Fields["textValue"].AsString() == "ABC INC");
        Assert.True(row.Fields["value"].IsMissing);
        Assert.Equal("en", row.Fields["lang"].AsString());
    }

    [Fact]
    public async Task CorruptReport_ThrowsAdapterException()
    {
        var path = Write("bad.json", "{ this is not xbrl }");
        var adapter = new XbrlReportAdapter();
        var conn = new SourceConnection("src", adapter.Kind, path);
        var schema = await adapter.InspectSchemaAsync(conn, CancellationToken.None);
        await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(conn, schema, CancellationToken.None)) { }
        });
    }
}
