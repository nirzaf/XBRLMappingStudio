using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>A host-enrolled reviewer public key. Never load this trust from the mapping project.</summary>
public sealed record TrustedPublicationReviewer(
    string KeyId, string ApprovedBy, string Authority, string SubjectPublicKeyInfoBase64);

/// <summary>Host-owned trust snapshot; an empty or malformed enrollment authorizes nobody.</summary>
public sealed class PublicationApprovalTrustPolicy
{
    private readonly TrustedPublicationReviewer[] _reviewers;
    private readonly TimeProvider _clock;
    public PublicationApprovalTrustPolicy(IEnumerable<TrustedPublicationReviewer> reviewers, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        _reviewers = reviewers.ToArray();
        _clock = clock ?? TimeProvider.System;
    }

    internal bool IsTrusted(MappingProject project, FilingProfile profile, ApprovalRecord approval)
    {
        var now = _clock.GetUtcNow();
        if (approval.SignatureAlgorithm != PublicationApprovalSignature.Algorithm
            || string.IsNullOrWhiteSpace(approval.SigningKeyId)
            || string.IsNullOrWhiteSpace(approval.ApprovedBy)
            || string.IsNullOrWhiteSpace(approval.Authority)
            || approval.ExpiresUtc is not { } expires
            || approval.ApprovedUtc > now || expires <= now || expires <= approval.ApprovedUtc)
            return false;
        var matches = _reviewers.Where(r => r is not null && r.KeyId == approval.SigningKeyId).ToArray();
        // Duplicate key IDs are ambiguous configuration, not an opportunity to try another identity.
        if (matches.Length != 1 || matches[0].ApprovedBy != approval.ApprovedBy
            || matches[0].Authority != approval.Authority) return false;
        try
        {
            var signature = Convert.FromBase64String(approval.SignatureBase64 ?? "");
            if (signature.Length != 64) return false;
            var encodedKey = Convert.FromBase64String(matches[0].SubjectPublicKeyInfoBase64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(encodedKey, out var read);
            if (read != encodedKey.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                return false;
            return key.VerifyData(PublicationApprovalSignature.CreatePayload(project, profile, approval),
                signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}

/// <summary>Versioned, deterministic approval bytes for an external signing workflow. Does not generate or store keys.</summary>
public static class PublicationApprovalSignature
{
    public const string Algorithm = "ECDSA-P256-SHA256-P1363-v1";

    public static byte[] CreatePayload(MappingProject project, FilingProfile profile, ApprovalRecord approval)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("purpose", "XBRLMappingStudio.publication-approval.v1");
            writer.WriteString("algorithm", approval.SignatureAlgorithm);
            writer.WriteString("keyId", approval.SigningKeyId);
            writer.WriteString("projectId", project.ProjectId);
            writer.WriteNumber("semanticRevision", approval.SemanticRevision);
            writer.WriteString("semanticDigest", approval.SemanticDigest);
            writer.WriteString("profileId", profile.ProfileId);
            writer.WriteString("profileDigest", approval.ProfileDigest);
            writer.WriteString("approvedBy", approval.ApprovedBy);
            writer.WriteString("authority", approval.Authority);
            writer.WriteString("approvedUtc", approval.ApprovedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("expiresUtc", approval.ExpiresUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
