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
        Patterns = new[] { "*.xlsx", "*.csv", "*.json", "*.xml", "*.db", "*.sqlite", "*.sqlite3" }
    };
    private static readonly FilePickerFileType SqliteDatabaseType = new("SQLite databases")
    {
        Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" }
    };
    private static readonly IReadOnlyDictionary<string, FilePickerFileType> DestinationFileTypes =
        new Dictionary<string, FilePickerFileType>(StringComparer.Ordinal)
        {
            ["csv"] = new("CSV records") { Patterns = new[] { "*.csv" } },
            ["excel"] = new("Excel workbooks") { Patterns = new[] { "*.xlsx" } },
            ["json"] = new("JSON records") { Patterns = new[] { "*.json" } },
            ["xml"] = new("XML records") { Patterns = new[] { "*.xml" } }
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

    public async Task<string?> OpenDestinationDatabaseAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an existing SQLite destination",
            AllowMultiple = false,
            FileTypeFilter = new[] { SqliteDatabaseType }
        });
        return LocalPath(files.FirstOrDefault()?.Path);
    }

    public async Task<string?> SaveDestinationFileAsync(string kind)
    {
        if (!DestinationFileTypes.TryGetValue(kind, out var fileType))
            throw new ArgumentException($"Unsupported file destination kind '{kind}'.", nameof(kind));
        var extension = kind == "excel" ? "xlsx" : kind;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Choose {fileType.Name} destination",
            SuggestedFileName = $"facts.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = new[] { fileType }
        });
        return LocalPath(file?.Path);
    }

    public async Task<bool> ConfirmDestinationPublicationAsync(
        string databasePath,
        string table,
        IReadOnlyList<string> businessKey,
        int factCount,
        string semanticDigest)
    {
        var dialog = new Window
        {
            Title = "Confirm SQLite publication",
            Width = 560,
            Height = 340,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var confirm = new Button
        {
            Content = $"Publish {factCount} fact row(s)",
            IsDefault = true,
            MinWidth = 150
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var content = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "This writes fact rows to the selected SQLite table. Existing rows with matching business keys will be updated. Review the details before committing; this action cannot be undone automatically.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Database: {databasePath}\nTable: {table}\nBusiness key: {string.Join(", ", businessKey)}\nFact rows: {factCount}\nApproved project digest: {semanticDigest}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<bool> ConfirmFileDestinationPublicationAsync(
        string destinationPath,
        string format,
        int factCount,
        string semanticDigest)
    {
        var dialog = new Window
        {
            Title = "Confirm file publication",
            Width = 560,
            Height = 300,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var confirm = new Button
        {
            Content = $"Publish {factCount} fact row(s)",
            IsDefault = true,
            MinWidth = 150
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var content = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "This stages and validates a replacement file before publishing it. Review the path and format contract; the action is approval-gated.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Destination: {destinationPath}\nFormat: {format}\nFact rows: {factCount}\nApproved project digest: {semanticDigest}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(owner);
    }

    private static string? LocalPath(Uri? uri) => uri is { IsFile: true } ? uri.LocalPath : null;
}
