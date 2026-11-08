using System.Globalization;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

/// <summary>
/// Generic JSON/XML sources must reach the existing synthetic mapping and publication
/// boundary without changing a numeric value or choosing between colliding source paths.
/// These are ordinary bounded runs, not qualifications of the large-run adapter path.
/// </summary>
public sealed class GenericSourceIntegrityTests
{
    private const string Field = "financial.amount";
    private static readonly FilingProfile Profile = SyntheticProfile.Build();

    public static IEnumerable<object[]> ExactNumbers()
    {
        // Expected values are hand-authored, independent of the adapter or XBRL reader.
        var cases = new (string Kind, string Lexical, decimal Value)[]
        {
            ("json", "1.2300E+2", 123m),
            ("json", "-0e+25", 0m),
            ("json", "1e-28", 0.0000000000000000000000000001m),
            ("json", "0.00000000000000000000000000010", 0.0000000000000000000000000001m),
            ("xml", "+00123.4500", 123.45m),
            ("xml", "0.00000000000000000000000000010", 0.0000000000000000000000000001m),
            ("xml", "1.230000000000000000000000000000", 1.23m)
        };
        foreach (var item in cases)
        foreach (var preview in new[] { false, true })
            yield return new object[] { item.Kind, item.Lexical, item.Value, preview };
    }

