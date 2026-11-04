using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using XBRLMappingStudio.Desktop.ViewModels;

namespace XBRLMappingStudio.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        // UI04: double-clicking a diagnostic row focuses and highlights its related node.
        // Wired at the window level so it is present regardless of template trimming.
        AddHandler(InputElement.DoubleTappedEvent, OnDiagnosticDoubleTapped, handledEventsToo: true);
    }

    private void OnDiagnosticDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Source is not Avalonia.Visual source) return;
        var item = source.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is DiagnosticRow { RelatedNodeId: not null } row)
        {
            vm.Session.Select(row.RelatedNodeId!, additive: false);
            vm.SelectedNode = vm.Nodes.FirstOrDefault(n => n.NodeId == row.RelatedNodeId);
        }
    }
}

public sealed class AvaloniaProjectFileDialog(Window owner) : IProjectFileDialog
{
    private static readonly FilePickerFileType ProjectType = new("XBRL Mapping")
    {
        Patterns = new[] { "*.xmap" }
    };
    private static readonly FilePickerFileType JsonType = new("xBRL-JSON report")
    {
        Patterns = new[] { "*.xbrl.json", "*.json" }
    };
    private static readonly FilePickerFileType SourceType = new("Supported source files")
    {
        Patterns = new[] { "*.xlsx", "*.csv", "*.json", "*.xml" }
    };

    public async Task<string?> OpenProjectAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open mapping project",
            AllowMultiple = false,
            FileTypeFilter = new[] { ProjectType }
        });
        return LocalPath(files.FirstOrDefault()?.Path);
    }

    public async Task<string?> SaveProjectAsync()
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save mapping project",
            SuggestedFileName = "mapping.xmap",
            DefaultExtension = "xmap",
            FileTypeChoices = new[] { ProjectType }
        });
        return LocalPath(file?.Path);
    }

    public async Task<string?> SaveReportAsync()
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save validated xBRL-JSON report",
            SuggestedFileName = "report.xbrl.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { JsonType }
        });
        return LocalPath(file?.Path);
    }

    public async Task<string?> OpenSourceAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a source file",
            AllowMultiple = false,
            FileTypeFilter = new[] { SourceType }
        });
        return LocalPath(files.FirstOrDefault()?.Path);
    }

    private static string? LocalPath(Uri? uri) => uri is { IsFile: true } ? uri.LocalPath : null;
}
