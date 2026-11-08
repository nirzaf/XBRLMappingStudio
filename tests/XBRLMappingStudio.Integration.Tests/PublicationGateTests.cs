using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

/// <summary>
/// EG03/XG01: the publication gate is checked directly, independent of the CLI's
/// argument parsing, so its three distinct blocking conditions are each pinned to a
/// stable, never-reused diagnostic code.
/// </summary>
public sealed class PublicationGateTests
{
    private static readonly FilingProfile Synthetic = SyntheticProfile.Build();

    private static MappingProject Project(string workbookPath = "unused.xlsx", bool approved = true)
        => TestFixtures.BuildRevenueMappingProject(workbookPath, approved: approved);

    [Fact]
    public void Approved_OptedInSyntheticRun_Passes()
    {
        var diagnostics = PublicationGate.Evaluate(Project(), Synthetic, allowSyntheticProfile: true);
        Assert.False(diagnostics.HasBlockingErrors);
    }

    [Fact]
    public void CopiedOrModifiedProfileCannotBorrowTheReservedSyntheticIdentity()
    {
        var project = Project();
        var copiedProfile = new FilingProfile
        {
            ProfileId = Synthetic.ProfileId,
            Digest = Synthetic.Digest,
            Concepts = Synthetic.Concepts,
            SupportedUnits = Synthetic.SupportedUnits,
            TaxonomyVersion = Synthetic.TaxonomyVersion,
            DimensionDomains = Synthetic.DimensionDomains,
            OutputFormat = Synthetic.OutputFormat,
            NamespaceUris = Synthetic.NamespaceUris,
            TaxonomyUrls = Synthetic.TaxonomyUrls,
            PackageHashes = Synthetic.PackageHashes,
            RequiredPlugins = Synthetic.RequiredPlugins,
            RequiredChecks = new HashSet<string>(StringComparer.Ordinal)
        };

        var diagnostics = PublicationGate.Evaluate(project, copiedProfile, allowSyntheticProfile: true);

        Assert.True(diagnostics.HasBlockingErrors);
        Assert.Contains(diagnostics.Items, diagnostic =>
            diagnostic.Code == DiagnosticCodes.XbrProfileMismatch && diagnostic.IsBlocking);
        Assert.DoesNotContain(diagnostics.Items, diagnostic =>
            diagnostic.Code == DiagnosticCodes.XbrApprovalRequired);
    }

