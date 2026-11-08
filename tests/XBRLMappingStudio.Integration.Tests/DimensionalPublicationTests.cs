using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class DimensionalPublicationTests
{
    private const string Rule = "xbrl:dimension-hypercube-valid";
    private const string Axis = "synthetic:RegionAxis";
    private const string Member = "synthetic:Domestic";
    private const string Role = "urn:test:dimensions";

    [Theory]
    [InlineData("segment", RunOutcome.InvalidMapping)]
    [InlineData("scenario", RunOutcome.IncompleteConfiguration)]
    public async Task ConditionalHypercubeFinding_BlocksPublicationAndRetainsFactEvidence(
        string contextElement, RunOutcome expectedOutcome)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-dimensional-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbook = TestFixtures.MaterializeWorkbook(directory);
            var project = TestFixtures.BuildRevenueMappingProject(workbook, approved: true);
            var profile = Profile(contextElement);
            Assert.DoesNotContain(Rule, profile.RequiredChecks); // Conditional checks still gate publication.
            var binding = project.Bindings.Single();
            var connections = new Dictionary<string, SourceConnection>
            {
                [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook,
                    binding.Configuration.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? ""))
            };
            await using var worker = new LocalSyntheticWorker(profile);
            var service = new MappingJobService(profile, worker);
            using var result = await service.RunAsync(project, connections, new Dictionary<string, string>(),
                preview: false, CancellationToken.None);

            Assert.Equal(expectedOutcome, result.Manifest.Outcome);
            Assert.False(result.IsPublishable);
            var finding = Assert.Single(result.Manifest.Diagnostics, diagnostic => diagnostic.RuleIdentifier == Rule);
            Assert.NotNull(finding.FactLocator);
            Assert.NotNull(finding.Source);
            Assert.NotEmpty(finding.SourceTransformationPaths!);
            Assert.Equal(contextElement == "segment", finding.IsBlocking);
            var expectedCode = contextElement == "segment"
                ? DiagnosticCodes.XbrDimensionInvalid : DiagnosticCodes.XbrRequiredCheckMissing;
            Assert.Equal(expectedCode, finding.Code);

            var target = Path.Combine(directory, "report.json");
            var prior = Encoding.UTF8.GetBytes("prior-accepted-report");
            await File.WriteAllBytesAsync(target, prior);
            var journal = new RunJournal(Path.Combine(directory, "journal"));
            var error = await Assert.ThrowsAsync<PublicationException>(() => new PublicationService(journal)
                .PublishAuthorizedAsync(target, Encoding.UTF8.GetBytes(result.XbrlJson), directory,
                    project, profile, allowSyntheticProfile: true, result: result,
                    validateStagedArtifactAsync: (_, _) => Task.FromResult("must-not-run"),
                    cancellationToken: CancellationToken.None));
            Assert.Equal(DiagnosticCodes.XbrApprovalRequired, error.Code);
            Assert.Equal(prior, await File.ReadAllBytesAsync(target));
            Assert.Null(journal.Load(result.Manifest.RunId));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("segment", WorkerStatus.DomainFailure)]
    [InlineData("scenario", WorkerStatus.IncompleteValidation)]
    public async Task StreamedWorker_GatesConditionalDimensionsAndLocatesEveryOccurrence(
        string contextElement, WorkerStatus expectedStatus)
    {
        var profile = Profile(contextElement);
        var facts = Enumerable.Range(1, 2).Select(index => new XbrlFact
        {
            ConceptName = "synthetic:Revenue",
            EntityScheme = "urn:test:entity",
            EntityIdentifier = "entity-" + index,
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 10m,
            Decimals = 0
        }).ToArray();
        var validation = new FilingRuleValidator(profile).ValidateStreaming(facts, null);
        await using var worker = new LocalSyntheticWorker(profile);
        var request = new WorkerRequest(WorkerOperations.ValidateReport, "dimensional-test", "job", profile.Digest,
            DateTimeOffset.UtcNow.AddMinutes(1), Payload: null);
        var response = await worker.ValidateLargeReport(request,
            facts.Select((fact, index) => new OimFactEntry("fact-" + index, fact)), validation, facts.Length);

        Assert.Equal(expectedStatus, response.Status);
        var findings = response.Diagnostics!.Where(diagnostic => diagnostic.RuleIdentifier == Rule).ToArray();
        Assert.Equal(new[] { "fact-0", "fact-1" }, findings.Select(finding => finding.FactLocator));
        Assert.Equal(2, response.Result!.Value.GetProperty("findingCount").GetInt32());
    }

    [Fact]
    public async Task LegacyConditionalMemberFailure_IsNotLostWhenItIsNotARequiredCheck()
    {
        var source = SyntheticProfile.Build();
        var profile = new FilingProfile
        {
            ProfileId = source.ProfileId,
            Digest = source.Digest,
            Concepts = source.Concepts.Concat(AdditionalConcepts()).ToArray(),
            NamespaceUris = source.NamespaceUris,
            TaxonomyUrls = source.TaxonomyUrls,
            SupportedUnits = source.SupportedUnits,
            RequiredChecks = source.RequiredChecks,
            DimensionDomains = new Dictionary<string, IReadOnlyList<string>> { [Axis] = new[] { Member } }
        };
        var fact = new XbrlFact
        {
            ConceptName = "synthetic:Revenue", EntityScheme = "urn:test:entity", EntityIdentifier = "one",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"), Value = 10m,
            Dimensions = new[] { new Dimension(Axis, "synthetic:Other") }
        };
        var json = OimSerializer.Serialize(profile, new[] { fact });
        var locator = Assert.Single(OimSerializer.ReadFactEntries(json, profile)).Locator;
        var request = new WorkerRequest(WorkerOperations.ValidateReport, "dimensional-test", "job", profile.Digest,
            DateTimeOffset.UtcNow.AddMinutes(1), JsonSerializer.SerializeToElement(json));
        await using var worker = new LocalSyntheticWorker(profile);
        var response = await worker.RequestAsync(request, CancellationToken.None);

        Assert.Equal(WorkerStatus.DomainFailure, response.Status);
        var finding = Assert.Single(response.Diagnostics!, diagnostic => diagnostic.RuleIdentifier == "synthetic:dimension-member-valid");
        Assert.Equal(locator, finding.FactLocator);
        Assert.True(finding.IsBlocking);
    }

    private static FilingProfile Profile(string contextElement)
    {
        var source = SyntheticProfile.Build();
        return new FilingProfile
        {
            ProfileId = source.ProfileId,
            Digest = source.Digest,
            Concepts = source.Concepts.Concat(AdditionalConcepts()).ToArray(),
            SupportedUnits = source.SupportedUnits,
            NamespaceUris = source.NamespaceUris,
            TaxonomyUrls = source.TaxonomyUrls,
            RequiredChecks = source.RequiredChecks,
            DimensionDomains = new Dictionary<string, IReadOnlyList<string>> { [Axis] = new[] { Member } },
            DefinitionLinks = new[]
            {
                new DefinitionLink(Role, XbrlArcRoles.All, "synthetic:Revenue", "synthetic:Cube",
                    Closed: true, ContextElement: contextElement),
                new DefinitionLink(Role, XbrlArcRoles.HypercubeDimension, "synthetic:Cube", Axis),
                new DefinitionLink(Role, XbrlArcRoles.DimensionDomain, Axis, Member)
            }
        };
    }

    private static IEnumerable<ConceptMetadata> AdditionalConcepts()
        => new[] { "RegionAxis", "Cube", "Domestic", "Other" }.Select(name => new ConceptMetadata(
            "synthetic:" + name, SyntheticProfile.Namespace, name, name, "string", "duration", true));
}
