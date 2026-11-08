using System.Security.Cryptography;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

internal sealed class LateApprovalTestContext : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Clock _clock = new();
    public PublicationApprovalTrustPolicy Policy { get; }
    public LateApprovalTestContext() => Policy = new(new[]
    {
        new TrustedPublicationReviewer("test-key", "reviewer", "publication",
            Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()))
    }, _clock);
    public void Expire() => _clock.Now = _clock.Now.AddHours(2);
    public void Sign(MappingProject project, FilingProfile profile)
    {
        var approval = new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision, SemanticDigest = project.ComputeSemanticDigest(),
            ProfileDigest = profile.Digest, ApprovedBy = "reviewer", Authority = "publication",
            ApprovedUtc = _clock.Now, ExpiresUtc = _clock.Now.AddHours(1),
            SigningKeyId = "test-key", SignatureAlgorithm = PublicationApprovalSignature.Algorithm
        };
        project.Approvals.Clear();
        project.Approvals.Add(approval with
        {
            SignatureBase64 = Convert.ToBase64String(_key.SignData(
                PublicationApprovalSignature.CreatePayload(project, profile, approval), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });
    }
    public void Dispose() => _key.Dispose();
}
