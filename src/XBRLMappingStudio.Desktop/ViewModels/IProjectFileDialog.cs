namespace XBRLMappingStudio.Desktop.ViewModels;

public enum UnsavedProjectChoice
{
    Cancel = 0,
    Save,
    Discard
}

public sealed record LegacyProjectProfileOption(
    string ProfileId,
    string ProfileDigest,
    string TaxonomyVersion,
    bool IsSynthetic)
{
    public override string ToString()
        => $"{ProfileId} · taxonomy {TaxonomyVersion} · {ProfileDigest}"
            + (IsSynthetic ? " · development only" : string.Empty);
}

public sealed record LegacyProjectProfileSelection(
    string ProfileId,
    string ProfileDigest,
    bool AllowSyntheticProfile);

/// <summary>Platform file-picker boundary for project and report destinations.</summary>
public interface IProjectFileDialog
{
    Task<string?> OpenProjectAsync();
    Task<string?> SaveProjectAsync();
    Task<LegacyProjectProfileSelection?> SelectLegacyProjectProfileAsync(
        IReadOnlyList<LegacyProjectProfileOption> candidates,
        string? declaredProfileId,
        string? declaredProfileDigest);
    Task<bool> ConfirmLegacyProjectProfileAdoptionAsync(
        string sourcePath,
        string destinationPath,
        LegacyProjectProfileOption selectedProfile,
        IReadOnlyList<string> addedIdentityFields);
    Task<string?> OpenFunctionPackageAsync();
    Task<string?> SaveFunctionPackageAsync();
    Task<bool> ConfirmSaveAsIndependentCopyAsync(string projectName, string destinationPath);
    Task<bool> ConfirmOpenProjectBackupAsync(string projectPath, string backupPath);
    Task<UnsavedProjectChoice> ConfirmUnsavedProjectChangesAsync(string projectName, string action);
    Task<string?> SaveReportAsync();
    Task<string?> OpenSourceAsync();
    Task<string?> OpenDestinationDatabaseAsync();
    Task<string?> SaveDestinationFileAsync(string kind);
    Task<bool> ConfirmDestinationPublicationAsync(
        string databasePath,
        string table,
        IReadOnlyList<string> businessKey,
        int factCount,
        string semanticDigest);
    Task<bool> ConfirmFileDestinationPublicationAsync(
        string destinationPath,
        string format,
        int factCount,
        string semanticDigest);
}
