using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>
/// EG03/XG01 publication gate. Checked before a full, unattended (production) run is
/// allowed to write anything to a durable destination — never at preview/draft time,
/// which is explicitly permitted to run against an unapproved revision so a reviewer
/// can still inspect a proposed mapping before accepting it.
///
/// Three independent conditions must all hold, and each is reported with its own
/// stable, never-reused diagnostic code:
///   1. Profile identity (XBR015): the project's declared <see cref="MappingProject.ProfileId"/>
///      and <see cref="MappingProject.ProfileDigest"/> must match the profile actually
///      resolved for this run. A missing or substituted profile blocks before any bytes move.
///   2. Explicit synthetic opt-in (XBR017): this build ships only the placeholder
///      <see cref="SyntheticProfile"/>; using it for a production publication is a
///      deliberate, caller-asserted decision and must be requested explicitly.
///   3. Approval (XBR016): an unattended publication requires an <see cref="ApprovalRecord"/>
///      on the project covering the current semantic revision, semantic graph digest and profile digest.
///
/// Callers treat a non-empty <see cref="DiagnosticBag.HasBlockingErrors"/> as "do not
/// publish"; the previously accepted destination, if any, must be left untouched.
/// </summary>
public static class PublicationGate
{
    public static DiagnosticBag Evaluate(
        MappingProject project,
        FilingProfile profile,
        bool allowSyntheticProfile,
        string? operationId = null,
        PublicationApprovalTrustPolicy? approvalTrustPolicy = null)
    {
        var diagnostics = EvaluateProfileIdentity(project, profile, DiagnosticStage.Publication, operationId);
        if (diagnostics.HasBlockingErrors)
            return diagnostics;

        var hasReservedSyntheticId =
            string.Equals(profile.ProfileId, SyntheticProfile.ProfileId, StringComparison.Ordinal);
        var isSynthetic = SyntheticProfile.IsBuiltInFixture(profile);
        if (hasReservedSyntheticId && !isSynthetic)
        {
            diagnostics.Error(
                DiagnosticCodes.XbrProfileMismatch,
                DiagnosticSubsystem.XBRL,
                DiagnosticStage.Publication,
                $"Profile ID '{SyntheticProfile.ProfileId}' is reserved for the immutable built-in development fixture; "
                + "a copied or modified profile cannot use the unsigned synthetic approval path.",
                operationId: operationId);
            return diagnostics;
        }

        if (isSynthetic && !allowSyntheticProfile)
        {
            diagnostics.Error(
                DiagnosticCodes.XbrSyntheticProfileNotAuthorized,
                DiagnosticSubsystem.XBRL,
                DiagnosticStage.Publication,
                $"'{SyntheticProfile.ProfileId}' is a placeholder profile for offline development; "
                + "it must not back a production publication unless the caller explicitly opts in. "
                + "Pass the synthetic-profile acknowledgment to proceed.",
                operationId: operationId);
        }

        var semanticDigest = project.ComputeSemanticDigest();
        var approval = project.Approvals.FirstOrDefault(a =>
            a.SemanticRevision == project.SemanticRevision
            && string.Equals(a.SemanticDigest, semanticDigest, StringComparison.Ordinal)
            && string.Equals(a.ProfileDigest, profile.Digest, StringComparison.Ordinal)
            && (approvalTrustPolicy is not null
                ? approvalTrustPolicy.IsTrusted(project, profile, a)
                : isSynthetic && allowSyntheticProfile));

        if (approval is null)
        {
            diagnostics.Error(
                DiagnosticCodes.XbrApprovalRequired,
                DiagnosticSubsystem.XBRL,
                DiagnosticStage.Publication,
                $"No approval record covers semantic revision {project.SemanticRevision}, graph digest "
                + $"{semanticDigest} and profile digest {profile.Digest}. An unattended publication "
                + "requires approval of the exact executable graph and profile being run. Non-synthetic publication requires a host-enrolled reviewer signature; a supplied trust policy also applies to synthetic runs.",
                operationId: operationId);
        }

        return diagnostics;
    }

    /// <summary>
    /// Confirms that the profile resolved for an operation is the immutable profile
    /// declared by the project. Shared by validation, execution, and publication so
    /// an unresolved or substituted profile is rejected before source data is read.
    /// </summary>
    public static DiagnosticBag EvaluateProfileIdentity(
        MappingProject project,
        FilingProfile profile,
        string stage,
        string? operationId = null)
    {
        var diagnostics = new DiagnosticBag();
        var identityMatches =
            string.Equals(project.ProfileId, profile.ProfileId, StringComparison.Ordinal)
            && string.Equals(project.ProfileDigest, profile.Digest, StringComparison.Ordinal);

        if (!identityMatches)
        {
            diagnostics.Error(
                DiagnosticCodes.XbrProfileMismatch,
                DiagnosticSubsystem.XBRL,
                stage,
                $"Project declares profile '{project.ProfileId}' @ {project.ProfileDigest}, but this "
                + $"run resolved '{profile.ProfileId}' @ {profile.Digest}. Refusing to continue under "
                + "a substituted or unrecognized profile.",
                operationId: operationId);
        }

        return diagnostics;
    }
}
