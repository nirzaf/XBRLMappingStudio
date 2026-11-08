using System.Security.Cryptography;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class TrustedPublicationApprovalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static FilingProfile Profile(bool synthetic = false) => synthetic ? SyntheticProfile.Build() : new()
    {
        ProfileId = "test-installed-profile", Digest = "sha256:installed-profile",
        Concepts = SyntheticProfile.Build().Concepts, SupportedUnits = SyntheticProfile.Build().SupportedUnits
    };
    private static MappingProject Project(FilingProfile profile, string id = "project-a") => new()
    {
        ProjectId = id, Name = "Approval test", ProfileId = profile.ProfileId, ProfileDigest = profile.Digest
    };
    private static ApprovalRecord Approval(MappingProject project) => new()
    {
        SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
        ProfileDigest = project.ProfileDigest, ApprovedBy = "reviewer@example.test", Authority = "filing-review",
        ApprovedUtc = Now.AddMinutes(-1), ExpiresUtc = Now.AddHours(1), SigningKeyId = "reviewer-key-1",
        SignatureAlgorithm = PublicationApprovalSignature.Algorithm
    };
    private static ApprovalRecord Sign(ECDsa key, MappingProject project, FilingProfile profile, ApprovalRecord approval)
        => approval with { SignatureBase64 = Convert.ToBase64String(key.SignData(
            PublicationApprovalSignature.CreatePayload(project, profile, approval), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    private static PublicationApprovalTrustPolicy Policy(ECDsa key, string? publicKey = null) => new(new[]
    {
        new TrustedPublicationReviewer("reviewer-key-1", "reviewer@example.test", "filing-review",
            publicKey ?? Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))
    }, new FixedClock());
    private static bool Blocked(MappingProject project, FilingProfile profile, PublicationApprovalTrustPolicy? policy = null)
        => PublicationGate.Evaluate(project, profile, true, approvalTrustPolicy: policy).HasBlockingErrors;

    [Fact]
    public void NonSyntheticFailsClosedWithoutHostTrustEvenWhenSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Profile(); var project = Project(profile);
        project.Approvals.Add(Sign(key, project, profile, Approval(project)));
        Assert.True(Blocked(project, profile));
        Assert.False(Blocked(project, profile, Policy(key)));
        Assert.True(Blocked(project, profile, new PublicationApprovalTrustPolicy(Array.Empty<TrustedPublicationReviewer>())));
    }

    [Fact]
    public void LegacySyntheticIsAcceptedOnlyWithoutPolicyAndWithExplicitOptIn()
    {
        var profile = Profile(true); var project = Project(profile);
        project.Approvals.Add(Approval(project) with { SignatureAlgorithm = null, SigningKeyId = null });
        Assert.False(Blocked(project, profile));
        Assert.True(PublicationGate.Evaluate(project, profile, false).HasBlockingErrors);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(Blocked(project, profile, Policy(key)));
        project.Approvals[0] = Sign(key, project, profile, Approval(project));
        Assert.False(Blocked(project, profile, Policy(key)));
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("authority")]
    [InlineData("key")]
    [InlineData("algorithm")]
    [InlineData("timestamp")]
    [InlineData("expiry")]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("profile")]
    [InlineData("signature")]
    [InlineData("missing")]
    public void MutatingAnySecurityClaimRejectsSignature(string mutation)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Profile(); var project = Project(profile);
        var a = Sign(key, project, profile, Approval(project));
        project.Approvals.Add(mutation switch
        {
            "reviewer" => a with { ApprovedBy = "attacker" }, "authority" => a with { Authority = "other" },
            "key" => a with { SigningKeyId = "untrusted" }, "algorithm" => a with { SignatureAlgorithm = "none" },
            "timestamp" => a with { ApprovedUtc = a.ApprovedUtc.AddSeconds(-1) },
            "expiry" => a with { ExpiresUtc = a.ExpiresUtc!.Value.AddSeconds(1) },
            "digest" => a with { SemanticDigest = "forged" }, "revision" => a with { SemanticRevision = 2 },
            "profile" => a with { ProfileDigest = "forged" },
            "signature" => a with { SignatureBase64 = "not base64" }, _ => a with { SignatureBase64 = null }
        });
        Assert.True(Blocked(project, profile, Policy(key)));
    }

    [Theory]
    [InlineData(-2, -1)]
    [InlineData(-1, 0)]
    [InlineData(1, 2)]
    [InlineData(-1, -2)]
    public void EvenAuthenticSignatureCannotAuthorizeInvalidTimeWindow(int approvedHours, int expiresHours)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Profile(); var project = Project(profile);
        project.Approvals.Add(Sign(key, project, profile, Approval(project) with
        { ApprovedUtc = Now.AddHours(approvedHours), ExpiresUtc = Now.AddHours(expiresHours) }));
        Assert.True(Blocked(project, profile, Policy(key)));
    }

    [Fact]
    public void SignatureCannotBeReplayedAcrossProjectsOrRewrittenGraph()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Profile(); var project = Project(profile); var other = Project(profile, "project-b");
        var a = Sign(key, project, profile, Approval(project)); other.Approvals.Add(a);
        Assert.True(Blocked(other, profile, Policy(key)));
        project.SemanticRevision++;
        project.Approvals.Add(a with { SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest() });
        Assert.True(Blocked(project, profile, Policy(key)));
    }

    [Fact]
    public void UnsignedReplacementDigestCannotReuseSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Profile(); var project = Project(profile);
        var signed = Sign(key, project, profile, Approval(project));
        project.Bindings.Add(new SourceBinding
        {
            BindingId = "changed-source", Kind = "csv", DisplayName = "Changed", Locator = "different.csv"
        });
        project.Approvals.Add(signed with { SemanticDigest = project.ComputeSemanticDigest() });
        Assert.True(Blocked(project, profile, Policy(key)));
    }

    [Fact]
    public void MissingExpiryAndDuplicateEnrollmentFailClosed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Profile(); var project = Project(profile);
        project.Approvals.Add(Sign(key, project, profile, Approval(project) with { ExpiresUtc = null }));
        Assert.True(Blocked(project, profile, Policy(key)));
        project.Approvals[0] = Sign(key, project, profile, Approval(project));
        var reviewer = new TrustedPublicationReviewer("reviewer-key-1", "reviewer@example.test", "filing-review",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.True(Blocked(project, profile, new PublicationApprovalTrustPolicy(new[] { reviewer, reviewer }, new FixedClock())));
    }

    [Fact]
    public void ForgedKeyAndMalformedEnrollmentFailClosed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Profile(); var project = Project(profile);
        project.Approvals.Add(Sign(attacker, project, profile, Approval(project)));
        Assert.True(Blocked(project, profile, Policy(key)));
        project.Approvals[0] = Sign(key, project, profile, Approval(project));
        foreach (var malformed in new[] { "invalid", "", Convert.ToBase64String(new byte[64]),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo().Concat(new byte[] { 0 }).ToArray()) })
            Assert.True(Blocked(project, profile, Policy(key, malformed)));
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.True(Blocked(project, profile, Policy(key, Convert.ToBase64String(wrongCurve.ExportSubjectPublicKeyInfo()))));
        project.Approvals[0] = project.Approvals[0] with { SignatureBase64 = Convert.ToBase64String(new byte[64]) };
        Assert.True(Blocked(project, profile, Policy(key)));
    }
}
