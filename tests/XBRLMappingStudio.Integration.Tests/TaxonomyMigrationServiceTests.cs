using System.Security.Cryptography;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class TaxonomyMigrationServiceTests
{
    [Fact]
    public void RenamedMappedConceptAndRemovedDimension_AreExplicitIncompatibilities()
    {
        var oldProfile = Profile("sha256:taxonomy-v1");
        var project = Project(oldProfile);
        AddApproval(project, oldProfile);
        var targetProfile = Profile("sha256:taxonomy-v2", concepts: new[]
        {
            Concept("ex:Sales", "Sales", periodType: "duration"),
            Concept("ex:RegionAxis", "Region", isAbstract: true),
            Concept("ex:NorthAmerica", "North America", isAbstract: true)
        }, domains: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        var proposal = TaxonomyMigrationService.Analyze(project, oldProfile, targetProfile);

        var removedConcept = Assert.Single(proposal.Changes,
            change => change.Code == "mapped-concept-removed-or-renamed");
        Assert.Equal(new[] { "fact-1" }, removedConcept.AffectedNodeIds);
        var removedDimension = Assert.Single(proposal.Changes,
            change => change.Code == "mapped-dimension-removed");
        Assert.Equal(new[] { "fact-1" }, removedDimension.AffectedNodeIds);
        Assert.True(proposal.HasSemanticIncompatibilities);
        Assert.True(proposal.RequiresReapproval);

        var draft = TaxonomyMigrationService.CreateDraft(proposal, project);

        Assert.Equal(oldProfile.Digest, project.ProfileDigest);
        Assert.Equal(oldProfile.Digest, TaxonomyMigrationService.RestoreSourceRevision(proposal).ProfileDigest);
        Assert.Equal(targetProfile.Digest, draft.ProfileDigest);
        Assert.Equal(project.SemanticRevision + 1, draft.SemanticRevision);
        Assert.Equal("ex:Revenue", draft.Nodes.Single().Parameters["concept"]?.ToString());
        Assert.Equal(project.ComputeSemanticDigest(), TaxonomyMigrationService.RestoreSourceRevision(proposal).ComputeSemanticDigest());
    }

    [Fact]
    public void PeriodTypeChange_OnMappedConceptRequiresReviewAndReapproval()
    {
        var oldProfile = Profile("sha256:taxonomy-v1");
        var targetProfile = Profile("sha256:taxonomy-v2", concepts: DefaultConcepts(periodType: "instant"));
        var project = Project(oldProfile);

        var proposal = TaxonomyMigrationService.Analyze(project, oldProfile, targetProfile);

        var change = Assert.Single(proposal.Changes, item => item.Code == "concept-period-type-changed");
        Assert.Equal(new[] { "fact-1" }, change.AffectedNodeIds);
        Assert.Equal(TaxonomyMigrationSeverity.Incompatible, change.Severity);
        Assert.True(proposal.RequiresReapproval);
    }

    [Fact]
    public void AddedRequiredCheckRequiresReview()
    {
        var oldProfile = Profile("sha256:taxonomy-v1");
        var targetProfile = Profile("sha256:taxonomy-v2",
            requiredChecks: new HashSet<string>(StringComparer.Ordinal) { "rule:filing-period" });
        var project = Project(oldProfile);

        var proposal = TaxonomyMigrationService.Analyze(project, oldProfile, targetProfile);

        var change = Assert.Single(proposal.Changes, item => item.Code == "required-check-added");
        Assert.Equal(TaxonomyMigrationSeverity.Review, change.Severity);
        Assert.Empty(change.AffectedNodeIds);
        Assert.True(proposal.RequiresReapproval);
        Assert.False(proposal.HasSemanticIncompatibilities);
    }

    [Fact]
    public void LabelOnlyChange_IsCompatibleButDraftCannotReuseOldApproval()
    {
        var oldProfile = Profile("sha256:taxonomy-v1");
        var project = Project(oldProfile);
        using var reviewerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var approval = new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "reviewer@example.test",
            ApprovedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            ProfileDigest = oldProfile.Digest,
            Authority = "taxonomy-migration-test",
            SigningKeyId = "migration-test-key",
            SignatureAlgorithm = PublicationApprovalSignature.Algorithm
        };
        approval = approval with
        {
            SignatureBase64 = Convert.ToBase64String(reviewerKey.SignData(
                PublicationApprovalSignature.CreatePayload(project, oldProfile, approval),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };
        project.Approvals.Add(approval);
        var trustPolicy = new PublicationApprovalTrustPolicy(new[]
        {
            new TrustedPublicationReviewer("migration-test-key", "reviewer@example.test",
                "taxonomy-migration-test", Convert.ToBase64String(reviewerKey.ExportSubjectPublicKeyInfo()))
        });
        Assert.False(PublicationGate.Evaluate(project, oldProfile, allowSyntheticProfile: false,
            approvalTrustPolicy: trustPolicy).HasBlockingErrors);
        var targetProfile = Profile("sha256:taxonomy-v2", concepts: DefaultConcepts(label: "Revenue and other income"));

        var proposal = TaxonomyMigrationService.Analyze(project, oldProfile, targetProfile);

        Assert.Contains(proposal.Changes, change => change.Code == "concept-label-changed");
        Assert.False(proposal.HasSemanticIncompatibilities);
        Assert.True(proposal.RequiresReapproval);
        Assert.Contains("new recorded human approval matching its semantic revision, graph digest, and target profile digest",
            proposal.Summary, StringComparison.Ordinal);

        var draft = TaxonomyMigrationService.CreateDraft(proposal, project);

        Assert.Single(draft.Approvals);
        Assert.DoesNotContain(draft.Approvals, approval =>
            approval.SemanticRevision == draft.SemanticRevision
            && approval.ProfileDigest == draft.ProfileDigest
            && approval.SemanticDigest == draft.ComputeSemanticDigest());
        var migratedGate = PublicationGate.Evaluate(draft, targetProfile, allowSyntheticProfile: false,
            approvalTrustPolicy: trustPolicy);
        Assert.Contains(migratedGate.Items, diagnostic =>
            diagnostic.Code == XBRLMappingStudio.Diagnostics.DiagnosticCodes.XbrApprovalRequired
            && diagnostic.IsBlocking);
        Assert.Equal(oldProfile.Digest, project.ProfileDigest);
    }

    [Fact]
    public void ExpandedNamePreservingPrefixChange_IsAppliedSafelyToDraft()
    {
        var oldProfile = Profile("sha256:taxonomy-v1", concepts: DefaultConcepts(), prefixes: new Dictionary<string, string>
        {
            ["ex"] = "urn:example"
        });
        var targetProfile = Profile("sha256:taxonomy-v2", concepts: new[]
        {
            Concept("tax:Revenue", "Revenue"),
            Concept("tax:RegionAxis", "Region", isAbstract: true),
            Concept("tax:NorthAmerica", "North America", isAbstract: true)
        }, domains: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["tax:RegionAxis"] = new[] { "tax:NorthAmerica" }
        }, prefixes: new Dictionary<string, string> { ["tax"] = "urn:example" });
        var project = Project(oldProfile);

        var proposal = TaxonomyMigrationService.Analyze(project, oldProfile, targetProfile);
        var draft = TaxonomyMigrationService.CreateDraft(proposal, project);

        Assert.Contains(proposal.Changes, change => change.Code == "concept-qname-prefix-changed");
        Assert.Equal("tax:Revenue", draft.Nodes.Single().Parameters["concept"]?.ToString());
        Assert.Equal("tax:RegionAxis=tax:NorthAmerica", draft.Nodes.Single().Parameters["dimensions"]?.ToString());
    }

    [Fact]
    public void UnchangedDigestProducesNoOpAndCannotCreateDraft()
    {
        var sourceProfile = Profile("sha256:taxonomy-v1");
        var targetProfile = Profile("sha256:taxonomy-v1");
        var project = Project(sourceProfile);

        var proposal = TaxonomyMigrationService.Analyze(project, sourceProfile, targetProfile);

        Assert.True(proposal.IsNoOp);
        Assert.False(proposal.RequiresReapproval);
        Assert.Empty(proposal.Changes);
        Assert.Throws<InvalidOperationException>(() => TaxonomyMigrationService.CreateDraft(proposal, project));
    }

    [Fact]
    public void StaleProposalIsRejectedAndSourceSnapshotCanRollback()
    {
        var oldProfile = Profile("sha256:taxonomy-v1");
        var targetProfile = Profile("sha256:taxonomy-v2", concepts: DefaultConcepts(label: "Updated Revenue"));
        var project = Project(oldProfile);
        var proposal = TaxonomyMigrationService.Analyze(project, oldProfile, targetProfile);
        project.Nodes.Single().Layout = new LayoutPosition(10, 20);

        Assert.Equal(proposal.SourceProjectDigest, project.ComputeSemanticDigest());
        Assert.Throws<InvalidOperationException>(() => TaxonomyMigrationService.CreateDraft(proposal, project));
        var restored = TaxonomyMigrationService.RestoreSourceRevision(proposal);
        Assert.Equal("ex:Revenue", restored.Nodes.Single().Parameters["concept"]?.ToString());
        Assert.Null(restored.Nodes.Single().Layout);
        Assert.Equal(oldProfile.Digest, restored.ProfileDigest);
    }

    private static FilingProfile Profile(
        string digest,
        IReadOnlyList<ConceptMetadata>? concepts = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? domains = null,
        IReadOnlyDictionary<string, string>? prefixes = null,
        IReadOnlySet<string>? requiredChecks = null)
        => new()
        {
            ProfileId = "example-filing",
            Digest = digest,
            TaxonomyVersion = digest.EndsWith("v1", StringComparison.Ordinal) ? "2025.1" : "2026.1",
            Concepts = concepts ?? DefaultConcepts(),
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" },
            DimensionDomains = domains ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["ex:RegionAxis"] = new[] { "ex:NorthAmerica" }
            },
            NamespaceUris = prefixes ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ex"] = "urn:example"
            },
            RequiredChecks = requiredChecks ?? new HashSet<string>(StringComparer.Ordinal)
        };

    private static IReadOnlyList<ConceptMetadata> DefaultConcepts(string periodType = "duration", string label = "Revenue")
        => new[]
        {
            Concept("ex:Revenue", label, periodType),
            Concept("ex:RegionAxis", "Region", isAbstract: true),
            Concept("ex:NorthAmerica", "North America", isAbstract: true)
        };

    private static ConceptMetadata Concept(string qname, string label, string periodType = "duration", bool isAbstract = false)
        => new(qname, "urn:example", qname[(qname.IndexOf(':') + 1)..], label,
            "monetary", periodType, isAbstract);

    private static MappingProject Project(FilingProfile profile)
        => new()
        {
            ProjectId = "project-1",
            Name = "Migration test",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest,
            SemanticRevision = 4,
            LayoutGeneration = 9,
            Metadata = new ProjectMetadata
            {
                Author = "test",
                Description = "",
                Organization = "test",
                CreatedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
            },
            Nodes = new List<MappingNode>
            {
                new()
                {
                    NodeId = "fact-1",
                    NodeType = NodeTypeConstants.FactConstruct,
                    DisplayName = "Revenue fact",
                    Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["concept"] = "ex:Revenue",
                        ["periodType"] = "duration",
                        ["dimensions"] = "ex:RegionAxis=ex:NorthAmerica"
                    }
                }
            }
        };

    private static void AddApproval(MappingProject project, FilingProfile profile)
        => project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "reviewer@example.invalid",
            ApprovedUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            ProfileDigest = profile.Digest,
            Authority = "test",
            Notes = "approved source revision"
        });
}
