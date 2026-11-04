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
///      on the project covering the current semantic revision under the same profile digest.
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
        string? operationId = null)
    {
        var diagnostics = new DiagnosticBag();

        var identityMatches =
            string.Equals(project.ProfileId, profile.ProfileId, StringComparison.Ordinal)
            && string.Equals(project.ProfileDigest, profile.Digest, StringComparison.Ordinal);

        if (!identityMatches)
        {
            // A substituted or unrecognized profile is fatal on its own; the synthetic
            // opt-in and approval checks below are meaningless (and would be misleading)
            // until the caller resolves the correct profile, so stop here.
            diagnostics.Error(
                DiagnosticCodes.XbrProfileMismatch,
                DiagnosticSubsystem.XBRL,
                DiagnosticStage.Publication,
                $"Project declares profile '{project.ProfileId}' @ {project.ProfileDigest}, but this "
                + $"run resolved '{profile.ProfileId}' @ {profile.Digest}. Refusing to publish under "
                + "a substituted or unrecognized profile.",
                operationId: operationId);
            return diagnostics;
        }

        var isSynthetic = string.Equals(profile.ProfileId, SyntheticProfile.ProfileId, StringComparison.Ordinal);
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

        var approval = project.Approvals.FirstOrDefault(a =>
            a.SemanticRevision == project.SemanticRevision
            && string.Equals(a.ProfileDigest, profile.Digest, StringComparison.Ordinal));

        if (approval is null)
        {
            diagnostics.Error(
                DiagnosticCodes.XbrApprovalRequired,
                DiagnosticSubsystem.XBRL,
                DiagnosticStage.Publication,
                $"No approval record covers semantic revision {project.SemanticRevision} under "
                + $"profile digest {profile.Digest}. An unattended publication requires a prior, "
                + "explicit approval of the exact revision being run.",
                operationId: operationId);
        }

        return diagnostics;
    }
}
