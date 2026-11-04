using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using XBRLMappingStudio.AI;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
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

public sealed class SuggestionRow : ViewModelBase
{
    public string SuggestionId { get; init; } = "";
    public string SourceFieldId { get; init; } = "";
    public string ProjectDigest { get; init; } = "";
    public string Concept { get; init; } = "";
    public int Score { get; init; }
    public string Basis { get; init; } = "";
    public string Display => $"{Concept}  ·  score {Score}  ·  {Basis}";
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
    private bool _isBusy;
    private bool _allowSyntheticProfile;
    private string? _projectPath;
    private string? _selectedSourceBindingId;
    private int? _expectedLayoutGeneration;
    private string? _selectedSourceColumn;
    private string? _selectedSourceTable;
    private string? _sqliteSourcePath;
    private int _sourceLoadGeneration;
    private bool _isSourceLoading;
    private Task _sqliteTableLoadTask = Task.CompletedTask;
    private ConceptRow? _selectedConcept;
    private SuggestionRow? _selectedSuggestion;
    private string _selectedFactUnit = "USD";
    private string _selectedFactPeriodType = "duration";
    private string _selectedFactDecimals = "0";
    private readonly IProjectFileDialog? _fileDialog;

    private readonly FilingProfile _profile;
    private readonly TaxonomyIndex _taxonomy;
    private DesignerSession _session;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                PreviewCommand?.NotifyCanExecuteChanged();
                ExecuteCommand?.NotifyCanExecuteChanged();
            }
        }
    }
    public bool IsSourceLoading
    {
        get => _isSourceLoading;
        private set
        {
            if (Set(ref _isSourceLoading, value))
            {
                PreviewCommand?.NotifyCanExecuteChanged();
                ExecuteCommand?.NotifyCanExecuteChanged();
            }
        }
    }
    public bool AllowSyntheticProfile { get => _allowSyntheticProfile; set => Set(ref _allowSyntheticProfile, value); }
    public string? ProjectPath { get => _projectPath; private set => Set(ref _projectPath, value); }
    public string? SelectedSourceBindingId { get => _selectedSourceBindingId; private set => Set(ref _selectedSourceBindingId, value); }
    public string? SelectedSourceColumn { get => _selectedSourceColumn; set => Set(ref _selectedSourceColumn, value); }
    public string? SelectedSourceTable
    {
        get => _selectedSourceTable;
        set
        {
            if (Set(ref _selectedSourceTable, value) && value is not null && _sqliteSourcePath is not null)
            {
                var generation = Interlocked.Increment(ref _sourceLoadGeneration);
                IsSourceLoading = true;
                _sqliteTableLoadTask = LoadSqliteTableAsync(value, generation);
            }
        }
    }
    public ConceptRow? SelectedConcept { get => _selectedConcept; set => Set(ref _selectedConcept, value); }
    public SuggestionRow? SelectedSuggestion { get => _selectedSuggestion; set => Set(ref _selectedSuggestion, value); }
    public string SelectedFactUnit { get => _selectedFactUnit; set => Set(ref _selectedFactUnit, value); }
    public string SelectedFactPeriodType { get => _selectedFactPeriodType; set => Set(ref _selectedFactPeriodType, value); }
    public string SelectedFactDecimals { get => _selectedFactDecimals; set => Set(ref _selectedFactDecimals, value); }
    public IReadOnlyList<string> FactPeriodTypes { get; } = new[] { "duration", "instant" };

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
                LoadSelectedFactProperties();
                Raise(nameof(SelectedNodeDisplayName));
                Raise(nameof(CanEditSelectedFact));
            }
        }
    }

    public string SelectedNodeDisplayName => _selectedNode?.DisplayName ?? "(none)";
    public bool CanEditSelectedFact => _selectedNode?.NodeType == NodeTypeConstants.FactConstruct;

    public string SearchTerm { get => _searchTerm; set => Set(ref _searchTerm, value); }
    public string SearchLanguage { get => _searchLanguage; set => Set(ref _searchLanguage, value); }
    public IReadOnlyList<string> SearchLanguages { get; } = new[] { "en", "fr", "ar" };
    public int ResultPage => _resultPage;
    public bool HasNextPage { get; private set; }
    public bool HasPreviousPage => _resultPage > 0;

    public List<ConceptRow> ConceptResults { get; } = new();
    public List<string> SourceColumns { get; } = new();
    public List<string> SourceTables { get; } = new();
    public bool HasSourceTables => SourceTables.Count > 0;
    public List<SuggestionRow> AiSuggestions { get; } = new();
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
    public RelayCommand ChooseSourceCommand { get; }
    public RelayCommand BindSourceColumnCommand { get; }
    public RelayCommand SetConceptCommand { get; }
    public RelayCommand ApplyFactPropertiesCommand { get; }
    public RelayCommand SuggestMappingsCommand { get; }
    public RelayCommand ApplySuggestedMappingCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }

    public MainWindowViewModel() : this(BuildSampleProject())
    {
    }

    public MainWindowViewModel(IProjectFileDialog fileDialog) : this(BuildSampleProject(), fileDialog)
    {
    }

    public MainWindowViewModel(MappingProject project) : this(project, null)
    {
    }

    public MainWindowViewModel(MappingProject project, IProjectFileDialog? fileDialog)
    {
        _fileDialog = fileDialog;
        _profile = SyntheticProfile.Build();
        _taxonomy = new TaxonomyIndex(_profile);
        _session = new DesignerSession(project);
        _session.Changed += OnSessionChanged;

        NewProjectCommand = new RelayCommand(_ => NewProject());
        OpenProjectCommand = new RelayCommand(async _ => await OpenProjectAsync());
        SaveProjectCommand = new RelayCommand(async _ => await SaveProjectAsync());
        ExitCommand = new RelayCommand(_ =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                d.MainWindow?.Close();
        });
        ValidateCommand = new RelayCommand(_ => Validate());
        PreviewCommand = new RelayCommand(async _ => await PreviewAsync(), _ => !IsBusy && !IsSourceLoading);
        ExecuteCommand = new RelayCommand(async _ => await ExecuteAsync(), _ => !IsBusy && !IsSourceLoading);
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected());
        UndoCommand = new RelayCommand(_ => { if (Session.CanUndo) { Session.Undo(); RefreshUndoState(); } });
        RedoCommand = new RelayCommand(_ => { if (Session.CanRedo) { Session.Redo(); RefreshUndoState(); } });
        SearchTaxonomyCommand = new RelayCommand(_ => RunSearch());
        ChooseSourceCommand = new RelayCommand(async _ => await ChooseSourceAsync());
        BindSourceColumnCommand = new RelayCommand(_ => BindSelectedSourceColumn());
        SetConceptCommand = new RelayCommand(_ => SetSelectedConcept());
        ApplyFactPropertiesCommand = new RelayCommand(_ => ApplySelectedFactProperties());
        SuggestMappingsCommand = new RelayCommand(async _ => await SuggestMappingsAsync());
        ApplySuggestedMappingCommand = new RelayCommand(_ => ApplySelectedSuggestion());
        NextPageCommand = new RelayCommand(_ => { if (HasNextPage) { _resultPage++; RunSearch(keepPage: true); } });
        PreviousPageCommand = new RelayCommand(_ => { if (_resultPage > 0) { _resultPage--; RunSearch(keepPage: true); } });

        RunSearch();
    }

    private void OnSessionChanged()
    {
        if (_selectedNode is not null)
        {
            var selectedId = _selectedNode.NodeId;
            _selectedNode = _session.Nodes.FirstOrDefault(n => n.NodeId == selectedId);
            LoadSelectedFactProperties();
            Raise(nameof(SelectedNode));
            Raise(nameof(CanEditSelectedFact));
        }
        Raise(nameof(Nodes));
        Raise(nameof(Edges));
        Raise(nameof(Session));
        Raise(nameof(SelectedNodeDisplayName));
    }

    private void LoadSelectedFactProperties()
    {
        var node = _selectedNode is null ? null : Session.Project.Nodes.FirstOrDefault(n => n.NodeId == _selectedNode.NodeId);
        if (node?.NodeType != NodeTypeConstants.FactConstruct) return;
        SelectedFactUnit = node.Parameters.GetValueOrDefault("unit")?.ToString() ?? "";
        SelectedFactPeriodType = node.Parameters.GetValueOrDefault("periodType")?.ToString() ?? "duration";
        SelectedFactDecimals = node.Parameters.GetValueOrDefault("decimals")?.ToString() ?? "0";
    }

    private void NewProject()
    {
        _session.Changed -= OnSessionChanged;
        _session = new DesignerSession(BuildSampleProject());
        _session.Changed += OnSessionChanged;
        SelectedNode = null;
        ProjectPath = null;
        _expectedLayoutGeneration = null;
        ClearSourceAndRunResults();
        OnSessionChanged();
        StatusText = "New project ready.";
    }

    private async Task OpenProjectAsync()
    {
        if (_fileDialog is null) { StatusText = "Project file picker is unavailable."; return; }
        try
        {
            var path = await _fileDialog.OpenProjectAsync();
            if (path is not null) LoadProject(path);
        }
        catch (Exception ex) { StatusText = $"Open failed: {ex.Message}"; }
    }

    private async Task ChooseSourceAsync()
    {
        if (_fileDialog is null) { StatusText = "Source file picker is unavailable."; return; }
        try
        {
            var path = await _fileDialog.OpenSourceAsync();
            if (path is not null) await LoadSourceAsync(path);
        }
        catch (Exception ex) { StatusText = $"Source load failed: {ex.Message}"; }
    }

    public async Task LoadSourceAsync(string path)
    {
        var generation = Interlocked.Increment(ref _sourceLoadGeneration);
        IsSourceLoading = true;
        var sqliteTableLoadPending = false;
        try
        {
            var root = WorkspaceRoot();
            var safePath = PathGuard.EnsureWithin(root, path);
            var binding = Session.Project.Bindings.FirstOrDefault(b => b.Kind != "param");
            if (binding is null) { StatusText = "Add a source binding to the project before choosing a file."; return; }
            var kind = Path.GetExtension(safePath).ToLowerInvariant() switch
            {
                ".xlsx" => "excel",
                ".csv" => "csv",
                ".json" => safePath.EndsWith(".xbrl.json", StringComparison.OrdinalIgnoreCase) ? "xbrl-report" : "json",
                ".xml" => "xml",
                ".db" or ".sqlite" or ".sqlite3" => "sqlite",
                _ => throw new InvalidOperationException("The selected file type is not supported.")
            };
            binding = binding with { Kind = kind, Locator = safePath, SchemaFingerprint = null };
            _sqliteSourcePath = kind == "sqlite" ? safePath : null;
            SourceTables.Clear();
            SourceColumns.Clear();
            _selectedSourceTable = null;
            Raise(nameof(SelectedSourceTable));
            Raise(nameof(SourceColumns));
            Raise(nameof(SourceTables));
            Raise(nameof(HasSourceTables));
            var config = binding.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal);
            var connection = new SourceConnection(binding.BindingId, kind, safePath,
                config);
            if (kind == "sqlite")
            {
                SelectedSourceBindingId = binding.BindingId;
                var sqlite = new SqliteSourceAdapter();
                var tables = await sqlite.DiscoverTablesAsync(connection, CancellationToken.None);
                if (generation != _sourceLoadGeneration) return;
                SourceTables.AddRange(tables);
                Raise(nameof(SourceTables));
                Raise(nameof(HasSourceTables));
                if (SourceTables.Count == 0) throw new InvalidOperationException("The SQLite database has no user tables.");
                SelectedSourceTable = config.TryGetValue("table", out var configured) && SourceTables.Contains(configured)
                    ? configured : SourceTables.Count == 1 ? SourceTables[0] : null;
                if (SelectedSourceTable is null)
                {
                    Session.SetBinding(binding with { SchemaFingerprint = null });
                    StatusText = "Choose a SQLite table to inspect its columns.";
                    return;
                }
                sqliteTableLoadPending = true;
                await _sqliteTableLoadTask;
                return;
            }
            var adapter = BuildSourceAdapter(connection);
            var schema = await adapter.InspectSchemaAsync(connection, CancellationToken.None);
            if (generation != _sourceLoadGeneration) return;
            SourceColumns.Clear();
            SourceColumns.AddRange(schema.Columns.Select(column => column.Name));
            Session.SetBinding(binding with { SchemaFingerprint = schema.Fingerprint });
            SelectedSourceBindingId = binding.BindingId;
            Raise(nameof(SourceColumns));
            StatusText = $"Loaded {schema.Columns.Count} fields from {Path.GetFileName(safePath)}.";
        }
        catch (Exception ex) { StatusText = $"Source load failed: {ex.Message}"; }
        finally
        {
            if (!sqliteTableLoadPending && generation == _sourceLoadGeneration) IsSourceLoading = false;
        }
    }

    private async Task LoadSqliteTableAsync(string table, int generation)
    {
        try
        {
            if (_sqliteSourcePath is null) return;
            var binding = Session.Project.Bindings.FirstOrDefault(b => b.BindingId == SelectedSourceBindingId)
                ?? Session.Project.Bindings.FirstOrDefault(b => b.Kind != "param");
            if (binding is null) return;
            var config = binding.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal);
            config["table"] = table;
            var connection = new SourceConnection(binding.BindingId, "sqlite", _sqliteSourcePath, config);
            var schema = await new SqliteSourceAdapter().InspectSchemaAsync(connection, CancellationToken.None);
            if (generation != _sourceLoadGeneration || table != SelectedSourceTable || connection.Path != _sqliteSourcePath) return;
            SourceColumns.Clear();
            SourceColumns.AddRange(schema.Columns.Select(column => column.Name));
            binding = binding with { Kind = "sqlite", Locator = _sqliteSourcePath,
                Configuration = config.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal),
                SchemaFingerprint = schema.Fingerprint };
            Session.SetBinding(binding);
            SelectedSourceBindingId = binding.BindingId;
            Raise(nameof(SourceColumns));
            StatusText = $"Loaded {schema.Columns.Count} fields from {table}.";
        }
        catch (Exception ex) { StatusText = $"SQLite table load failed: {ex.Message}"; }
        finally
        {
            if (generation == _sourceLoadGeneration) IsSourceLoading = false;
        }
    }

    private void BindSelectedSourceColumn()
    {
        if (SelectedSourceColumn is null || SelectedNode is null)
        { StatusText = "Select a source field and an Excel Field node first."; return; }
        if (SelectedNode.NodeType != NodeTypeConstants.ExcelField)
        { StatusText = "The selected node is not an Excel Field node."; return; }
        Session.SetNodeParameter(SelectedNode.NodeId, "column", SelectedSourceColumn);
        SelectedNode = Session.Nodes.FirstOrDefault(n => n.NodeId == SelectedNode.NodeId);
        StatusText = $"Bound {SelectedSourceColumn} to {SelectedNodeDisplayName}.";
    }

    private void SetSelectedConcept()
    {
        if (SelectedConcept is null || SelectedNode is null)
        { StatusText = "Select a taxonomy concept and an XBRL Fact node first."; return; }
        if (SelectedNode.NodeType != NodeTypeConstants.FactConstruct)
        { StatusText = "The selected node is not an XBRL Fact node."; return; }
        if (!SelectedConcept.IsFactTargetable)
        { StatusText = "Abstract concepts cannot be used as fact targets."; return; }
        Session.SetNodeParameter(SelectedNode.NodeId, "concept", SelectedConcept.QualifiedName);
        StatusText = $"Proposed target {SelectedConcept.QualifiedName} for {SelectedNodeDisplayName}. Review its unit and period before execution.";
    }

    private void ApplySelectedFactProperties()
    {
        if (SelectedNode is null || !CanEditSelectedFact) return;
        var periodType = SelectedFactPeriodType.Trim().ToLowerInvariant();
        var decimals = SelectedFactDecimals.Trim();
        if (string.IsNullOrWhiteSpace(SelectedFactUnit)
            || periodType is not ("instant" or "duration")
            || (decimals != "INF" && !int.TryParse(decimals, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            StatusText = "Enter a unit, instant or duration period, and integer decimals or INF.";
            return;
        }
        Session.SetNodeParameters(SelectedNode.NodeId, new Dictionary<string, object?>
        {
            ["unit"] = SelectedFactUnit.Trim(),
            ["periodType"] = periodType,
            ["decimals"] = decimals
        });
        StatusText = "Fact properties updated.";
    }

    public async Task SuggestMappingsAsync()
    {
        if (SelectedSourceBindingId is null || string.IsNullOrWhiteSpace(SelectedSourceColumn))
        {
            StatusText = "Choose a source file and select a source field first.";
            return;
        }

        try
        {
            var id = $"{SelectedSourceBindingId}:{SelectedSourceColumn}";
            var service = new AiSuggestionService(AiPolicy.Offline, _taxonomy);
            var batch = await service.SuggestAsync(
                new[] { new SourceFieldDescriptor(id, SelectedSourceColumn) }, CancellationToken.None);
            AiSuggestions.Clear();
            AiSuggestions.AddRange(batch.Suggestions.Select(s => new SuggestionRow
            {
                SuggestionId = s.SuggestionId,
                SourceFieldId = id,
                ProjectDigest = Session.Project.ComputeSemanticDigest(),
                Concept = s.Draft.Concept ?? "",
                Score = s.Score,
                Basis = s.Basis
            }));
            SelectedSuggestion = AiSuggestions.FirstOrDefault();
            Raise(nameof(AiSuggestions));
            StatusText = batch.Suggestions.Count == 0
                ? "No local concept candidates matched this field."
                : $"Found {batch.Suggestions.Count} local candidates. No source data was sent over the network.";
        }
        catch (Exception ex) { StatusText = $"Local suggestions failed: {ex.Message}"; }
    }

    private void ApplySelectedSuggestion()
    {
        if (SelectedSuggestion is null || SelectedSourceBindingId is null || SelectedNode is null
            || SelectedNode.NodeType != NodeTypeConstants.FactConstruct
            || string.IsNullOrWhiteSpace(SelectedSourceColumn))
        {
            StatusText = "Select a local candidate, source field, and XBRL Fact node first.";
            return;
        }
        if (!string.Equals(SelectedSuggestion.SourceFieldId,
                $"{SelectedSourceBindingId}:{SelectedSourceColumn}", StringComparison.Ordinal))
        {
            StatusText = "The source field changed after these suggestions were generated. Request fresh suggestions.";
            return;
        }
        if (!string.Equals(SelectedSuggestion.ProjectDigest, Session.Project.ComputeSemanticDigest(), StringComparison.Ordinal))
        {
            StatusText = "The mapping changed after these suggestions were generated. Request fresh suggestions.";
            return;
        }
        var sourceNodes = Session.Project.Nodes.Where(node => node.NodeType == NodeTypeConstants.ExcelField
            && node.Parameters.TryGetValue("bindingId", out var bindingId)
            && string.Equals(bindingId?.ToString(), SelectedSourceBindingId, StringComparison.Ordinal)).ToList();
        if (sourceNodes.Count != 1)
        {
            StatusText = "A suggested mapping requires exactly one source field node for the selected binding.";
            return;
        }
        if (!IsUpstream(sourceNodes[0].NodeId, SelectedNode.NodeId))
        {
            StatusText = "The selected source field does not feed the selected fact node.";
            return;
        }
        var concept = _taxonomy.Get(SelectedSuggestion.Concept);
        if (concept is null || !TaxonomyIndex.IsFactTargetable(concept))
        {
            StatusText = "The selected candidate is no longer a valid fact concept.";
            return;
        }

        var fact = Session.Project.Nodes.Single(node => node.NodeId == SelectedNode.NodeId);
        var unit = fact.Parameters.GetValueOrDefault("unit")?.ToString();
        var decimalsText = fact.Parameters.GetValueOrDefault("decimals")?.ToString();
        int? decimals = int.TryParse(decimalsText, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedDecimals) ? parsedDecimals : null;
        var draft = new DraftMapping(SelectedSuggestion.SuggestionId, SelectedSourceColumn,
            concept.QualifiedName, unit, concept.PeriodType, decimals);
        var draftValidation = new DraftValidator(_taxonomy, _profile).Validate(new[] { draft });
        if (!draftValidation.Accepted)
        {
            Diagnostics.Clear();
            Diagnostics.AddRange(draftValidation.Diagnostics.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));
            StatusText = "Suggestion failed draft validation; the project was not changed.";
            return;
        }

        var candidate = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(Session.Project));
        candidate.Nodes.Single(node => node.NodeId == sourceNodes[0].NodeId).Parameters["column"] = SelectedSourceColumn;
        var candidateFact = candidate.Nodes.Single(node => node.NodeId == SelectedNode.NodeId);
        candidateFact.Parameters["concept"] = concept.QualifiedName;
        candidateFact.Parameters["periodType"] = concept.PeriodType;
        var compile = new MappingJobService(_profile, new LocalSyntheticWorker(_profile)).Validate(candidate);
        if (!compile.Success)
        {
            Diagnostics.Clear();
            Diagnostics.AddRange(compile.Diagnostics.Items.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));
            StatusText = "Suggestion failed graph validation; the project was not changed.";
            return;
        }

        Session.SetNodeParameterValues(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            [sourceNodes[0].NodeId] = new Dictionary<string, object?> { ["column"] = SelectedSourceColumn },
            [SelectedNode.NodeId] = new Dictionary<string, object?>
            {
                ["concept"] = concept.QualifiedName,
                ["periodType"] = concept.PeriodType
            }
        });
        StatusText = $"Applied {concept.QualifiedName} after draft and graph validation. Review period, entity and dimensions before execution.";
    }

    private bool IsUpstream(string sourceNodeId, string factNodeId)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceNodeId };
        pending.Push(sourceNodeId);
        while (pending.TryPop(out var nodeId))
        {
            if (nodeId == factNodeId) return true;
            foreach (var edge in Session.Project.Edges.Where(edge => edge.SourceNodeId == nodeId))
                if (visited.Add(edge.TargetNodeId)) pending.Push(edge.TargetNodeId);
        }
        return false;
    }

    private static ISourceAdapter BuildSourceAdapter(SourceConnection connection) => connection.Kind switch
    {
        "excel" => new ExcelAdapter(),
        "csv" => new CsvAdapter(CsvAdapter.Options.FromConfiguration(connection.Configuration)),
        "json" => new JsonAdapter(JsonAdapter.Options.FromConfiguration(connection.Configuration)),
        "xml" => new XmlAdapter(),
        "xbrl-report" => new XbrlReportAdapter(),
        _ => throw new NotSupportedException($"Adapter '{connection.Kind}' is not supported for source browsing.")
    };

    public void LoadProject(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var manager = new ProjectManager(fullPath);
        var project = manager.Load();
        _session.Changed -= OnSessionChanged;
        _session = new DesignerSession(project);
        _session.Changed += OnSessionChanged;
        SelectedNode = null;
        ProjectPath = manager.FullPath;
        _expectedLayoutGeneration = project.LayoutGeneration;
        ClearSourceAndRunResults();
        OnSessionChanged();
        StatusText = $"Opened {Path.GetFileName(fullPath)}.";
    }

    private void ClearSourceAndRunResults()
    {
        Interlocked.Increment(ref _sourceLoadGeneration);
        IsSourceLoading = false;
        _sqliteSourcePath = null;
        _selectedSourceTable = null;
        SelectedSourceBindingId = null;
        SelectedSourceColumn = null;
        SelectedSuggestion = null;
        SourceColumns.Clear();
        SourceTables.Clear();
        AiSuggestions.Clear();
        PreviewFacts.Clear();
        Diagnostics.Clear();
        Raise(nameof(SourceColumns));
        Raise(nameof(SourceTables));
        Raise(nameof(HasSourceTables));
        Raise(nameof(SelectedSourceTable));
        Raise(nameof(AiSuggestions));
        Raise(nameof(PreviewFacts));
        Raise(nameof(Diagnostics));
    }

    private async Task SaveProjectAsync()
    {
        try
        {
            var path = ProjectPath;
            if (path is null)
            {
                if (_fileDialog is null) { StatusText = "Project file picker is unavailable."; return; }
                path = await _fileDialog.SaveProjectAsync();
            }
            if (path is not null) SaveProjectTo(path);
        }
        catch (Exception ex) { StatusText = $"Save failed: {ex.Message}"; }
    }

    public void SaveProjectTo(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var manager = new ProjectManager(fullPath);
        var saved = manager.Save(Session.Project,
            string.Equals(ProjectPath, manager.FullPath, StringComparison.Ordinal) ? _expectedLayoutGeneration : null);
        ProjectPath = manager.FullPath;
        _expectedLayoutGeneration = saved.NewLayoutGeneration;
        StatusText = $"Saved {Path.GetFileName(fullPath)} (semantic revision {saved.NewSemanticRevision}).";
    }

    private void Validate()
    {
        Diagnostics.Clear();
        var compiled = new MappingJobService(_profile, new LocalSyntheticWorker(_profile)).Validate(Session.Project);
        if (compiled.Diagnostics.Items.Count == 0)
            Diagnostics.Add(new DiagnosticRow { Display = "Mapping graph and filing profile validated.", Severity = "info" });
        else
            Diagnostics.AddRange(compiled.Diagnostics.Items.Select(ToDiagnosticRow));
        StatusText = compiled.Success ? "Validation complete." : "Validation found blocking issues.";
        Raise(nameof(Diagnostics));
    }

    private async Task PreviewAsync()
    {
        if (IsSourceLoading) { StatusText = "Wait for source loading to finish before previewing."; return; }
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Running bounded preview…";
        try
        {
            var root = WorkspaceRoot();
            var connections = BuildConnections(root);
            await using var worker = new LocalSyntheticWorker(_profile);
            var result = await new MappingJobService(_profile, worker).RunAsync(
                Session.Project, connections, ReadParameters(), preview: true, CancellationToken.None);
            ShowRunResult(result);
            StatusText = $"Preview finished: {result.Manifest.Outcome}. Sampled output is partial.";
        }
        catch (Exception ex)
        {
            Diagnostics.Clear();
            Diagnostics.Add(new DiagnosticRow { Display = ex.Message, Severity = "error" });
            Raise(nameof(Diagnostics));
            StatusText = "Preview failed.";
        }
        finally { IsBusy = false; }
    }

    private async Task ExecuteAsync()
    {
        if (IsSourceLoading) { StatusText = "Wait for source loading to finish before executing."; return; }
        if (IsBusy) return;
        var gate = PublicationGate.Evaluate(Session.Project, _profile, AllowSyntheticProfile);
        if (gate.HasBlockingErrors)
        {
            Diagnostics.Clear();
            Diagnostics.AddRange(gate.Items.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));
            StatusText = "Execution blocked by the publication authorization gate.";
            return;
        }
        if (_fileDialog is null) { StatusText = "Report file picker is unavailable."; return; }

        string? outputPath;
        try { outputPath = await _fileDialog.SaveReportAsync(); }
        catch (Exception ex) { StatusText = $"Output selection failed: {ex.Message}"; return; }
        if (outputPath is null) return;

        IsBusy = true;
        StatusText = "Executing and validating report…";
        try
        {
            var root = WorkspaceRoot();
            var connections = BuildConnections(root);
            await using var worker = new LocalSyntheticWorker(_profile);
            var result = await new MappingJobService(_profile, worker).RunAsync(
                Session.Project, connections, ReadParameters(), preview: false, CancellationToken.None);
            ShowRunResult(result);
            if (!result.IsPublishable)
            {
                StatusText = $"Execution finished: {result.Manifest.Outcome}. No artifact was published.";
                return;
            }
            var receipt = new PublicationService(new RunJournal()).PublishAuthorized(
                outputPath, System.Text.Encoding.UTF8.GetBytes(result.XbrlJson), root,
                Session.Project, _profile, AllowSyntheticProfile, result);
            StatusText = $"Published {Path.GetFileName(receipt.DestinationPath)}; digest {receipt.FinalDigest}.";
        }
        catch (Exception ex)
        {
            Diagnostics.Clear();
            Diagnostics.Add(new DiagnosticRow { Display = ex.Message, Severity = "error" });
            Raise(nameof(Diagnostics));
            StatusText = "Execution failed; no unvalidated output was published.";
        }
        finally { IsBusy = false; }
    }

    private string WorkspaceRoot() => ProjectPath is null
        ? Environment.CurrentDirectory
        : Path.GetDirectoryName(ProjectPath)!;

    private Dictionary<string, SourceConnection> BuildConnections(string root)
    {
        var result = new Dictionary<string, SourceConnection>(StringComparer.Ordinal);
        foreach (var binding in Session.Project.Bindings)
        {
            var path = binding.Locator;
            if (path is not null)
                path = PathGuard.EnsureWithin(root, Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            var config = binding.Configuration.ToDictionary(kv => kv.Key,
                kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal);
            result[binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, path, config);
        }
        return result;
    }

    private Dictionary<string, string> ReadParameters() => Session.Project.Nodes
        .Where(node => node.NodeType == NodeTypeConstants.RunParameter
                       && node.Parameters.TryGetValue("name", out var name) && name is not null
                       && node.Parameters.TryGetValue("value", out var value) && value is not null)
        .ToDictionary(node => node.Parameters["name"]!.ToString()!,
            node => node.Parameters["value"]!.ToString()!, StringComparer.Ordinal);

    private void ShowRunResult(JobResult result)
    {
        Diagnostics.Clear();
        Diagnostics.AddRange(result.Diagnostics.Items.Select(ToDiagnosticRow));
        PreviewFacts.Clear();
        foreach (var fact in result.Facts)
        {
            var value = fact.IsNil ? "nil" : fact.Value?.ToString() ?? fact.TextValue ?? "(empty)";
            var unit = fact.Unit?.ToString() ?? fact.CompoundUnit?.CanonicalKey;
            var sources = string.Join(", ", fact.ContributingSources.Select(source => source.Locator));
            PreviewFacts.Add(new FactRow
            {
                Display = $"{fact.ConceptName} = {value}{(unit is null ? "" : " " + unit)}; {fact.Period}"
                    + (sources.Length == 0 ? "" : $"; source: {sources}")
            });
        }
        Raise(nameof(Diagnostics));
        Raise(nameof(PreviewFacts));
    }

    private static DiagnosticRow ToDiagnosticRow(Diagnostic diagnostic) => new()
    {
        Display = $"{diagnostic.Code}: {diagnostic.Message}",
        Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
        RelatedNodeId = diagnostic.NodeId
    };

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

    /// <summary>Runnable sample graph; source file and column are selected in the source pane.</summary>
    private static MappingProject BuildSampleProject()
    {
        const string source = "sample-source";
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "Sample Revenue Mapping",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest,
            Bindings = new List<SourceBinding>
            {
                new()
                {
                    BindingId = source,
                    Kind = "excel",
                    DisplayName = "Revenue workbook",
                    Configuration = new Dictionary<string, object?> { ["headerRow"] = "1" }
                }
            },
            Nodes = new List<MappingNode>
            {
                new()
                {
                    NodeId = "sample-value", NodeType = NodeTypeConstants.ExcelField,
                    DisplayName = "Revenue source field",
                    Parameters = new() { ["bindingId"] = source, ["column"] = "Revenue" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) },
                    Layout = new LayoutPosition(40, 80)
                },
                new()
                {
                    NodeId = "sample-scale", NodeType = NodeTypeConstants.Scale,
                    DisplayName = "Scale to thousands",
                    Parameters = new() { ["factor"] = "1000" },
                    InputPorts = new() { new PortDefinition("input", PortShape.Decimal, PortCardinality.Single) },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) },
                    Layout = new LayoutPosition(280, 80)
                },
                new()
                {
                    NodeId = "sample-entity", NodeType = NodeTypeConstants.Constant,
                    DisplayName = "Entity", Parameters = new() { ["literal"] = "ABC-Corp", ["shape"] = "String" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) },
                    Layout = new LayoutPosition(40, 200)
                },
                new()
                {
                    NodeId = "sample-start", NodeType = NodeTypeConstants.Constant,
                    DisplayName = "Period start", Parameters = new() { ["literal"] = "2025-01-01", ["shape"] = "String" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) },
                    Layout = new LayoutPosition(40, 320)
                },
                new()
                {
                    NodeId = "sample-end", NodeType = NodeTypeConstants.Constant,
                    DisplayName = "Period end", Parameters = new() { ["literal"] = "2026-01-01", ["shape"] = "String" },
                    OutputPorts = new() { new PortDefinition("value", PortShape.String, PortCardinality.Single) },
                    Layout = new LayoutPosition(280, 320)
                },
                new()
                {
                    NodeId = "sample-period", NodeType = NodeTypeConstants.BuildDurationDate,
                    DisplayName = "Duration period",
                    InputPorts = new()
                    {
                        new PortDefinition("startText", PortShape.String, PortCardinality.Single),
                        new PortDefinition("endText", PortShape.String, PortCardinality.Single)
                    },
                    OutputPorts = new() { new PortDefinition("value", PortShape.Instant, PortCardinality.Single) },
                    Layout = new LayoutPosition(520, 280)
                },
                new()
                {
                    NodeId = "sample-fact", NodeType = NodeTypeConstants.FactConstruct,
                    DisplayName = "XBRL Revenue",
                    Parameters = new()
                    {
                        ["concept"] = SyntheticProfile.Qualified("Revenue"), ["unit"] = "USD",
                        ["periodType"] = "duration", ["decimals"] = "0"
                    },
                    InputPorts = new()
                    {
                        new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, IsRequired: false),
                        new PortDefinition("entity", PortShape.String, PortCardinality.Single),
                        new PortDefinition("period", PortShape.Instant, PortCardinality.Single, IsRequired: false)
                    },
                    OutputPorts = new() { new PortDefinition("fact", PortShape.Fact, PortCardinality.Single) },
                    Layout = new LayoutPosition(760, 120)
                }
            },
            Edges = new List<MappingEdge>
            {
                new() { EdgeId = "sample-e1", SourceNodeId = "sample-value", SourcePortName = "value", TargetNodeId = "sample-scale", TargetPortName = "input" },
                new() { EdgeId = "sample-e2", SourceNodeId = "sample-scale", SourcePortName = "value", TargetNodeId = "sample-fact", TargetPortName = "value" },
                new() { EdgeId = "sample-e3", SourceNodeId = "sample-entity", SourcePortName = "value", TargetNodeId = "sample-fact", TargetPortName = "entity" },
                new() { EdgeId = "sample-e4", SourceNodeId = "sample-start", SourcePortName = "value", TargetNodeId = "sample-period", TargetPortName = "startText" },
                new() { EdgeId = "sample-e5", SourceNodeId = "sample-end", SourcePortName = "value", TargetNodeId = "sample-period", TargetPortName = "endText" },
                new() { EdgeId = "sample-e6", SourceNodeId = "sample-period", SourcePortName = "value", TargetNodeId = "sample-fact", TargetPortName = "period" }
            }
        };
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
    private readonly Predicate<object?>? _canExecute;
    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        => (_execute, _canExecute) = (execute, canExecute);

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
