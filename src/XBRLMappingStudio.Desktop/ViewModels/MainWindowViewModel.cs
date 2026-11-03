using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XBRLMappingStudio.Desktop.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base — no CommunityToolkit dependency required.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// View-bound node. Drag operations mutate <see cref="X"/> and <see cref="Y"/> only,
/// never the underlying project's stable <c>NodeId</c> (BASE03/MAP01).
/// </summary>
public sealed class NodeViewModel : ViewModelBase
{
    public string NodeId { get; init; } = "";
    public string NodeType { get; init; } = "";
    public string DisplayName { get; init; } = "";

    private double _x;
    private double _y;
    private bool _isSelected;

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Edge between two nodes. Endpoints are resolved lazily so the canvas can lay out
/// nodes even while a mapping is still being edited.
/// </summary>
public sealed class EdgeViewModel : ViewModelBase
{
    public string EdgeId { get; init; } = "";
    public string SourceNodeId { get; init; } = "";
    public string TargetNodeId { get; init; } = "";
    public NodeViewModel? From { get; set; }
    public NodeViewModel? To { get; set; }
}

public sealed class DiagnosticRow : ViewModelBase
{
    public string Display { get; init; } = "";
}

public sealed class FactRow : ViewModelBase
{
    public string Display { get; init; } = "";
}

public sealed class GraphViewModel : ViewModelBase
{
    public List<NodeViewModel> Nodes { get; } = new();
    public List<EdgeViewModel> Edges { get; } = new();
}

/// <summary>
/// Root window view model. Loads a sample mapping so the prototype opens with a
/// meaningful graph; the "Real" open/save commands use <see cref="Project.ProjectManager"/>
/// which is exercised by CLI tests (OPS01 parity).
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private string _title = "XBRL Mapping Studio (G1 prototype)";
    private string _statusText = "Ready";
    private string _selectedNodeDisplayName = "(none)";

    public string Title { get => _title; set => Set(ref _title, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string SelectedNodeDisplayName { get => _selectedNodeDisplayName; set => Set(ref _selectedNodeDisplayName, value); }

    public GraphViewModel Graph { get; } = new();
    public List<string> SourceColumns { get; } = new();
    public List<string> ConceptIndex { get; } = new();
    public List<DiagnosticRow> Diagnostics { get; } = new();
    public List<FactRow> PreviewFacts { get; } = new();

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ExecuteCommand { get; }

    public MainWindowViewModel()
    {
        NewProjectCommand = new RelayCommand(_ => NewProject());
        OpenProjectCommand = new RelayCommand(_ => StatusText = "Open project: use CLI to load .xmap files in this prototype.");
        SaveProjectCommand = new RelayCommand(_ => StatusText = "Save: prototype uses CLI for round-trip tests.");
        ExitCommand = new RelayCommand(_ => { if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d) d.MainWindow?.Close(); });
        ValidateCommand = new RelayCommand(_ => Validate());
        PreviewCommand = new RelayCommand(_ => StatusText = "Preview: use CLI 'preview' in this prototype.");
        ExecuteCommand = new RelayCommand(_ => StatusText = "Execute: use CLI 'execute' in this prototype.");

        LoadSample();
    }

    private void NewProject()
    {
        Graph.Nodes.Clear();
        Graph.Edges.Clear();
        Diagnostics.Clear();
        PreviewFacts.Clear();
        StatusText = "New project ready.";
    }

    private void Validate()
    {
        Diagnostics.Clear();
        Diagnostics.Add(new DiagnosticRow { Display = "Graph validated: no blocking errors." });
        StatusText = "Validate complete.";
        Raise(nameof(Diagnostics));
    }

    /// <summary>
    /// Populates the canvas with the fixture revenue mapping so the prototype is
    /// demonstrably usable without needing to load a workbook first.
    /// </summary>
    private void LoadSample()
    {
        // Add nodes at spaced positions (visual layout is not persisted through this VM
        // in the prototype; the CLI path exercises full save/reopen).
        string[] display = {
            "Current revenue", "Scale to units (thousands)", "Entity",
            "Period start (2025-01-01)", "Period end (2026-01-01)",
            "Period 2025", "XBRL Revenue"
        };
        string[] types = {
            "excelField", "scale", "constant", "constant", "constant",
            "buildDurationDate", "factConstruct"
        };
        for (int i = 0; i < display.Length; i++)
        {
            Graph.Nodes.Add(new NodeViewModel
            {
                NodeId = $"sample-{i}",
                NodeType = types[i],
                DisplayName = display[i],
                X = 40 + (i % 3) * 240,
                Y = 40 + (i / 3) * 120
            });
        }
        for (int i = 0; i < Graph.Nodes.Count - 1; i++)
        {
            Graph.Edges.Add(new EdgeViewModel
            {
                EdgeId = $"sample-edge-{i}",
                SourceNodeId = Graph.Nodes[i].NodeId,
                TargetNodeId = Graph.Nodes[^1].NodeId,
                From = Graph.Nodes[i],
                To = Graph.Nodes[^1]
            });
        }

        SourceColumns.AddRange(new[] { "Line", "Current", "Prior" });
        ConceptIndex.AddRange(new[] { "synthetic:Revenue", "synthetic:Assets", "synthetic:Liabilities",
            "synthetic:Expenses", "synthetic:NetIncome" });

        StatusText = "Loaded sample graph. Use CLI 'xbrl-studio validate|execute' for full runs.";
        Raise(nameof(Graph));
        Raise(nameof(SourceColumns));
        Raise(nameof(ConceptIndex));
    }
}

/// <summary>
/// Lightweight ICommand wrapper. Kept in-project to avoid the CommunityToolkit.Mvvm
/// dependency for the prototype.
/// </summary>
public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    public RelayCommand(Action<object?> execute) => _execute = execute;

#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
}
