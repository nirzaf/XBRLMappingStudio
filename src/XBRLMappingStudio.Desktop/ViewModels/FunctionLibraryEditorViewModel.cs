using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Desktop.ViewModels;

/// <summary>A selectable immutable function revision in the project library.</summary>
public sealed class FunctionRevisionRow
{
    public FunctionDefinition Definition { get; }
    public string FunctionId => Definition.FunctionId;
    public int Revision => Definition.Revision;
    public string Name => Definition.Name;
    public string Display => $"{Definition.Name} · {Definition.FunctionId}@{Definition.Revision}";

    public FunctionRevisionRow(FunctionDefinition definition) => Definition = definition;
}

/// <summary>A bounded synthetic value emitted at one function node during draft review.</summary>
public sealed class FunctionNodePreviewRow
{
    public string NodeId { get; }
    public string NodeDisplay { get; }
    public decimal? Value { get; }
    public string Preview { get; }

    public FunctionNodePreviewRow(string nodeId, string nodeDisplay, decimal? value, string preview)
    {
        NodeId = nodeId;
        NodeDisplay = nodeDisplay;
        Value = value;
        Preview = preview;
    }
}

/// <summary>
/// Headless-testable library for typed, revision-pinned functions, with numeric draft
/// authoring. Draft graph edits stay isolated until saved; a saved revision is added to
/// the mapping project in one undoable project edit.
/// </summary>
public sealed class FunctionLibraryEditorViewModel : ViewModelBase
{
    private const string InputMarkerType = "functionInput";
    private const string OutputMarkerType = "functionOutput";
    private const string PreviewBindingId = "function-preview";
    private const int MaximumIntermediatePreviewRows = 64;
    private const int MaximumIntermediatePreviewGraphNodes = 512;
    private const string InspectionOnlyMessage = "This typed signature is inspection-only. Export or pin the saved revision; numeric authoring requires one Single Decimal input and output.";

    private readonly Func<DesignerSession> _projectSession;
    private readonly IProjectFileDialog? _fileDialog;
    private DesignerSession? _observedProjectSession;
    private DesignerSession _draftSession;
    private FunctionRevisionRow? _selectedFunction;
    private string _draftFunctionId = "";
    private string _draftName = "";
    private string _previewInput = "1";
    private string _previewStatus = "Run a synthetic numeric preview to inspect the draft output.";
    private string _status = "";
    private string _selectedScaleFactor = "1";
    private string _selectedConstantValue = "";
    private string _selectedRoundDigits = "0";
    private string _selectedRoundMode = "HalfEven";
    private string? _selectedDraftNodeId;
    private string? _editingFunctionId;
    private string _draftProjectId = "";
    private DesignerSession? _draftOwnerSession;
    private List<PortDefinition> _draftInputs = new() { DecimalPort("input") };
    private List<PortDefinition> _draftOutputs = new() { DecimalPort("value") };
    private string _draftCatalogVersion = FunctionCatalog.Default.Version;
    private int _expectedLatestRevision;
    private string _importFunctionId = "";
    private string _importFunctionName = "";
    private bool _isPreviewRunning;
    private bool _isSavingFunctionRevision;
    private bool _isImportingFunctionPackage;
    private bool _isExportingFunctionPackage;

    public ObservableCollection<FunctionRevisionRow> Functions { get; } = new();
    public ObservableCollection<FunctionNodePreviewRow> NodePreviews { get; } = new();

    public DesignerSession Session => _draftSession;
    public IReadOnlyList<NodeViewModel> Nodes => _draftSession.Nodes;
    public IReadOnlyList<EdgeViewModel> Edges => _draftSession.Edges;