    [Theory]
    [MemberData(nameof(ExactNumbers))]
    public async Task ExactNumbers_PreserveSourceEvidenceAndProduceTheIndependentFact(
        string kind, string lexical, decimal expected, bool preview)
    {
        using var workspace = new Workspace();
        var sourcePath = workspace.PathFor("source." + kind);
        await File.WriteAllTextAsync(sourcePath, Document(kind, lexical));
        var project = BuildProject(kind, sourcePath);
        var connection = Connection(project);
        var source = new SourceReference(connection.SourceId, $"{kind}#r1:{Field}");

        ISourceAdapter adapter = kind == "json" ? new JsonAdapter() : new XmlAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        DataRecord row;
        if (preview)
        {
            var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
            row = Assert.Single(sample.Rows);
        }
        else
        {
            var rows = new List<DataRecord>();
            await foreach (var record in adapter.ReadFullAsync(connection, schema, default)) rows.Add(record);
            row = Assert.Single(rows);
        }
        Assert.Equal(connection.SourceId + "#r1", row.RowId);
        Assert.Equal(PortShape.Decimal, row[Field].Shape);
        Assert.Equal(expected, row[Field].AsDecimal());
        Assert.Equal(lexical, row[Field].LexicalSource);
        Assert.Equal(source, row[Field].Source);
        Assert.Equal(source, row.CellSources![Field]);

        await using var worker = new CountingSyntheticWorker();
        var service = new MappingJobService(Profile, worker);
        using var result = await Run(service, project, preview);
        Assert.Equal(preview ? RunOutcome.SuccessWithWarnings : RunOutcome.Success, result.Manifest.Outcome);
        Assert.False(result.Diagnostics.HasBlockingErrors, Describe(result));
        Assert.Equal(!preview, result.IsPublishable);
        Assert.Equal(1, worker.ReportValidationCount);
        Assert.Null(result.LargeArtifact);
        var input = Assert.Single(result.Manifest.Inputs);
        Assert.Equal(connection.SourceId, input.SourceId);
        Assert.Equal(sourcePath, input.OriginalPath);
        Assert.Equal(RunManifest.ComputeSha256(await File.ReadAllBytesAsync(sourcePath)), input.Digest);
        Assert.Equal(preview ? RunProvenanceStatus.Incomplete : RunProvenanceStatus.Complete,
            result.Manifest.ProvenanceStatus);

        var fact = Assert.Single(result.Facts);
        Assert.Equal(expected, fact.Value);
        Assert.Equal("synthetic:Revenue", fact.ConceptName);
        Assert.Equal("ABC-Corp", fact.EntityIdentifier);
        Assert.Equal("QAR", fact.Unit!.Measure);
        Assert.Equal(28, fact.Decimals);
        Assert.Equal(XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)), fact.Period);
        Assert.Equal(source, Assert.Single(fact.ContributingSources));
        var path = Assert.Single(fact.SourceTransformationPaths);
        Assert.Equal(source, path.Source);
        Assert.Equal(new[]
        {
            project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField).NodeId,
            project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct).NodeId
        }, path.TransformationIds);
        using var json = JsonDocument.Parse(result.XbrlJson);
        var reported = Assert.Single(json.RootElement.GetProperty("facts").EnumerateObject()).Value;
        Assert.Equal(expected, decimal.Parse(reported.GetProperty("value").GetString()!, CultureInfo.InvariantCulture));
        Assert.Equal("synthetic:Revenue", reported.GetProperty("dimensions").GetProperty("concept").GetString());

        if (preview)
        {
            // Even numerically correct preview evidence cannot cross the final write boundary.
            await AssertPublicationRejected(workspace, project, service, result,
                DiagnosticCodes.XbrApprovalRequired, expectedBytes: null);
        }
        else
        {
            var accepted = await Publish(workspace, project, service, result);
            Assert.Equal(Encoding.UTF8.GetBytes(result.XbrlJson), accepted);
            Assert.Equal(2, worker.ReportValidationCount);
        }
    }

    public static IEnumerable<object[]> InvalidSources()
    {
        var cases = new (string Kind, string Document, string Code)[]
        {
            ("json", Document("json", "1e-29"), DiagnosticCodes.IoNumberOutOfBudget),
            ("json", Document("json", "9e-29"), DiagnosticCodes.IoNumberOutOfBudget),
            ("json", Document("json", "1.000000000000000000000000001e-2"), DiagnosticCodes.IoNumberOutOfBudget),
            ("json", Document("json", "0.00000000000000000000000000015"), DiagnosticCodes.IoNumberOutOfBudget),
            // Each fits the configured 32-digit budget but cannot fit CLR's 96-bit coefficient exactly.
            ("json", Document("json", "7922816251426433759354395033.6"), DiagnosticCodes.IoNumberOutOfBudget),
            ("xml", Document("xml", "7922816251426433759354395033.6"), DiagnosticCodes.IoNumberOutOfBudget),
            ("xml", Document("xml", "1.00000000000000000000000000001"), DiagnosticCodes.IoNumberOutOfBudget),
            ("xml", Document("xml", "0.00000000000000000000000000001"), DiagnosticCodes.IoNumberOutOfBudget),
            ("json", "{\"financial.amount\":123.45,\"financial\":{\"amount\":456.78}}", DiagnosticCodes.IoDuplicateKey),
            ("json", "{\"financial\":{\"amount\":456.78},\"financial.amount\":123.45}", DiagnosticCodes.IoDuplicateKey),
            ("json", "{\"financial.amount\":123.45,\"financial\":{\"amount\":null}}", DiagnosticCodes.IoDuplicateKey),
            ("xml", "<root><financial amount=\"123.45\"><amount>456.78</amount></financial></root>", DiagnosticCodes.IoDuplicateKey),
            ("xml", "<root xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><financial amount=\"123.45\"><amount xsi:nil=\"true\"/></financial></root>", DiagnosticCodes.IoDuplicateKey),
            ("xml", "<root><financial.amount>123.45</financial.amount><financial><amount>456.78</amount></financial></root>", DiagnosticCodes.IoDuplicateKey)
        };
        foreach (var item in cases)
        foreach (var preview in new[] { false, true })
            yield return new object[] { item.Kind, item.Document, item.Code, preview };
    }

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public async Task InvalidSource_FailsBeforeReportValidationAndPreservesAcceptedDestination(
        string kind, string invalidDocument, string expectedCode, bool preview)
    {
        using var workspace = new Workspace();
        var sourcePath = workspace.PathFor("source." + kind);
        await File.WriteAllTextAsync(sourcePath, Document(kind, "123.45"));
        // Raise only the JSON lexical budget so coefficient rounding cannot be hidden
        // by the default mantissa-length rejection. The exact-decimal limit still applies.
        var project = BuildProject(kind, sourcePath, maxNumberDigits: 32);
        await using var worker = new CountingSyntheticWorker();
        var service = new MappingJobService(Profile, worker);
        using var baseline = await Run(service, project, preview: false);
        Assert.True(baseline.IsPublishable, Describe(baseline));
        Assert.Equal(123.45m, Assert.Single(baseline.Facts).Value);
        var accepted = await Publish(workspace, project, service, baseline);
        var validationCount = worker.ReportValidationCount;

        await File.WriteAllTextAsync(sourcePath, invalidDocument);
        using var failed = await Run(service, project, preview);

        Assert.Equal(RunOutcome.DataError, failed.Manifest.Outcome);
        Assert.Equal(preview, failed.Manifest.IsPreview);
        Assert.False(failed.IsPublishable);
        Assert.Empty(failed.Facts);
        Assert.Empty(failed.Manifest.Outputs);
        Assert.Empty(failed.XbrlJson);
        Assert.Null(failed.LargeArtifact);
        var diagnostic = Assert.Single(failed.Manifest.Diagnostics, item => item.IsBlocking);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(DiagnosticSubsystem.Adapter, diagnostic.Subsystem);
        Assert.Equal(DiagnosticStage.Introspection, diagnostic.Stage);
        Assert.Equal(project.Bindings.Single().BindingId, diagnostic.Source!.SourceId);
        Assert.Equal(failed.Manifest.OperationId, diagnostic.OperationId);
        Assert.Equal(validationCount, worker.ReportValidationCount);
        Assert.Null(failed.Manifest.PublishedArtifactDigest);

        await AssertPublicationRejected(workspace, project, service, failed,
            DiagnosticCodes.XbrApprovalRequired, accepted);
        Assert.Equal(validationCount, worker.ReportValidationCount);
    }

    [Fact]
    public async Task JsonInputBudget_BlocksBeforeReportValidationAndPreservesPriorOutput()
    {
        using var workspace = new Workspace();
        var sourcePath = workspace.PathFor("source.json");
        await File.WriteAllTextAsync(sourcePath, Document("json", "123.45"));
        var project = BuildProject("json", sourcePath, maxInputBytes: 8);
        var priorOutput = Encoding.UTF8.GetBytes("accepted prior output");
        await File.WriteAllBytesAsync(workspace.Destination, priorOutput);
        await using var worker = new CountingSyntheticWorker();
        var service = new MappingJobService(Profile, worker);

        using var result = await Run(service, project, preview: false);

        Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        Assert.Empty(result.Facts);
        Assert.Empty(result.Manifest.Outputs);
        Assert.Empty(result.XbrlJson);
        Assert.Equal(0, worker.ReportValidationCount);
        var diagnostic = Assert.Single(result.Manifest.Diagnostics, item => item.IsBlocking);
        Assert.Equal(DiagnosticCodes.IoInputBudgetExceeded, diagnostic.Code);
        Assert.Equal(project.Bindings.Single().BindingId, diagnostic.Source!.SourceId);
        Assert.Equal(priorOutput, await File.ReadAllBytesAsync(workspace.Destination));
    }

    [Theory]
    [InlineData("json", "approval", DiagnosticCodes.XbrApprovalRequired)]
    [InlineData("xml", "approval", DiagnosticCodes.XbrApprovalRequired)]
    [InlineData("json", "graph", DiagnosticCodes.XbrApprovalRequired)]
    [InlineData("xml", "graph", DiagnosticCodes.XbrApprovalRequired)]
    [InlineData("json", "profile", DiagnosticCodes.XbrProfileMismatch)]
    [InlineData("xml", "profile", DiagnosticCodes.XbrProfileMismatch)]
    [InlineData("json", "synthetic-opt-in", DiagnosticCodes.XbrSyntheticProfileNotAuthorized)]
    [InlineData("xml", "synthetic-opt-in", DiagnosticCodes.XbrSyntheticProfileNotAuthorized)]
    [InlineData("json", "source-identity", DiagnosticCodes.PubDigestMismatch)]
    [InlineData("xml", "source-identity", DiagnosticCodes.PubDigestMismatch)]
    public async Task ExactGenericSource_DoesNotBypassPublicationApprovalOrIdentity(
        string kind, string mutation, string expectedCode)
    {
        using var workspace = new Workspace();
        var sourcePath = workspace.PathFor("source." + kind);
        await File.WriteAllTextAsync(sourcePath, Document(kind, "123.45"));
        var project = BuildProject(kind, sourcePath);
        await using var worker = new CountingSyntheticWorker();
        var service = new MappingJobService(Profile, worker);
        using var result = await Run(service, project, preview: false);
        Assert.True(result.IsPublishable, Describe(result));
        var accepted = await Publish(workspace, project, service, result);
        var validationCount = worker.ReportValidationCount;
        var selectedProfile = Profile;
        switch (mutation)
        {
            case "approval":
                project.Approvals.Clear();
                break;
            case "graph":
                project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField)
                    .Parameters["column"] = "financial.otherAmount";
                break;
            case "profile":
                selectedProfile = new FilingProfile
                {
                    ProfileId = "different-profile", Digest = "sha256:different-profile",
                    Concepts = Profile.Concepts, SupportedUnits = Profile.SupportedUnits
                };
                break;
            case "source-identity":
                // Equal decimal value does not authorize replacing the bytes whose
                // lexical evidence and digest were recorded by the completed run.
                await File.WriteAllTextAsync(sourcePath, Document(kind, "123.450"));
                break;
        }

        await AssertPublicationRejected(workspace, project, service, result, expectedCode, accepted,
            selectedProfile, allowSynthetic: mutation != "synthetic-opt-in",
            validateStage: mutation == "source-identity");
        Assert.Equal(validationCount, worker.ReportValidationCount);
    }

    [Theory]
    [InlineData("1e-29", "1e-29")]
    [InlineData(" 123.45 ", "123.45")]
    [InlineData("1,234.5", "1,234.5")]
    [InlineData("79228162514264337593543950336", "79228162514264337593543950336")]
    public async Task Xml_NonNumericLexicalGrammarRemainsText(string lexical, string expectedText)
    {
        using var workspace = new Workspace();
        var sourcePath = workspace.PathFor("source.xml");
        await File.WriteAllTextAsync(sourcePath, Document("xml", lexical));
        var connection = Connection(BuildProject("xml", sourcePath));
        var adapter = new XmlAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        var value = Assert.Single(sample.Rows)[Field];
        Assert.Equal(PortShape.String, value.Shape);
        Assert.Equal(expectedText, value.AsString());
        Assert.Null(value.LexicalSource);
        Assert.Equal(new SourceReference(connection.SourceId, "xml#r1:" + Field), value.Source);
    }

    private static string Document(string kind, string lexical) => kind == "json"
        ? "{\"financial\":{\"amount\":" + lexical + "}}"
        : "<root><financial><amount>" + lexical + "</amount></financial></root>";

    private static MappingProject BuildProject(
        string kind,
        string sourcePath,
        int? maxNumberDigits = null,
        int? maxInputBytes = null)
    {
        var project = TestFixtures.BuildRevenueMappingProject(sourcePath,
            idSeed: Guid.Parse("4fa6c9d8-8e20-4ad1-ab20-399ed91cd276"));
        project.Bindings[0] = project.Bindings[0] with
        {
            Kind = kind, DisplayName = "Generic " + kind + " source", Configuration = new()
        };
        if (maxNumberDigits is { } budget && kind == "json")
            project.Bindings[0].Configuration["maxNumberDigits"] = budget.ToString(CultureInfo.InvariantCulture);
        if (maxInputBytes is { } byteBudget && kind == "json")
            project.Bindings[0].Configuration["maxInputBytes"] = byteBudget.ToString(CultureInfo.InvariantCulture);

        // ExcelField is the existing format-agnostic source-field node. Do not add
        // another graph registration or a parser that could mask adapter corruption.
        var source = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField);
        source.Parameters["column"] = Field;
        source.Parameters.Remove("rowFilterColumn");
        source.Parameters.Remove("rowFilterValue");
        var scale = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        var fact = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        fact.Parameters["decimals"] = "28";
        project.Nodes.Remove(scale);
        project.Edges.RemoveAll(edge => edge.SourceNodeId == scale.NodeId || edge.TargetNodeId == scale.NodeId);
        project.Edges.Add(new MappingEdge
        {
            EdgeId = "44ad9a7ba5284843ac7d0ed1809a44a1",
            SourceNodeId = source.NodeId, SourcePortName = "value",
            TargetNodeId = fact.NodeId, TargetPortName = "value"
        });
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
            ProfileDigest = Profile.Digest, ApprovedBy = "generic-source-fixture", Authority = "test-fixture",
            ApprovedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        });
        return project;
    }

    private static SourceConnection Connection(MappingProject project)
    {
        var binding = project.Bindings.Single();
        return new SourceConnection(binding.BindingId, binding.Kind, binding.Locator,
            binding.Configuration.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? ""));
    }

    private static Task<JobResult> Run(MappingJobService service, MappingProject project, bool preview)
    {
        var connection = Connection(project);
        return service.RunAsync(project, new Dictionary<string, SourceConnection>
        {
            [connection.SourceId] = connection
        }, new Dictionary<string, string>(), preview, default);
    }

    private static async Task<byte[]> Publish(
        Workspace workspace, MappingProject project, MappingJobService service, JobResult result)
    {
        var journal = new RunJournal(workspace.PathFor("accepted-journal"));
        var receipt = await new PublicationService(journal).PublishAuthorizedAsync(workspace.Destination,
            Encoding.UTF8.GetBytes(result.XbrlJson), workspace.Root, project, Profile, true, result,
            (staged, token) => service.ValidateStagedArtifactAsync(staged, workspace.Destination, result, token));
        var bytes = await File.ReadAllBytesAsync(workspace.Destination);
        Assert.Equal(RunManifest.ComputeSha256(bytes), receipt.FinalDigest);
        Assert.Equal(receipt.FinalDigest, receipt.ValidatedDigest);
        Assert.Equal(receipt.FinalDigest, result.Manifest.PublishedArtifactDigest);
        Assert.NotNull(journal.Load(result.Manifest.RunId));
        return bytes;
    }

    private static async Task AssertPublicationRejected(
        Workspace workspace, MappingProject project, MappingJobService service, JobResult result,
        string expectedCode, byte[]? expectedBytes, FilingProfile? profile = null,
        bool allowSynthetic = true, bool validateStage = false)
    {
        var journal = new RunJournal(workspace.PathFor("rejected-journal"));
        var validationCalled = false;
        var error = await Assert.ThrowsAsync<PublicationException>(() => new PublicationService(journal)
            .PublishAuthorizedAsync(workspace.Destination, Encoding.UTF8.GetBytes(result.XbrlJson), workspace.Root,
                project, profile ?? Profile, allowSynthetic, result, (staged, token) =>
                {
                    validationCalled = true;
                    Assert.True(validateStage, "A rejected run or approval must never reach staged validation.");
                    return service.ValidateStagedArtifactAsync(staged, workspace.Destination, result, token);
                }));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(validateStage, validationCalled);
        if (expectedBytes is null) Assert.False(File.Exists(workspace.Destination));
        else Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(workspace.Destination));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.xbsl-staging-*"));
        if (validateStage) Assert.Equal(JournalState.Aborted, journal.Load(result.Manifest.RunId)!.State);
        else Assert.Null(journal.Load(result.Manifest.RunId));
    }

    private static string Describe(JobResult result)
        => result.Manifest.Outcome + ": " + string.Join("; ", result.Manifest.Diagnostics.Select(d => d.Code + " " + d.Message));

    private sealed class CountingSyntheticWorker : IWorkerClient
    {
        private readonly LocalSyntheticWorker _inner = new(Profile);
        public int ReportValidationCount { get; private set; }
        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => _inner.HandshakeAsync(cancellationToken);
        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            if (request.Operation == WorkerOperations.ValidateReport) ReportValidationCount++;
            return _inner.RequestAsync(request, cancellationToken);
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "xms-generic-source-" + Guid.NewGuid().ToString("N"));
        public string Destination => PathFor("accepted-report.json");
        public Workspace() => Directory.CreateDirectory(Root);
        public string PathFor(string name) => Path.Combine(Root, name);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
