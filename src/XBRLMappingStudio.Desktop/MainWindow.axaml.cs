using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using XBRLMappingStudio.Desktop.Controls;
using XBRLMappingStudio.Desktop.ViewModels;

namespace XBRLMappingStudio.Desktop;

public partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closePromptRunning;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<GraphCanvas>("Canvas")!.InteractionRejected += message =>
        {
            if (DataContext is MainWindowViewModel viewModel)
                viewModel.StatusText = message;
        };

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

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closePromptRunning) return;

        _closePromptRunning = true;
        try
        {
            if (DataContext is MainWindowViewModel vm && !await vm.ConfirmCloseApplicationAsync()) return;
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closePromptRunning = false;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable) disposable.Dispose();
    }
}

public sealed class AvaloniaProjectFileDialog(Window owner) : IProjectFileDialog
{
    private static readonly FilePickerFileType ProjectType = new("XBRL Mapping")
    {
        Patterns = new[] { "*.xmap" }
    };
    private static readonly FilePickerFileType FunctionPackageType = new("XBRL Mapping function package")
    {
        Patterns = new[] { "*.xfunc.json", "*.json" }
    };
    private static readonly FilePickerFileType JsonType = new("xBRL-JSON report")
    {
        Patterns = new[] { "*.xbrl.json", "*.json" }
    };
    private static readonly FilePickerFileType SourceType = new("Supported source files")
    {
        Patterns = new[] { "*.xlsx", "*.csv", "*.json", "*.xml", "*.pdf", "*.db", "*.sqlite", "*.sqlite3" }
    };
    private static readonly FilePickerFileType SqliteDatabaseType = new("SQLite databases")
    {
        Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" }
    };
    private static readonly FilePickerFileType SqliteSnapshotMetadataType = new("Retained SQLite source snapshot")
    {
        Patterns = new[] { "snapshot.json" }
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

    public async Task<LegacyProjectProfileSelection?> SelectLegacyProjectProfileAsync(
        IReadOnlyList<LegacyProjectProfileOption> candidates,
        string? declaredProfileId,
        string? declaredProfileDigest)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return null;

        var dialog = new Window
        {
            Title = "Choose the legacy project's filing profile",
            Width = 700,
            Height = 440,
            MinWidth = 620,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var profiles = new ComboBox
        {
            ItemsSource = candidates,
            SelectedIndex = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            MinHeight = 36
        };
        AutomationProperties.SetName(profiles, "Installed filing profile version");
        var syntheticOptIn = new CheckBox
        {
            Content = "Allow the synthetic development profile for this adoption",
            IsChecked = false,
            IsEnabled = false
        };
        AutomationProperties.SetName(syntheticOptIn, "Allow synthetic development profile");
        var continueButton = new Button
        {
            Content = "Continue",
            IsDefault = true,
            MinWidth = 110,
            IsEnabled = false
        };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancelButton);
        actions.Children.Add(continueButton);

        void RefreshContinueState()
        {
            var selected = profiles.SelectedItem as LegacyProjectProfileOption;
            syntheticOptIn.IsEnabled = selected?.IsSynthetic == true;
            if (selected?.IsSynthetic != true) syntheticOptIn.IsChecked = false;
            continueButton.IsEnabled = selected is not null
                && (!selected.IsSynthetic || syntheticOptIn.IsChecked == true);
        }

        profiles.SelectionChanged += (_, _) => RefreshContinueState();
        syntheticOptIn.IsCheckedChanged += (_, _) => RefreshContinueState();
        dialog.Opened += (_, _) => profiles.Focus();
        cancelButton.Click += (_, _) => dialog.Close(null);
        continueButton.Click += (_, _) =>
        {
            if (profiles.SelectedItem is not LegacyProjectProfileOption selected) return;
            dialog.Close(new LegacyProjectProfileSelection(
                selected.ProfileId,
                selected.ProfileDigest,
                selected.IsSynthetic && syntheticOptIn.IsChecked == true));
        };
        RefreshContinueState();

        var content = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "Profile identity cannot be inferred from this file. Choose the exact installed version "
                + "that the project was built for. The selected ID and digest will be checked before any copy is saved.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Already declared: profile ID {FormatIdentity(declaredProfileId)}; "
                + $"digest {FormatIdentity(declaredProfileDigest)}.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock { Text = "Installed profile version (ID, version and digest):" });
        content.Children.Add(profiles);
        content.Children.Add(syntheticOptIn);
        content.Children.Add(new TextBlock
        {
            Text = "This creates a new project file, leaves the source untouched and clears all existing approvals. "
                + "Relative source and destination paths will resolve from the new file's folder. Real filing profiles "
                + "remain unavailable until their owner and rights gates are approved.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<LegacyProjectProfileSelection?>(owner);
    }

    public async Task<bool> ConfirmLegacyProjectProfileAdoptionAsync(
        string sourcePath,
        string destinationPath,
        LegacyProjectProfileOption selectedProfile,
        IReadOnlyList<string> addedIdentityFields)
    {
        var dialog = new Window
        {
            Title = "Create adopted project copy",
            Width = 680,
            Height = 420,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var create = new Button { Content = "Create new project", IsDefault = true, MinWidth = 140 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        create.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancel);
        actions.Children.Add(create);
        var content = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = $"Create '{Path.GetFileName(destinationPath)}' from '{Path.GetFileName(sourcePath)}'?",
            FontWeight = Avalonia.Media.FontWeight.Bold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Exact profile: {selectedProfile.ProfileId} · taxonomy {selectedProfile.TaxonomyVersion} · "
                + selectedProfile.ProfileDigest
                + (selectedProfile.IsSynthetic ? " (synthetic development profile)" : string.Empty),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Identity fields to add: {string.Join(", ", addedIdentityFields)}. "
                + "The new file gets an independent project ID and has no inherited approvals. "
                + "The selected profile does not certify that this identity is historically correct; verify it against your filing requirements.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "The source file is never overwritten. Relative source and destination paths are retained and "
                + "will resolve from the new project's folder. Choose a new destination; an existing file cannot be replaced.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(owner);
    }

    private static string FormatIdentity(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(missing)" : value;

    public async Task<string?> OpenFunctionPackageAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import function package",
            AllowMultiple = false,
            FileTypeFilter = new[] { FunctionPackageType }
        });
        return LocalPath(files.FirstOrDefault()?.Path);
    }

    public async Task<string?> SaveFunctionPackageAsync()
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export function package",
            SuggestedFileName = "function-package.xfunc.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { FunctionPackageType }
        });
        return LocalPath(file?.Path);
    }

    public async Task<bool> ConfirmSaveAsIndependentCopyAsync(string projectName, string destinationPath)
    {
        var dialog = new Window
        {
            Title = "Create independent project copy",
            Width = 500,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var create = new Button { Content = "Create copy", IsDefault = true, MinWidth = 110 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        create.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var content = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = $"Saving '{projectName}' to '{Path.GetFileName(destinationPath)}' creates an independent project. "
                + "It gets a new identity and clears existing approvals. Relative paths resolve from the new project "
                + "folder, and files are not copied. The current project file remains unchanged.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancel);
        actions.Children.Add(create);
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<bool> ConfirmOpenProjectBackupAsync(string projectPath, string backupPath)
    {
        var dialog = new Window
        {
            Title = "Open project backup?",
            Width = 520,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var open = new Button { Content = "Open backup", IsDefault = true, MinWidth = 110 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        open.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var content = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = $"'{Path.GetFileName(projectPath)}' could not be opened. Its backup "
                + $"'{Path.GetFileName(backupPath)}' is readable. Opening the backup leaves the original "
                + "untouched. Use Save As to create an independent recovery copy; saving over the original "
                + "or backup is disabled.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancel);
        actions.Children.Add(open);
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<UnsavedProjectChoice> ConfirmUnsavedProjectChangesAsync(string projectName, string action)
    {
        var dialog = new Window
        {
            Title = "Unsaved project changes",
            Width = 480,
            Height = 230,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 100 };
        var discard = new Button { Content = "Discard", MinWidth = 100 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        save.Click += (_, _) => dialog.Close(UnsavedProjectChoice.Save);
        discard.Click += (_, _) => dialog.Close(UnsavedProjectChoice.Discard);
        cancel.Click += (_, _) => dialog.Close(UnsavedProjectChoice.Cancel);

        var content = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = $"'{projectName}' has unsaved changes. Save them before you {action}?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancel);
        actions.Children.Add(discard);
        actions.Children.Add(save);
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<UnsavedProjectChoice>(owner);
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

    public async Task<string?> SelectSqliteSnapshotParentDirectoryAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to store the retained SQLite snapshot",
            AllowMultiple = false
        });
        return LocalPath(folders.FirstOrDefault()?.Path);
    }

    public async Task<string?> OpenSqliteSnapshotMetadataAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select retained SQLite source snapshot metadata",
            AllowMultiple = false,
            FileTypeFilter = new[] { SqliteSnapshotMetadataType }
        });
        return LocalPath(files.FirstOrDefault()?.Path);
    }

    public async Task<bool> ConfirmSqliteSnapshotCaptureAsync(string sourcePath, string bundleDirectory)
    {
        var dialog = new Window
        {
            Title = "Confirm SQLite source snapshot",
            Width = 580,
            Height = 340,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var confirm = new Button { Content = "Capture selected-table snapshot", IsDefault = true, MinWidth = 220 };
        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var content = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "Only the configured SQLite table and its indexes are copied. Other tables, views, and triggers are excluded. "
                + "The snapshot is local and unencrypted; treat it as confidential source data. "
                + "The existing source is read-only and an existing snapshot folder will never be replaced.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Source database: {sourcePath}\nNew snapshot folder: {bundleDirectory}",
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
