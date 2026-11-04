namespace XBRLMappingStudio.Desktop.ViewModels;

/// <summary>Platform file-picker boundary for project and report destinations.</summary>
public interface IProjectFileDialog
{
    Task<string?> OpenProjectAsync();
    Task<string?> SaveProjectAsync();
    Task<string?> SaveReportAsync();
    Task<string?> OpenSourceAsync();
    Task<string?> OpenDestinationDatabaseAsync();
    Task<bool> ConfirmDestinationPublicationAsync(
        string databasePath,
        string table,
        IReadOnlyList<string> businessKey,
        int factCount,
        string semanticDigest);
}
