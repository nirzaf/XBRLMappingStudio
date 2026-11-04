namespace XBRLMappingStudio.Desktop.ViewModels;

/// <summary>Platform file-picker boundary for project and report destinations.</summary>
public interface IProjectFileDialog
{
    Task<string?> OpenProjectAsync();
    Task<string?> SaveProjectAsync();
    Task<string?> SaveReportAsync();
    Task<string?> OpenSourceAsync();
}
