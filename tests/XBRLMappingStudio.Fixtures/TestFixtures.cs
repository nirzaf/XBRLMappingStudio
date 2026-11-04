using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Fixtures;

internal static class DeterministicId
{
    private sealed class Generator
    {
        private long _counter;
        private readonly byte[] _seed;
        public Generator(Guid seed) => _seed = seed.ToByteArray();
        public string Next()
        {
            var input = new byte[_seed.Length + 8];
            Buffer.BlockCopy(_seed, 0, input, 0, _seed.Length);
            BitConverter.TryWriteBytes(input.AsSpan(_seed.Length), Interlocked.Increment(ref _counter));
            var hash = SHA256.HashData(input);
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }

    public static Func<string> FromSeed(Guid seed)
    {
        var g = new Generator(seed);
        return g.Next;
    }
}

/// <summary>
/// Fixture helpers used across engine, project, integration tests. Encapsulates the
/// synthetic profile inputs required for G1 acceptance:
///   - A workbook with current and prior-year revenue, instant assets, zeros and blanks.
///   - A canonical revenue-mapping project.
///   - An independently authored golden oracle for financial correctness.
/// </summary>
public static class TestFixtures
{
    public const string WorkbookName = "test-revenue.xlsx";
    public const string SheetName = "Financials";

