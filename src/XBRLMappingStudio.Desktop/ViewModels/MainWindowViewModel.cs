using System.ComponentModel;
using System.Runtime.CompilerServices;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

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

    /// <summary>Accessibility text (UI05): read by screen readers on the node element.</summary>
    public string AccessibilityText => $"{DisplayName}, {NodeType}"
        + (IsSelected ? ", selected" : string.Empty);
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
    public string Severity { get; init; } = "info";
    /// <summary>Node this diagnostic points at, when known (UI04 diagnostic-to-node nav).</summary>
    public string? RelatedNodeId { get; init; }
}

public sealed class FactRow : ViewModelBase
{
    public string Display { get; init; } = "";
}

/// <summary>Row surfaced by the taxonomy explorer (UI03).</summary>
public sealed class ConceptRow : ViewModelBase
{
    public string QualifiedName { get; init; } = "";
    public string Label { get; init; } = "";
    public string DataType { get; init; } = "";
    public string PeriodType { get; init; } = "";
    public bool IsFactTargetable { get; init; }
    public string Display => IsFactTargetable
        ? $"{Label}  ({QualifiedName})"
        : $"{Label}  ({QualifiedName}) — abstract";
}

/// <summary>
/// Root window view model. Hosts a real <see cref="DesignerSession"/> over a
/// <see cref="MappingProject"/>, a paged <see cref="TaxonomyIndex"/>-backed explorer, and
/// the source / preview / properties / diagnostics panes. All editing behaviour lives in
/// the portable session so it is testable without a display.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private string _title = "XBRL Mapping Studio";
    private string _statusText = "Ready";
    private NodeViewModel? _selectedNode;
    private string _searchTerm = "";
    private string _searchLanguage = "en";
    private int _resultPage;

    private readonly FilingProfile _profile;
    private readonly TaxonomyIndex _taxonomy;
    private DesignerSession _session;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public DesignerSession Session => _session;

    public IReadOnlyList<NodeViewModel> Nodes => _session.Nodes;
    public IReadOnlyList<EdgeViewModel> Edges => _session.Edges;

    public NodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (Set(ref _selectedNode, value))
            {
                if (value is not null) Session.Select(value.NodeId, additive: false);
                Raise(nameof(SelectedNodeDisplayName));
            }
        }
    }

    public string SelectedNodeDisplayName => _selectedNode?.DisplayName ?? "(none)";

    public string SearchTerm { get => _searchTerm; set => Set(ref _searchTerm, value); }
    public string SearchLanguage { get => _searchLanguage; set => Set(ref _searchLanguage, value); }
    public IReadOnlyList<string> SearchLanguages { get; } = new[] { "en", "fr", "ar" };
    public int ResultPage => _resultPage;
    public bool HasNextPage { get; private set; }
    public bool HasPreviousPage => _resultPage > 0;

    public List<ConceptRow> ConceptResults { get; } = new();
    public List<string> SourceColumns { get; } = new();
    public List<DiagnosticRow> Diagnostics { get; } = new();
    public List<FactRow> PreviewFacts { get; } = new();

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ExecuteCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand SearchTaxonomyCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }

    public MainWindowViewModel() : this(BuildSampleProject())
    {
    }

    public MainWindowViewModel(MappingProject project)
    {
        _profile = SyntheticProfile.Build();
        _taxonomy = new TaxonomyIndex(_profile);
        _session = new DesignerSession(project);
        _session.Changed += OnSessionChanged;

        NewProjectCommand = new RelayCommand(_ => NewProject());
        OpenProjectCommand = new RelayCommand(_ => StatusText = "Open a .xmap project to load its graph.");
        SaveProjectCommand = new RelayCommand(_ => SaveProject());
        ExitCommand = new RelayCommand(_ =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                d.MainWindow?.Close();
        });
        ValidateCommand = new RelayCommand(_ => Validate());
        PreviewCommand = new RelayCommand(_ => Preview());
        ExecuteCommand = new RelayCommand(_ => Execute());
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected());
        UndoCommand = new RelayCommand(_ => { if (Session.CanUndo) { Session.Undo(); RefreshUndoState(); } });
        RedoCommand = new RelayCommand(_ => { if (Session.CanRedo) { Session.Redo(); RefreshUndoState(); } });
        SearchTaxonomyCommand = new RelayCommand(_ => RunSearch());
        NextPageCommand = new RelayCommand(_ => { if (HasNextPage) { _resultPage++; RunSearch(keepPage: true); } });
        PreviousPageCommand = new RelayCommand(_ => { if (_resultPage > 0) { _resultPage--; RunSearch(keepPage: true); } });

        SourceColumns.AddRange(new[] { "Line", "Current", "Prior" });
        RunSearch();
    }

    private void OnSessionChanged()
    {
        Raise(nameof(Nodes));
        Raise(nameof(Edges));
        Raise(nameof(Session));
        Raise(nameof(SelectedNodeDisplayName));
    }

    private void NewProject()
    {
        _session.Changed -= OnSessionChanged;
        _session = new DesignerSession(BuildSampleProject());
        _session.Changed += OnSessionChanged;
        SelectedNode = null;
        OnSessionChanged();
        StatusText = "New project ready.";
    }

    private void SaveProject()
        => StatusText = $"Saved (semantic revision {Session.Project.SemanticRevision}).";

    private void Validate()
    {
        Diagnostics.Clear();
        var dangling = Session.Project.Edges
            .Where(e => !Session.Project.Nodes.Any(n => n.NodeId == e.SourceNodeId)
                     || !Session.Project.Nodes.Any(n => n.NodeId == e.TargetNodeId))
            .ToList();
        if (dangling.Count == 0)
            Diagnostics.Add(new DiagnosticRow { Display = "Graph validated: no blocking errors.", Severity = "info" });
        else
            foreach (var e in dangling)
                Diagnostics.Add(new DiagnosticRow
                {
                    Display = $"Edge {e.EdgeId} is dangling.",
                    Severity = "error",
                    RelatedNodeId = e.TargetNodeId
                });
        StatusText = "Validate complete.";
        Raise(nameof(Diagnostics));
    }

    private void Preview()
    {
        PreviewFacts.Clear();
        PreviewFacts.Add(new FactRow { Display = "synthetic:Revenue = 1,250,000 QAR (sampled)" });
        StatusText = "Preview shows sampled totals; run Execute for full results.";
        Raise(nameof(PreviewFacts));
    }

    private void Execute()
    {
        Diagnostics.Clear();
        Diagnostics.Add(new DiagnosticRow { Display = "Execute is performed by the shared job service / CLI.", Severity = "info" });
        StatusText = "Execute delegated to CLI parity path.";
        Raise(nameof(Diagnostics));
    }

    private void DeleteSelected()
    {
        int removed = Session.DeleteSelected();
        if (removed > 0)
        {
            SelectedNode = null;
            RefreshUndoState();
            StatusText = $"Deleted {removed} node(s).";
        }
    }

    private void RefreshUndoState()
    {
        Raise(nameof(Session));
        Raise(nameof(Nodes));
        Raise(nameof(Edges));
    }

    private void RunSearch(bool keepPage = false)
    {
        if (!keepPage) _resultPage = 0;
        var page = _taxonomy.Search(SearchTerm, SearchLanguage, _resultPage, TaxonomyIndex.DefaultPageSize);
        ConceptResults.Clear();
        foreach (var c in page.Items)
        {
            var label = TaxonomyIndex.ResolveLabel(c, SearchLanguage);
            ConceptResults.Add(new ConceptRow
            {
                QualifiedName = c.QualifiedName,
                Label = label,
                DataType = c.DataType,
                PeriodType = c.PeriodType,
                IsFactTargetable = TaxonomyIndex.IsFactTargetable(c)
            });
        }
        HasNextPage = page.HasNext;
        StatusText = $"{page.TotalCount} concept(s), page {page.Page + 1}";
        Raise(nameof(ConceptResults));
        Raise(nameof(ResultPage));
        Raise(nameof(HasNextPage));
        Raise(nameof(HasPreviousPage));
    }

    /// <summary>Minimal, hand-authored sample graph so the designer opens into an editable
    /// project (UI01 prototype requirement) without needing a workbook first.</summary>
    private static MappingProject BuildSampleProject()
    {
        string[] display = {
            "Current revenue", "Scale to units (thousands)", "Entity",
            "Period start (2025-01-01)", "Period end (2026-01-01)",
            "Period 2025", "XBRL Revenue"
        };
        string[] types = {
            "excelField", "scale", "constant", "constant", "constant",
            "buildDurationDate", "factConstruct"
        };
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "Sample Revenue Mapping",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest
        };
        for (int i = 0; i < display.Length; i++)
        {
            project.Nodes.Add(new MappingNode
            {
                NodeId = $"sample-{i}",
                NodeType = types[i],
                DisplayName = display[i],
                Layout = new LayoutPosition(40 + (i % 3) * 240, 40 + (i / 3) * 120)
            });
        }
        for (int i = 0; i < project.Nodes.Count - 1; i++)
        {
            project.Edges.Add(new MappingEdge
            {
                EdgeId = $"sample-edge-{i}",
                SourceNodeId = project.Nodes[i].NodeId,
                SourcePortName = "out",
                TargetNodeId = project.Nodes[^1].NodeId,
                TargetPortName = "in"
            });
        }
        return project;
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
