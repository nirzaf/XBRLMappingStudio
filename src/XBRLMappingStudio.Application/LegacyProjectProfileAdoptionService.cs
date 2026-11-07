using System.Security.Cryptography;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Creates a new independent project copy for a legacy file that lacks complete
/// profile identity. The caller must select the exact profile ID and digest.
/// </summary>
public sealed class LegacyProjectProfileAdoptionService(FilingProfileResolver profileResolver)
{
    private readonly FilingProfileResolver _profileResolver =
        profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));

    public LegacyProjectProfileAdoptionResult Adopt(
        string sourcePath,
        string destinationPath,
        string selectedProfileId,
        string selectedProfileDigest,
        bool allowSyntheticProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(sourceFullPath, destinationFullPath, pathComparison))
            throw new InvalidOperationException("Profile adoption must write to a new project path; the source is never rewritten.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(destinationFullPath), ProjectManager.ProjectExtension))
            throw new ArgumentException($"Destination must use the {ProjectManager.ProjectExtension} extension.", nameof(destinationPath));

        var sourceManager = new ProjectManager(sourceFullPath);
        var originalBytes = sourceManager.ReadSnapshotBytes();
        var sourceDigest = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var draft = LegacyProjectProfileAdoption.CreateDraft(
            originalBytes, selectedProfileId, selectedProfileDigest);

        var profileResolution = _profileResolver.Resolve(
            draft.Project, allowSyntheticProfile, DiagnosticStage.Validation,
            operationId: "legacy-profile-adoption");
        if (!profileResolution.Success)
            throw new LegacyProjectProfileAdoptionBlockedException(profileResolution.Diagnostics.Items);

        // Refuse to produce a stale copy if another conforming editor changed the
        // source between inspection and Save As.
        var latestBytes = sourceManager.ReadSnapshotBytes();
        var latestDigest = SHA256.HashData(latestBytes);
        var expectedDigest = SHA256.HashData(originalBytes);
        if (!CryptographicOperations.FixedTimeEquals(latestDigest, expectedDigest))
            throw new InvalidOperationException("The legacy source project changed during profile adoption; no copy was written.");

        var destinationManager = new ProjectManager(destinationFullPath);
        destinationManager.SaveNew(draft.Project);
        return new LegacyProjectProfileAdoptionResult(
            sourceFullPath,
            destinationManager.FullPath,
            sourceDigest,
            profileResolution.Profile!.ProfileId,
            profileResolution.Profile.Digest,
            draft.AddedIdentityFields,
            draft.Project.ProjectId,
            draft.Project.ComputeSemanticDigest());
    }
}

public sealed record LegacyProjectProfileAdoptionResult(
    string SourcePath,
    string DestinationPath,
    string SourceSha256,
    string ProfileId,
    string ProfileDigest,
    IReadOnlyList<string> AddedIdentityFields,
    string NewProjectId,
    string NewSemanticDigest);

public sealed class LegacyProjectProfileAdoptionBlockedException(
    IReadOnlyList<Diagnostic> diagnostics)
    : Exception("The selected profile is not available for legacy-project adoption.")
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}
