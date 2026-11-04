using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Resolves a saved project's profile identity against the profiles installed by the
/// caller. Unknown, substituted, and unacknowledged synthetic profiles fail before a
/// source adapter is opened. The built-in resolver intentionally contains only the
/// synthetic development profile; a real profile must be installed and verified by a
/// profile provider before it can be added to a catalog.
/// </summary>
public sealed class FilingProfileResolver
{
    private readonly IReadOnlyDictionary<string, FilingProfile> _profiles;

    public FilingProfileResolver(IEnumerable<FilingProfile> installedProfiles)
    {
        ArgumentNullException.ThrowIfNull(installedProfiles);

        var profiles = new Dictionary<string, FilingProfile>(StringComparer.Ordinal);
        foreach (var profile in installedProfiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.ProfileId))
                throw new ArgumentException("An installed profile must have a profile ID.", nameof(installedProfiles));
            if (!profiles.TryAdd(profile.ProfileId, profile))
                throw new ArgumentException($"Profile ID '{profile.ProfileId}' is registered more than once.", nameof(installedProfiles));
        }

        _profiles = profiles;
    }

    public static FilingProfileResolver DevelopmentOnly()
        => new(new[] { SyntheticProfile.Build() });

    public FilingProfileResolution Resolve(
        MappingProject project,
        bool allowSyntheticProfile,
        string stage = DiagnosticStage.Validation,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var diagnostics = new DiagnosticBag();
        if (string.IsNullOrWhiteSpace(project.ProfileId)
            || !_profiles.TryGetValue(project.ProfileId, out var profile))
        {
            diagnostics.Error(
                DiagnosticCodes.XbrProfileMismatch,
                DiagnosticSubsystem.XBRL,
                stage,
                $"No installed filing profile is registered for '{project.ProfileId}'. "
                + "Install the exact profile declared by the project; no source data was read.",
                operationId: operationId);
            return new FilingProfileResolution(null, diagnostics);
        }

        foreach (var diagnostic in PublicationGate.EvaluateProfileIdentity(
            project, profile, stage, operationId).Items)
        {
            diagnostics.Add(diagnostic);
        }

        var isSynthetic = string.Equals(profile.ProfileId, SyntheticProfile.ProfileId, StringComparison.Ordinal);
        if (isSynthetic && !allowSyntheticProfile)
        {
            diagnostics.Error(
                DiagnosticCodes.XbrSyntheticProfileNotAuthorized,
                DiagnosticSubsystem.XBRL,
                stage,
                $"The '{SyntheticProfile.ProfileId}' development profile requires explicit opt-in. "
                + "Pass --allow-synthetic-profile for local development use.",
                operationId: operationId);
        }

        return diagnostics.HasBlockingErrors
            ? new FilingProfileResolution(null, diagnostics)
            : new FilingProfileResolution(profile, diagnostics);
    }
}

public sealed record FilingProfileResolution(FilingProfile? Profile, DiagnosticBag Diagnostics)
{
    public bool Success => Profile is not null && !Diagnostics.HasBlockingErrors;
}
