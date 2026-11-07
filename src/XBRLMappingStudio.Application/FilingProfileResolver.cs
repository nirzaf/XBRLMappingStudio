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
    private readonly IReadOnlyDictionary<string, IReadOnlyList<FilingProfile>> _profiles;
    private readonly IReadOnlyDictionary<(string ProfileId, string Digest), InstalledFilingProfile> _installations;
    private readonly IReadOnlyList<FilingProfileCatalogIssue> _catalogIssues;

    public FilingProfileResolver(IEnumerable<FilingProfile> installedProfiles)
        : this(installedProfiles, Array.Empty<InstalledFilingProfile>(), Array.Empty<FilingProfileCatalogIssue>())
    {
    }

    private FilingProfileResolver(
        IEnumerable<FilingProfile> installedProfiles,
        IEnumerable<InstalledFilingProfile> installations,
        IReadOnlyList<FilingProfileCatalogIssue> catalogIssues)
    {
        ArgumentNullException.ThrowIfNull(installedProfiles);

        var profiles = new Dictionary<string, List<FilingProfile>>(StringComparer.Ordinal);
        var identities = new HashSet<(string ProfileId, string Digest)>();
        foreach (var profile in installedProfiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.ProfileId))
                throw new ArgumentException("An installed profile must have a profile ID.", nameof(installedProfiles));
            if (string.IsNullOrWhiteSpace(profile.Digest))
                throw new ArgumentException($"Installed profile '{profile.ProfileId}' must have a digest.", nameof(installedProfiles));
            if (!identities.Add((profile.ProfileId, profile.Digest)))
                throw new ArgumentException(
                    $"Profile identity '{profile.ProfileId}' with digest '{profile.Digest}' is registered more than once.",
                    nameof(installedProfiles));
            if (!profiles.TryGetValue(profile.ProfileId, out var versions))
                profiles[profile.ProfileId] = versions = new List<FilingProfile>();
            versions.Add(profile);
        }

        _profiles = profiles.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<FilingProfile>)pair.Value.AsReadOnly(),
            StringComparer.Ordinal);
        _installations = installations.ToDictionary(
            installation => (installation.Profile.ProfileId, installation.Profile.Digest));
        _catalogIssues = catalogIssues.ToArray();
    }

    public static FilingProfileResolver DevelopmentOnly()
        => new(new[] { SyntheticProfile.Build() });

    public static FilingProfileResolver FromInstalledDirectory(string directory)
    {
        var catalog = InstalledFilingProfileCatalog.Load(directory);
        return new FilingProfileResolver(
            catalog.Profiles.Select(installation => installation.Profile)
                .Append(SyntheticProfile.Build()),
            catalog.Profiles,
            catalog.Issues);
    }

    public static FilingProfileResolver FromDefaultCatalog()
        => FromInstalledDirectory(InstalledFilingProfileCatalog.DefaultDirectory);

    public InstalledFilingProfile? FindInstallation(string profileId, string digest)
        => _installations.TryGetValue((profileId, digest), out var installation) ? installation : null;

    /// <summary>Return the installed versions for one exact filing profile ID in stable order.</summary>
    public IReadOnlyList<FilingProfile> GetVersions(string profileId)
        => _profiles.TryGetValue(profileId, out var versions)
            ? versions.OrderBy(profile => profile.TaxonomyVersion, StringComparer.Ordinal)
                .ThenBy(profile => profile.Digest, StringComparer.Ordinal).ToArray()
            : Array.Empty<FilingProfile>();

    /// <summary>Return all registered profile versions in stable identity order.</summary>
    public IReadOnlyList<FilingProfile> GetAllProfiles()
        => _profiles.Values.SelectMany(versions => versions)
            .OrderBy(profile => profile.ProfileId, StringComparer.Ordinal)
            .ThenBy(profile => profile.TaxonomyVersion, StringComparer.Ordinal)
            .ThenBy(profile => profile.Digest, StringComparer.Ordinal)
            .ToArray();

    public FilingProfileResolution Resolve(
        MappingProject project,
        bool allowSyntheticProfile,
        string stage = DiagnosticStage.Validation,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var diagnostics = new DiagnosticBag();
        if (string.IsNullOrWhiteSpace(project.ProfileId)
            || !_profiles.TryGetValue(project.ProfileId, out var versions))
        {
            diagnostics.Error(
                DiagnosticCodes.XbrProfileMismatch,
                DiagnosticSubsystem.XBRL,
                stage,
                $"No installed filing profile is registered for '{project.ProfileId}'. "
                + "Install the exact profile declared by the project; no source data was read."
                + CatalogIssueContext(project.ProfileId),
                operationId: operationId);
            return new FilingProfileResolution(null, diagnostics);
        }

        var profile = versions.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Digest, project.ProfileDigest));
        if (profile is null)
        {
            diagnostics.Error(
                DiagnosticCodes.XbrProfileMismatch,
                DiagnosticSubsystem.XBRL,
                stage,
                $"No installed version of profile '{project.ProfileId}' matches digest '{project.ProfileDigest}'. "
                + "Install the exact version declared by the project; no source data was read."
                + CatalogIssueContext(project.ProfileId),
                operationId: operationId);
            return new FilingProfileResolution(null, diagnostics);
        }

        foreach (var diagnostic in PublicationGate.EvaluateProfileIdentity(
            project, profile, stage, operationId).Items)
        {
            diagnostics.Add(diagnostic);
        }

        if (_installations.TryGetValue((profile.ProfileId, profile.Digest), out var installation))
        {
            if (!installation.IsEnabled)
            {
                diagnostics.Error(
                    DiagnosticCodes.XbrProfileNotAuthorized,
                    DiagnosticSubsystem.XBRL,
                    stage,
                    $"Profile '{profile.ProfileId}' version '{installation.Version}' is disabled in the installed catalog; no source data was read.",
                    operationId: operationId);
            }
            else
            {
                diagnostics.Error(
                    DiagnosticCodes.XbrProfileNotAuthorized,
                    DiagnosticSubsystem.XBRL,
                    stage,
                    $"Profile '{profile.ProfileId}' is installed and hash-verified, but has not passed the owner, filing-authority, and redistribution-rights gates. "
                    + "Close GAT-01 and GAT-02 before using it; no source data was read.",
                    operationId: operationId);
            }
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

    private string CatalogIssueContext(string? profileId)
    {
        var issues = _catalogIssues.Where(issue => issue.ProfileId is null
                || StringComparer.Ordinal.Equals(issue.ProfileId, profileId))
            .Take(2)
            .Select(issue => issue.Message)
            .ToArray();
        return issues.Length == 0 ? string.Empty : " Catalog issue: " + string.Join(" ", issues);
    }
}

public sealed record FilingProfileResolution(FilingProfile? Profile, DiagnosticBag Diagnostics)
{
    public bool Success => Profile is not null && !Diagnostics.HasBlockingErrors;
}