    [Fact]
    public void BuiltInSyntheticProfileIsSharedAndDeeplyReadOnly()
    {
        var profile = SyntheticProfile.Build();

        Assert.Same(profile, SyntheticProfile.Build());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ConceptMetadata>)profile.Concepts)[0] = profile.Concepts[1]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)profile.Concepts[0].SupportedLanguages)[0] = "spoofed-language");
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)profile.Concepts[0].LabelsByLanguage)["en"] = "spoofed label");
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)profile.Concepts[1].SupportedLanguages)[0] = "spoofed-language");
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)profile.Concepts[1].LabelsByLanguage)["en"] = "spoofed label");
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)profile.TaxonomyUrls)[0] = "spoofed-taxonomy.xsd");
    }

    [Fact]
    public void SubstitutedProfile_Blocks_XBR015_BeforeAnyOtherCheck()
    {
        var project = Project();
        var foreignProfile = new FilingProfile
        {
            ProfileId = "some-real-taxonomy-v1",
            Digest = "sha256:deadbeef",
            Concepts = Synthetic.Concepts,
            SupportedUnits = Synthetic.SupportedUnits,
        };

        var diagnostics = PublicationGate.Evaluate(project, foreignProfile, allowSyntheticProfile: true);

        Assert.True(diagnostics.HasBlockingErrors);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.XbrProfileMismatch && d.IsBlocking);
        // A profile-identity failure short-circuits; it must not also pile on a
        // (moot) approval or synthetic-mode complaint.
        Assert.DoesNotContain(diagnostics.Items, d => d.Code == DiagnosticCodes.XbrApprovalRequired);
        Assert.DoesNotContain(diagnostics.Items, d => d.Code == DiagnosticCodes.XbrSyntheticProfileNotAuthorized);
    }

    [Fact]
    public void SyntheticProfile_WithoutExplicitOptIn_Blocks_XBR017()
    {
        var diagnostics = PublicationGate.Evaluate(Project(), Synthetic, allowSyntheticProfile: false);

        Assert.True(diagnostics.HasBlockingErrors);
        Assert.Contains(diagnostics.Items,
            d => d.Code == DiagnosticCodes.XbrSyntheticProfileNotAuthorized && d.IsBlocking);
    }

    [Fact]
    public void UnapprovedRevision_Blocks_XBR016()
    {
        var diagnostics = PublicationGate.Evaluate(Project(approved: false), Synthetic, allowSyntheticProfile: true);

        Assert.True(diagnostics.HasBlockingErrors);
        Assert.Contains(diagnostics.Items,
            d => d.Code == DiagnosticCodes.XbrApprovalRequired && d.IsBlocking);
        Assert.DoesNotContain(diagnostics.Items, d => d.Code == DiagnosticCodes.XbrSyntheticProfileNotAuthorized);
    }

    [Fact]
    public void ApprovalForADifferentRevisionOrProfileDigest_DoesNotSatisfyTheGate()
    {
        var project = Project();
        var approval = project.Approvals.Single();

        var staleRevision = approval with { SemanticRevision = approval.SemanticRevision + 1 };
        var staleDigest = approval with { ProfileDigest = "sha256:some-other-digest" };

        var projectWithStaleRevision = CloneWithApprovals(project, staleRevision);
        var projectWithStaleDigest = CloneWithApprovals(project, staleDigest);

        Assert.Contains(
            PublicationGate.Evaluate(projectWithStaleRevision, Synthetic, allowSyntheticProfile: true).Items,
            d => d.Code == DiagnosticCodes.XbrApprovalRequired);
        Assert.Contains(
            PublicationGate.Evaluate(projectWithStaleDigest, Synthetic, allowSyntheticProfile: true).Items,
            d => d.Code == DiagnosticCodes.XbrApprovalRequired);
    }

    [Fact]
    public void ApprovalForSameRevisionButDifferentGraphDigest_DoesNotSatisfyTheGate()
    {
        var project = Project();
        var approval = project.Approvals.Single();
        var changed = CloneWithApprovals(project, approval);
        changed.Nodes[0].Parameters["displayOnly-test-change"] = "changed executable graph";

        var diagnostics = PublicationGate.Evaluate(changed, Synthetic, allowSyntheticProfile: true);

        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.XbrApprovalRequired);
    }

    [Fact]
    public async Task FinalPublisherRejectsGraphChangedAfterTheRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Project();
            const string json = "{}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var manifest = new RunManifest
            {
                RunId = "run-test",
                OperationId = "op-test",
                ProjectId = project.ProjectId,
                SemanticRevision = project.SemanticRevision,
                ProjectSemanticDigest = project.ComputeSemanticDigest(),
                ProfileDigest = Synthetic.Digest,
                EngineVersion = "test",
                Outcome = RunOutcome.Success,
                Outputs = new[] { new OutputIdentity("<memory>", RunManifest.ComputeSha256(bytes), bytes.Length, "xbrl-json") }
            };
            var result = new JobResult(manifest, Array.Empty<XbrlFact>(), json,
                new DiagnosticBag(), Array.Empty<SampledTotal>());
            project.Nodes[0].Parameters["changed-after-run"] = true;

            await Assert.ThrowsAsync<PublicationException>(() =>
                new PublicationService(new RunJournal(root)).PublishAuthorizedAsync(
                    "report.json", bytes, root, project, Synthetic, true, result,
                    (_, _) => Task.FromResult(RunManifest.ComputeSha256(bytes))));
            Assert.False(File.Exists(Path.Combine(root, "report.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FinalPublisherRejectsGraphChangedDuringStagedValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Project();
            const string json = "{}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var manifest = new RunManifest
            {
                RunId = "run-test",
                OperationId = "op-test",
                ProjectId = project.ProjectId,
                SemanticRevision = project.SemanticRevision,
                ProjectSemanticDigest = project.ComputeSemanticDigest(),
                ProfileDigest = Synthetic.Digest,
                EngineVersion = "test",
                Outcome = RunOutcome.Success,
                Outputs = new[] { new OutputIdentity("<memory>", RunManifest.ComputeSha256(bytes), bytes.Length, "xbrl-json") }
            };
            var result = new JobResult(manifest, Array.Empty<XbrlFact>(), json,
                new DiagnosticBag(), Array.Empty<SampledTotal>());


            await Assert.ThrowsAsync<PublicationException>(() =>
                new PublicationService(new RunJournal(root)).PublishAuthorizedAsync(
                    "report.json", bytes, root, project, Synthetic, true, result,
                    (_, _) =>
                    {
                        project.Nodes[0].Parameters["changed-during-validation"] = true;
                        return Task.FromResult(RunManifest.ComputeSha256(bytes));
                    }));
            Assert.False(File.Exists(Path.Combine(root, "report.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MappingProject CloneWithApprovals(MappingProject source, ApprovalRecord approval)
        => new()
        {
            ProjectId = source.ProjectId,
            Name = source.Name,
            ProfileId = source.ProfileId,
            ProfileDigest = source.ProfileDigest,
            FunctionCatalogVersion = source.FunctionCatalogVersion,
            SemanticRevision = source.SemanticRevision,
            Metadata = source.Metadata,
            Nodes = source.Nodes,
            Edges = source.Edges,
            Bindings = source.Bindings,
            Functions = source.Functions,
            Approvals = new List<ApprovalRecord> { approval },
        };
}