    /// <summary>
    /// Workbook layout (rows 1-6, columns A-D):
    ///   A1: <paramref name="unitHeading">,  B1: "2025",     C1: "2024",   D1: "instant"
    ///   A2: (blank headings)
    ///   A3: "Line",     B3: "Current",  C3: "Prior",  D3: "AsOf"
    ///   A4: "Revenue",  B4: 1250,       C4: 1100,     D4: (blank)
    ///   A5: "Assets",   B5: 5000,       C5: 4700,     D5: 2025-12-31
    ///   A6: "Zero",     B6: 0,          C6: 0,        D6: (blank)
    ///   A7: "Blank",    B7: "",         C7: "",       D7: (blank)
    /// <paramref name="unitHeading"/> exists so a test can re-materialize the same
    /// workbook with a different unit/scale banner (e.g. "QAR units") over the same
    /// path to reproduce the IG01 heading-drift scenario.
    /// </summary>
    public static string MaterializeWorkbook(string directory, string unitHeading = "QAR thousands")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, WorkbookName);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName);

        ws.Cell("A1").Value = unitHeading; // unit + scale heading for section 5.2 fixture
        ws.Cell("B1").Value = "2025";
        ws.Cell("C1").Value = "2024";

        ws.Cell("A3").Value = "Line";
        ws.Cell("B3").Value = "Current";
        ws.Cell("C3").Value = "Prior";

        ws.Cell("A4").Value = "Revenue";
        ws.Cell("B4").Style.NumberFormat.Format = "#,##0";
        ws.Cell("B4").Value = 1250m;   // QAR thousands
        ws.Cell("C4").Value = 1100m;

        ws.Cell("A5").Value = "Assets";
        ws.Cell("B5").Value = 5000m;
        ws.Cell("C5").Value = 4700m;

        ws.Cell("A6").Value = "Zero";
        ws.Cell("B6").Value = 0m;
        ws.Cell("C6").Value = 0m;

        ws.Cell("A7").Value = "Blank";
        // B7 and C7 remain blank (missing)

        wb.SaveAs(path);
        return path;
    }

    /// <summary>
    /// A minimal revenue-mapping project on top of the fixture workbook:
    /// ExcelField(Revenue column) -> Scale(1000) -> FactConstruct(Revenue, QAR, decimals=-3).
    /// <paramref name="approved"/> adds an <see cref="ApprovalRecord"/> for the initial
    /// semantic revision; production (non-preview) publication via the CLI requires one
    /// (EG03/XG01) so a project destined for an unattended execute run must be built approved.
    /// </summary>
    public static MappingProject BuildRevenueMappingProject(
        string? workbookPath = null,
        string? concept = null,
        string? periodType = null,
        Guid? idSeed = null,
        bool approved = false)
    {
        var nextId = idSeed is { } seed
            ? DeterministicId.FromSeed(seed)
            : (Func<string>)Id.New;

        var bindingId = nextId();
        var excelNodeId = nextId();
        var scaleNodeId = nextId();
        var entityNodeId = nextId();
        var periodStartNodeId = nextId();
        var periodEndNodeId = nextId();
        var durationNodeId = nextId();
        var factNodeId = nextId();

        var profile = SyntheticProfile.Build();
        var project = new MappingProject
        {
            ProjectId = nextId(),
            Name = "RevenueMapping",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = profile.Digest,
            FunctionCatalogVersion = FunctionCatalog.Default.Version,
            Metadata = new ProjectMetadata
            {
                Author = "g1-fixture",
                Description = "Revenue mapped to synthetic:Revenue (duration) with QAR scale.",
                Organization = "XBRL Mapping Studio",
                CreatedUtc = DateTimeOffset.UtcNow,
                LastModifiedUtc = DateTimeOffset.UtcNow
            },
            Bindings = new List<SourceBinding>
            {
                new SourceBinding
                {
                    BindingId = bindingId,
                    Kind = "excel",
                    DisplayName = "Financials sheet",
                    Locator = workbookPath,
                    Configuration = new Dictionary<string, object?> { ["sheet"] = SheetName, ["headerRow"] = "3" }
                }
            },
            Nodes = new List<MappingNode>
            {
                new MappingNode
                {
                    NodeId = excelNodeId,
                    NodeType = NodeTypeConstants.ExcelField,
                    DisplayName = "Current revenue",
                    Parameters = new() { ["bindingId"] = bindingId, ["column"] = "Current", ["rowFilterColumn"] = "Line", ["rowFilterValue"] = "Revenue" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 0)
                },
                new MappingNode
                {
                    NodeId = scaleNodeId,
                    NodeType = NodeTypeConstants.Scale,
                    DisplayName = "Scale to units (thousands)",
                    Parameters = new() { ["factor"] = "1000" },
                    InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 1)
                },
                new MappingNode
                {
                    NodeId = entityNodeId,
                    NodeType = NodeTypeConstants.Constant,
                    DisplayName = "Entity",
                    Parameters = new() { ["literal"] = "ABC-Corp", ["shape"] = "String" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 2)
                },
                new MappingNode
                {
                    NodeId = periodStartNodeId,
                    NodeType = NodeTypeConstants.Constant,
                    DisplayName = "Period start (2025-01-01)",
                    Parameters = new() { ["literal"] = "2025-01-01", ["shape"] = "String" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 3)
                },
                new MappingNode
                {
                    NodeId = periodEndNodeId,
                    NodeType = NodeTypeConstants.Constant,
                    DisplayName = "Period end (2026-01-01)",
                    Parameters = new() { ["literal"] = "2026-01-01", ["shape"] = "String" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 4)
                },
                new MappingNode
                {
                    NodeId = durationNodeId,
                    NodeType = NodeTypeConstants.BuildDurationDate,
                    DisplayName = "Period 2025",
                    InputPorts = new()
                    {
                        new PortDefinition("startText", PortShape.String, PortCardinality.Single),
                        new PortDefinition("endText", PortShape.String, PortCardinality.Single)
                    },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Instant, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 5)
                },
                new MappingNode
                {
                    NodeId = factNodeId,
                    NodeType = NodeTypeConstants.FactConstruct,
                    DisplayName = "XBRL Revenue",
                    Parameters = new()
                    {
                        ["concept"] = concept ?? SyntheticProfile.Qualified("Revenue"),
                        ["unit"] = "QAR",
                        ["periodType"] = periodType ?? "duration",
                        ["decimals"] = "-3"
                    },
                    InputPorts = new()
                    {
                        new PortDefinition("value", PortShape.Decimal, PortCardinality.Single),
                        new PortDefinition("entity", PortShape.String, PortCardinality.Single),
                        new PortDefinition("period", PortShape.Instant, PortCardinality.Single, IsRequired: false)
                    },
                    OutputPorts = new() { new PortDefinition("fact", PortShape.Fact, PortCardinality.Single) },
                    Layout = new LayoutPosition(0, 6)
                }
            },
            Edges = new List<MappingEdge>
            {
                // Excel field value (string) is parsed and scaled. For simplicity the
                // adapter emits numeric cells already as decimal-shaped strings.
                new MappingEdge
                {
                    EdgeId = nextId(),
                    SourceNodeId = excelNodeId,
                    SourcePortName = "value",
                    TargetNodeId = scaleNodeId,
                    TargetPortName = "input"
                },
                new MappingEdge
                {
                    EdgeId = nextId(),
                    SourceNodeId = scaleNodeId,
                    SourcePortName = "value",
                    TargetNodeId = factNodeId,
                    TargetPortName = "value"
                },
                new MappingEdge
                {
                    EdgeId = nextId(),
                    SourceNodeId = entityNodeId,
                    SourcePortName = "value",
                    TargetNodeId = factNodeId,
                    TargetPortName = "entity"
                },
                new MappingEdge
                {
                    EdgeId = nextId(),
                    SourceNodeId = periodStartNodeId,
                    SourcePortName = "value",
                    TargetNodeId = durationNodeId,
                    TargetPortName = "startText"
                },
                new MappingEdge
                {
                    EdgeId = nextId(),
                    SourceNodeId = periodEndNodeId,
                    SourcePortName = "value",
                    TargetNodeId = durationNodeId,
                    TargetPortName = "endText"
                },
                new MappingEdge
                {
                    EdgeId = nextId(),
                    SourceNodeId = durationNodeId,
                    SourcePortName = "value",
                    TargetNodeId = factNodeId,
                    TargetPortName = "period"
                }
            }
        };
        if (approved)
        {
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "fixture-approver",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest,
                Authority = "fixture-harness",
                Notes = "Approved for use in fail-closed publication regression tests."
            });
        }
        return project;
    }

    /// <summary>
    /// Independently authored expected facts (per section 10 acceptance):
    /// a reviewer writes down what the report must contain; the engine output is
    /// compared against this oracle.
    /// </summary>
    public static IReadOnlyList<XbrlFact> GoldenRevenueFacts()
    {
        var src = new SourceReference("expected", "oracle/golden-revenue-facts.json");
        return new[]
        {
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "ABC-Corp",
                Period = XbrlPeriod.OfDuration(
                    new DateOnly(2025, 1, 1),
                    new DateOnly(2026, 1, 1)),
                Unit = new XbrlUnit("QAR"),
                Value = 1_250_000m,
                Decimals = -3,
                ContributingSources = new[] { src }
            }
        };
    }
}