    public FunctionRevisionRow? SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (!Set(ref _selectedFunction, value)) return;
            if (value is not null) LoadFunction(value);
            Raise(nameof(CanPinSelectedFunction));
            PinSelectedFunctionCommand.NotifyCanExecuteChanged();
            Raise(nameof(CanExportFunctionPackage));
            ExportFunctionPackageCommand.NotifyCanExecuteChanged();
        }
    }

    public string DraftFunctionId
    {
        get => _draftFunctionId;
        private set
        {
            if (!Set(ref _draftFunctionId, value)) return;
            Raise(nameof(DraftRevisionSummary));
        }
    }

    public string DraftName
    {
        get => _draftName;
        set
        {
            if (!Set(ref _draftName, value)) return;
            Raise(nameof(DraftRevisionSummary));
            SaveFunctionRevisionCommand.NotifyCanExecuteChanged();
        }
    }

    public string DraftRevisionSummary => _editingFunctionId is null
        ? $"New function {DraftFunctionId}@1"
        : !CanEditDraft ? $"Inspecting {DraftFunctionId}; the saved typed signature is preserved."
        : $"Saving {DraftFunctionId}@{_expectedLatestRevision + 1}; based on project revision {_expectedLatestRevision}";

    public bool CanEditDraft => IsNumericSignature(_draftInputs, _draftOutputs);

    public string DraftSignatureSummary => $"Inputs: {DescribePorts(_draftInputs)}. Outputs: {DescribePorts(_draftOutputs)}. Catalog: {_draftCatalogVersion}."
        + (CanEditDraft ? " Numeric authoring is available." : " " + InspectionOnlyMessage);

    public string DraftGraphSummary
    {
        get
        {
            var operations = _draftSession.Project.Nodes
                .Where(node => node.NodeType is not (InputMarkerType or OutputMarkerType or NodeTypeConstants.Constant))
                .Select(node => node.DisplayName);
            var steps = operations.ToArray();
            var decimalConstants = _draftSession.Project.Nodes.Count(node =>
                node.NodeType == NodeTypeConstants.Constant
                && StringComparer.OrdinalIgnoreCase.Equals(ReadParameter(node, "shape"), "Decimal"));
            var operationsSummary = steps.Length == 0
                ? CanEditDraft ? "Decimal input passes through to output." : "Typed boundary graph."
                : $"Operations: {string.Join(", ", steps)}.";
            var sideInputsSummary = decimalConstants == 0
                ? ""
                : $" {decimalConstants} explicit Decimal side-input constant(s) require review.";
            return $"Function graph. {operationsSummary}{sideInputsSummary}";
        }
    }

    public string PreviewInput
    {
        get => _previewInput;
        set
        {
            if (!Set(ref _previewInput, value)) return;
            TestFunctionCommand.NotifyCanExecuteChanged();
        }
    }

    public string PreviewStatus
    {
        get => _previewStatus;
        private set => Set(ref _previewStatus, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string ImportFunctionId
    {
        get => _importFunctionId;
        set
        {
            if (!Set(ref _importFunctionId, value)) return;
            Raise(nameof(CanImportFunctionPackage));
            ImportFunctionPackageCommand.NotifyCanExecuteChanged();
        }
    }

    public string ImportFunctionName
    {
        get => _importFunctionName;
        set => Set(ref _importFunctionName, value);
    }

    public string SelectedDraftNodeDisplay
        => SelectedDraftNode is { } node ? $"{node.DisplayName} ({node.NodeType})" : "Select a function node to inspect it.";

    public bool CanEditScaleFactor => CanEditDraft && SelectedDraftNode?.NodeType == NodeTypeConstants.Scale;
    public bool CanEditConstantValue => CanEditDraft && SelectedDraftNode?.NodeType == NodeTypeConstants.Constant
        && StringComparer.OrdinalIgnoreCase.Equals(ReadParameter(SelectedDraftNode, "shape"), "Decimal");
    public bool CanEditRound => CanEditDraft && SelectedDraftNode?.NodeType == NodeTypeConstants.Round;

    public string SelectedScaleFactor
    {
        get => _selectedScaleFactor;
        set => Set(ref _selectedScaleFactor, value);
    }

    public string SelectedConstantValue
    {
        get => _selectedConstantValue;
        set => Set(ref _selectedConstantValue, value);
    }

    public string SelectedRoundDigits
    {
        get => _selectedRoundDigits;
        set => Set(ref _selectedRoundDigits, value);
    }

    public string SelectedRoundMode
    {
        get => _selectedRoundMode;
        set => Set(ref _selectedRoundMode, value);
    }

    public IReadOnlyList<string> RoundModes { get; } = new[] { "HalfEven", "HalfUp", "Truncate" };

    public bool CanAddTransform => CanEditDraft && FindOutputEdge() is not null;
    public bool CanSaveFunctionRevision => CanEditDraft && !_isSavingFunctionRevision
        && !string.IsNullOrWhiteSpace(DraftName)
        && !string.IsNullOrWhiteSpace(DraftFunctionId)
        && _draftSession.Project.Nodes.Count >= 2;
    public bool CanTestFunction => CanEditDraft && !_isPreviewRunning && CanParsePreviewInput();
    public bool CanPinSelectedFunction => SelectedFunction is not null;
    public bool CanExportFunctionPackage => !_isExportingFunctionPackage && SelectedFunction is not null && _fileDialog is not null;
    public bool CanImportFunctionPackage => !_isImportingFunctionPackage && !string.IsNullOrWhiteSpace(ImportFunctionId) && _fileDialog is not null;
    public bool CanUndoDraft => CanEditDraft && _draftSession.CanUndo;
    public bool CanRedoDraft => CanEditDraft && _draftSession.CanRedo;

    private MappingNode? SelectedDraftNode => _selectedDraftNodeId is null
        ? null
        : _draftSession.Project.Nodes.FirstOrDefault(node =>
            StringComparer.Ordinal.Equals(node.NodeId, _selectedDraftNodeId));

    public RelayCommand NewFunctionCommand { get; }
    public RelayCommand AddScaleCommand { get; }
    public RelayCommand AddChangeSignCommand { get; }
    public RelayCommand AddRoundCommand { get; }
    public RelayCommand AddCoalesceCommand { get; }
    public RelayCommand AddRatioCommand { get; }
    public RelayCommand ApplyNodeParametersCommand { get; }
    public RelayCommand TestFunctionCommand { get; }
    public RelayCommand SaveFunctionRevisionCommand { get; }
    public RelayCommand PinSelectedFunctionCommand { get; }
    public RelayCommand ExportFunctionPackageCommand { get; }
    public RelayCommand ImportFunctionPackageCommand { get; }
    public RelayCommand UndoDraftCommand { get; }
    public RelayCommand RedoDraftCommand { get; }

    public FunctionLibraryEditorViewModel(Func<DesignerSession> projectSession, IProjectFileDialog? fileDialog = null)
    {
        _projectSession = projectSession ?? throw new ArgumentNullException(nameof(projectSession));
        _fileDialog = fileDialog;
        _draftSession = CreateDraftSession();
        SubscribeDraftSession();

        NewFunctionCommand = new RelayCommand(_ => BeginNewFunction());
        AddScaleCommand = new RelayCommand(_ => AddTransform(NodeTypeConstants.Scale), _ => CanAddTransform);
        AddChangeSignCommand = new RelayCommand(_ => AddTransform(NodeTypeConstants.ChangeSign), _ => CanAddTransform);
        AddRoundCommand = new RelayCommand(_ => AddTransform(NodeTypeConstants.Round), _ => CanAddTransform);
        AddCoalesceCommand = new RelayCommand(_ => AddBinaryDecimalOperation(NodeTypeConstants.Coalesce), _ => CanAddTransform);
        AddRatioCommand = new RelayCommand(_ => AddBinaryDecimalOperation(NodeTypeConstants.Ratio), _ => CanAddTransform);
        ApplyNodeParametersCommand = new RelayCommand(_ => ApplySelectedNodeParameters(),
            _ => CanEditScaleFactor || CanEditConstantValue || CanEditRound);
        TestFunctionCommand = new RelayCommand(async _ => await TestFunctionAsync(), _ => CanTestFunction);
        SaveFunctionRevisionCommand = new RelayCommand(async _ => await SaveFunctionRevisionAsync(),
            _ => CanSaveFunctionRevision);
        PinSelectedFunctionCommand = new RelayCommand(_ => PinSelectedFunction(), _ => CanPinSelectedFunction);
        ExportFunctionPackageCommand = new RelayCommand(async _ => await ExportFunctionPackageAsync(),
            _ => CanExportFunctionPackage);
        ImportFunctionPackageCommand = new RelayCommand(async _ => await ImportFunctionPackageAsync(),
            _ => CanImportFunctionPackage);
        UndoDraftCommand = new RelayCommand(_ => { if (RequireNumericDraft()) _draftSession.Undo(); }, _ => CanUndoDraft);
        RedoDraftCommand = new RelayCommand(_ => { if (RequireNumericDraft()) _draftSession.Redo(); }, _ => CanRedoDraft);

        RefreshFromProject();
        BeginNewFunction();
    }

    /// <summary>Refreshes revision rows after a project open, undo, or library edit.</summary>
    public void RefreshFromProject()
    {
        var activeSession = _projectSession();
        var projectContextChanged = _observedProjectSession is not null
            && !ReferenceEquals(_observedProjectSession, activeSession);
        _observedProjectSession = activeSession;
        if (projectContextChanged)
            Status = "Project changed. The current function draft remains isolated and cannot be saved to the active project; start a new draft or select a saved revision.";
        (string FunctionId, int Revision)? selectedKey = projectContextChanged || _selectedFunction is null
            ? null
            : (_selectedFunction.FunctionId, _selectedFunction.Revision);
        var definitions = activeSession.Project.Functions
            .OrderBy(function => function.Name, StringComparer.Ordinal)
            .ThenBy(function => function.FunctionId, StringComparer.Ordinal)
            .ThenBy(function => function.Revision)
            .ToArray();

        Functions.Clear();
        foreach (var definition in definitions) Functions.Add(new FunctionRevisionRow(definition));

        var selectedReplacement = selectedKey is null ? null : Functions.FirstOrDefault(row =>
            row.FunctionId == selectedKey.Value.FunctionId && row.Revision == selectedKey.Value.Revision);
        if (!ReferenceEquals(_selectedFunction, selectedReplacement))
        {
            _selectedFunction = selectedReplacement;
            Raise(nameof(SelectedFunction));
        }
        Raise(nameof(CanPinSelectedFunction));
        PinSelectedFunctionCommand?.NotifyCanExecuteChanged();
        Raise(nameof(CanExportFunctionPackage));
        ExportFunctionPackageCommand?.NotifyCanExecuteChanged();
    }

    private void BeginNewFunction()
    {
        _selectedFunction = null;
        Raise(nameof(SelectedFunction));
        _editingFunctionId = null;
        _expectedLatestRevision = 0;
        _draftOwnerSession = _projectSession();
        _draftProjectId = _draftOwnerSession.Project.ProjectId;
        _draftInputs = new List<PortDefinition> { DecimalPort("input") };
        _draftOutputs = new List<PortDefinition> { DecimalPort("value") };
        _draftCatalogVersion = FunctionCatalog.Default.Version;
        DraftFunctionId = "fn" + Guid.NewGuid().ToString("N")[..12];
        DraftName = "New numeric function";
        SetDraftSession(CreateDraftSession());
        NodePreviews.Clear();
        PreviewStatus = "Run a synthetic numeric preview to inspect the draft output.";
        Status = "New draft. Add a numeric operation, test it, and save revision 1.";
        NotifyCommandState();
    }

    private void LoadFunction(FunctionRevisionRow row)
    {
        _draftOwnerSession = _projectSession();
        _draftProjectId = _draftOwnerSession.Project.ProjectId;
        _draftInputs = row.Definition.Inputs.Select(port => port with { }).ToList();
        _draftOutputs = row.Definition.Outputs.Select(port => port with { }).ToList();
        _draftCatalogVersion = row.Definition.CatalogVersion;
        _editingFunctionId = row.FunctionId;
        _expectedLatestRevision = _projectSession().Project.Functions
            .Where(function => function.FunctionId == row.FunctionId)
            .Select(function => function.Revision)
            .DefaultIfEmpty(0)
            .Max();
        DraftFunctionId = row.FunctionId;
        DraftName = row.Name;
        SetDraftSession(CreateDraftSession(row.Definition));
        NodePreviews.Clear();
        PreviewStatus = CanEditDraft ? "Run a synthetic numeric preview to inspect this revision draft." : InspectionOnlyMessage;
        Status = CanEditDraft
            ? $"Loaded {row.FunctionId}@{row.Revision}. Saving will append revision {_expectedLatestRevision + 1}."
            : $"Loaded {row.FunctionId}@{row.Revision}. {InspectionOnlyMessage}";
        NotifyCommandState();
    }

    private void SetDraftSession(DesignerSession session)
    {
        _draftSession.Changed -= OnDraftSessionChanged;
        _draftSession.SelectionChanged -= OnDraftSelectionChanged;
        _draftSession = session;
        SubscribeDraftSession();
        _selectedDraftNodeId = null;
        Raise(nameof(Session));
        Raise(nameof(Nodes));
        Raise(nameof(Edges));
        Raise(nameof(SelectedDraftNodeDisplay));
        Raise(nameof(CanEditScaleFactor));
        Raise(nameof(CanEditConstantValue));
        Raise(nameof(CanEditRound));
        OnDraftSessionChanged();
    }

    private void SubscribeDraftSession()
    {
        _draftSession.Changed += OnDraftSessionChanged;
        _draftSession.SelectionChanged += OnDraftSelectionChanged;
    }

    private void OnDraftSessionChanged()
    {
        Raise(nameof(Nodes));
        Raise(nameof(Edges));
        Raise(nameof(DraftGraphSummary));
        Raise(nameof(CanAddTransform));
        Raise(nameof(CanSaveFunctionRevision));
        Raise(nameof(CanUndoDraft));
        Raise(nameof(CanRedoDraft));
        AddScaleCommand?.NotifyCanExecuteChanged();
        AddChangeSignCommand?.NotifyCanExecuteChanged();
        AddRoundCommand?.NotifyCanExecuteChanged();
        AddCoalesceCommand?.NotifyCanExecuteChanged();
        AddRatioCommand?.NotifyCanExecuteChanged();
        SaveFunctionRevisionCommand?.NotifyCanExecuteChanged();
        UndoDraftCommand?.NotifyCanExecuteChanged();
        RedoDraftCommand?.NotifyCanExecuteChanged();
        OnDraftSelectionChanged();
    }

    private void OnDraftSelectionChanged()
    {
        _selectedDraftNodeId = _draftSession.SelectedNodeIds.Count == 1
            ? _draftSession.SelectedNodeIds.Single()
            : null;
        var node = SelectedDraftNode;
        SelectedScaleFactor = ReadParameter(node, "factor") ?? "1";
        SelectedConstantValue = ReadParameter(node, "literal") ?? "";
        SelectedRoundDigits = ReadParameter(node, "digits") ?? "0";
        SelectedRoundMode = ReadParameter(node, "mode") ?? "HalfEven";
        Raise(nameof(SelectedDraftNodeDisplay));
        Raise(nameof(CanEditScaleFactor));
        Raise(nameof(CanEditConstantValue));
        Raise(nameof(CanEditRound));
        ApplyNodeParametersCommand?.NotifyCanExecuteChanged();
    }

    private void AddTransform(string nodeType)
    {
        if (!RequireNumericDraft()) return;
        var edge = FindOutputEdge();
        if (edge is null)
        {
            Status = "Connect the function graph before adding an operation.";
            return;
        }

        var signature = FunctionCatalog.Default.Find(nodeType);
        if (signature is null || signature.Inputs.Count != 1 || signature.Outputs.Count != 1)
        {
            Status = "This function editor currently supports one-input, one-output operations.";
            return;
        }

        var parameters = signature.Parameters.Values
            .Where(parameter => parameter.DefaultValue is not null)
            .ToDictionary(parameter => parameter.Name, parameter => (object?)parameter.DefaultValue,
                StringComparer.Ordinal);
        if (nodeType == NodeTypeConstants.Round) parameters["mode"] = "HalfEven";

        var transformCount = _draftSession.Project.Nodes.Count(node =>
            node.NodeType is not (InputMarkerType or OutputMarkerType));
        var node = new MappingNode
        {
            NodeId = "n" + Id.New(),
            NodeType = signature.NodeType,
            DisplayName = signature.DisplayName,
            Parameters = parameters,
            InputPorts = signature.Inputs.ToList(),
            OutputPorts = signature.Outputs.ToList(),
            Layout = new LayoutPosition(300 + transformCount * 220, 150)
        };

        if (!_draftSession.TryInsertNodeOnEdge(edge.EdgeId, node,
                signature.Inputs[0].Name, signature.Outputs[0].Name, out var refusal))
        {
            Status = refusal ?? "The operation could not be inserted into the function graph.";
            return;
        }
        Status = $"Added {signature.DisplayName}. Select it to edit its parameters.";
    }

    private void AddBinaryDecimalOperation(string nodeType)
    {
        if (!RequireNumericDraft()) return;
        var edge = FindOutputEdge();
        if (edge is null)
        {
            Status = "Connect the function graph before adding an operation.";
            return;
        }

        var signature = FunctionCatalog.Default.Find(nodeType);
        if (signature is null || signature.Inputs.Count != 2 || signature.Outputs.Count != 1
            || signature.Inputs.Any(port => port.Shape != PortShape.Decimal
                || port.Cardinality != PortCardinality.Single)
            || signature.Outputs[0].Shape != PortShape.Decimal
            || signature.Outputs[0].Cardinality != PortCardinality.Single)
        {
            Status = "This operation does not match the editor's two-input Decimal contract.";
            return;
        }

        var transformCount = _draftSession.Project.Nodes.Count(node =>
            node.NodeType is not (InputMarkerType or OutputMarkerType));
        var operation = new MappingNode
        {
            NodeId = "n" + Id.New(),
            NodeType = signature.NodeType,
            DisplayName = signature.DisplayName,
            Parameters = signature.Parameters.Values
                .Where(parameter => parameter.DefaultValue is not null)
                .ToDictionary(parameter => parameter.Name, parameter => (object?)parameter.DefaultValue,
                    StringComparer.Ordinal),
            InputPorts = signature.Inputs.ToList(),
            OutputPorts = signature.Outputs.ToList(),
            Layout = new LayoutPosition(300 + transformCount * 220, 150)
        };
        var sideInput = signature.Inputs[1];
        var constant = new MappingNode
        {
            NodeId = "n" + Id.New(),
            NodeType = NodeTypeConstants.Constant,
            DisplayName = $"Set {sideInput.Name} constant",
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["literal"] = "",
                ["shape"] = "Decimal"
            },
            OutputPorts = new List<PortDefinition> { DecimalPort("value") },
            Layout = new LayoutPosition(300 + transformCount * 220, 330)
        };
        var sideConnection = new MappingEdge
        {
            EdgeId = "pending",
            SourceNodeId = constant.NodeId,
            SourcePortName = "value",
            TargetNodeId = operation.NodeId,
            TargetPortName = sideInput.Name
        };
        if (!_draftSession.TryInsertNodeOnEdgeWithCompanions(edge.EdgeId, operation,
                signature.Inputs[0].Name, signature.Outputs[0].Name,
                new[] { constant }, new[] { sideConnection }, out var refusal))
        {
            Status = refusal ?? "The operation could not be inserted into the function graph.";
            return;
        }

        _draftSession.Select(constant.NodeId, additive: false);
        Status = $"Added {signature.DisplayName} with an explicit Decimal {sideInput.Name} constant. Set its value before testing or saving.";
    }

    private void ApplySelectedNodeParameters()
    {
        if (!RequireNumericDraft()) return;
        var node = SelectedDraftNode;
        if (node is null) return;
        if (node.NodeType == NodeTypeConstants.Scale)
        {
            if (!decimal.TryParse(SelectedScaleFactor, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out _))
            {
                Status = "Scale factor must be an invariant decimal value.";
                return;
            }
            _draftSession.SetNodeParameter(node.NodeId, "factor", SelectedScaleFactor);
            Status = "Scale factor updated in the draft graph.";
            return;
        }
        if (CanEditConstantValue)
        {
            if (!decimal.TryParse(SelectedConstantValue,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out _))
            {
                Status = "Decimal constant must be an invariant decimal value without grouping separators.";
                return;
            }
            _draftSession.SetNodeParameter(node.NodeId, "literal", SelectedConstantValue);
            Status = "Decimal side-input constant updated in the draft graph.";
            return;
        }
        if (node.NodeType == NodeTypeConstants.Round)
        {
            if (!int.TryParse(SelectedRoundDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var digits)
                || digits is < 0 or > 28
                || !RoundModes.Contains(SelectedRoundMode, StringComparer.Ordinal))
            {
                Status = "Round requires digits from 0 to 28 and a supported rounding mode.";
                return;
            }
            _draftSession.SetNodeParameters(node.NodeId, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["digits"] = SelectedRoundDigits,
                ["mode"] = SelectedRoundMode
            });
            Status = "Round parameters updated in the draft graph.";
        }
    }

    private async Task TestFunctionAsync()
    {
        if (!RequireNumericDraft()) return;
        if (_isPreviewRunning) return;
        if (!TryParsePreviewInput(out var input))
        {
            NodePreviews.Clear();
            PreviewStatus = "Enter a valid invariant decimal input.";
            return;
        }

        _isPreviewRunning = true;
        TestFunctionCommand.NotifyCanExecuteChanged();
        var draftSession = _draftSession;
        var projectSession = _projectSession();
        var previewInput = PreviewInput;
        var previewChanged = false;
        bool IsSamePreviewContext() => ReferenceEquals(_draftSession, draftSession)
            && ReferenceEquals(_projectSession(), projectSession)
            && StringComparer.Ordinal.Equals(PreviewInput, previewInput);
        try
        {
            var draftSnapshot = ProjectSerializer.Serialize(draftSession.Project);
            var projectSnapshot = ProjectSerializer.Serialize(projectSession.Project);
            bool IsCurrentPreview() => IsSamePreviewContext()
                && StringComparer.Ordinal.Equals(ProjectSerializer.Serialize(draftSession.Project), draftSnapshot)
                && StringComparer.Ordinal.Equals(ProjectSerializer.Serialize(projectSession.Project), projectSnapshot);
            var function = BuildDraftDefinition();
            var sourceProject = ProjectSerializer.Deserialize(projectSnapshot);
            NodePreviews.Clear();
            PreviewStatus = "Running synthetic numeric preview…";
            var previews = await BuildIntermediatePreviewsAsync(function, input, sourceProject);
            if (!IsCurrentPreview())
            {
                previewChanged = true;
                return;
            }
            var outcome = await PreviewFunctionAsync(function, input, sourceProject);
            if (!IsCurrentPreview())
            {
                previewChanged = true;
                return;
            }
            foreach (var preview in previews) NodePreviews.Add(preview);
            PreviewStatus = outcome.Success
                ? $"Synthetic preview output: {outcome.Value!.Value.ToString(CultureInfo.InvariantCulture)}. No filing or publication occurred."
                : $"Preview blocked: {outcome.Error}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            if (!IsSamePreviewContext()) return;
            NodePreviews.Clear();
            PreviewStatus = $"Preview blocked: {ex.Message}";
        }
        finally
        {
            _isPreviewRunning = false;
            if (ReferenceEquals(_draftSession, draftSession) && (previewChanged || !IsSamePreviewContext()))
                PreviewStatus = "The project, draft, or preview input changed. Run the preview again to inspect the current draft.";
            TestFunctionCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<IReadOnlyList<FunctionNodePreviewRow>> BuildIntermediatePreviewsAsync(
        FunctionDefinition function, decimal input, MappingProject sourceProject)
    {
        var previews = new List<FunctionNodePreviewRow>();
        var orderedNodes = TopologicallyOrderNodes(function);
        var operations = orderedNodes
            .Where(node => node.NodeType is not (InputMarkerType or OutputMarkerType))
            .ToArray();
        if (operations.Length == 0) return previews;

        if (function.Nodes.Count > MaximumIntermediatePreviewGraphNodes)
        {
            previews.Add(new FunctionNodePreviewRow("", "Intermediate previews",
                null, $"Per-node preview skipped: function has {function.Nodes.Count} graph nodes; the preview limit is {MaximumIntermediatePreviewGraphNodes}."));
            return previews;
        }

        var previewCount = Math.Min(operations.Length, MaximumIntermediatePreviewRows);
        for (var index = 0; index < previewCount; index++)
        {
            var node = operations[index];
            var decimalOutput = node.OutputPorts.FirstOrDefault(port => port.Shape == PortShape.Decimal
                && port.Cardinality == PortCardinality.Single);
            if (decimalOutput is null)
            {
                previews.Add(new FunctionNodePreviewRow(node.NodeId, node.DisplayName, null,
                    "No single Decimal output is available for a numeric preview."));
                continue;
            }

            try
            {
                var prefix = BuildPrefixFunction(function, node, decimalOutput.Name);
                var outcome = await PreviewFunctionAsync(prefix, input, sourceProject);
                previews.Add(outcome.Success
                    ? new FunctionNodePreviewRow(node.NodeId, node.DisplayName, outcome.Value,
                        $"Partial synthetic value: {outcome.Value!.Value.ToString(CultureInfo.InvariantCulture)}. Not a filing result.")
                    : new FunctionNodePreviewRow(node.NodeId, node.DisplayName, null,
                        $"Preview blocked: {outcome.Error}"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or KeyNotFoundException or InvalidDataException)
            {
                previews.Add(new FunctionNodePreviewRow(node.NodeId, node.DisplayName, null,
                    $"Preview blocked: {ex.Message}"));
            }
        }

        if (operations.Length > previewCount)
            previews.Add(new FunctionNodePreviewRow("", "Preview limit", null,
                $"Showing the first {previewCount} of {operations.Length} operation nodes."));
        return previews;
    }

    private static FunctionDefinition BuildPrefixFunction(FunctionDefinition function,
        MappingNode terminalNode, string terminalOutputPort)
    {
        var inputMarker = function.Nodes.SingleOrDefault(node => node.NodeType == InputMarkerType)
            ?? throw new InvalidOperationException("Function input marker is missing.");
        var outputMarker = function.Nodes.SingleOrDefault(node => node.NodeType == OutputMarkerType)
            ?? throw new InvalidOperationException("Function output marker is missing.");
        var nodesById = function.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var incoming = function.Edges.GroupBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(terminalNode.NodeId);
        while (pending.Count > 0)
        {
            var nodeId = pending.Pop();
            if (!included.Add(nodeId)) continue;
            if (!nodesById.ContainsKey(nodeId))
                throw new InvalidOperationException($"Function preview references unknown node '{nodeId}'.");
            if (!incoming.TryGetValue(nodeId, out var edges)) continue;
            foreach (var edge in edges) pending.Push(edge.SourceNodeId);
        }

        if (!included.Contains(inputMarker.NodeId))
            throw new InvalidOperationException($"Node '{terminalNode.DisplayName}' is not connected to the function input.");
        included.Add(outputMarker.NodeId);

        var outputPort = outputMarker.InputPorts.SingleOrDefault()
            ?? throw new InvalidOperationException("Function output marker must have one input port.");
        var prefixEdges = function.Edges
            .Where(edge => included.Contains(edge.SourceNodeId)
                && included.Contains(edge.TargetNodeId)
                && edge.TargetNodeId != outputMarker.NodeId)
            .Select(edge => edge with { })
            .ToList();
        var previewEdgeId = "e-preview-output";
        while (prefixEdges.Any(edge => StringComparer.Ordinal.Equals(edge.EdgeId, previewEdgeId)))
            previewEdgeId += "-";
        prefixEdges.Add(new MappingEdge
        {
            EdgeId = previewEdgeId,
            SourceNodeId = terminalNode.NodeId,
            SourcePortName = terminalOutputPort,
            TargetNodeId = outputMarker.NodeId,
            TargetPortName = outputPort.Name
        });

        return new FunctionDefinition
        {
            FunctionId = "fnpreview" + Guid.NewGuid().ToString("N"),
            Name = $"{function.Name} intermediate preview",
            Revision = 1,
            CatalogVersion = function.CatalogVersion,
            Inputs = function.Inputs.Select(port => port with { }).ToList(),
            Outputs = function.Outputs.Select(port => port with { }).ToList(),
            Nodes = function.Nodes.Where(node => included.Contains(node.NodeId))
                .Select(node => node.ShallowCopy()).ToList(),
            Edges = prefixEdges
        };
    }

    private static IReadOnlyList<MappingNode> TopologicallyOrderNodes(FunctionDefinition function)
    {
        var originalOrder = function.Nodes.Select((node, index) => (node.NodeId, index))
            .ToDictionary(item => item.NodeId, item => item.index, StringComparer.Ordinal);
        var nodesById = function.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var outgoing = function.Nodes.ToDictionary(node => node.NodeId, _ => new List<string>(), StringComparer.Ordinal);
        var indegree = function.Nodes.ToDictionary(node => node.NodeId, _ => 0, StringComparer.Ordinal);
        foreach (var edge in function.Edges)
        {
            if (!outgoing.TryGetValue(edge.SourceNodeId, out var targets)
                || !indegree.ContainsKey(edge.TargetNodeId)) continue;
            targets.Add(edge.TargetNodeId);
            indegree[edge.TargetNodeId]++;
        }

        var ready = new SortedSet<(int Order, string NodeId)>();
        foreach (var node in function.Nodes)
            if (indegree[node.NodeId] == 0) ready.Add((originalOrder[node.NodeId], node.NodeId));
        var ordered = new List<MappingNode>(function.Nodes.Count);
        while (ready.Count > 0)
        {
            var item = ready.Min;
            ready.Remove(item);
            ordered.Add(nodesById[item.NodeId]);
            foreach (var targetId in outgoing[item.NodeId])
            {
                if (--indegree[targetId] == 0)
                    ready.Add((originalOrder[targetId], targetId));
            }
        }
        return ordered.Count == function.Nodes.Count ? ordered : function.Nodes;
    }

    private async Task SaveFunctionRevisionAsync()
    {
        if (!RequireNumericDraft()) return;
        if (_isSavingFunctionRevision) return;
        if (!CanSaveFunctionRevision)
        {
            Status = "A non-empty function name and a valid input-to-output graph are required.";
            return;
        }

        try
        {
            var projectSession = _projectSession();
            if (!ReferenceEquals(_draftOwnerSession, projectSession)
                || !StringComparer.Ordinal.Equals(_draftProjectId, projectSession.Project.ProjectId))
            {
                Status = "This function draft belongs to a different project. Start a new draft or select a function from the active project.";
                return;
            }
            var definition = BuildDraftDefinition();
            var draftSession = _draftSession;
            var draftProjectSnapshot = ProjectSerializer.Serialize(_draftSession.Project);
            var projectSnapshot = ProjectSerializer.Serialize(projectSession.Project);
            var editingFunctionId = _editingFunctionId;
            var expectedLatestRevision = _expectedLatestRevision;
            _isSavingFunctionRevision = true;
            Raise(nameof(CanSaveFunctionRevision));
            SaveFunctionRevisionCommand.NotifyCanExecuteChanged();
            Status = "Validating the function draft with a synthetic preview…";

            var validation = await PreviewFunctionAsync(definition, 1m, ProjectSerializer.Deserialize(projectSnapshot));
            if (!ReferenceEquals(_projectSession(), projectSession)
                || !ReferenceEquals(_draftSession, draftSession)
                || !StringComparer.Ordinal.Equals(ProjectSerializer.Serialize(projectSession.Project), projectSnapshot)
                || !StringComparer.Ordinal.Equals(ProjectSerializer.Serialize(_draftSession.Project), draftProjectSnapshot)
                || !StringComparer.Ordinal.Equals(DraftName.Trim(), definition.Name)
                || !StringComparer.Ordinal.Equals(DraftFunctionId, definition.FunctionId)
                || !StringComparer.Ordinal.Equals(_editingFunctionId, editingFunctionId)
                || _expectedLatestRevision != expectedLatestRevision)
            {
                Status = "The active project or function draft changed during validation. Review it and save again.";
                return;
            }

            if (!validation.Success)
            {
                Status = $"Function revision was not saved because synthetic validation failed: {validation.Error}";
                return;
            }

            var updated = _editingFunctionId is null
                ? FunctionLibraryService.CreateFunction(projectSession.Project, definition)
                : FunctionLibraryService.CreateRevision(projectSession.Project, _editingFunctionId,
                    _expectedLatestRevision, definition.Nodes, definition.Edges, definition.Name,
                    definition.Inputs, definition.Outputs);
            // CreateRevision bases its catalog on the latest revision. The draft may
            // instead have been loaded from an earlier, compatible catalog spelling.
            var savedIndex = updated.Functions.FindIndex(function => function.FunctionId == definition.FunctionId
                && function.Revision == definition.Revision);
            updated.Functions[savedIndex] = updated.Functions[savedIndex] with
            {
                CatalogVersion = definition.CatalogVersion
            };
            projectSession.ReplaceProject(updated);
            RefreshFromProject();
            var saved = projectSession.Project.Functions
                .Where(function => function.FunctionId == definition.FunctionId)
                .OrderBy(function => function.Revision)
                .Last();
            SelectedFunction = Functions.First(row => row.FunctionId == saved.FunctionId
                && row.Revision == saved.Revision);
            Status = $"Saved immutable function revision {saved.FunctionId}@{saved.Revision}. Earlier revisions remain pinned.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Status = $"Function revision was not saved: {ex.Message}";
        }
        finally
        {
            _isSavingFunctionRevision = false;
            Raise(nameof(CanSaveFunctionRevision));
            SaveFunctionRevisionCommand.NotifyCanExecuteChanged();
        }
    }

    private void PinSelectedFunction()
    {
        if (SelectedFunction is not { } selected) return;
        var projectSession = _projectSession();
        if (!projectSession.Project.Functions.Any(function => ReferenceEquals(function, selected.Definition)))
        {
            Status = "The selected revision is no longer in the active project. Select a saved revision again.";
            return;
        }
        var definition = selected.Definition;
        var signature = FunctionCatalog.Default.Find(NodeTypeConstants.FunctionCall)!;
        var node = new MappingNode
        {
            NodeId = "n" + Id.New(),
            NodeType = signature.NodeType,
            DisplayName = $"{definition.Name} @ {definition.Revision}",
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["functionId"] = definition.FunctionId,
                ["revision"] = definition.Revision.ToString(CultureInfo.InvariantCulture)
            },
            InputPorts = definition.Inputs.ToList(),
            OutputPorts = definition.Outputs.ToList(),
            Layout = new LayoutPosition(520, 180)
        };
        projectSession.AddNode(node);
        Status = $"Added a call pinned to {definition.FunctionId}@{definition.Revision}. Connect its typed ports in the Mapping tab.";
    }

    private async Task ExportFunctionPackageAsync()
    {
        if (!CanExportFunctionPackage || _fileDialog is null || SelectedFunction is not { } selected) return;
        _isExportingFunctionPackage = true;
        NotifyPackageCommandState();
        try
        {
            var projectSession = _projectSession();
            var projectSnapshot = ProjectSerializer.Serialize(projectSession.Project);
            // Serialize before the picker so changing selection cannot change the
            // selected revision or its dependency closure during the await.
            var package = BuildFunctionPackage(selected, projectSession.Project);
            var json = FunctionPackageSerializer.Serialize(package);
            var path = await _fileDialog.SaveFunctionPackageAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            EnsureProjectUnchanged(projectSession, projectSnapshot, "export");
            await WritePackageAtomicallyAsync(path, json);
            Status = $"Exported {package.RootFunctionId}@{package.RootRevision} with {package.Functions.Count} pinned revision(s).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException)
        {
            Status = $"Function package export failed: {ex.Message}";
        }
        finally
        {
            _isExportingFunctionPackage = false;
            NotifyPackageCommandState();
        }
    }

    private async Task ImportFunctionPackageAsync()
    {
        if (!CanImportFunctionPackage || _fileDialog is null) return;
        _isImportingFunctionPackage = true;
        NotifyPackageCommandState();
        try
        {
            var projectSession = _projectSession();
            var projectSnapshot = ProjectSerializer.Serialize(projectSession.Project);
            var importFunctionId = ImportFunctionId.Trim();
            var importFunctionName = string.IsNullOrWhiteSpace(ImportFunctionName) ? null : ImportFunctionName.Trim();
            var path = await _fileDialog.OpenFunctionPackageAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            EnsureProjectUnchanged(projectSession, projectSnapshot, "import");
            var json = await ReadPackageTextAsync(path);
            EnsureProjectUnchanged(projectSession, projectSnapshot, "import");
            var package = FunctionPackageSerializer.Deserialize(json);
            var imported = FunctionLibraryService.ImportAs(ProjectSerializer.Deserialize(projectSnapshot), package.Functions,
                package.RootFunctionId, importFunctionId, importFunctionName);
            var importedIds = package.Functions.Select(function => function.FunctionId == package.RootFunctionId
                    ? importFunctionId
                    : importFunctionId + "__" + function.FunctionId)
                .ToHashSet(StringComparer.Ordinal);
            Status = "Validating imported function signatures and pinned dependencies…";
            // The candidate is an isolated snapshot. Compilation of a large typed
            // package must not monopolize the UI thread while the user is navigating.
            await Task.Run(async () =>
            {
                foreach (var function in imported.Functions.Where(function => importedIds.Contains(function.FunctionId))
                             .OrderBy(function => function.FunctionId, StringComparer.Ordinal)
                             .ThenBy(function => function.Revision))
                {
                    var structuralError = ValidateFunctionStructure(function, imported);
                    if (structuralError is not null)
                        throw new InvalidOperationException($"Imported function '{function.FunctionId}@{function.Revision}' "
                            + $"failed structural compilation: {structuralError}");

                    // Preserve runtime refusal for malformed numeric parameters
                    // without pretending to execute other typed contracts.
                    if (!IsNumericSignature(function.Inputs, function.Outputs)) continue;
                    var validation = await PreviewFunctionAsync(function, 1m, imported);
                    if (!validation.Success)
                        throw new InvalidOperationException($"Imported function '{function.FunctionId}@{function.Revision}' "
                            + $"failed synthetic compile and preview: {validation.Error}");
                }
            });

            EnsureProjectUnchanged(projectSession, projectSnapshot, "import");

            projectSession.ReplaceProject(imported);
            RefreshFromProject();
            SelectedFunction = Functions.First(row => row.FunctionId == importFunctionId
                && row.Revision == package.RootRevision);
            Status = $"Imported {package.RootFunctionId}@{package.RootRevision} as {importFunctionId} with namespaced dependencies."
                + (CanEditDraft ? "" : " " + InspectionOnlyMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or KeyNotFoundException)
        {
            Status = $"Function package import failed: {ex.Message}";
        }
        finally
        {
            _isImportingFunctionPackage = false;
            NotifyPackageCommandState();
        }
    }

    private static FunctionPackageDocument BuildFunctionPackage(FunctionRevisionRow selected, MappingProject project)
    {
        var definitions = project.Functions;
        var byKey = definitions.ToDictionary(definition => (definition.FunctionId, definition.Revision));
        if (!byKey.TryGetValue((selected.FunctionId, selected.Revision), out var selectedDefinition)
            || !ReferenceEquals(selectedDefinition, selected.Definition))
            throw new InvalidOperationException("The selected revision is no longer in the active project. Select a saved revision again.");
        var pending = new Queue<(string FunctionId, int Revision)>();
        foreach (var revision in definitions.Where(definition => definition.FunctionId == selected.FunctionId
                     && definition.Revision <= selected.Revision).OrderBy(definition => definition.Revision))
            pending.Enqueue((revision.FunctionId, revision.Revision));

        var included = new Dictionary<(string FunctionId, int Revision), FunctionDefinition>();
        while (pending.Count > 0)
        {
            var key = pending.Dequeue();
            if (included.ContainsKey(key)) continue;
            if (!byKey.TryGetValue(key, out var definition))
                throw new InvalidOperationException($"Function package dependency '{key.FunctionId}@{key.Revision}' is missing.");
            included.Add(key, definition);

            foreach (var node in definition.Nodes.Where(node => node.NodeType == NodeTypeConstants.FunctionCall))
            {
                var functionId = ReadParameter(node, "functionId");
                var revisionText = ReadParameter(node, "revision") ?? "1";
                if (string.IsNullOrWhiteSpace(functionId)
                    || !int.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                    || revision < 1)
                    throw new InvalidOperationException($"Function call node '{node.NodeId}' has an invalid pinned revision.");
                pending.Enqueue((functionId, revision));
            }
        }

        return new FunctionPackageDocument
        {
            RootFunctionId = selected.FunctionId,
            RootRevision = selected.Revision,
            Functions = included.Values.OrderBy(definition => definition.FunctionId, StringComparer.Ordinal)
                .ThenBy(definition => definition.Revision).ToList()
        };
    }

    private static async Task WritePackageAtomicallyAsync(string path, string json)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Function package destination must have a parent directory.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Function package destination directory does not exist.");

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json.AsMemory());
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<string> ReadPackageTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > FunctionPackageSerializer.MaximumPackageBytes)
            throw new InvalidOperationException("Function package exceeds the 16 MiB size limit.");

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes.AsMemory(), CancellationToken.None);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }

    private FunctionDefinition BuildDraftDefinition()
    {
        var definitionRevision = _expectedLatestRevision == 0 ? 1 : _expectedLatestRevision + 1;
        return new FunctionDefinition
        {
            FunctionId = DraftFunctionId,
            Name = DraftName.Trim(),
            Revision = definitionRevision,
            CatalogVersion = _draftCatalogVersion,
            Inputs = _draftInputs.Select(port => port with { }).ToList(),
            Outputs = _draftOutputs.Select(port => port with { }).ToList(),
            Nodes = _draftSession.Project.Nodes.Select(node => node.ShallowCopy()).ToList(),
            Edges = _draftSession.Project.Edges.Select(edge => edge with { }).ToList()
        };
    }

    private static string? ValidateFunctionStructure(FunctionDefinition function, MappingProject library)
    {
        var profile = SyntheticProfile.Build();
        var call = new MappingNode
        {
            NodeId = "validation-call",
            NodeType = NodeTypeConstants.FunctionCall,
            DisplayName = "Imported function validation",
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["functionId"] = function.FunctionId,
                ["revision"] = function.Revision.ToString(CultureInfo.InvariantCulture)
            },
            InputPorts = function.Inputs.Select(port => port with { }).ToList(),
            OutputPorts = function.Outputs.Select(port => port with { }).ToList()
        };
        var nodes = new List<MappingNode> { call };
        var edges = new List<MappingEdge>();
        // These source declarations only supply typed boundary edges for the ordinary
        // compiler/expander. This isolated graph is never executed and has no adapter,
        // destination, or user binding. A source is also needed for zero-input functions.
        var sourcePorts = function.Inputs.Count == 0
            ? new[] { DecimalPort("iteration") }
            : function.Inputs.ToArray();
        for (var index = 0; index < sourcePorts.Length; index++)
        {
            var port = sourcePorts[index];
            var source = new MappingNode
            {
                NodeId = $"validation-source-{index}",
                NodeType = NodeTypeConstants.ExcelField,
                DisplayName = $"Compile-only {port.Shape}/{port.Cardinality} input",
                Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["bindingId"] = PreviewBindingId,
                    ["column"] = port.Name
                },
                OutputPorts = new List<PortDefinition> { port with { Name = "value" } }
            };
            nodes.Add(source);
            if (function.Inputs.Count > 0) edges.Add(Link(source, "value", call, port.Name));
        }
        var validationProject = new MappingProject
        {
            ProjectId = "function-package-validation",
            Name = "Compile-only typed function validation",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest,
            FunctionCatalogVersion = library.FunctionCatalogVersion,
            Nodes = nodes,
            Edges = edges,
            Functions = library.Functions.ToList()
        };
        var compilation = new PlanCompiler(FunctionCatalog.Default)
            .Compile(new MappingGraph(validationProject), profile);
        if (compilation.Success && compilation.Plan is not null) return null;
        var error = string.Join(" | ", compilation.Diagnostics.Items
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical)
            .Select(diagnostic => diagnostic.Message));
        return string.IsNullOrWhiteSpace(error) ? "Function compilation failed." : error;
    }

    private static async Task<FunctionPreviewOutcome> PreviewFunctionAsync(FunctionDefinition function, decimal input,
        MappingProject sourceProject)
    {
        if (!IsNumericSignature(function.Inputs, function.Outputs))
            return FunctionPreviewOutcome.Blocked(InspectionOnlyMessage);
        var profile = SyntheticProfile.Build();
        var functions = sourceProject.Functions
            .Where(existing => existing.FunctionId != function.FunctionId
                || existing.Revision != function.Revision)
            .Append(function)
            .ToList();
        var field = new MappingNode
        {
            NodeId = "preview-field",
            NodeType = NodeTypeConstants.ExcelField,
            DisplayName = "Synthetic decimal input",
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["bindingId"] = PreviewBindingId,
                ["column"] = "amount"
            },
            OutputPorts = new List<PortDefinition> { DecimalPort("value") }
        };
        var call = new MappingNode
        {
            NodeId = "preview-call",
            NodeType = NodeTypeConstants.FunctionCall,
            DisplayName = "Draft function",
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["functionId"] = function.FunctionId,
                ["revision"] = function.Revision.ToString(CultureInfo.InvariantCulture)
            },
            InputPorts = function.Inputs.Select(port => port with { }).ToList(),
            OutputPorts = function.Outputs.Select(port => port with { }).ToList()
        };
        var entity = ConstantNode("preview-entity", "Synthetic entity", "ACME", PortShape.String);
        var date = ConstantNode("preview-date", "Synthetic instant", "2025-12-31", PortShape.String);
        var instant = new MappingNode
        {
            NodeId = "preview-instant",
            NodeType = NodeTypeConstants.BuildInstantDate,
            DisplayName = "Synthetic instant date",
            InputPorts = new List<PortDefinition> { new("dateText", PortShape.String, PortCardinality.Single) },
            OutputPorts = new List<PortDefinition> { new("value", PortShape.Instant, PortCardinality.Single) }
        };
        var fact = new MappingNode
        {
            NodeId = "preview-fact",
            NodeType = NodeTypeConstants.FactConstruct,
            DisplayName = "Synthetic preview fact",
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["concept"] = SyntheticProfile.Qualified("Assets"),
                ["unit"] = "USD",
                ["periodType"] = "instant"
            },
            InputPorts = new List<PortDefinition>
            {
                DecimalPort("value", required: false),
                new("entity", PortShape.String, PortCardinality.Single),
                new("instantDate", PortShape.Instant, PortCardinality.Single, IsRequired: false)
            },
            OutputPorts = new List<PortDefinition> { new("fact", PortShape.Fact, PortCardinality.Single) }
        };

        var projectForPreview = new MappingProject
        {
            ProjectId = "function-preview",
            Name = "Synthetic function preview",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest,
            FunctionCatalogVersion = FunctionCatalog.Default.Version,
            Nodes = new List<MappingNode> { field, call, entity, date, instant, fact },
            Edges = new List<MappingEdge>
            {
                Link(field, "value", call, function.Inputs[0].Name),
                Link(call, function.Outputs[0].Name, fact, "value"),
                Link(entity, "value", fact, "entity"),
                Link(date, "value", instant, "dateText"),
                Link(instant, "value", fact, "instantDate")
            },
            Bindings = new List<SourceBinding>
            {
                new()
                {
                    BindingId = PreviewBindingId,
                    Kind = "excel",
                    DisplayName = "Synthetic function preview input",
                    Configuration = new Dictionary<string, object?>(StringComparer.Ordinal)
                }
            },
            Functions = functions
        };

        var compile = new PlanCompiler(FunctionCatalog.Default)
            .Compile(new MappingGraph(projectForPreview), profile);
        if (!compile.Success || compile.Plan is null)
            return FunctionPreviewOutcome.Blocked(string.Join(" | ", compile.Diagnostics.Items
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical)
                .Select(diagnostic => diagnostic.Message)));

        var context = new XBRLMappingStudio.Engine.ExecutionContext
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Manifest = new RunManifest
            {
                RunId = "function-preview",
                OperationId = "function-preview",
                ProjectId = projectForPreview.ProjectId,
                ProfileDigest = profile.Digest,
                EngineVersion = "function-preview"
            },
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
            CancellationToken = CancellationToken.None
        };
        context.LoadedSources[PreviewBindingId] = new List<DataRecord>
        {
            new()
            {
                RowId = "function-preview-row",
                Fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
                {
                    ["amount"] = DataValue.DecimalValue(input)
                }
            }
        };
        var result = await compile.Plan.ExecuteAsync(context);
        var values = result.Facts.Select(pendingFact => pendingFact.Value).ToArray();
        if (values.Length != 1 || values[0] is null)
            return FunctionPreviewOutcome.Blocked("the synthetic graph did not emit exactly one decimal fact.");
        return FunctionPreviewOutcome.Completed(values[0]!.Value);
    }

    private static MappingNode ConstantNode(string id, string name, string literal, PortShape shape)
        => new()
        {
            NodeId = id,
            NodeType = NodeTypeConstants.Constant,
            DisplayName = name,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["literal"] = literal,
                ["shape"] = shape.ToString()
            },
            OutputPorts = new List<PortDefinition> { new("value", shape, PortCardinality.Single) }
        };

    private static MappingEdge Link(MappingNode source, string sourcePort, MappingNode target, string targetPort)
        => new()
        {
            EdgeId = "e" + Id.New(),
            SourceNodeId = source.NodeId,
            SourcePortName = sourcePort,
            TargetNodeId = target.NodeId,
            TargetPortName = targetPort
        };

    private static PortDefinition DecimalPort(string name, bool required = true)
        => new(name, PortShape.Decimal, PortCardinality.Single, required);

    private static string? ReadParameter(MappingNode? node, string name)
    {
        if (node is null || !node.Parameters.TryGetValue(name, out var value) || value is null) return null;
        return value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText()
            : value.ToString();
    }

    private MappingEdge? FindOutputEdge()
    {
        var output = _draftSession.Project.Nodes.FirstOrDefault(node => node.NodeType == OutputMarkerType);
        return output is null || output.InputPorts.Count != 1
            ? null
            : _draftSession.Project.Edges.SingleOrDefault(edge =>
                edge.TargetNodeId == output.NodeId && edge.TargetPortName == output.InputPorts[0].Name);
    }

    private static bool IsNumericSignature(IReadOnlyList<PortDefinition> inputs, IReadOnlyList<PortDefinition> outputs)
        => inputs.Count == 1 && outputs.Count == 1
            && inputs[0].Shape == PortShape.Decimal && inputs[0].Cardinality == PortCardinality.Single
            && outputs[0].Shape == PortShape.Decimal && outputs[0].Cardinality == PortCardinality.Single;

    private static string DescribePorts(IEnumerable<PortDefinition> ports)
    {
        var descriptions = ports.Select(port => $"{port.Name} ({port.Shape}, {port.Cardinality}, "
            + (port.IsRequired ? "required" : "optional") + ")"
            + (string.IsNullOrWhiteSpace(port.Description) ? "" : $" [{port.Description}]")).ToArray();
        return descriptions.Length == 0 ? "none" : string.Join("; ", descriptions);
    }

    private bool RequireNumericDraft()
    {
        if (CanEditDraft) return true;
        Status = InspectionOnlyMessage;
        PreviewStatus = InspectionOnlyMessage;
        return false;
    }

    private void EnsureProjectUnchanged(DesignerSession session, string snapshot, string operation)
    {
        if (!ReferenceEquals(_projectSession(), session)
            || !StringComparer.Ordinal.Equals(ProjectSerializer.Serialize(session.Project), snapshot))
            throw new InvalidOperationException($"The active project changed during package {operation}; the {operation} was not applied.");
    }

    private void NotifyPackageCommandState()
    {
        Raise(nameof(CanImportFunctionPackage));
        Raise(nameof(CanExportFunctionPackage));
        ImportFunctionPackageCommand.NotifyCanExecuteChanged();
        ExportFunctionPackageCommand.NotifyCanExecuteChanged();
    }

    private bool CanParsePreviewInput()
        => decimal.TryParse(PreviewInput, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private bool TryParsePreviewInput(out decimal input)
        => decimal.TryParse(PreviewInput, NumberStyles.Number, CultureInfo.InvariantCulture, out input);

    private void NotifyCommandState()
    {
        Raise(nameof(CanEditDraft));
        Raise(nameof(DraftSignatureSummary));
        Raise(nameof(DraftRevisionSummary));
        Raise(nameof(CanTestFunction));
        Raise(nameof(CanUndoDraft));
        Raise(nameof(CanRedoDraft));
        Raise(nameof(CanAddTransform));
        Raise(nameof(CanSaveFunctionRevision));
        Raise(nameof(CanPinSelectedFunction));
        AddScaleCommand.NotifyCanExecuteChanged();
        AddChangeSignCommand.NotifyCanExecuteChanged();
        AddRoundCommand.NotifyCanExecuteChanged();
        AddCoalesceCommand.NotifyCanExecuteChanged();
        AddRatioCommand.NotifyCanExecuteChanged();
        TestFunctionCommand.NotifyCanExecuteChanged();
        SaveFunctionRevisionCommand.NotifyCanExecuteChanged();
        PinSelectedFunctionCommand.NotifyCanExecuteChanged();
        ExportFunctionPackageCommand.NotifyCanExecuteChanged();
        ImportFunctionPackageCommand.NotifyCanExecuteChanged();
    }

    private static DesignerSession CreateDraftSession(FunctionDefinition? definition = null)
    {
        var nodes = definition?.Nodes.Select(node => node.ShallowCopy()).ToList()
            ?? new List<MappingNode>
            {
                new()
                {
                    NodeId = "function-input",
                    NodeType = InputMarkerType,
                    DisplayName = "Function input",
                    OutputPorts = new List<PortDefinition> { DecimalPort("value") },
                    Layout = new LayoutPosition(40, 150)
                },
                new()
                {
                    NodeId = "function-output",
                    NodeType = OutputMarkerType,
                    DisplayName = "Function output",
                    InputPorts = new List<PortDefinition> { DecimalPort("value") },
                    Layout = new LayoutPosition(520, 150)
                }
            };
        var edges = definition?.Edges.Select(edge => edge with { }).ToList()
            ?? new List<MappingEdge>
            {
                Link(nodes[0], "value", nodes[1], "value")
            };
        return new DesignerSession(new MappingProject
        {
            ProjectId = "function-editor-draft",
            Name = "Function editor draft",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = SyntheticProfile.Build().Digest,
            FunctionCatalogVersion = definition?.CatalogVersion ?? FunctionCatalog.Default.Version,
            Nodes = nodes,
            Edges = edges
        });
    }
    private sealed record FunctionPreviewOutcome(bool Success, decimal? Value, string? Error)
    {
        public static FunctionPreviewOutcome Completed(decimal value) => new(true, value, null);
        public static FunctionPreviewOutcome Blocked(string? error) => new(false, null,
            string.IsNullOrWhiteSpace(error) ? "function compilation failed." : error);
    }
}
