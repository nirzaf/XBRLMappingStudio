using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.AI;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Adapters.Pdf;
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
    public IReadOnlyList<PortDefinition> InputPorts { get; init; } = Array.Empty<PortDefinition>();
    public IReadOnlyList<PortDefinition> OutputPorts { get; init; } = Array.Empty<PortDefinition>();
    public double Height => Math.Max(DesignerSession.NodeHeight,
        48 + Math.Max(InputPorts.Count, OutputPorts.Count) * 18);

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
    public string SourcePortName { get; init; } = "";
    public string TargetNodeId { get; init; } = "";
    public string TargetPortName { get; init; } = "";
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
    /// <summary>The semantic fact represented by this preview row, retained for full-aspect review.</summary>
    public XbrlFact? SemanticFact { get; init; }
    public string Display { get; init; } = "";
    public string StageSummary { get; init; } = "Mapping stage is unavailable.";
    public IReadOnlyList<PreviewEvidenceRow> Evidence { get; init; } = Array.Empty<PreviewEvidenceRow>();
}

/// <summary>A source location contributing to a generated fact.</summary>
public sealed class PreviewEvidenceRow
{
    public SourceReference Source { get; }
    public string Display => Source.DisplayName is { Length: > 0 } displayName
        ? $"{displayName} · {Source.SourceId}: {Source.Locator}"
        : $"{Source.SourceId}: {Source.Locator}";
    public PdfEvidenceRegion? PdfEvidence { get; }

    public PreviewEvidenceRow(SourceReference source)
    {
        Source = source;
        PdfEvidence = ParsePdfEvidence(source.Locator);
    }

    private static PdfEvidenceRegion? ParsePdfEvidence(string locator)
    {
        if (!locator.StartsWith("pdf-evidence:", StringComparison.OrdinalIgnoreCase)) return null;
        var pageMarker = locator.IndexOf("page=", StringComparison.OrdinalIgnoreCase);
        var regionMarker = locator.IndexOf("region=", StringComparison.OrdinalIgnoreCase);
        if (pageMarker < 0 || regionMarker < 0 || regionMarker <= pageMarker) return null;

        var pageEnd = locator.IndexOf(';', pageMarker);
        var pageText = locator[(pageMarker + "page=".Length)..(pageEnd < 0 ? regionMarker : pageEnd)];
        var regionText = locator[(regionMarker + "region=".Length)..];
        var parts = regionText.Split(',', StringSplitOptions.TrimEntries);
        if (!int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var pageNumber)
            || pageNumber < 1
            || parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bottom)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
            || !double.IsFinite(left) || !double.IsFinite(bottom)
            || !double.IsFinite(right) || !double.IsFinite(top)
            || left < 0 || bottom < 0 || right > 1 || top > 1
            || right <= left || top <= bottom)
            return null;

        return new PdfEvidenceRegion(pageNumber, left, bottom, right, top);
    }
}

public sealed class SuggestionRow : ViewModelBase
{
    private string _concept = "";
    private SuggestionState _reviewState = SuggestionState.Pending;
    private bool _isAnalystEdited;
    private bool _isBatchSelected;
    public string SuggestionId { get; init; } = "";
    public string SourceFieldId { get; init; } = "";
    public string SourceNodeId { get; init; } = "";
    public string TargetNodeId { get; init; } = "";
    public bool IsConnectedBatch { get; init; }
    public string ProjectDigest { get; init; } = "";
    public string Concept
    {
        get => _concept;
        set { if (Set(ref _concept, value)) Raise(nameof(Display)); }
    }
    public int Score { get; init; }
    public string Basis { get; init; } = "";
    public TaxonomyCandidateEvidence? TaxonomyEvidence { get; init; }
    public MappingRecommendation? Recommendation { get; init; }
    public bool IsAnalystEdited
    {
        get => _isAnalystEdited;
        set
        {
            if (Set(ref _isAnalystEdited, value))
            {
                Raise(nameof(Display));
                Raise(nameof(TaxonomyEvidenceDisplay));
                Raise(nameof(RecommendationDisplay));
            }
        }
    }
    public string TaxonomyEvidenceDisplay
    {
        get
        {
            if (TaxonomyEvidence is not { } evidence) return "";
            var roles = evidence.PresentationRoles.Count == 0
                ? "none loaded"
                : string.Join(", ", evidence.PresentationRoles);
            if (evidence.TotalPresentationRoleCount > evidence.PresentationRoles.Count)
                roles += $" (+{evidence.TotalPresentationRoleCount - evidence.PresentationRoles.Count} more)";
            var prefix = IsAnalystEdited ? "Original candidate evidence (draft edited): " : "";
            return $"{prefix}{evidence.Label} · {{{evidence.NamespaceUri}}}{evidence.LocalName} · {evidence.DataType}/{evidence.PeriodType} · {evidence.ProfileId} {evidence.TaxonomyVersion} · roles: {roles}";
        }
    }
    public string RecommendationDisplay
    {
        get
        {
            if (Recommendation is not { } recommendation) return "";
            var aspects = string.Join("; ", recommendation.AspectAssessments
                .Select(aspect =>
                {
                    var value = string.IsNullOrWhiteSpace(aspect.Value) ? "" : $" ({aspect.Value})";
                    var explanation = aspect.Status is MappingAspectReviewStatus.NeedsReview
                        or MappingAspectReviewStatus.NotProposed or MappingAspectReviewStatus.RunContext
                        ? $" — {aspect.Explanation}"
                        : "";
                    return $"{aspect.Aspect}: {aspect.Status}{value}{explanation}";
                }));
            var alternatives = recommendation.Alternatives.Count == 0
                ? "none"
                : string.Join(", ", recommendation.Alternatives.Take(3).Select(candidate => candidate.QualifiedName))
                    + (recommendation.Alternatives.Count > 3 ? $", +{recommendation.Alternatives.Count - 3} more" : "");
            var prefix = IsAnalystEdited ? "Edited draft; original assessment: " : "Review before acceptance: ";
            var functions = string.Join(", ", recommendation.AllowedDeterministicTransformationIds);
            return $"{prefix}{aspects} Alternatives: {alternatives}. Deterministic functions available: {functions}; none proposed.";
        }
    }
    public SuggestionState ReviewState
    {
        get => _reviewState;
        set { if (Set(ref _reviewState, value)) Raise(nameof(Display)); }
    }
    public bool IsBatchSelected
    {
        get => _isBatchSelected;
        set => Set(ref _isBatchSelected, value);
    }
    public string Display
    {
        get
        {
            var source = Recommendation is { } recommendation
                ? $"{recommendation.SourceFieldName} ({recommendation.SourceFieldId})"
                : SourceFieldId;
            return $"{source} → {Concept}  ·  rank {Score} (not confidence)  ·  evidence {Basis}  ·  {ReviewState}"
                + (IsAnalystEdited ? "  ·  edited" : "");
        }
    }
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

public sealed record DestinationKeyOption(IReadOnlyList<string> Columns)
{
    public string Display => string.Join(", ", Columns);
}

public sealed record DestinationKindOption(string Kind, string Display);

public sealed record PdfRowDirectionOption(bool RowsAtLowLineCoordinate, string Display);

public sealed record PresentationOccurrenceRow(string RoleUri, string Path)
{
    public string Display => $"{RoleUri}: {Path}";
}

public sealed record AdapterCapabilityOption(
    string Kind,
    string Display,
    bool Allowed,
    string OperationSummary,
    string? Reason)
{
    public string Summary => Allowed
        ? $"{Display}: {OperationSummary}."
        : $"{Display}: unavailable — {Reason}";
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
    private readonly object _taxonomySearchGate = new();
    private CancellationTokenSource? _taxonomySearchCancellation;
    private int _taxonomySearchGeneration;
    private bool _isTaxonomySearchBusy;
    private string _taxonomySearchStatus = "Search the loaded taxonomy index.";
    private string _selectedConceptOccurrencesStatus = "Select a concept to inspect its presentation paths.";
    private bool _isBusy;
    private ActiveMappingOperation? _activeMappingOperation;
    private int _mappingOperationGeneration;
    private bool _allowSyntheticProfile;
    private string? _projectPath;
    private string? _selectedSourceBindingId;
    private int? _expectedLayoutGeneration;
    private string _savedProjectSnapshot = "";
    private string? _selectedSourceColumn;
    private string? _selectedSourceTable;
    private string? _sqliteSourcePath;
    private string _sourceInspectionSummary = "Choose a source file to inspect its schema, row identity and adapter capabilities.";
    private int _sourceLoadGeneration;
    private bool _isSourceLoading;
    private Task _sqliteTableLoadTask = Task.CompletedTask;
    private PdfDigitalDocument? _pdfDocument;
    private PdfTableTemplate? _pdfTemplate;
    private PdfTableReconstructionResult? _pdfReconstruction;
    private string? _pdfSourcePath;
    private int _currentPdfPageNumber = 1;
    private double _pdfReviewZoom = 1.0;
    private string _pdfReviewStatus = "Choose a digital PDF source to begin review.";
    private string _pdfTemplateStatus = "Declare the row-label header, year headers, and row direction before reconstruction.";
    private string _pdfRowLabelHeader = "";
    private string _pdfPeriodHeaders = "";
    private PdfRowDirectionOption? _selectedPdfRowDirection;
    private string _pdfDecimalSeparator = "";
    private string _pdfGroupSeparator = "";
    private bool _pdfAllowGrouping;
    private string _pdfScaleFactor = "";
    private string _pdfCurrencyCode = "";
    private bool _pdfAllowLeadingSign;
    private bool _pdfAllowParenthesizedNegative;
    private bool _pdfAllowTrailingMinus;
    private string _pdfReviewer = "";
    private PdfReviewValueRow? _selectedPdfReviewValue;
    private IReadOnlyList<PdfReviewWord> _pdfPageWords = Array.Empty<PdfReviewWord>();
    private readonly Dictionary<PdfReviewValueRow, PdfReviewRowState> _pdfReviewStates = new();
    private readonly List<PdfReviewAuditEvent> _pdfReviewAuditEvents = new();
    private bool _applyingPdfReviewAudit;
    private bool _restoringPdfReviewSettings;
    private string? _pdfRestoreTemplateRevisionOverride;
    private static readonly DestinationKindOption SqliteDestinationKind = new("sqlite", "SQLite fact table");
    private string _destinationCapabilityStatus = "SQLite fact table: transactional fact publication.";
    private DestinationKindOption _selectedDestinationKind = SqliteDestinationKind;
    private string? _destinationPath;
    private string? _destinationTable;
    private DestinationKeyOption? _selectedDestinationKey;
    private SqliteTargetTableSchema? _destinationTableSchema;
    private IReadOnlyList<string>? _preferredDestinationKey;
    private bool _isDestinationLoading;
    private bool _destinationSchemaIsValid;
    private bool _isDestinationSchemaConfirmed;
    private string _destinationSchemaStatus = "Choose an existing SQLite database to inspect its tables.";
    private string _destinationPreviewSummary = "Run a destination dry run to see its estimated changes.";
    private string? _destinationPreviewSemanticDigest;
    private int _destinationLoadGeneration;
    private Task _destinationSchemaTask = Task.CompletedTask;
    private ConceptRow? _selectedConcept;
    private SuggestionRow? _selectedSuggestion;
    private string _suggestionFilter = "";
    private string _suggestionBatchSummary = "Generate suggestions for uniquely connected source fields to review a batch.";
    private string? _suggestionEditorBaselineId;
    private string _suggestionEditorBaselineConcept = "";
    private string _suggestionEditorBaselineUnit = "";
    private string _suggestionEditorBaselinePeriodType = "";
    private string _selectedSuggestionConcept = "";
    private string _selectedSuggestionUnit = "";
    private string _selectedSuggestionPeriodType = "duration";
    private string _selectedFactUnit = "USD";
    private string _selectedFactPeriodType = "duration";
    private string _selectedFactDecimals = "0";
    private string _selectedFactDimensions = "";
    private string _selectedFactEntityIdentifier = "";
    private bool _canEditSelectedFactEntity;
    private string _selectedFactConceptIdentity = "Select a fact to inspect its taxonomy identity.";
    private string _selectedFactSourceSummary = "Select a fact to inspect its source mapping.";
    private string _selectedFactPeriodSummary = "Select a fact to inspect its period mapping.";
    private string _selectedFactEntitySourceSummary = "Select a fact to inspect its entity source.";
    private string _previewResultSummary = "Run a preview or full execution to inspect generated facts and evidence.";
    private FactRow? _selectedPreviewFact;
    private PreviewEvidenceRow? _selectedPreviewEvidence;
    private PdfEvidenceRegion? _selectedPreviewPdfEvidence;
    private bool _isPdfReviewTabSelected;
    private readonly IProjectFileDialog? _fileDialog;
    private readonly Func<SourceConnection, CancellationToken, Task<ISourceSchema>> _sqliteSchemaLoader;
    private readonly Func<IAuditSink>? _auditSinkFactory;
    private readonly Func<TaxonomyConceptIndexStore>? _taxonomyIndexStoreFactory;
    private readonly Func<string, CancellationToken, Task<PdfDigitalDocument>> _pdfDocumentLoader;
    private readonly Func<PdfEvidenceStore> _pdfEvidenceStoreFactory;
    private readonly Func<PdfReviewAuditStore> _pdfReviewAuditStoreFactory;
    private readonly SqliteDestinationInspector _destinationInspector = new();

    private FilingProfile _profile;
    private readonly FilingProfileResolver _profileResolver;
    private readonly FilingProfileWorkerFactory _workerFactory;
    private TaxonomyIndex? _taxonomy;
    private DesignerSession _session;
    private SuggestionSession? _suggestionSession;
    private bool _isSuggestionHistoryMutation;
    private readonly Stack<AcceptedSuggestionUndo> _acceptedSuggestionUndo = new();
    private readonly Dictionary<string, DraftMapping> _unsavedSuggestionDrafts = new(StringComparer.Ordinal);
    private SuggestionRedoState? _suggestionRedoState;

    private sealed record AcceptedSuggestionUndo(
        SuggestionSession Session, IReadOnlyList<string> SuggestionIds, string DigestBefore,
        string DigestAfter, bool GraphChanged, bool IsBatch);
    private sealed record SuggestionRedoState(
        SuggestionSession Session, IReadOnlyList<string> SuggestionIds, string DigestBefore,
        bool GraphChanged, bool IsBatch);
    private sealed record PdfReviewRowState(string ReviewedValue, string? CorrectionReason, bool IsApproved);

    private sealed class ActiveMappingOperation(
        int generation,
        CancellationTokenSource cancellation,
        MappingProject project,
        string projectDigest,
        int sourceGeneration,
        int destinationGeneration,
        string destinationSelection,
        bool syntheticProfileAuthorization,
        bool destinationSchemaConfirmed,
        bool destinationSchemaValid,
        string profileDigest)
    {
        public int Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public MappingProject Project { get; } = project;
        public string ProjectDigest { get; } = projectDigest;
        public int SourceGeneration { get; } = sourceGeneration;
        public int DestinationGeneration { get; } = destinationGeneration;
        public string DestinationSelection { get; } = destinationSelection;
        public bool SyntheticProfileAuthorization { get; } = syntheticProfileAuthorization;
        public bool DestinationSchemaConfirmed { get; } = destinationSchemaConfirmed;
        public bool DestinationSchemaValid { get; } = destinationSchemaValid;
        public string ProfileDigest { get; } = profileDigest;
        public string? InvalidationReason { get; set; }
    }

    public string Title => _title;
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(CanCancelOperation));
                Raise(nameof(CancelOperationButtonText));
                CancelOperationCommand?.NotifyCanExecuteChanged();
                PreviewCommand?.NotifyCanExecuteChanged();
                ExecuteCommand?.NotifyCanExecuteChanged();
                SuggestConnectedMappingsCommand?.NotifyCanExecuteChanged();
                Raise(nameof(CanAcceptSelectedSuggestionBatch));
                AcceptSelectedSuggestionBatchCommand?.NotifyCanExecuteChanged();
                ChooseSourceCommand?.NotifyCanExecuteChanged();
                NotifyDestinationCommands();
                RefreshPdfReviewCommandState();
            }
        }
    }
    public bool CanCancelOperation => IsBusy
        && _activeMappingOperation is { } operation
        && !operation.Cancellation.IsCancellationRequested;
    public string CancelOperationButtonText =>
        _activeMappingOperation?.Cancellation.IsCancellationRequested == true ? "Cancelling…" : "Cancel operation";
    public bool IsSourceLoading
    {
        get => _isSourceLoading;
        private set
        {
            if (Set(ref _isSourceLoading, value))
            {
                PreviewCommand?.NotifyCanExecuteChanged();
                ExecuteCommand?.NotifyCanExecuteChanged();
                SuggestConnectedMappingsCommand?.NotifyCanExecuteChanged();
                Raise(nameof(CanAcceptSelectedSuggestionBatch));
                AcceptSelectedSuggestionBatchCommand?.NotifyCanExecuteChanged();
                ChooseSourceCommand?.NotifyCanExecuteChanged();
                NotifyDestinationCommands();
                RefreshPdfReviewCommandState();
            }
        }
    }
    public bool IsDestinationLoading
    {
        get => _isDestinationLoading;
        private set
        {
            if (!Set(ref _isDestinationLoading, value)) return;
            Raise(nameof(CanConfirmDestinationSchema));
            Raise(nameof(CanSelectDestinationKey));
            NotifyDestinationCommands();
        }
    }
    public bool AllowSyntheticProfile
    {
        get => _allowSyntheticProfile;
        set
        {
            if (!Set(ref _allowSyntheticProfile, value)) return;
            InvalidateMappingOperationIfStale();
            if (value)
            {
                Diagnostics.Clear();
                Raise(nameof(Diagnostics));
            }
            RunSearch();
        }
    }
    public string? ProjectPath { get => _projectPath; private set => Set(ref _projectPath, value); }
    public bool IsProjectDirty => !StringComparer.Ordinal.Equals(
        _savedProjectSnapshot, ProjectSerializer.Serialize(Session.Project));
    public string? SelectedSourceBindingId { get => _selectedSourceBindingId; private set => Set(ref _selectedSourceBindingId, value); }
    public IReadOnlyList<AdapterCapabilityOption> SourceCapabilities { get; } = BuildSourceCapabilities();
    public IReadOnlyList<AdapterCapabilityOption> TargetCapabilities { get; } = BuildTargetCapabilities();
    public string SourceCapabilitySummary => FormatCapabilitySummary(SourceCapabilities);
    public string TargetCapabilitySummary => FormatCapabilitySummary(TargetCapabilities);
    public IReadOnlyList<DestinationKindOption> DestinationKindOptions { get; } = BuildDestinationKindOptions();
    public string DestinationCapabilityStatus
    {
        get => _destinationCapabilityStatus;
        private set => Set(ref _destinationCapabilityStatus, value);
    }
    public DestinationKindOption SelectedDestinationKind
    {
        get => _selectedDestinationKind;
        set
        {
            var suitability = MappingJobService.DescribeTargetSuitability(
                new SourceConnection("destination", value.Kind, Path: null));
            if (!suitability.Allowed)
            {
                DestinationCapabilityStatus = $"Cannot use {value.Display} as a target. {suitability.Reason}";
                StatusText = DestinationCapabilityStatus;
                return;
            }

            DestinationCapabilityStatus = DescribeSelectedTarget(value, suitability);
            if (!Set(ref _selectedDestinationKind, value))
            {
                Raise(nameof(DestinationCapabilityStatus));
                return;
            }
            ClearDestinationSelection();
            Raise(nameof(IsSqliteDestination));
            Raise(nameof(IsFileDestination));
            Raise(nameof(DestinationFormatDisplay));
            Raise(nameof(DestinationPickerButtonText));
            Raise(nameof(DestinationPreviewButtonText));
            Raise(nameof(DestinationPublishButtonText));
            Raise(nameof(DestinationGuidance));
            NotifyDestinationCommands();
        }
    }
    public bool IsSqliteDestination => StringComparer.Ordinal.Equals(SelectedDestinationKind.Kind, "sqlite");
    public bool IsFileDestination => DestinationPlanValidator.IsFileKind(SelectedDestinationKind.Kind);
    public string DestinationFormatDisplay => IsSqliteDestination
        ? "Contract: xbrl-facts-v1 · upsert"
        : $"Contract: {DestinationPlanValidator.FileFormatFor(SelectedDestinationKind.Kind)} · replace";
    public string DestinationPickerButtonText => IsSqliteDestination
        ? "Choose SQLite database…"
        : "Choose output file…";
    public string DestinationPreviewButtonText => IsSqliteDestination
        ? "Dry-run (no database writes)"
        : "Dry-run (no file writes)";
    public string DestinationPublishButtonText => IsSqliteDestination
        ? "Publish to SQLite…"
        : "Publish file…";
    public string DestinationGuidance => IsSqliteDestination
        ? "The dry run checks the full mapping and target schema. Publication rechecks approval and schema, then asks for final confirmation."
        : "The dry run checks the full mapping and output contract. Publication rechecks approval, stages and validates the file, then asks for final confirmation.";
    public string? SelectedSourceColumn
    {
        get => _selectedSourceColumn;
        set
        {
            if (!Set(ref _selectedSourceColumn, value)) return;
            RefreshSelectedFactSourceSummary();
        }
    }
    public string SourceInspectionSummary
    {
        get => _sourceInspectionSummary;
        private set => Set(ref _sourceInspectionSummary, value);
    }
    public string? SelectedSourceTable
    {
        get => _selectedSourceTable;
        set
        {
            if (Set(ref _selectedSourceTable, value) && value is not null && _sqliteSourcePath is not null)
            {
                var generation = Interlocked.Increment(ref _sourceLoadGeneration);
                InvalidateMappingOperationIfStale();
                IsSourceLoading = true;
                _sqliteTableLoadTask = LoadSqliteTableAsync(value, generation);
            }
        }
    }
    public string? DestinationPath => _destinationPath;
    public string DestinationPathDisplay => _destinationPath ?? "No destination selected.";
    public ObservableCollection<string> DestinationTables { get; } = new();
    public bool HasDestinationTables => DestinationTables.Count > 0;
    public string? DestinationTable
    {
        get => _destinationTable;
        set
        {
            if (!Set(ref _destinationTable, value)) return;
            InvalidateMappingOperationIfStale();
            _destinationTableSchema = null;
            _selectedDestinationKey = null;
            DestinationKeys.Clear();
            Raise(nameof(SelectedDestinationKey));
            Raise(nameof(DestinationKeys));
            ResetDestinationPreviewSummary("The destination table changed; rerun the dry run to refresh its estimate.");
            ResetDestinationSchemaConfirmation("Inspecting the selected table…");
            NotifyDestinationCommands();
            if (value is not null && _destinationPath is not null)
            {
                var generation = Interlocked.Increment(ref _destinationLoadGeneration);
                IsDestinationLoading = true;
                _destinationSchemaTask = LoadDestinationTableSchemaAsync(value, generation);
            }
        }
    }
    public ObservableCollection<DestinationKeyOption> DestinationKeys { get; } = new();
    public DestinationKeyOption? SelectedDestinationKey
    {
        get => _selectedDestinationKey;
        set
        {
            if (!Set(ref _selectedDestinationKey, value)) return;
            InvalidateMappingOperationIfStale();
            ResetDestinationPreviewSummary("The business key changed; rerun the dry run to refresh its estimate.");
            IsDestinationSchemaConfirmed = false;
            ValidateSelectedDestinationSchema();
            NotifyDestinationCommands();
        }
    }
    public bool DestinationSchemaIsValid
    {
        get => _destinationSchemaIsValid;
        private set
        {
            if (!Set(ref _destinationSchemaIsValid, value)) return;
            InvalidateMappingOperationIfStale();
            Raise(nameof(CanConfirmDestinationSchema));
            NotifyDestinationCommands();
        }
    }
    public bool IsDestinationSchemaConfirmed
    {
        get => _isDestinationSchemaConfirmed;
        set
        {
            if (value && !DestinationSchemaIsValid) return;
            if (!Set(ref _isDestinationSchemaConfirmed, value)) return;
            InvalidateMappingOperationIfStale();
            NotifyDestinationCommands();
        }
    }
    public bool CanConfirmDestinationSchema => DestinationSchemaIsValid && !IsDestinationLoading;
    public bool CanSelectDestinationKey => DestinationKeys.Count > 0 && !IsDestinationLoading;
    public string DestinationSchemaStatus
    {
        get => _destinationSchemaStatus;
        private set => Set(ref _destinationSchemaStatus, value);
    }
    public string DestinationPreviewSummary
    {
        get => _destinationPreviewSummary;
        private set => Set(ref _destinationPreviewSummary, value);
    }
    public bool CanSaveDestinationPlan => CanUseSelectedDestination && ProjectPath is not null
        && !IsBusy && !IsSourceLoading;
    public bool CanPreviewDestination => CanUseSelectedDestination && !IsBusy && !IsSourceLoading;
    public bool CanPublishDestination => CanUseSelectedDestination && ProjectPath is not null
        && IsSavedDestinationCurrent && !IsBusy && !IsSourceLoading;
    public ConceptRow? SelectedConcept
    {
        get => _selectedConcept;
        set
        {
            if (Set(ref _selectedConcept, value)) RefreshSelectedConceptOccurrences();
        }
    }
    public SuggestionRow? SelectedSuggestion
    {
        get => _selectedSuggestion;
        set
        {
            if (ReferenceEquals(_selectedSuggestion, value)) return;
            CaptureSelectedSuggestionDraft();
            if (Set(ref _selectedSuggestion, value))
            {
                if (value is null) ClearSelectedSuggestionEditor();
                else LoadSelectedSuggestionEditor();
            }
        }
    }
    public string SuggestionFilter
    {
        get => _suggestionFilter;
        set
        {
            if (Set(ref _suggestionFilter, value)) RefreshFilteredSuggestions();
        }
    }
    public string SuggestionBatchSummary
    {
        get => _suggestionBatchSummary;
        private set => Set(ref _suggestionBatchSummary, value);
    }
    public bool CanAcceptSelectedSuggestionBatch =>
        !IsBusy && !IsSourceLoading && _suggestionSession is not null
        && AiSuggestions.Any(row => row.IsBatchSelected);
    public string SelectedSuggestionConcept { get => _selectedSuggestionConcept; set => Set(ref _selectedSuggestionConcept, value); }
    public string SelectedSuggestionUnit { get => _selectedSuggestionUnit; set => Set(ref _selectedSuggestionUnit, value); }
    public string SelectedSuggestionPeriodType { get => _selectedSuggestionPeriodType; set => Set(ref _selectedSuggestionPeriodType, value); }
    public string SelectedFactUnit { get => _selectedFactUnit; set => Set(ref _selectedFactUnit, value); }
    public string SelectedFactPeriodType { get => _selectedFactPeriodType; set => Set(ref _selectedFactPeriodType, value); }
    public string SelectedFactDecimals { get => _selectedFactDecimals; set => Set(ref _selectedFactDecimals, value); }
    public string SelectedFactDimensions { get => _selectedFactDimensions; set => Set(ref _selectedFactDimensions, value); }
    public string SelectedFactEntityIdentifier { get => _selectedFactEntityIdentifier; set => Set(ref _selectedFactEntityIdentifier, value); }
    public bool CanEditSelectedFactEntity
    {
        get => _canEditSelectedFactEntity;
        private set => Set(ref _canEditSelectedFactEntity, value);
    }
    public string SelectedFactConceptIdentity
    {
        get => _selectedFactConceptIdentity;
        private set => Set(ref _selectedFactConceptIdentity, value);
    }
    public string SelectedFactSourceSummary
    {
        get => _selectedFactSourceSummary;
        private set => Set(ref _selectedFactSourceSummary, value);
    }
    public string SelectedFactPeriodSummary
    {
        get => _selectedFactPeriodSummary;
        private set => Set(ref _selectedFactPeriodSummary, value);
    }
    public string SelectedFactEntitySourceSummary
    {
        get => _selectedFactEntitySourceSummary;
        private set => Set(ref _selectedFactEntitySourceSummary, value);
    }
    public string PreviewResultSummary
    {
        get => _previewResultSummary;
        private set => Set(ref _previewResultSummary, value);
    }
    public FactRow? SelectedPreviewFact
    {
        get => _selectedPreviewFact;
        set
        {
            if (!Set(ref _selectedPreviewFact, value)) return;
            _selectedPreviewPdfEvidence = null;
            Raise(nameof(SelectedPdfEvidence));
            SelectedPreviewEvidence = value?.Evidence.FirstOrDefault();
            Raise(nameof(HasSelectedPreviewFact));
        }
    }
    public bool HasSelectedPreviewFact => SelectedPreviewFact is not null;
    public PreviewEvidenceRow? SelectedPreviewEvidence
    {
        get => _selectedPreviewEvidence;
        set
        {
            if (!Set(ref _selectedPreviewEvidence, value)) return;
            if (_selectedPreviewPdfEvidence is not null)
            {
                _selectedPreviewPdfEvidence = null;
                Raise(nameof(SelectedPdfEvidence));
            }
            Raise(nameof(CanOpenSelectedPreviewEvidence));
            OpenSelectedPreviewEvidenceCommand?.NotifyCanExecuteChanged();
        }
    }
    public bool CanOpenSelectedPreviewEvidence =>
        SelectedPreviewEvidence?.PdfEvidence is { } evidence
        && _pdfDocument?.Pages.Any(page => page.PageNumber == evidence.PageNumber) == true;
    public bool IsPdfReviewTabSelected
    {
        get => _isPdfReviewTabSelected;
        set => Set(ref _isPdfReviewTabSelected, value);
    }
    public IReadOnlyList<string> FactPeriodTypes { get; } = new[] { "duration", "instant" };

    private static IReadOnlyList<AdapterCapabilityOption> BuildSourceCapabilities()
    {
        var definitions = new (string Kind, string Display)[]
        {
            ("excel", "Excel"),
            ("csv", "CSV"),
            ("json", "JSON"),
            ("xml", "XML"),
            ("xbrl-report", "xBRL-JSON"),
            ("sqlite", "SQLite"),
            ("pdf-digital", "Digital PDF"),
            ("oracle", "Oracle database")
        };
        return Array.AsReadOnly(definitions.Select(definition =>
        {
            var suitability = MappingJobService.DescribeSourceSuitability(
                new SourceConnection("capability", definition.Kind, Path: null));
            return new AdapterCapabilityOption(definition.Kind, definition.Display, suitability.Allowed,
                suitability.Capabilities is null ? string.Empty : DescribeOperations(suitability.Capabilities, source: true),
                suitability.Reason);
        }).ToArray());
    }

    private static IReadOnlyList<AdapterCapabilityOption> BuildTargetCapabilities()
    {
        var definitions = new (string Kind, string Display)[]
        {
            ("sqlite", "SQLite"),
            ("csv", "CSV"),
            ("excel", "Excel"),
            ("json", "JSON"),
            ("xml", "XML"),
            ("xbrl-report", "xBRL-JSON"),
            ("pdf-digital", "PDF output"),
            ("oracle", "Oracle database")
        };
        return Array.AsReadOnly(definitions.Select(definition =>
        {
            var suitability = MappingJobService.DescribeTargetSuitability(
                new SourceConnection("capability", definition.Kind, Path: null));
            return new AdapterCapabilityOption(definition.Kind, definition.Display, suitability.Allowed,
                suitability.Capabilities is null ? string.Empty
                    : DescribeTargetOperations(suitability.Capabilities, definition.Kind),
                suitability.Reason);
        }).ToArray());
    }

    private static IReadOnlyList<DestinationKindOption> BuildDestinationKindOptions()
    {
        var definitions = new[]
        {
            SqliteDestinationKind,
            new DestinationKindOption("csv", "CSV records"),
            new DestinationKindOption("excel", "Excel workbook"),
            new DestinationKindOption("json", "JSON records"),
            new DestinationKindOption("xml", "XML records")
        };
        return Array.AsReadOnly(definitions.Where(option =>
            MappingJobService.DescribeTargetSuitability(
                new SourceConnection("destination", option.Kind, Path: null)).Allowed).ToArray());
    }

    private static string DescribeOperations(AdapterCapabilities capabilities, bool source)
    {
        var operations = source
            ? new[]
            {
                (capabilities.SupportsSchemaInspection, "inspect"),
                (capabilities.SupportsSampling, "sample"),
                (capabilities.SupportsReading, "read"),
                (capabilities.SupportsLineage, "lineage")
            }
            : new[]
            {
                (capabilities.SupportsStagedWrite, "staged output"),
                (capabilities.SupportsPublication, "publish"),
                (capabilities.SupportsLineage, "lineage")
            };
        return string.Join(", ", operations.Where(operation => operation.Item1).Select(operation => operation.Item2));
    }

    private static string DescribeTargetOperations(AdapterCapabilities capabilities, string kind)
        => StringComparer.Ordinal.Equals(kind, DestinationPlanValidator.SupportedKind)
            ? "transactional fact upsert"
            : DescribeOperations(capabilities, source: false);

    private static string DescribeSelectedTarget(DestinationKindOption option, TargetSuitability suitability)
        => $"{option.Display}: {DescribeTargetOperations(suitability.Capabilities!, suitability.Kind)}.";

    private static string FormatCapabilitySummary(IReadOnlyList<AdapterCapabilityOption> capabilities)
    {
        var available = capabilities.Where(option => option.Allowed)
            .GroupBy(option => option.OperationSummary, StringComparer.Ordinal)
            .Select(group => $"{string.Join("/", group.Select(option => option.Display))} ({group.Key})");
        var unavailable = capabilities.Where(option => !option.Allowed)
            .Select(option => $"{option.Display} — {FormatCapabilityReason(option)}");
        var parts = new List<string>();
        var ready = string.Join("; ", available);
        if (ready.Length > 0) parts.Add($"Ready: {ready}.");
        var notReady = string.Join("; ", unavailable);
        if (notReady.Length > 0) parts.Add($"Unavailable: {notReady}.");
        return string.Join(" ", parts);
    }

    private static string FormatCapabilityReason(AdapterCapabilityOption option) => option.Kind switch
    {
        "xbrl-report" => "input only; no report output contract",
        "pdf" or "pdf-digital" => "no qualified PDF writer",
        "oracle" => "provider not qualified",
        _ => option.Reason ?? "operation unavailable"
    };

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
                if (SelectedSuggestion is not null)
                {
                    CaptureSelectedSuggestionDraft();
                    LoadSelectedSuggestionEditor();
                }
                Raise(nameof(SelectedNodeDisplayName));
                Raise(nameof(CanEditSelectedFact));
            }
        }
    }

    public string SelectedNodeDisplayName => _selectedNode?.DisplayName ?? "(none)";
    public bool CanEditSelectedFact => _selectedNode?.NodeType == NodeTypeConstants.FactConstruct;

    public string SearchTerm
    {
        get => _searchTerm;
        set
        {
            if (!Set(ref _searchTerm, value)) return;
            ResetTaxonomySearchForCriteriaChange();
        }
    }
    public string SearchLanguage
    {
        get => _searchLanguage;
        set
        {
            if (!Set(ref _searchLanguage, value)) return;
            ResetTaxonomySearchForCriteriaChange();
            RefreshSelectedConceptOccurrences();
        }
    }
    public IReadOnlyList<string> SearchLanguages { get; } = new[] { "en", "fr", "ar" };
    public int ResultPage => _resultPage;
    public bool HasNextPage { get; private set; }
    public bool HasPreviousPage => _resultPage > 0;
    public bool IsTaxonomySearchBusy
    {
        get => _isTaxonomySearchBusy;
        private set
        {
            if (!Set(ref _isTaxonomySearchBusy, value)) return;
            Raise(nameof(CanCancelTaxonomySearch));
            CancelTaxonomySearchCommand?.NotifyCanExecuteChanged();
        }
    }
    public bool CanCancelTaxonomySearch => IsTaxonomySearchBusy
        && _taxonomySearchCancellation is { IsCancellationRequested: false };
    public string TaxonomySearchStatus
    {
        get => _taxonomySearchStatus;
        private set => Set(ref _taxonomySearchStatus, value);
    }
    public string SelectedConceptOccurrencesStatus
    {
        get => _selectedConceptOccurrencesStatus;
        private set => Set(ref _selectedConceptOccurrencesStatus, value);
    }

    public ObservableCollection<ConceptRow> ConceptResults { get; } = new();
    public ObservableCollection<PresentationOccurrenceRow> SelectedConceptOccurrences { get; } = new();
    public ObservableCollection<string> SourceColumns { get; } = new();
    public ObservableCollection<string> SourceTables { get; } = new();
    public bool HasSourceTables => SourceTables.Count > 0;
    public bool HasPdfDocument => _pdfDocument is not null;
    public bool CanEditPdfReview => HasPdfDocument && !IsSourceLoading && !IsBusy;
    public PdfDigitalPage? CurrentPdfPage => _pdfDocument?.Pages.FirstOrDefault(page => page.PageNumber == CurrentPdfPageNumber);
    public int PdfPageCount => _pdfDocument?.Pages.Count ?? 0;
    public string PdfPageSummary => HasPdfDocument
        ? $"Page {CurrentPdfPageNumber} of {PdfPageCount} · {_pdfDocument!.SourceSha256}"
        : "No PDF page loaded.";
    public IReadOnlyList<PdfReviewWord> PdfPageWords => _pdfPageWords;
    public bool HasPreviousPdfPage => CurrentPdfPageNumber > 1;
    public bool HasNextPdfPage => CurrentPdfPageNumber < PdfPageCount;
    public string PdfReviewStatus { get => _pdfReviewStatus; private set => Set(ref _pdfReviewStatus, value); }
    public string PdfTemplateStatus { get => _pdfTemplateStatus; private set => Set(ref _pdfTemplateStatus, value); }
    public string PdfRowLabelHeader { get => _pdfRowLabelHeader; set { if (Set(ref _pdfRowLabelHeader, value)) InvalidatePdfAnalysis(); } }
    public string PdfPeriodHeaders { get => _pdfPeriodHeaders; set { if (Set(ref _pdfPeriodHeaders, value)) InvalidatePdfAnalysis(); } }
    public IReadOnlyList<PdfRowDirectionOption> PdfRowDirections { get; } = new[]
    {
        new PdfRowDirectionOption(true, "Rows toward lower page coordinates"),
        new PdfRowDirectionOption(false, "Rows toward higher page coordinates")
    };
    public PdfRowDirectionOption? SelectedPdfRowDirection { get => _selectedPdfRowDirection; set { if (Set(ref _selectedPdfRowDirection, value)) InvalidatePdfAnalysis(); } }
    public string PdfDecimalSeparator { get => _pdfDecimalSeparator; set { if (Set(ref _pdfDecimalSeparator, value)) InvalidatePdfAnalysis(); } }
    public string PdfGroupSeparator { get => _pdfGroupSeparator; set { if (Set(ref _pdfGroupSeparator, value)) InvalidatePdfAnalysis(); } }
    public bool PdfAllowGrouping { get => _pdfAllowGrouping; set { if (Set(ref _pdfAllowGrouping, value)) InvalidatePdfAnalysis(); } }
    public string PdfScaleFactor { get => _pdfScaleFactor; set { if (Set(ref _pdfScaleFactor, value)) InvalidatePdfAnalysis(); } }
    public string PdfCurrencyCode { get => _pdfCurrencyCode; set { if (Set(ref _pdfCurrencyCode, value)) InvalidatePdfAnalysis(); } }
    public bool PdfAllowLeadingSign { get => _pdfAllowLeadingSign; set { if (Set(ref _pdfAllowLeadingSign, value)) InvalidatePdfAnalysis(); } }
    public bool PdfAllowParenthesizedNegative { get => _pdfAllowParenthesizedNegative; set { if (Set(ref _pdfAllowParenthesizedNegative, value)) InvalidatePdfAnalysis(); } }
    public bool PdfAllowTrailingMinus { get => _pdfAllowTrailingMinus; set { if (Set(ref _pdfAllowTrailingMinus, value)) InvalidatePdfAnalysis(); } }
    public string PdfReviewer { get => _pdfReviewer; set { if (Set(ref _pdfReviewer, value)) RefreshPdfReviewCommandState(); } }
    public int CurrentPdfPageNumber
    {
        get => _currentPdfPageNumber;
        set
        {
            var next = PdfPageCount == 0 ? 1 : Math.Clamp(value, 1, PdfPageCount);
            if (!Set(ref _currentPdfPageNumber, next)) return;
            RefreshPdfPageWords();
            Raise(nameof(CurrentPdfPage));
            Raise(nameof(PdfPageSummary));
            Raise(nameof(HasPreviousPdfPage));
            Raise(nameof(HasNextPdfPage));
            PreviousPdfPageCommand?.NotifyCanExecuteChanged();
            NextPdfPageCommand?.NotifyCanExecuteChanged();
        }
    }
    public double PdfReviewZoom
    {
        get => _pdfReviewZoom;
        set => Set(ref _pdfReviewZoom, Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.5, 3.0));
    }
    public ObservableCollection<int> PdfPageNumbers { get; } = new();
    public ObservableCollection<PdfReviewValueRow> PdfReviewValues { get; } = new();
    public PdfReviewValueRow? SelectedPdfReviewValue
    {
        get => _selectedPdfReviewValue;
        set
        {
            var clearedPreviewEvidence = _selectedPreviewPdfEvidence is not null;
            _selectedPreviewPdfEvidence = null;
            if (!Set(ref _selectedPdfReviewValue, value))
            {
                if (clearedPreviewEvidence) Raise(nameof(SelectedPdfEvidence));
                return;
            }
            if (value is not null) CurrentPdfPageNumber = value.PageNumber;
            Raise(nameof(SelectedPdfEvidence));
            Raise(nameof(SelectedPdfReviewHistory));
            RefreshPdfReviewCommandState();
        }
    }
    public PdfEvidenceRegion? SelectedPdfEvidence =>
        _selectedPreviewPdfEvidence ?? SelectedPdfReviewValue?.Evidence;
    public string SelectedPdfReviewHistory => SelectedPdfReviewValue is null
        ? "Select a value to inspect its durable review history."
        : string.Join(Environment.NewLine, _pdfReviewAuditEvents
            .Where(entry => entry.RowOccurrence == SelectedPdfReviewValue.SourceRow.Occurrence
                && entry.ReportingYear == SelectedPdfReviewValue.ReportingYear)
            .Select(entry => $"{entry.OccurredUtc:yyyy-MM-dd HH:mm:ss 'UTC'} · {entry.Action}: {entry.ReviewedValue}"
                + (entry.CorrectionReason is null ? "" : $" · {entry.CorrectionReason}")
                + $" · {entry.Reviewer}")) is { Length: > 0 } history
            ? history : "No durable review actions have been recorded for this value.";
    public bool CanApplyPdfCorrection => !IsSourceLoading && !IsBusy
        && SelectedPdfReviewValue?.CanApplyCorrection == true
        && TryGetPdfReviewer(out _);
    public bool CanUndoPdfReview => !IsSourceLoading && !IsBusy
        && SelectedPdfReviewValue is not null
        && _pdfReviewAuditEvents.Any(entry => entry.RowOccurrence == SelectedPdfReviewValue.SourceRow.Occurrence
            && entry.ReportingYear == SelectedPdfReviewValue.ReportingYear)
        && TryGetPdfReviewer(out _);
    public bool CanSavePdfReview => !IsSourceLoading && !IsBusy
        && _pdfDocument is not null && _pdfTemplate is not null
        && _pdfReconstruction is { State: PdfTableReconstructionState.Anchored, Alternatives.Count: 0 }
        && PdfReviewValues.Count > 0 && PdfReviewValues.All(row => row.CanApprove && row.IsApproved && !row.HasPendingCorrectionDraft)
        && TryGetPdfReviewer(out _);
    public ObservableCollection<SuggestionRow> AiSuggestions { get; } = new();
    public ObservableCollection<SuggestionRow> FilteredAiSuggestions { get; } = new();
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = new();
    public ObservableCollection<FactRow> PreviewFacts { get; } = new();

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand SaveProjectAsCommand { get; }
    public RelayCommand CloseProjectCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ExecuteCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand SearchTaxonomyCommand { get; }
    public RelayCommand CancelTaxonomySearchCommand { get; }
    public RelayCommand ChooseSourceCommand { get; }
    public RelayCommand ChooseDestinationCommand { get; }
    public RelayCommand SaveDestinationPlanCommand { get; }
    public RelayCommand PreviewDestinationCommand { get; }
    public RelayCommand PublishDestinationCommand { get; }
    public RelayCommand BindSourceColumnCommand { get; }
    public RelayCommand SetConceptCommand { get; }
    public RelayCommand ApplyFactPropertiesCommand { get; }
    public RelayCommand SuggestMappingsCommand { get; }
    public RelayCommand SuggestConnectedMappingsCommand { get; }
    public RelayCommand ApplySuggestedMappingCommand { get; }
    public RelayCommand AcceptSelectedSuggestionBatchCommand { get; }
    public RelayCommand EditSelectedSuggestionCommand { get; }
    public RelayCommand RejectSelectedSuggestionCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand BuildPdfTemplateCommand { get; }
    public RelayCommand SavePdfReviewCommand { get; }
    public RelayCommand ApplyPdfCorrectionCommand { get; }
    public RelayCommand UndoPdfReviewCommand { get; }
    public RelayCommand PreviousPdfPageCommand { get; }
    public RelayCommand NextPdfPageCommand { get; }
    public RelayCommand OpenSelectedPreviewEvidenceCommand { get; }

    public MainWindowViewModel() : this(BuildSampleProject())
    {
    }

    public MainWindowViewModel(IProjectFileDialog fileDialog)
        : this(BuildSampleProject(), fileDialog,
            new SqliteSourceAdapter().InspectSchemaAsync,
            FilingProfileResolver.FromDefaultCatalog())
    {
    }

    public MainWindowViewModel(
        IProjectFileDialog fileDialog,
        Func<IAuditSink> auditSinkFactory,
        Func<TaxonomyConceptIndexStore>? taxonomyIndexStoreFactory = null)
        : this(BuildSampleProject(), fileDialog,
            new SqliteSourceAdapter().InspectSchemaAsync,
            FilingProfileResolver.FromDefaultCatalog(),
            auditSinkFactory: auditSinkFactory,
            taxonomyIndexStoreFactory: taxonomyIndexStoreFactory)
    {
    }

    public MainWindowViewModel(MappingProject project) : this(project, null)
    {
    }

    public MainWindowViewModel(MappingProject project, IProjectFileDialog? fileDialog)
        : this(project, fileDialog, new SqliteSourceAdapter().InspectSchemaAsync)
    {
    }

    internal MainWindowViewModel(Func<IAuditSink> auditSinkFactory)
        : this(BuildSampleProject(), null, new SqliteSourceAdapter().InspectSchemaAsync,
            FilingProfileResolver.DevelopmentOnly(), auditSinkFactory: auditSinkFactory)
    {
    }

    internal MainWindowViewModel(MappingProject project, IProjectFileDialog? fileDialog,
        Func<SourceConnection, CancellationToken, Task<ISourceSchema>> sqliteSchemaLoader,
        FilingProfileResolver? profileResolver = null,
        FilingProfileWorkerFactory? workerFactory = null,
        Func<IAuditSink>? auditSinkFactory = null,
        Func<string, CancellationToken, Task<PdfDigitalDocument>>? pdfDocumentLoader = null,
        Func<PdfEvidenceStore>? pdfEvidenceStoreFactory = null,
        Func<PdfReviewAuditStore>? pdfReviewAuditStoreFactory = null,
        Func<TaxonomyConceptIndexStore>? taxonomyIndexStoreFactory = null)
    {
        _fileDialog = fileDialog;
        _sqliteSchemaLoader = sqliteSchemaLoader;
        _auditSinkFactory = auditSinkFactory;
        _taxonomyIndexStoreFactory = taxonomyIndexStoreFactory;
        _pdfDocumentLoader = pdfDocumentLoader ?? ((path, cancellationToken) =>
            new PdfExtractionProcessClient().ExtractFileAsync(path, cancellationToken: cancellationToken));
        _pdfEvidenceStoreFactory = pdfEvidenceStoreFactory ?? (() =>
            new PdfEvidenceStore(new LocalApplicationDatabase()));
        _pdfReviewAuditStoreFactory = pdfReviewAuditStoreFactory ?? (() =>
            new PdfReviewAuditStore(new LocalApplicationDatabase()));
        _profileResolver = profileResolver ?? FilingProfileResolver.DevelopmentOnly();
        _workerFactory = workerFactory ?? FilingProfileWorkerFactory.ForInstalledProfiles(_profileResolver);
        _profile = CreateUnresolvedProfileIdentity(project);
        _taxonomy = CreateUnresolvedDisplayTaxonomyIndex(_profile);
        RefreshProfileForProject(project);
        _session = new DesignerSession(project);
        _session.Changed += OnSessionChanged;
        _session.SelectionChanged += OnSessionSelectionChanged;
        _savedProjectSnapshot = ProjectSerializer.Serialize(project);
        RefreshTitle();

        NewProjectCommand = new RelayCommand(async _ => await NewProjectAsync());
        OpenProjectCommand = new RelayCommand(async _ => await OpenProjectAsync());
        SaveProjectCommand = new RelayCommand(async _ => await SaveProjectAsync());
        SaveProjectAsCommand = new RelayCommand(async _ => await SaveProjectAsAsync());
        CloseProjectCommand = new RelayCommand(async _ => await CloseProjectAsync());
        ExitCommand = new RelayCommand(_ =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                d.MainWindow?.Close();
        });
        ValidateCommand = new RelayCommand(_ => Validate());
        PreviewCommand = new RelayCommand(async _ => await PreviewAsync(), _ => !IsBusy && !IsSourceLoading);
        ExecuteCommand = new RelayCommand(async _ => await ExecuteAsync(), _ => !IsBusy && !IsSourceLoading);
        CancelOperationCommand = new RelayCommand(_ => CancelMappingOperation(), _ => CanCancelOperation);
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected());
        UndoCommand = new RelayCommand(_ => Undo());
        RedoCommand = new RelayCommand(_ => Redo());
        SearchTaxonomyCommand = new RelayCommand(_ => RunSearch());
        CancelTaxonomySearchCommand = new RelayCommand(_ => CancelTaxonomySearch(), _ => CanCancelTaxonomySearch);
        ChooseSourceCommand = new RelayCommand(async _ => await ChooseSourceAsync(), _ => !IsBusy && !IsSourceLoading);
        ChooseDestinationCommand = new RelayCommand(async _ => await ChooseDestinationAsync(),
            _ => !IsBusy && !IsDestinationLoading);
        SaveDestinationPlanCommand = new RelayCommand(_ => SaveDestinationPlan(), _ => CanSaveDestinationPlan);
        PreviewDestinationCommand = new RelayCommand(async _ => await PreviewDestinationAsync(),
            _ => CanPreviewDestination);
        PublishDestinationCommand = new RelayCommand(async _ => await PublishDestinationAsync(),
            _ => CanPublishDestination);
        BindSourceColumnCommand = new RelayCommand(_ => BindSelectedSourceColumn());
        SetConceptCommand = new RelayCommand(_ => SetSelectedConcept());
        ApplyFactPropertiesCommand = new RelayCommand(_ => ApplySelectedFactProperties());
        SuggestMappingsCommand = new RelayCommand(async _ => await SuggestMappingsAsync());
        SuggestConnectedMappingsCommand = new RelayCommand(async _ => await SuggestConnectedMappingsAsync(),
            _ => !IsBusy && !IsSourceLoading);
        ApplySuggestedMappingCommand = new RelayCommand(_ => ApplySelectedSuggestion());
        AcceptSelectedSuggestionBatchCommand = new RelayCommand(_ => AcceptSelectedSuggestionBatch(),
            _ => CanAcceptSelectedSuggestionBatch);
        EditSelectedSuggestionCommand = new RelayCommand(_ => EditSelectedSuggestion());
        RejectSelectedSuggestionCommand = new RelayCommand(_ => RejectSelectedSuggestion());
        NextPageCommand = new RelayCommand(_ => { if (HasNextPage) { _resultPage++; RunSearch(keepPage: true); } });
        PreviousPageCommand = new RelayCommand(_ => { if (_resultPage > 0) { _resultPage--; RunSearch(keepPage: true); } });
        BuildPdfTemplateCommand = new RelayCommand(_ => BuildPdfTemplate());
        SavePdfReviewCommand = new RelayCommand(async _ => await SavePdfReviewAsync(), _ => CanSavePdfReview);
        ApplyPdfCorrectionCommand = new RelayCommand(_ => ApplySelectedPdfCorrection(), _ => CanApplyPdfCorrection);
        UndoPdfReviewCommand = new RelayCommand(_ => UndoSelectedPdfReview(), _ => CanUndoPdfReview);
        PreviousPdfPageCommand = new RelayCommand(_ => { if (HasPreviousPdfPage) CurrentPdfPageNumber--; }, _ => HasPreviousPdfPage);
        NextPdfPageCommand = new RelayCommand(_ => { if (HasNextPdfPage) CurrentPdfPageNumber++; }, _ => HasNextPdfPage);
        OpenSelectedPreviewEvidenceCommand = new RelayCommand(_ => OpenSelectedPreviewEvidence(),
            _ => CanOpenSelectedPreviewEvidence);

        RunSearch();
    }

    private void OnSessionChanged()
    {
        InvalidateMappingOperationIfStale();
        if (!_isSuggestionHistoryMutation) _suggestionRedoState = null;
        if (_destinationPreviewSemanticDigest is not null
            && !StringComparer.Ordinal.Equals(_destinationPreviewSemanticDigest, Session.Project.ComputeSemanticDigest()))
            ResetDestinationPreviewSummary("The project changed after this preview; rerun the dry run to refresh its estimate.");
        SyncSelectedNodeWithSession();
        Raise(nameof(Nodes));
        Raise(nameof(Edges));
        Raise(nameof(Session));
        Raise(nameof(SelectedNodeDisplayName));
        Raise(nameof(IsProjectDirty));
        RefreshTitle();
    }

    private void OnSessionSelectionChanged() => SyncSelectedNodeWithSession();

    private void SyncSelectedNodeWithSession()
    {
        var previousSelection = _selectedNode?.NodeId;
        var selectedNodeId = _session.SelectedNodeIds.Count == 1
            ? _session.SelectedNodeIds.Single()
            : null;
        _selectedNode = selectedNodeId is null
            ? null
            : _session.Nodes.FirstOrDefault(node => node.NodeId == selectedNodeId);
        var selectionChanged = !StringComparer.Ordinal.Equals(previousSelection, _selectedNode?.NodeId);

        if (_selectedNode is not null)
        {
            LoadSelectedFactProperties();
            if (selectionChanged && SelectedSuggestion is not null)
            {
                CaptureSelectedSuggestionDraft();
                LoadSelectedSuggestionEditor();
            }
        }
        if (!selectionChanged) return;
        Raise(nameof(SelectedNode));
        Raise(nameof(CanEditSelectedFact));
        Raise(nameof(SelectedNodeDisplayName));
    }

    private void RefreshTitle()
    {
        var projectName = Path.GetFileNameWithoutExtension(ProjectPath) is { Length: > 0 } fileName
            ? fileName
            : Session.Project.Name;
        var nextTitle = $"{projectName}{(IsProjectDirty ? " *" : string.Empty)} — XBRL Mapping Studio";
        if (StringComparer.Ordinal.Equals(_title, nextTitle)) return;
        _title = nextTitle;
        Raise(nameof(Title));
    }

    private void LoadSelectedFactProperties()
    {
        var node = _selectedNode is null ? null : Session.Project.Nodes.FirstOrDefault(n => n.NodeId == _selectedNode.NodeId);
        if (node?.NodeType != NodeTypeConstants.FactConstruct)
        {
            CanEditSelectedFactEntity = false;
            SelectedFactConceptIdentity = "Select a fact to inspect its taxonomy identity.";
            SelectedFactSourceSummary = "Select a fact to inspect its source mapping.";
            SelectedFactPeriodSummary = "Select a fact to inspect its period mapping.";
            SelectedFactEntitySourceSummary = "Select a fact to inspect its entity source.";
            return;
        }
        SelectedFactUnit = node.Parameters.GetValueOrDefault("unit")?.ToString() ?? "";
        SelectedFactPeriodType = node.Parameters.GetValueOrDefault("periodType")?.ToString() ?? "duration";
        SelectedFactDecimals = node.Parameters.GetValueOrDefault("decimals")?.ToString() ?? "0";
        SelectedFactDimensions = node.Parameters.GetValueOrDefault("dimensions")?.ToString() ?? "";

        var conceptName = node.Parameters.GetValueOrDefault("concept")?.ToString();
        var concept = conceptName is null ? null : _profile.FindConcept(conceptName);
        SelectedFactConceptIdentity = concept is null
            ? $"Concept '{conceptName ?? "(unset)"}' is unavailable in profile '{_profile.ProfileId}' ({_profile.Digest})."
            : $"{concept.Label} · {concept.QualifiedName} · {{{concept.Namespace}}}{concept.LocalName} · {concept.DataType}, {concept.PeriodType} · profile {_profile.ProfileId} ({_profile.Digest})";

        RefreshSelectedFactSourceSummary();
        SelectedFactPeriodSummary = DescribeFactInputSources(node, "period", "instantDate");
        LoadSelectedFactEntity(node);
    }

    private void RefreshSelectedFactSourceSummary()
    {
        var node = _selectedNode is null ? null : Session.Project.Nodes.FirstOrDefault(candidate => candidate.NodeId == _selectedNode.NodeId);
        if (node?.NodeType != NodeTypeConstants.FactConstruct)
        {
            SelectedFactSourceSummary = "Select a fact to inspect its source mapping.";
            return;
        }

        var selectedColumn = string.IsNullOrWhiteSpace(SelectedSourceColumn) ? "(none selected)" : SelectedSourceColumn;
        var valueSource = DescribeFactInputSources(node, "value");
        SelectedFactSourceSummary = $"Selected column: {selectedColumn}. Value path: {valueSource}";
    }

    private string DescribeFactInputSources(MappingNode factNode, params string[] inputPorts)
    {
        var edges = Session.Project.Edges
            .Where(edge => edge.TargetNodeId == factNode.NodeId && inputPorts.Contains(edge.TargetPortName, StringComparer.Ordinal))
            .OrderBy(edge => Array.IndexOf(inputPorts, edge.TargetPortName))
            .ToArray();
        if (edges.Length == 0) return $"No {string.Join(" or ", inputPorts)} source is connected.";
        return string.Join("; ", edges.Select(edge =>
            $"{edge.TargetPortName}: {DescribeNodePath(edge.SourceNodeId, new HashSet<string>(StringComparer.Ordinal))}"));
    }

    private string DescribeNodePath(string nodeId, HashSet<string> visiting)
    {
        const int maximumDepth = 128;
        return DescribeNodePath(nodeId, visiting, depth: 0, maximumDepth);
    }

    private string DescribeNodePath(string nodeId, HashSet<string> visiting, int depth, int maximumDepth)
    {
        if (depth >= maximumDepth) return $"Path truncated after {maximumDepth} nodes";
        var node = Session.Project.Nodes.FirstOrDefault(candidate => candidate.NodeId == nodeId);
        if (node is null) return $"Missing node {nodeId}";
        if (!visiting.Add(nodeId)) return $"Cycle at {node.DisplayName}";

        var parents = Session.Project.Edges
            .Where(edge => edge.TargetNodeId == nodeId)
            .OrderBy(edge => node.InputPorts.FindIndex(port => StringComparer.Ordinal.Equals(port.Name, edge.TargetPortName)))
            .Select(edge => DescribeNodePath(edge.SourceNodeId, visiting, depth + 1, maximumDepth))
            .ToArray();
        visiting.Remove(nodeId);

        var label = DescribeNode(node);
        return parents.Length == 0 ? label : $"{string.Join(" + ", parents)} → {label}";
    }

    private string DescribeNode(MappingNode node)
    {
        if (node.NodeType == NodeTypeConstants.ExcelField)
        {
            var column = node.Parameters.GetValueOrDefault("column")?.ToString() ?? "(unbound column)";
            var bindingId = node.Parameters.GetValueOrDefault("bindingId")?.ToString();
            var binding = Session.Project.Bindings.FirstOrDefault(candidate => candidate.BindingId == bindingId);
            return $"{binding?.DisplayName ?? bindingId ?? "Source"}.{column}";
        }
        if (node.NodeType == NodeTypeConstants.Constant)
            return $"{node.DisplayName}: {node.Parameters.GetValueOrDefault("literal")?.ToString() ?? "(empty)"}";

        var transformKeys = new[] { "factor", "culture", "rounding", "mode", "operator", "format", "scale" };
        var settings = transformKeys
            .Where(key => node.Parameters.ContainsKey(key))
            .Select(key => $"{key}={node.Parameters[key]}")
            .ToArray();
        return settings.Length == 0 ? node.DisplayName : $"{node.DisplayName} ({string.Join(", ", settings)})";
    }

    private void LoadSelectedFactEntity(MappingNode factNode)
    {
        var edge = Session.Project.Edges.FirstOrDefault(candidate =>
            candidate.TargetNodeId == factNode.NodeId && candidate.TargetPortName == "entity");
        var source = edge is null
            ? null
            : Session.Project.Nodes.FirstOrDefault(candidate => candidate.NodeId == edge.SourceNodeId);
        CanEditSelectedFactEntity = source?.NodeType == NodeTypeConstants.Constant
            && string.Equals(source.Parameters.GetValueOrDefault("shape")?.ToString(), "String", StringComparison.OrdinalIgnoreCase);
        SelectedFactEntityIdentifier = CanEditSelectedFactEntity
            ? source!.Parameters.GetValueOrDefault("literal")?.ToString() ?? ""
            : "";
        SelectedFactEntitySourceSummary = source is null
            ? "No entity source is connected. Add a String source before executing this mapping."
            : CanEditSelectedFactEntity
                ? $"Entity identifier comes from the String constant '{source.DisplayName}' and can be edited here."
                : $"Entity identifier is supplied by '{source.DisplayName}' ({source.NodeType}); select that source node to edit it.";
    }

    public async Task NewProjectAsync()
    {
        if (!await ConfirmUnsavedChangesAsync("create a new project")) return;
        ResetToNewProject("New project ready.");
    }

    public async Task CloseProjectAsync()
    {
        if (!await ConfirmUnsavedChangesAsync("close this project")) return;
        ResetWorkspace(BuildEmptyProject(), "Project closed.");
    }

    private void ResetToNewProject(string status)
        => ResetWorkspace(BuildSampleProject(), status);

    private void ResetWorkspace(MappingProject project, string status)
    {
        _session.Changed -= OnSessionChanged;
        _session.SelectionChanged -= OnSessionSelectionChanged;
        RefreshProfileForProject(project);
        _session = new DesignerSession(project);
        _session.Changed += OnSessionChanged;
        _session.SelectionChanged += OnSessionSelectionChanged;
        SelectedNode = null;
        SelectedDestinationKind = SqliteDestinationKind;
        ProjectPath = null;
        _expectedLayoutGeneration = null;
        _savedProjectSnapshot = ProjectSerializer.Serialize(project);
        ClearSourceAndRunResults();
        OnSessionChanged();
        RunSearch();
        StatusText = status;
    }

    public async Task OpenProjectAsync()
    {
        if (_fileDialog is null) { StatusText = "Project file picker is unavailable."; return; }
        try
        {
            var path = await _fileDialog.OpenProjectAsync();
            if (path is null) return;

            var manager = new ProjectManager(Path.GetFullPath(path));
            var project = manager.Load(); // Validate the requested file before prompting to replace the current workspace.
            var reopeningCurrentProject = PathsEqual(ProjectPath, manager.FullPath);
            if (reopeningCurrentProject && !IsProjectDirty) return;
            if (!await ConfirmUnsavedChangesAsync(reopeningCurrentProject
                    ? "reopen the current project"
                    : "open another project")) return;
            if (reopeningCurrentProject) project = manager.Load();
            var profileAvailable = ActivateProject(manager, project);
            await LoadWorkspaceBindingsAsync(profileAvailable);
        }
        catch (Exception ex) { StatusText = $"Open failed: {ex.Message}"; }
    }

    public async Task<bool> ConfirmCloseApplicationAsync()
        => await ConfirmUnsavedChangesAsync("close the application");

    private async Task<bool> ConfirmUnsavedChangesAsync(string action)
    {
        if (!IsProjectDirty) return true;
        if (_fileDialog is null)
        {
            StatusText = $"Cannot {action}: the current project has unsaved changes and no confirmation dialog is available.";
            return false;
        }

        var choice = await _fileDialog.ConfirmUnsavedProjectChangesAsync(
            Path.GetFileNameWithoutExtension(ProjectPath ?? Session.Project.Name), action);
        if (choice == UnsavedProjectChoice.Discard) return true;
        if (choice == UnsavedProjectChoice.Save) return await SaveProjectAsync();

        StatusText = $"Canceled {action}; the active project is unchanged.";
        return false;
    }

    private static bool PathsEqual(string? left, string? right)
        => left is not null && right is not null
           && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

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

    public async Task ChooseDestinationAsync()
    {
        if (ProjectPath is null)
        {
            StatusText = "Save the project inside its workspace before configuring a destination.";
            return;
        }
        if (_fileDialog is null)
        {
            StatusText = "Destination picker is unavailable.";
            return;
        }

        try
        {
            if (IsSqliteDestination)
            {
                var path = await _fileDialog.OpenDestinationDatabaseAsync();
                if (path is not null) await LoadDestinationDatabaseAsync(path, preferredTable: null, preferredKey: null);
            }
            else
            {
                var path = await _fileDialog.SaveDestinationFileAsync(SelectedDestinationKind.Kind);
                if (path is not null) await LoadDestinationFileAsync(path);
            }
        }
        catch (Exception ex)
        {
            ResetDestinationSchemaConfirmation($"Destination load failed: {ex.Message}");
            StatusText = "Destination could not be loaded.";
        }
    }

    public Task LoadDestinationFileAsync(string path)
    {
        if (!IsFileDestination)
        {
            StatusText = "Select a file destination kind before choosing an output path.";
            return Task.CompletedTask;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = WorkspaceRoot();
            _ = PathGuard.EnsureWithin(root, fullPath);
            var pathDiagnostics = DestinationPlanValidator.ValidateFileDestinationPath(fullPath);
            if (pathDiagnostics.HasBlockingErrors)
                throw new InvalidOperationException(string.Join(" ", pathDiagnostics.Items.Select(item => item.Message)));
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null || !Directory.Exists(directory) || Directory.Exists(fullPath))
                throw new IOException("The output file must be inside an existing workspace directory.");

            Interlocked.Increment(ref _destinationLoadGeneration);
            InvalidateMappingOperationIfStale();
            _destinationPath = fullPath;
            _destinationTable = null;
            _selectedDestinationKey = null;
            _destinationTableSchema = null;
            _preferredDestinationKey = null;
            DestinationTables.Clear();
            DestinationKeys.Clear();
            Raise(nameof(DestinationPath));
            Raise(nameof(DestinationPathDisplay));
            Raise(nameof(DestinationTable));
            Raise(nameof(DestinationTables));
            Raise(nameof(HasDestinationTables));
            Raise(nameof(DestinationKeys));
            Raise(nameof(SelectedDestinationKey));
            IsDestinationLoading = false;
            DestinationSchemaIsValid = true;
            IsDestinationSchemaConfirmed = false;
            DestinationSchemaStatus = $"Ready to publish {DestinationFormatDisplay} at the selected path.";
            ResetDestinationPreviewSummary("The output path or format changed; rerun the dry run before publication.");
            StatusText = "Review the output path and format before saving the destination plan.";
        }
        catch (Exception ex)
        {
            _destinationPath = null;
            DestinationSchemaIsValid = false;
            IsDestinationSchemaConfirmed = false;
            DestinationSchemaStatus = $"File destination is invalid: {ex.Message}";
            StatusText = "File destination could not be selected.";
            Raise(nameof(DestinationPath));
            Raise(nameof(DestinationPathDisplay));
        }

        NotifyDestinationCommands();
        return Task.CompletedTask;
    }

    private async Task LoadDestinationDatabaseAsync(
        string path,
        string? preferredTable,
        IReadOnlyList<string>? preferredKey)
    {
        var fullPath = Path.GetFullPath(path);
        var generation = Interlocked.Increment(ref _destinationLoadGeneration);
        InvalidateMappingOperationIfStale();
        _destinationPath = fullPath;
        _preferredDestinationKey = preferredKey;
        Raise(nameof(DestinationPath));
        Raise(nameof(DestinationPathDisplay));
        DestinationTables.Clear();
        DestinationKeys.Clear();
        _destinationTableSchema = null;
        _destinationTable = null;
        _selectedDestinationKey = null;
        Raise(nameof(DestinationTables));
        Raise(nameof(HasDestinationTables));
        Raise(nameof(DestinationTable));
        Raise(nameof(DestinationKeys));
        Raise(nameof(SelectedDestinationKey));
        ResetDestinationSchemaConfirmation("Reading destination tables…");
        IsDestinationLoading = true;
        try
        {
            var tables = await _destinationInspector.DiscoverTablesAsync(fullPath);
            if (generation != _destinationLoadGeneration) return;
            ReplaceItems(DestinationTables, tables);
            Raise(nameof(DestinationTables));
            Raise(nameof(HasDestinationTables));
            if (tables.Count == 0)
            {
                DestinationSchemaStatus = "This database has no user tables.";
                StatusText = DestinationSchemaStatus;
                return;
            }

            var selectedTable = preferredTable is not null && tables.Contains(preferredTable, StringComparer.Ordinal)
                ? preferredTable
                : tables.Count == 1 ? tables[0] : null;
            if (selectedTable is null)
            {
                DestinationSchemaStatus = "Choose a table to inspect its fact columns and unique keys.";
                StatusText = DestinationSchemaStatus;
                return;
            }

            DestinationTable = selectedTable;
            await _destinationSchemaTask;
        }
        catch (Exception ex)
        {
            if (generation != _destinationLoadGeneration) return;
            ResetDestinationSchemaConfirmation($"Destination inspection failed: {ex.Message}");
            StatusText = "SQLite destination inspection failed.";
        }
        finally
        {
            if (generation == _destinationLoadGeneration) IsDestinationLoading = false;
        }
    }

    public Task LoadDestinationAsync(string path)
    {
        if (!IsSqliteDestination) SelectedDestinationKind = SqliteDestinationKind;
        return LoadDestinationDatabaseAsync(path, preferredTable: null, preferredKey: null);
    }

    private async Task LoadDestinationTableSchemaAsync(string table, int generation)
    {
        try
        {
            if (_destinationPath is null) return;
            var schema = await _destinationInspector.InspectTableAsync(_destinationPath, table);
            if (generation != _destinationLoadGeneration || table != DestinationTable) return;
            _destinationTableSchema = schema;
            DestinationKeys.Clear();
            var outputColumns = XbrlFactRecordMapper.OutputColumns.ToHashSet(StringComparer.Ordinal);
            var options = schema.UniqueKeys
                .Where(key => key.Count > 0 && key.All(outputColumns.Contains))
                .Select(key => new DestinationKeyOption(key.ToArray()))
                .OrderByDescending(key => key.Columns.SequenceEqual(new[] { "fact_key" }, StringComparer.Ordinal))
                .ThenBy(key => key.Display, StringComparer.Ordinal)
                .ToArray();
            ReplaceItems(DestinationKeys, options);
            Raise(nameof(DestinationKeys));
            Raise(nameof(CanSelectDestinationKey));

            var preferred = _preferredDestinationKey;
            var selected = preferred is null ? null : options.FirstOrDefault(option =>
                option.Columns.SequenceEqual(preferred, StringComparer.Ordinal));
            _preferredDestinationKey = null;
            SelectedDestinationKey = selected
                ?? options.FirstOrDefault(option => option.Columns.SequenceEqual(new[] { "fact_key" }, StringComparer.Ordinal))
                ?? options.FirstOrDefault();
            if (DestinationKeys.Count == 0)
            {
                DestinationSchemaStatus = "No unique key uses supported XBRL fact columns. Choose a table with a unique fact key.";
                StatusText = DestinationSchemaStatus;
            }
        }
        catch (Exception ex)
        {
            if (generation != _destinationLoadGeneration) return;
            _destinationTableSchema = null;
            DestinationKeys.Clear();
            Raise(nameof(DestinationKeys));
            Raise(nameof(CanSelectDestinationKey));
            ResetDestinationSchemaConfirmation($"Table inspection failed: {ex.Message}");
            StatusText = "SQLite destination table inspection failed.";
        }
        finally
        {
            if (generation == _destinationLoadGeneration) IsDestinationLoading = false;
        }
    }

    private void ValidateSelectedDestinationSchema()
    {
        if (IsFileDestination)
        {
            DestinationSchemaIsValid = false;
            DestinationSchemaStatus = _destinationPath is null
                ? "Choose an output file in the workspace."
                : $"Review the output path and {DestinationPlanValidator.FileFormatFor(SelectedDestinationKind.Kind)} contract.";
            return;
        }

        var plan = BuildSelectedDestinationPlan();
        if (plan is null || _destinationTableSchema is null)
        {
            DestinationSchemaIsValid = false;
            if (_destinationPath is null)
                DestinationSchemaStatus = "Choose an existing SQLite database to inspect its tables.";
            else if (DestinationTable is null)
                DestinationSchemaStatus = "Choose a table to inspect its fact columns and unique keys.";
            else if (SelectedDestinationKey is null)
                DestinationSchemaStatus = "Select an available unique business key.";
            return;
        }

        var diagnostics = DestinationPlanValidator.ValidateSchema(plan, _destinationTableSchema);
        if (diagnostics.HasBlockingErrors)
        {
            DestinationSchemaIsValid = false;
            DestinationSchemaStatus = string.Join(" ", diagnostics.Items.Select(item => item.Message));
            return;
        }

        DestinationSchemaIsValid = true;
        DestinationSchemaStatus = $"Compatible fact schema. Exact decimals use TEXT; business key '{SelectedDestinationKey?.Display ?? "(none)"}' is unique.";
    }

    private void ResetDestinationSchemaConfirmation(string message)
    {
        DestinationSchemaIsValid = false;
        IsDestinationSchemaConfirmed = false;
        DestinationSchemaStatus = message;
    }

    private DestinationPlan? BuildSelectedDestinationPlan()
    {
        if (_destinationPath is null) return null;
        if (IsSqliteDestination)
        {
            return DestinationTable is null || SelectedDestinationKey is null
                ? null
                : new DestinationPlan
                {
                    Kind = DestinationPlanValidator.SupportedKind,
                    Locator = _destinationPath,
                    Table = DestinationTable,
                    BusinessKey = SelectedDestinationKey.Columns.ToList()
                };
        }

        if (!IsFileDestination) return null;
        return new DestinationPlan
        {
            Kind = SelectedDestinationKind.Kind,
            Locator = _destinationPath,
            Table = string.Empty,
            BusinessKey = new List<string>(),
            Format = DestinationPlanValidator.FileFormatFor(SelectedDestinationKind.Kind)!,
            WriteMode = "replace"
        };
    }

    private bool CanUseSelectedDestination
    {
        get
        {
            if (!MappingJobService.DescribeTargetSuitability(
                    new SourceConnection("destination", SelectedDestinationKind.Kind, Path: _destinationPath)).Allowed)
                return false;
            if (ProjectPath is null || IsDestinationLoading || _destinationPath is null
                || !DestinationSchemaIsValid || !IsDestinationSchemaConfirmed)
                return false;
            if (IsSqliteDestination)
                return File.Exists(_destinationPath) && DestinationTable is not null
                    && SelectedDestinationKey is not null;
            var directory = Path.GetDirectoryName(_destinationPath);
            return IsFileDestination && directory is not null && Directory.Exists(directory)
                && !Directory.Exists(_destinationPath);
        }
    }

    private bool IsSavedDestinationCurrent
    {
        get
        {
            var selected = BuildSelectedDestinationPlan();
            var saved = Session.Project.Destination;
            if (selected is null || saved is null || ProjectPath is null) return false;
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
            var savedPath = Path.IsPathRooted(saved.Locator)
                ? Path.GetFullPath(saved.Locator)
                : Path.GetFullPath(Path.Combine(projectDirectory, saved.Locator));
            return PathEquals(savedPath, selected.Locator)
                && StringComparer.Ordinal.Equals(saved.Kind, selected.Kind)
                && StringComparer.Ordinal.Equals(saved.Table, selected.Table)
                && saved.BusinessKey is not null
                && saved.BusinessKey.SequenceEqual(selected.BusinessKey, StringComparer.Ordinal)
                && StringComparer.Ordinal.Equals(saved.Format, selected.Format)
                && StringComparer.Ordinal.Equals(saved.WriteMode, selected.WriteMode)
                && StringComparer.Ordinal.Equals(saved.Compatibility, selected.Compatibility);
        }
    }

    private static bool PathEquals(string left, string right)
        => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private void NotifyDestinationCommands()
    {
        SaveDestinationPlanCommand?.NotifyCanExecuteChanged();
        PreviewDestinationCommand?.NotifyCanExecuteChanged();
        PublishDestinationCommand?.NotifyCanExecuteChanged();
        ChooseDestinationCommand?.NotifyCanExecuteChanged();
        Raise(nameof(CanSaveDestinationPlan));
        Raise(nameof(CanPreviewDestination));
        Raise(nameof(CanPublishDestination));
        Raise(nameof(CanSelectDestinationKey));
        Raise(nameof(DestinationFormatDisplay));
        Raise(nameof(DestinationPickerButtonText));
        Raise(nameof(DestinationPreviewButtonText));
        Raise(nameof(DestinationPublishButtonText));
        Raise(nameof(DestinationGuidance));
        Raise(nameof(DestinationCapabilityStatus));
    }

    private void SaveDestinationPlan()
    {
        if (ProjectPath is null)
        {
            StatusText = "Save the project inside its workspace before saving a destination plan.";
            return;
        }
        var plan = BuildSelectedDestinationPlan();
        if (plan is null || !CanUseSelectedDestination
            || (IsSqliteDestination && _destinationTableSchema is null))
        {
            StatusText = IsSqliteDestination
                ? "Confirm a compatible destination table and unique key before saving."
                : "Confirm a valid output path and format before saving.";
            return;
        }
        var diagnostics = IsSqliteDestination
            ? DestinationPlanValidator.ValidateSchema(plan, _destinationTableSchema!)
            : DestinationPlanValidator.Validate(plan);
        if (diagnostics.HasBlockingErrors)
        {
            ShowDestinationDiagnostics(diagnostics.Items);
            StatusText = "Destination plan was not saved because the target schema is incompatible.";
            return;
        }

        try
        {
            Session.SetDestination(plan);
            SaveProjectTo(ProjectPath);
            StatusText = IsSqliteDestination
                ? $"Saved SQLite target {plan.Table}. A reviewer must approve this destination-bound revision before publication."
                : $"Saved {SelectedDestinationKind.Display} output. A reviewer must approve this destination-bound revision before publication.";
            NotifyDestinationCommands();
        }
        catch (Exception ex)
        {
            StatusText = $"Destination plan could not be saved: {ex.Message}";
        }
    }

    public async Task PreviewDestinationAsync()
    {
        if (IsBusy || !CanUseSelectedDestination) return;
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        var plan = BuildSelectedDestinationPlan()!;
        var project = Session.Project;
        var semanticDigest = project.ComputeSemanticDigest();
        ResetDestinationPreviewSummary("Running a full dry run; the selected destination will not be changed…");
        var operation = BeginMappingOperation(profile, IsSqliteDestination
            ? "Running a full destination dry run; no database rows will be written…"
            : "Running a full destination dry run; no file will be written…");
        var cancellationToken = operation.Cancellation.Token;
        try
        {
            var root = WorkspaceRoot();
            var connections = BuildConnections(root);
            await using var worker = _workerFactory.Create(profile);
            var service = new MappingJobService(profile, worker);
            using var result = await service.RunAsync(
                project, connections, ReadParameters(), preview: false, cancellationToken,
                CreateMappingProgress(operation));
            if (!IsMappingOperationCurrent(operation))
            {
                StatusText = OperationStoppedStatus(operation, "Destination dry run");
                ResetDestinationPreviewSummary(StatusText);
                return;
            }
            if (!result.IsPublishable)
            {
                ShowRunResult(result);
                ResetDestinationPreviewSummary($"No destination estimate is available for the {result.Manifest.Outcome} run; no writes were made.");
                StatusText = result.Manifest.Outcome == RunOutcome.Cancelled
                    ? "Destination dry run canceled by the validation worker. No destination writes were made."
                    : IsSqliteDestination
                        ? $"Destination dry run ended with {result.Manifest.Outcome}. No database rows were written."
                        : $"Destination dry run ended with {result.Manifest.Outcome}. No file was written.";
                return;
            }

            if (IsSqliteDestination)
            {
                var estimate = await service.PreviewDestinationChangesAsync(
                    project, result, root, cancellationToken);
                if (!IsMappingOperationCurrent(operation))
                {
                    StatusText = OperationStoppedStatus(operation, "Destination dry run");
                    ResetDestinationPreviewSummary(StatusText);
                    return;
                }
                ShowRunResult(result);
                if (estimate.SchemaDiscrepancies.Count > 0)
                {
                    DestinationPreviewSummary = "Preview blocked by destination schema discrepancies: "
                        + string.Join(" ", estimate.SchemaDiscrepancies) + " No rows were written.";
                    _destinationPreviewSemanticDigest = null;
                    StatusText = "Dry run found destination schema problems. No database rows were written.";
                    return;
                }

                DestinationPreviewSummary = FormatDestinationPreview(estimate);
                _destinationPreviewSemanticDigest = semanticDigest;
                StatusText = $"Dry run complete: {estimate.Inserts} insert(s), {estimate.Updates} upsert update(s), "
                    + $"including {estimate.UnchangedValueUpdates} with unchanged values, and {estimate.Conflicts} duplicate-key conflict(s). "
                    + "No database rows were written. The estimate is read-only; publication rechecks the source run and target schema.";
                return;
            }

            var diagnostics = DestinationPlanValidator.Validate(plan);
            if (diagnostics.HasBlockingErrors)
            {
                ShowDestinationDiagnostics(diagnostics.Items);
                DestinationPreviewSummary = "Preview blocked by output-contract discrepancies: "
                    + string.Join(" ", diagnostics.Items.Select(item => item.Message)) + " No file was written.";
                _destinationPreviewSemanticDigest = null;
                StatusText = "Dry run found an invalid file destination contract. No file was written.";
                return;
            }

            ShowRunResult(result);
            DestinationPreviewSummary = $"{result.Facts.Count} fact row(s) fit {plan.Format}. The existing file was not read or changed during dry run; publication stages and validates a replacement.";
            _destinationPreviewSemanticDigest = semanticDigest;
            StatusText = $"Dry run complete: {result.Facts.Count} fact row(s) fit {plan.Format} at {Path.GetFileName(_destinationPath)}. No file was written. Publication requires a current approval and final confirmation.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Destination dry run");
            ResetDestinationPreviewSummary(StatusText);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Destination dry run");
            ResetDestinationPreviewSummary(StatusText);
        }
        catch (Exception ex)
        {
            ShowDestinationError(ex);
            ResetDestinationPreviewSummary($"Destination preview failed: {ex.Message} No writes were made.");
            StatusText = IsSqliteDestination
                ? "Destination dry run failed; no database rows were written."
                : "Destination dry run failed; no file was written.";
        }
        finally { FinishMappingOperation(operation); }
    }

    private static string FormatDestinationPreview(SqliteTargetChangePreview preview)
    {
        var completeness = preview.CountsAreComplete
            ? $"Inspected all {preview.InspectedRows} candidate row(s)."
            : $"Inspected {preview.InspectedRows} of {preview.CandidateRows}; {preview.UncertainRows} row(s) are uninspected.";
        var sample = preview.Samples.Count == 0
            ? "No outcome sample is available."
            : "Sample: " + string.Join(", ", preview.Samples.Select(item => $"row {item.CandidateRow} {item.Outcome}")) + ".";
        var conflictNotice = preview.Conflicts > 0
            ? " Duplicate candidate keys block the whole publication batch until resolved."
            : "";
        return $"Candidate estimate: {preview.Inserts} insert(s), {preview.Updates} upsert update(s), including "
            + $"{preview.UnchangedValueUpdates} with unchanged values, and {preview.Conflicts} duplicate-key conflict(s). "
            + $"{completeness} Scan limit: {SqliteTargetPublisher.MaximumPreviewRows} rows or "
            + $"{SqliteTargetPublisher.MaximumPreviewBytes / (1024 * 1024)} MiB of field values; sample limit: {preview.SampleLimit}. {sample}"
            + conflictNotice + " This read-only estimate is not a receipt; publication rechecks the run and target schema.";
    }

    public async Task PublishDestinationAsync()
    {
        if (IsBusy || IsSourceLoading) return;
        var project = Session.Project;
        var plan = project.Destination;
        if (plan is null || !CanUseSelectedDestination || !IsSavedDestinationCurrent)
        {
            StatusText = "Save and confirm the current destination plan before publication.";
            return;
        }
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        var gate = PublicationGate.Evaluate(project, profile, AllowSyntheticProfile);
        if (gate.HasBlockingErrors)
        {
            ShowDestinationDiagnostics(gate.Items);
            StatusText = "Publication blocked. The current destination-bound revision requires approval.";
            return;
        }
        if (_fileDialog is null)
        {
            StatusText = "Publication confirmation is unavailable.";
            return;
        }

        var operation = BeginMappingOperation(profile, "Executing the approved mapping before publication…");
        var cancellationToken = operation.Cancellation.Token;
        try
        {
            var root = WorkspaceRoot();
            var connections = BuildConnections(root);
            await using var worker = _workerFactory.Create(profile);
            var service = new MappingJobService(profile, worker);
            using var result = await service.RunAsync(project, connections, ReadParameters(),
                preview: false, cancellationToken, CreateMappingProgress(operation));
            if (!IsMappingOperationCurrent(operation))
            {
                StatusText = OperationStoppedStatus(operation, "Destination publication");
                return;
            }
            if (!result.IsPublishable)
            {
                ShowRunResult(result);
                StatusText = result.Manifest.Outcome == RunOutcome.Cancelled
                    ? "Execution canceled by the validation worker. The destination was not changed."
                    : IsSqliteDestination
                        ? $"Execution ended with {result.Manifest.Outcome}. The SQLite destination was not changed."
                        : $"Execution ended with {result.Manifest.Outcome}. The file destination was not changed.";
                return;
            }

            var diagnostics = IsSqliteDestination
                ? DestinationPlanValidator.ValidateSchema(plan,
                    await _destinationInspector.InspectTableAsync(_destinationPath!, plan.Table, cancellationToken))
                : DestinationPlanValidator.Validate(plan);
            if (!IsMappingOperationCurrent(operation))
            {
                StatusText = OperationStoppedStatus(operation, "Destination publication");
                return;
            }
            if (diagnostics.HasBlockingErrors)
            {
                ShowDestinationDiagnostics(diagnostics.Items);
                StatusText = IsSqliteDestination
                    ? "The destination schema changed or is incompatible. No database rows were written."
                    : "The file destination contract is invalid. No file was written.";
                return;
            }

            var confirmed = IsSqliteDestination
                ? await _fileDialog.ConfirmDestinationPublicationAsync(
                    _destinationPath!, plan.Table, plan.BusinessKey, result.Facts.Count, project.ComputeSemanticDigest())
                : await _fileDialog.ConfirmFileDestinationPublicationAsync(
                    _destinationPath!, plan.Format, result.Facts.Count, project.ComputeSemanticDigest());
            if (!IsMappingOperationCurrent(operation))
            {
                StatusText = OperationStoppedStatus(operation, "Destination publication");
                return;
            }
            if (!confirmed)
            {
                ShowRunResult(result);
                StatusText = IsSqliteDestination
                    ? "Publication cancelled. The SQLite destination was not changed."
                    : "Publication cancelled. The file destination was not changed.";
                return;
            }

            ShowRunResult(result);
            if (IsSqliteDestination)
            {
                var receipt = await service.PublishDestinationAsync(
                    project, result, root, AllowSyntheticProfile, cancellationToken);
                StatusText = $"Published {receipt.WrittenRows} fact row(s) to {plan.Table}; receipt {receipt.OperationKey}.";
            }
            else
            {
                var output = await service.PublishFileDestinationAsync(
                    project, result, root, AllowSyntheticProfile, cancellationToken);
                StatusText = $"Published {output.Kind} file {Path.GetFileName(output.Path)}; digest {output.Digest}.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Destination publication");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Destination publication");
        }
        catch (Exception ex)
        {
            ShowDestinationError(ex);
            StatusText = IsSqliteDestination
                ? "SQLite publication failed; inspect diagnostics and reconcile the destination."
                : "File publication failed; inspect diagnostics and reconcile the destination.";
        }
        finally { FinishMappingOperation(operation); }
    }

    private void ShowDestinationDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostics.Clear();
        ReplaceItems(Diagnostics, diagnostics.Select(ToDiagnosticRow));
        Raise(nameof(Diagnostics));
    }

    private void ShowDestinationError(Exception exception)
    {
        Diagnostics.Clear();
        Diagnostics.Add(new DiagnosticRow { Display = exception.Message, Severity = "error" });
        Raise(nameof(Diagnostics));
    }

    private ActiveMappingOperation BeginMappingOperation(FilingProfile profile, string initialStatus)
    {
        var operation = new ActiveMappingOperation(
            ++_mappingOperationGeneration,
            new CancellationTokenSource(),
            Session.Project,
            Session.Project.ComputeSemanticDigest(),
            _sourceLoadGeneration,
            _destinationLoadGeneration,
            CurrentDestinationSelectionKey(),
            AllowSyntheticProfile,
            IsDestinationSchemaConfirmed,
            DestinationSchemaIsValid,
            profile.Digest);
        _activeMappingOperation = operation;
        IsBusy = true;
        StatusText = initialStatus;
        return operation;
    }

    private void CancelMappingOperation()
    {
        var operation = _activeMappingOperation;
        if (operation is null || operation.Cancellation.IsCancellationRequested) return;
        operation.Cancellation.Cancel();
        Raise(nameof(CanCancelOperation));
        Raise(nameof(CancelOperationButtonText));
        CancelOperationCommand.NotifyCanExecuteChanged();
        StatusText = "Cancellation requested; waiting for the active operation to stop…";
    }

    private bool IsMappingOperationCurrent(ActiveMappingOperation operation)
    {
        if (!ReferenceEquals(_activeMappingOperation, operation)
            || operation.Generation != _mappingOperationGeneration
            || operation.InvalidationReason is not null)
            return false;

        string? reason = null;
        if (!ReferenceEquals(operation.Project, Session.Project)
            || !StringComparer.Ordinal.Equals(operation.ProjectDigest, Session.Project.ComputeSemanticDigest()))
            reason = "the mapping changed";
        else if (operation.SourceGeneration != _sourceLoadGeneration)
            reason = "the selected source changed";
        else if (operation.DestinationGeneration != _destinationLoadGeneration
            || !StringComparer.Ordinal.Equals(operation.DestinationSelection, CurrentDestinationSelectionKey()))
            reason = "the selected destination changed";
        else if (!StringComparer.Ordinal.Equals(operation.ProfileDigest, _profile.Digest))
            reason = "the filing profile changed";
        else if (operation.SyntheticProfileAuthorization != AllowSyntheticProfile)
            reason = "the synthetic-profile authorization setting changed";
        else if (operation.DestinationSchemaConfirmed != IsDestinationSchemaConfirmed
            || operation.DestinationSchemaValid != DestinationSchemaIsValid)
            reason = "the destination review state changed";

        if (reason is not null && !operation.Cancellation.IsCancellationRequested)
        {
            operation.InvalidationReason = reason;
            operation.Cancellation.Cancel();
            Raise(nameof(CanCancelOperation));
            Raise(nameof(CancelOperationButtonText));
            CancelOperationCommand.NotifyCanExecuteChanged();
            StatusText = $"Stopping because {reason}; the stale result will not be applied or published…";
            return false;
        }

        return reason is null && !operation.Cancellation.IsCancellationRequested;
    }

    private void InvalidateMappingOperationIfStale()
    {
        if (_activeMappingOperation is { } operation)
            IsMappingOperationCurrent(operation);
    }

    private string OperationStoppedStatus(ActiveMappingOperation operation, string action)
        => operation.InvalidationReason is { } reason
            ? $"{action} stopped because {reason}. No stale result was applied or published."
            : $"{action} canceled. No output was published.";

    private void FinishMappingOperation(ActiveMappingOperation operation)
    {
        if (ReferenceEquals(_activeMappingOperation, operation))
        {
            _activeMappingOperation = null;
            IsBusy = false;
        }
        operation.Cancellation.Dispose();
    }

    private IProgress<MappingJobProgress> CreateMappingProgress(ActiveMappingOperation operation)
        => new Progress<MappingJobProgress>(progress =>
        {
            if (!IsMappingOperationCurrent(operation)) return;
            if (progress.Stage.StartsWith("Run ended:", StringComparison.Ordinal)) return;
            StatusText = progress.Total is int total && progress.Current is int current
                ? $"{progress.Stage} ({current:N0} of {total:N0})"
                : progress.Current is int count
                    ? $"{progress.Stage} ({count:N0} row(s))"
                    : progress.Stage;
        });

    private string CurrentDestinationSelectionKey()
        => string.Join("\u001f", SelectedDestinationKind.Kind, _destinationPath ?? "", _destinationTable ?? "",
            string.Join("\u001e", SelectedDestinationKey?.Columns ?? Array.Empty<string>()));

    public async Task LoadSourceAsync(string path)
    {
        if (ProjectPath is null) { StatusText = "Save the project inside its workspace before adding a source."; return; }
        var generation = Interlocked.Increment(ref _sourceLoadGeneration);
        InvalidateMappingOperationIfStale();
        IsSourceLoading = true;
        var sqliteTableLoadPending = false;
        try
        {
            var root = WorkspaceRoot();
            var safePath = PathGuard.EnsureWithin(root, path);
            var binding = Session.Project.Bindings.FirstOrDefault(b => b.BindingId == SelectedSourceBindingId)
                ?? Session.Project.Bindings.FirstOrDefault(b => b.Kind != "param");
            if (binding is null) { StatusText = "Add a source binding to the project before choosing a file."; return; }
            var kind = Path.GetExtension(safePath).ToLowerInvariant() switch
            {
                ".xlsx" => "excel",
                ".csv" => "csv",
                ".json" => safePath.EndsWith(".xbrl.json", StringComparison.OrdinalIgnoreCase) ? "xbrl-report" : "json",
                ".xml" => "xml",
                ".pdf" => "pdf-digital",
                ".db" or ".sqlite" or ".sqlite3" => "sqlite",
                _ => throw new InvalidOperationException("The selected file type is not supported.")
            };
            if (kind != "pdf-digital") ResetPdfReviewState();
            binding = binding with
            {
                Kind = kind,
                Locator = Path.GetRelativePath(root, safePath),
                SchemaFingerprint = null
            };
            _sqliteSourcePath = kind == "sqlite" ? safePath : null;
            SourceTables.Clear();
            SourceColumns.Clear();
            SourceInspectionSummary = kind == "sqlite"
                ? "Choose a SQLite table to inspect its primary key, decimal rules and read-only capabilities."
                : kind == "pdf-digital"
                    ? "Digital PDF text is a review proposal. It is not a verified report or approved financial data."
                    : $"Source type: {kind}.";
            _selectedSourceTable = null;
            Raise(nameof(SelectedSourceTable));
            Raise(nameof(SourceColumns));
            Raise(nameof(SourceTables));
            Raise(nameof(HasSourceTables));
            var config = binding.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal);
            var connection = new SourceConnection(binding.BindingId, kind, safePath,
                config);
            if (kind == "pdf-digital")
            {
                SelectedSourceBindingId = binding.BindingId;
                Session.SetBinding(binding, clearBindingSchemasFromHistory: true);
                await LoadPdfDocumentAsync(safePath, connection, binding, generation);
                return;
            }
            if (kind == "sqlite")
            {
                SelectedSourceBindingId = binding.BindingId;
                var sqlite = new SqliteSourceAdapter();
                var tables = await sqlite.DiscoverTablesAsync(connection, CancellationToken.None);
                if (generation != _sourceLoadGeneration) return;
                ReplaceItems(SourceTables, tables);
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
            ReplaceItems(SourceColumns, schema.Columns.Select(column => column.Name));
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

    private async Task LoadPdfDocumentAsync(
        string path,
        SourceConnection connection,
        SourceBinding binding,
        int generation)
    {
        ResetPdfReviewState();
        _pdfSourcePath = path;
        PdfReviewStatus = "Digital text extraction is a proposal for review. It is not a verified report or approved financial data.";
        var document = await _pdfDocumentLoader(path, CancellationToken.None);
        if (generation != _sourceLoadGeneration) return;

        _pdfDocument = document;
        foreach (var page in document.Pages) PdfPageNumbers.Add(page.PageNumber);
        Raise(nameof(HasPdfDocument));
        Raise(nameof(CanEditPdfReview));
        Raise(nameof(PdfPageCount));
        Raise(nameof(CanOpenSelectedPreviewEvidence));
        OpenSelectedPreviewEvidenceCommand?.NotifyCanExecuteChanged();
        CurrentPdfPageNumber = 1;
        RefreshPdfPageWords();
        PdfReviewStatus = $"Digital text preview loaded from {document.Pages.Count} pages. This extraction is not a verified report; mapping requires an explicit reviewed sidecar.";
        SourceInspectionSummary = $"Digital PDF: {document.Pages.Count} pages, {document.SourceByteLength:N0} bytes. Text and positions are review evidence only.";
        Raise(nameof(PdfPageSummary));

        var reviewPath = PdfSourceAdapter.ResolveReviewManifestPath(connection);
        if (!File.Exists(reviewPath))
        {
            Session.SetBinding(binding with { SchemaFingerprint = null }, clearBindingSchemasFromHistory: true);
            return;
        }

        try
        {
            var pdfAdapter = new PdfSourceAdapter(_pdfEvidenceStoreFactory());
            var manifest = pdfAdapter.ReadReviewedSourceManifest(connection, CancellationToken.None);
            if (!RestorePdfReviewManifest(manifest))
                throw new InvalidDataException("The approved sidecar does not match the current declared template and extracted values.");
            if (generation != _sourceLoadGeneration) return;

            var hasNewerAudit = _pdfReviewAuditEvents.Any(entry => entry.OccurredUtc > manifest.ReviewedUtc);
            if (hasNewerAudit)
            {
                Session.SetBinding(binding with { SchemaFingerprint = null }, clearBindingSchemasFromHistory: true);
                SourceColumns.Clear();
                Raise(nameof(SourceColumns));
                SourceInspectionSummary = "The PDF is evidence-validated, but newer local review actions must be saved in a new sidecar before mapping.";
                PdfReviewStatus = "Restored newer correction and approval history from local state. Save this review to activate mapping; reviewer identity is recorded but not authenticated.";
                StatusText = "Restored unsaved PDF review decisions; mapping remains blocked until the review is saved.";
                return;
            }

            var schema = await pdfAdapter.InspectSchemaAsync(connection, CancellationToken.None);
            if (generation != _sourceLoadGeneration) return;
            ReplaceItems(SourceColumns, schema.Columns.Select(column => column.Name));
            Session.SetBinding(binding with { SchemaFingerprint = schema.Fingerprint }, clearBindingSchemasFromHistory: true);
            Raise(nameof(SourceColumns));
            SourceInspectionSummary = $"Reviewed digital PDF schema: {schema.Columns.Count} typed fields. Source and persisted evidence matched the configured approval sidecar.";
            PdfReviewStatus = "The configured sidecar passes source and evidence validation. The visible extraction preview is still not a verified report.";
            StatusText = $"Loaded a reviewed PDF source from {Path.GetFileName(path)}.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not OperationCanceledException)
        {
            _restoringPdfReviewSettings = false;
            _pdfRestoreTemplateRevisionOverride = null;
            Session.SetBinding(binding with { SchemaFingerprint = null }, clearBindingSchemasFromHistory: true);
            ClearPdfReviewProposal();
            SourceInspectionSummary = $"PDF review sidecar could not be validated: {exception.Message}";
            PdfReviewStatus = "A configured sidecar is stale, invalid, or incomplete. Mapping remains blocked until a current review is saved; extracted text is not a verified report.";
            StatusText = "PDF text loaded for review; the configured sidecar did not validate.";
        }
    }

    private void RefreshPdfPageWords()
    {
        _pdfPageWords = CurrentPdfPage?.Words
            .Select((word, index) => new PdfReviewWord(word.Text,
                $"PDF page {CurrentPdfPageNumber}, extracted word {index + 1}: {word.Text}"))
            .ToArray() ?? Array.Empty<PdfReviewWord>();
        Raise(nameof(PdfPageWords));
    }

    private void ResetPdfReviewState()
    {
        _pdfDocument = null;
        _pdfTemplate = null;
        _pdfReconstruction = null;
        _pdfSourcePath = null;
        _currentPdfPageNumber = 1;
        _pdfReviewZoom = 1.0;
        _pdfReviewStatus = "Choose a digital PDF source to begin review.";
        _pdfTemplateStatus = "Declare the row-label header, year headers, and row direction before reconstruction.";
        _pdfRowLabelHeader = "";
        _pdfPeriodHeaders = "";
        _selectedPdfRowDirection = null;
        _pdfDecimalSeparator = "";
        _pdfGroupSeparator = "";
        _pdfAllowGrouping = false;
        _pdfScaleFactor = "";
        _pdfCurrencyCode = "";
        _pdfAllowLeadingSign = false;
        _pdfAllowParenthesizedNegative = false;
        _pdfAllowTrailingMinus = false;
        _pdfReviewer = "";
        _selectedPdfReviewValue = null;
        _pdfReviewStates.Clear();
        _pdfReviewAuditEvents.Clear();
        _pdfPageWords = Array.Empty<PdfReviewWord>();
        PdfPageNumbers.Clear();
        PdfReviewValues.Clear();
        Raise(nameof(HasPdfDocument));
        Raise(nameof(CanEditPdfReview));
        Raise(nameof(CurrentPdfPage));
        Raise(nameof(PdfPageCount));
        Raise(nameof(PdfPageSummary));
        Raise(nameof(PdfPageWords));
        Raise(nameof(HasPreviousPdfPage));
        Raise(nameof(HasNextPdfPage));
        Raise(nameof(PdfReviewZoom));
        Raise(nameof(PdfReviewStatus));
        Raise(nameof(PdfTemplateStatus));
        Raise(nameof(PdfRowLabelHeader));
        Raise(nameof(PdfPeriodHeaders));
        Raise(nameof(SelectedPdfRowDirection));
        Raise(nameof(PdfDecimalSeparator));
        Raise(nameof(PdfGroupSeparator));
        Raise(nameof(PdfAllowGrouping));
        Raise(nameof(PdfScaleFactor));
        Raise(nameof(PdfCurrencyCode));
        Raise(nameof(PdfAllowLeadingSign));
        Raise(nameof(PdfAllowParenthesizedNegative));
        Raise(nameof(PdfAllowTrailingMinus));
        Raise(nameof(PdfReviewer));
        Raise(nameof(SelectedPdfReviewValue));
        Raise(nameof(SelectedPdfEvidence));
        Raise(nameof(PdfPageNumbers));
        Raise(nameof(PdfReviewValues));
        Raise(nameof(SelectedPdfReviewHistory));
        RefreshPdfReviewCommandState();
    }

    private void InvalidatePdfAnalysis()
    {
        if (!_restoringPdfReviewSettings)
        {
            InvalidateActivePdfBindingSchema();
            _pdfReviewAuditEvents.Clear();
            _pdfReviewStates.Clear();
            Raise(nameof(SelectedPdfReviewHistory));
        }
        if (_pdfTemplate is null && _pdfReconstruction is null && PdfReviewValues.Count == 0)
        {
            RefreshPdfReviewCommandState();
            return;
        }
        _pdfTemplate = null;
        _pdfReconstruction = null;
        PdfReviewValues.Clear();
        _pdfReviewStates.Clear();
        SelectedPdfReviewValue = null;
        PdfTemplateStatus = "Template or numeric policy changed. Rebuild the proposal before reviewing values.";
        RefreshPdfReviewCommandState();
    }

    private void BuildPdfTemplate()
    {
        if (_pdfDocument is null)
        {
            PdfTemplateStatus = "Load a digital PDF before declaring a table template.";
            return;
        }
        try
        {
            if (SelectedPdfRowDirection is null)
                throw new InvalidOperationException("Choose the direction in which table rows continue on the page.");
            var policy = CreatePdfNumericPolicy();
            var headers = ParsePdfPeriodHeaders(PdfPeriodHeaders);
            var geometryTemplate = PdfTableTemplate.CreateFromFirstPage(_pdfDocument, CurrentPdfPageNumber,
                PdfRowLabelHeader, headers, SelectedPdfRowDirection.RowsAtLowLineCoordinate);
            var template = geometryTemplate with
            {
                Revision = _pdfRestoreTemplateRevisionOverride
                    ?? CreatePdfReviewRevision(geometryTemplate, policy)
            };
            if (!_restoringPdfReviewSettings && _pdfTemplate is not null)
                InvalidateActivePdfBindingSchema();
            var reconstruction = PdfTableTemplateReconstructor.Reconstruct(_pdfDocument, template);
            var anchored = reconstruction.State == PdfTableReconstructionState.Anchored
                && reconstruction.Alternatives.Count == 0;

            _pdfTemplate = template;
            _pdfReconstruction = reconstruction;
            PdfReviewValues.Clear();
            _pdfReviewStates.Clear();
            foreach (var row in reconstruction.Rows)
            foreach (var cell in row.Cells)
            {
                var parse = PdfNumericInterpreter.Parse(cell, policy);
                var viewRow = new PdfReviewValueRow(row, cell, parse, anchored);
                viewRow.PropertyChanged += OnPdfReviewValueChanged;
                PdfReviewValues.Add(viewRow);
                _pdfReviewStates[viewRow] = new PdfReviewRowState(viewRow.ReviewedValue, viewRow.CorrectionReason, false);
            }
            SelectedPdfReviewValue = PdfReviewValues.FirstOrDefault();
            if (!_restoringPdfReviewSettings)
                RestorePdfAuditHistoryFromBeginning();

            PdfTemplateStatus = anchored
                ? $"Anchored {reconstruction.Rows.Count} table rows across {headers.Count} declared years. Each parsed value still needs individual reviewer approval."
                : $"Template state: {reconstruction.State}; {reconstruction.Alternatives.Count} ambiguous alternative(s). Value approval and saving are disabled.";
            PdfReviewStatus = "These are extracted text proposals. Check each raw value against its highlighted page region before approval.";
            StatusText = anchored
                ? $"Built a review proposal with {PdfReviewValues.Count} numeric cells. No values have been approved."
                : "PDF table layout needs review; proposed values cannot be approved.";
            RefreshPdfReviewCommandState();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _pdfTemplate = null;
            _pdfReconstruction = null;
            PdfReviewValues.Clear();
            _pdfReviewStates.Clear();
            SelectedPdfReviewValue = null;
            PdfTemplateStatus = exception.Message;
            StatusText = "Could not build the PDF review proposal.";
            RefreshPdfReviewCommandState();
        }
    }

    private PdfNumericFormatPolicy CreatePdfNumericPolicy()
    {
        if (PdfDecimalSeparator.Trim().Length != 1)
            throw new InvalidOperationException("Enter exactly one decimal separator character.");
        char? groupSeparator = PdfGroupSeparator.Length == 0
            ? null
            : PdfGroupSeparator.Length == 1 ? PdfGroupSeparator[0]
            : throw new InvalidOperationException("The group separator must be blank or one character.");
        if (!int.TryParse(PdfScaleFactor, NumberStyles.None, CultureInfo.InvariantCulture, out var scaleFactor))
            throw new InvalidOperationException("Enter an explicit scale factor: 1, 1000, or 1000000.");
        return new PdfNumericFormatPolicy(PdfDecimalSeparator.Trim()[0], groupSeparator,
            PdfAllowGrouping, scaleFactor, PdfCurrencyCode.Trim(), PdfAllowLeadingSign,
            PdfAllowParenthesizedNegative, PdfAllowTrailingMinus, PdfNumericPolicyBasis.ExplicitTemplate).Validate();
    }

    private static string CreatePdfReviewRevision(PdfTableTemplate geometryTemplate, PdfNumericFormatPolicy policy)
    {
        var policyRevision = string.Join("|", new[]
        {
            policy.DecimalSeparator.ToString(),
            policy.GroupSeparator?.ToString() ?? "",
            policy.AllowGrouping.ToString(CultureInfo.InvariantCulture),
            policy.ScaleFactor.ToString(CultureInfo.InvariantCulture),
            policy.CurrencyCode,
            policy.AllowLeadingSign.ToString(CultureInfo.InvariantCulture),
            policy.AllowParenthesizedNegative.ToString(CultureInfo.InvariantCulture),
            policy.AllowTrailingMinus.ToString(CultureInfo.InvariantCulture),
            policy.Basis.ToString()
        });
        var revisionInput = $"pdf-review-v1|{geometryTemplate.Revision}|{policyRevision}";
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput)))
            .ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, int> ParsePdfPeriodHeaders(string text)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator != part.LastIndexOf('='))
                throw new InvalidOperationException("Declare each year heading as visible-header=reporting-year, separated by semicolons.");
            var header = part[..separator].Trim();
            if (!int.TryParse(part[(separator + 1)..].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var reportingYear)
                || !headers.TryAdd(header, reportingYear))
                throw new InvalidOperationException("Each visible year header must map to one unique reporting year.");
        }
        if (headers.Count is < 2 or > 30)
            throw new InvalidOperationException("Declare between two and thirty year headings explicitly.");
        return headers;
    }

    private void OnPdfReviewValueChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(PdfReviewValueRow.IsApproved)
            && !_applyingPdfReviewAudit && sender is PdfReviewValueRow row)
            RecordPdfApprovalChange(row);
        RefreshPdfReviewCommandState();
    }

    private void RefreshPdfReviewCommandState()
    {
        Raise(nameof(CanSavePdfReview));
        Raise(nameof(CanApplyPdfCorrection));
        Raise(nameof(CanUndoPdfReview));
        Raise(nameof(CanEditPdfReview));
        SavePdfReviewCommand?.NotifyCanExecuteChanged();
        ApplyPdfCorrectionCommand?.NotifyCanExecuteChanged();
        UndoPdfReviewCommand?.NotifyCanExecuteChanged();
    }

    private void InvalidateActivePdfBindingSchema()
    {
        var binding = Session.Project.Bindings.FirstOrDefault(candidate =>
            candidate.BindingId == SelectedSourceBindingId && candidate.Kind == "pdf-digital");
        if (binding?.SchemaFingerprint is null) return;
        Session.SetBinding(binding with { SchemaFingerprint = null }, clearBindingSchemasFromHistory: true);
        SourceColumns.Clear();
        Raise(nameof(SourceColumns));
        SourceInspectionSummary = "PDF review or template changed. Save and validate a current review before mapping this source.";
    }

    private void ClearPdfReviewProposal()
    {
        _pdfTemplate = null;
        _pdfReconstruction = null;
        PdfReviewValues.Clear();
        _pdfReviewStates.Clear();
        _pdfReviewAuditEvents.Clear();
        SelectedPdfReviewValue = null;
        Raise(nameof(PdfReviewValues));
        Raise(nameof(SelectedPdfReviewHistory));
        RefreshPdfReviewCommandState();
    }

    private bool RestorePdfReviewManifest(PdfReviewedSourceManifest manifest)
    {
        var document = _pdfDocument;
        if (document is null) return false;

        var headers = manifest.PeriodColumns.ToDictionary(
            period => period.HeaderText, period => period.ReportingYear, StringComparer.OrdinalIgnoreCase);
        var candidatePages = manifest.TemplatePageNumber is int declaredPage
            ? new[] { declaredPage }
            : document.Pages.Select(page => page.PageNumber).ToArray();
        var candidateDirections = manifest.RowsAtLowLineCoordinate is bool declaredDirection
            ? new[] { declaredDirection }
            : PdfRowDirections.Select(direction => direction.RowsAtLowLineCoordinate).ToArray();
        PdfTableTemplate? matchingTemplate = null;

        _restoringPdfReviewSettings = true;
        try
        {
            PdfRowLabelHeader = manifest.RowLabelHeader;
            PdfPeriodHeaders = string.Join("; ", manifest.PeriodColumns
                .Select(period => $"{period.HeaderText}={period.ReportingYear}"));
            PdfDecimalSeparator = manifest.NumericPolicy.DecimalSeparator.ToString();
            PdfGroupSeparator = manifest.NumericPolicy.GroupSeparator?.ToString() ?? "";
            PdfAllowGrouping = manifest.NumericPolicy.AllowGrouping;
            PdfScaleFactor = manifest.NumericPolicy.ScaleFactor.ToString(CultureInfo.InvariantCulture);
            PdfCurrencyCode = manifest.NumericPolicy.CurrencyCode;
            PdfAllowLeadingSign = manifest.NumericPolicy.AllowLeadingSign;
            PdfAllowParenthesizedNegative = manifest.NumericPolicy.AllowParenthesizedNegative;
            PdfAllowTrailingMinus = manifest.NumericPolicy.AllowTrailingMinus;
            PdfReviewer = manifest.Reviewer;

            foreach (var pageNumber in candidatePages)
            foreach (var rowsAtLowCoordinate in candidateDirections)
            {
                try
                {
                    var geometryTemplate = PdfTableTemplate.CreateFromFirstPage(document, pageNumber,
                        manifest.RowLabelHeader, headers, rowsAtLowCoordinate);
                    var reviewRevision = CreatePdfReviewRevision(geometryTemplate, manifest.NumericPolicy);
                    if (StringComparer.Ordinal.Equals(geometryTemplate.Revision, manifest.TemplateRevision))
                        _pdfRestoreTemplateRevisionOverride = manifest.TemplateRevision; // legacy PDF-07 sidecar
                    else if (StringComparer.Ordinal.Equals(reviewRevision, manifest.TemplateRevision))
                        _pdfRestoreTemplateRevisionOverride = null;
                    else
                        continue;
                    matchingTemplate = geometryTemplate with
                    {
                        Revision = _pdfRestoreTemplateRevisionOverride ?? reviewRevision
                    };
                    CurrentPdfPageNumber = pageNumber;
                    SelectedPdfRowDirection = PdfRowDirections.Single(direction =>
                        direction.RowsAtLowLineCoordinate == rowsAtLowCoordinate);
                    break;
                }
                catch (AdapterException)
                {
                    // Legacy sidecars omit the page and direction, so try the remaining declared candidates.
                }
            }
        }
        finally { _restoringPdfReviewSettings = false; }

        if (matchingTemplate is null)
        {
            _pdfRestoreTemplateRevisionOverride = null;
            throw new InvalidDataException($"The declared PDF template and numeric policy do not reproduce revision {manifest.TemplateRevision}.");
        }
        _restoringPdfReviewSettings = true;
        try { BuildPdfTemplate(); }
        finally
        {
            _restoringPdfReviewSettings = false;
            _pdfRestoreTemplateRevisionOverride = null;
        }
        if (_pdfTemplate is null || _pdfReconstruction is null
            || !StringComparer.Ordinal.Equals(_pdfTemplate.Revision, manifest.TemplateRevision)
            || _pdfReconstruction.State != PdfTableReconstructionState.Anchored
            || _pdfReconstruction.Alternatives.Count != 0
            || _pdfReconstruction.Rows.Count != manifest.Rows.Count)
            throw new InvalidDataException($"The declared PDF review could not be reconstructed (state {_pdfReconstruction?.State}, revision {_pdfTemplate?.Revision}).");

        var reviewedRows = manifest.Rows.ToDictionary(row => row.Occurrence);
        foreach (var sourceRow in _pdfReconstruction.Rows)
        {
            if (!reviewedRows.TryGetValue(sourceRow.Occurrence, out var reviewedRow)
                || !StringComparer.Ordinal.Equals(sourceRow.LabelText, reviewedRow.LabelText)
                || sourceRow.Evidence != reviewedRow.LabelEvidence
                || sourceRow.Cells.Count != reviewedRow.Cells.Count)
                throw new InvalidDataException($"PDF row occurrence {sourceRow.Occurrence} no longer matches the reviewed label and evidence.");

            var reviewedCells = reviewedRow.Cells.ToDictionary(cell => cell.ReportingYear);
            foreach (var sourceCell in sourceRow.Cells)
            {
                if (!reviewedCells.TryGetValue(sourceCell.ReportingYear, out var reviewedCell)
                    || !StringComparer.Ordinal.Equals(sourceCell.RawText, reviewedCell.RawText)
                    || sourceCell.Evidence != reviewedCell.Evidence)
                    throw new InvalidDataException($"PDF cell occurrence {sourceRow.Occurrence}, year {sourceCell.ReportingYear} no longer matches its reviewed evidence.");
                var row = PdfReviewValues.SingleOrDefault(value =>
                    value.SourceRow.Occurrence == sourceRow.Occurrence
                    && value.ReportingYear == sourceCell.ReportingYear);
                if (row is null || !PdfReviewValueRow.IsCanonicalDecimal(reviewedCell.CanonicalValue))
                    throw new InvalidDataException($"PDF cell occurrence {sourceRow.Occurrence}, year {sourceCell.ReportingYear} has no canonical reviewed value.");
                ApplyPdfReviewState(row, new PdfReviewRowState(
                    reviewedCell.CanonicalValue, reviewedCell.CorrectionReason, reviewedCell.IsApproved));
                if (row.IsApproved && !row.CanApprove)
                    throw new InvalidDataException($"PDF cell occurrence {sourceRow.Occurrence}, year {sourceCell.ReportingYear} is approved but not currently approvable.");
            }
        }

        _pdfReviewAuditEvents.Clear();
        _pdfReviewAuditEvents.AddRange(_pdfReviewAuditStoreFactory().Read(
            document.SourceSha256, manifest.TemplateRevision));
        var pendingEvents = _pdfReviewAuditEvents
            .Where(entry => entry.OccurredUtc > manifest.ReviewedUtc)
            .ToArray();
        foreach (var entry in pendingEvents)
        {
            var row = PdfReviewValues.SingleOrDefault(value =>
                value.SourceRow.Occurrence == entry.RowOccurrence
                && value.ReportingYear == entry.ReportingYear);
            if (row is null
                || !StringComparer.Ordinal.Equals(row.RawText, entry.OriginalRawText)
                || !StringComparer.Ordinal.Equals(row.ParseResult.CanonicalValue, entry.ParsedValue)
                || !_pdfReviewStates.TryGetValue(row, out var current)
                || !StringComparer.Ordinal.Equals(current.ReviewedValue, entry.PreviousReviewedValue)
                || !StringComparer.Ordinal.Equals(current.CorrectionReason, entry.PreviousCorrectionReason)
                || current.IsApproved != entry.PreviousIsApproved)
                throw new InvalidDataException($"PDF audit event {entry.EventId} does not continue the restored sidecar state.");
            ApplyPdfReviewState(row, new PdfReviewRowState(
                entry.ReviewedValue, entry.CorrectionReason, entry.IsApproved));
            if (row.IsApproved && !row.CanApprove)
                throw new InvalidDataException($"PDF audit event {entry.EventId} restores a value that cannot be approved.");
        }

        Raise(nameof(SelectedPdfReviewHistory));
        RefreshPdfReviewCommandState();
        return true;
    }

    private void RestorePdfAuditHistoryFromBeginning()
    {
        if (_pdfDocument is null || _pdfTemplate is null) return;
        _pdfReviewAuditEvents.Clear();
        _pdfReviewAuditEvents.AddRange(_pdfReviewAuditStoreFactory().Read(
            _pdfDocument.SourceSha256, _pdfTemplate.Revision));
        foreach (var entry in _pdfReviewAuditEvents)
        {
            var row = PdfReviewValues.SingleOrDefault(value =>
                value.SourceRow.Occurrence == entry.RowOccurrence
                && value.ReportingYear == entry.ReportingYear);
            if (row is null
                || !StringComparer.Ordinal.Equals(row.RawText, entry.OriginalRawText)
                || !StringComparer.Ordinal.Equals(row.ParseResult.CanonicalValue, entry.ParsedValue)
                || !_pdfReviewStates.TryGetValue(row, out var current)
                || !StringComparer.Ordinal.Equals(current.ReviewedValue, entry.PreviousReviewedValue)
                || !StringComparer.Ordinal.Equals(current.CorrectionReason, entry.PreviousCorrectionReason)
                || current.IsApproved != entry.PreviousIsApproved)
                throw new InvalidDataException("The local PDF review audit history does not follow the current source value state.");
            ApplyPdfReviewState(row, new PdfReviewRowState(
                entry.ReviewedValue, entry.CorrectionReason, entry.IsApproved));
            if (row.IsApproved && !row.CanApprove)
                throw new InvalidDataException("The local PDF review audit history contains an unapprovable value.");
        }
        Raise(nameof(SelectedPdfReviewHistory));
    }

    private void ApplyPdfReviewState(PdfReviewValueRow row, PdfReviewRowState state)
    {
        _applyingPdfReviewAudit = true;
        try { row.SetReviewedState(state.ReviewedValue, state.CorrectionReason, state.IsApproved); }
        finally { _applyingPdfReviewAudit = false; }
        _pdfReviewStates[row] = state;
    }

    private void RecordPdfApprovalChange(PdfReviewValueRow row)
    {
        if (!_pdfReviewStates.TryGetValue(row, out var previous)) return;
        var current = new PdfReviewRowState(row.ReviewedValue, row.CorrectionReason, row.IsApproved);
        if (current.IsApproved == previous.IsApproved) return;
        if (!TryGetPdfReviewer(out var reviewer)
            || _pdfDocument is null || _pdfTemplate is null || row.ParseResult.CanonicalValue is null)
        {
            ApplyPdfReviewState(row, previous);
            PdfReviewStatus = "Enter a valid reviewer identity before recording an approval or rejection.";
            return;
        }
        var action = current.IsApproved ? PdfReviewAuditAction.Approve : PdfReviewAuditAction.Reject;
        AppendPdfReviewAuditEvent(row, action, previous, current, reviewer);
    }

    private void ApplySelectedPdfCorrection()
    {
        var row = SelectedPdfReviewValue;
        if (row is null || !row.CanApplyCorrection || !TryGetPdfReviewer(out var reviewer)
            || _pdfDocument is null || _pdfTemplate is null
            || !_pdfReviewStates.TryGetValue(row, out var previous))
            return;
        var current = new PdfReviewRowState(row.CorrectionValue.Trim(),
            row.CorrectionReasonInput.Trim(), false);
        AppendPdfReviewAuditEvent(row, PdfReviewAuditAction.Correct, previous, current, reviewer);
        if (StringComparer.Ordinal.Equals(row.ReviewedValue, current.ReviewedValue)
            && StringComparer.Ordinal.Equals(row.CorrectionReason, current.CorrectionReason))
            row.ClearCorrectionDraft();
    }

    private void UndoSelectedPdfReview()
    {
        var row = SelectedPdfReviewValue;
        if (row is null || !TryGetPdfReviewer(out var reviewer)
            || _pdfDocument is null || _pdfTemplate is null
            || !_pdfReviewStates.TryGetValue(row, out var previous))
            return;
        var latest = _pdfReviewAuditEvents.LastOrDefault(entry =>
            entry.RowOccurrence == row.SourceRow.Occurrence && entry.ReportingYear == row.ReportingYear);
        if (latest is null) return;
        var current = new PdfReviewRowState(latest.PreviousReviewedValue,
            latest.PreviousCorrectionReason, latest.PreviousIsApproved);
        var undo = AppendPdfReviewAuditEvent(row, PdfReviewAuditAction.Undo, previous, current,
            reviewer, latest.EventId);
        if (undo is not null) row.ClearCorrectionDraft();
    }

    private PdfReviewAuditEvent? AppendPdfReviewAuditEvent(
        PdfReviewValueRow row,
        PdfReviewAuditAction action,
        PdfReviewRowState previous,
        PdfReviewRowState current,
        string reviewer,
        string? undoesEventId = null)
    {
        if (_pdfDocument is null || _pdfTemplate is null || row.ParseResult.CanonicalValue is not { } parsedValue)
            return null;
        var entry = new PdfReviewAuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            SourceSha256 = _pdfDocument.SourceSha256,
            TemplateRevision = _pdfTemplate.Revision,
            RowOccurrence = row.SourceRow.Occurrence,
            ReportingYear = row.ReportingYear,
            Action = action,
            OriginalRawText = row.RawText,
            ParsedValue = parsedValue,
            PreviousReviewedValue = previous.ReviewedValue,
            PreviousCorrectionReason = previous.CorrectionReason,
            PreviousIsApproved = previous.IsApproved,
            ReviewedValue = current.ReviewedValue,
            CorrectionReason = current.CorrectionReason,
            IsApproved = current.IsApproved,
            Reviewer = reviewer,
            OccurredUtc = DateTimeOffset.UtcNow,
            UndoesEventId = undoesEventId
        };
        try
        {
            _pdfReviewAuditStoreFactory().Append(entry);
            _pdfReviewAuditEvents.Add(entry);
            ApplyPdfReviewState(row, current);
            InvalidateActivePdfBindingSchema();
            PdfReviewStatus = $"{action} decision recorded in local review history. Save a new review sidecar before mapping; reviewer identity is not authenticated.";
            Raise(nameof(SelectedPdfReviewHistory));
            RefreshPdfReviewCommandState();
            return entry;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not OperationCanceledException)
        {
            ApplyPdfReviewState(row, previous);
            PdfReviewStatus = $"The review decision was not saved: {exception.Message}";
            RefreshPdfReviewCommandState();
            return null;
        }
    }

    private bool TryGetPdfReviewer(out string reviewer)
    {
        reviewer = PdfReviewer.Trim();
        return reviewer.Length is > 0 and <= 128 && !reviewer.Any(char.IsControl);
    }

    internal async Task SavePdfReviewAsync()
    {
        var document = _pdfDocument;
        var template = _pdfTemplate;
        var reconstruction = _pdfReconstruction;
        var sourcePath = _pdfSourcePath;
        if (!CanSavePdfReview || document is null || template is null
            || reconstruction is null || sourcePath is null)
        {
            StatusText = "Complete the anchored template and approve every parsed value before saving.";
            return;
        }

        var generation = _sourceLoadGeneration;
        var reviewer = PdfReviewer.Trim();
        var numericPolicy = CreatePdfNumericPolicy();
        var approvedValues = PdfReviewValues.ToDictionary(
            row => (row.SourceRow.Occurrence, row.ReportingYear),
            row => row);
        IsSourceLoading = true;
        try
        {
            sourcePath = Path.GetFullPath(sourcePath);
            var binding = Session.Project.Bindings.FirstOrDefault(candidate =>
                candidate.BindingId == SelectedSourceBindingId);
            if (binding is null) throw new InvalidOperationException("The selected PDF source binding no longer exists.");

            var captureCount = reconstruction.Rows.Sum(row => 1 + row.Cells.Count);
            if (captureCount is <= 0 or > 5_000)
                throw new InvalidOperationException("This PDF proposal exceeds the supported 5,000 evidence-region save limit.");
            var captures = new List<PdfEvidenceCapture>(captureCount);
            foreach (var row in reconstruction.Rows)
            {
                captures.Add(new PdfEvidenceCapture(row.Evidence, row.LabelText));
                captures.AddRange(row.Cells.Select(cell => new PdfEvidenceCapture(cell.Evidence, cell.RawText)));
            }

            var references = await _pdfEvidenceStoreFactory().CaptureAsync(
                sourcePath, document, captures, template.Revision, CancellationToken.None);
            if (generation != _sourceLoadGeneration)
            {
                PdfReviewStatus = "Review save stopped because the selected source changed during evidence persistence.";
                StatusText = "PDF review save cancelled after source selection changed.";
                return;
            }
            if (references.Count != captures.Count)
                throw new InvalidOperationException("PDF evidence persistence returned an incomplete reference set.");

            var index = 0;
            var reviewedRows = new List<PdfReviewedSourceRow>(reconstruction.Rows.Count);
            foreach (var row in reconstruction.Rows)
            {
                var labelEvidence = references[index++];
                var reviewedCells = new List<PdfReviewedSourceCell>(row.Cells.Count);
                foreach (var cell in row.Cells)
                {
                    var value = approvedValues[(row.Occurrence, cell.ReportingYear)];
                    if (!value.IsApproved || value.ParseResult.Value is null || !value.CanApprove
                        || value.HasPendingCorrectionDraft
                        || !PdfReviewValueRow.IsCanonicalDecimal(value.ReviewedValue))
                        throw new InvalidOperationException("Every source value must remain parsed and explicitly approved while the review is saved.");
                    var evidence = references[index++];
                    reviewedCells.Add(new PdfReviewedSourceCell
                    {
                        ReportingYear = cell.ReportingYear,
                        RawText = cell.RawText,
                        CanonicalValue = value.ReviewedValue,
                        EvidenceId = evidence.EvidenceId,
                        Evidence = cell.Evidence,
                        IsApproved = true,
                        CorrectionReason = value.CorrectionReason
                    });
                }
                reviewedRows.Add(new PdfReviewedSourceRow
                {
                    Occurrence = row.Occurrence,
                    LabelText = row.LabelText,
                    LabelEvidenceId = labelEvidence.EvidenceId,
                    LabelEvidence = row.Evidence,
                    Cells = reviewedCells
                });
            }

            var manifest = new PdfReviewedSourceManifest
            {
                FormatVersion = 1,
                SourceSha256 = document.SourceSha256,
                TemplateRevision = template.Revision,
                Reviewer = reviewer,
                ReviewedUtc = DateTimeOffset.UtcNow,
                IsApproved = true,
                RowLabelHeader = template.RowLabelHeader,
                TemplatePageNumber = template.SeedPageNumber,
                RowsAtLowLineCoordinate = template.RowsAtLowLineCoordinate,
                NumericPolicy = numericPolicy,
                PeriodColumns = template.PeriodColumns,
                Rows = reviewedRows
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
            });
            var manifestPath = await WritePdfReviewManifestAsync(sourcePath, manifestBytes);
            if (generation != _sourceLoadGeneration)
            {
                PdfReviewStatus = "The review sidecar was written but not activated because the selected source changed.";
                StatusText = "PDF review sidecar was not activated after source selection changed.";
                return;
            }

            var configuration = binding.Configuration.ToDictionary(pair => pair.Key,
                pair => pair.Value?.ToString() ?? "", StringComparer.Ordinal);
            configuration[PdfSourceAdapter.ReviewManifestConfigurationKey] = Path.GetFileName(manifestPath);
            var pendingBinding = binding with
            {
                Kind = "pdf-digital",
                Configuration = configuration.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
                SchemaFingerprint = null
            };
            Session.SetBinding(pendingBinding, clearBindingSchemasFromHistory: true);
            var connection = new SourceConnection(binding.BindingId, "pdf-digital", sourcePath, configuration);
            var schema = await new PdfSourceAdapter(_pdfEvidenceStoreFactory())
                .InspectSchemaAsync(connection, CancellationToken.None);
            if (generation != _sourceLoadGeneration)
            {
                PdfReviewStatus = "The review sidecar was written but not activated because the selected source changed.";
                StatusText = "PDF review sidecar was not activated after source selection changed.";
                return;
            }

            ReplaceItems(SourceColumns, schema.Columns.Select(column => column.Name));
            Session.SetBinding(pendingBinding with { SchemaFingerprint = schema.Fingerprint }, clearBindingSchemasFromHistory: true);
            Raise(nameof(SourceColumns));
            SourceInspectionSummary = $"Reviewed digital PDF schema: {schema.Columns.Count} typed fields. Source digest, page regions and immutable evidence snapshot are current.";
            PdfReviewStatus = $"Review saved with reviewer identifier {reviewer}. This local identifier is not authenticated; raw extraction is evidence for review, not a verified report.";
            StatusText = $"Saved and validated the PDF review sidecar {Path.GetFileName(manifestPath)}.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not OperationCanceledException)
        {
            PdfReviewStatus = $"The review was not activated: {exception.Message}";
            StatusText = "Could not save or validate the PDF review.";
        }
        finally
        {
            if (generation == _sourceLoadGeneration) IsSourceLoading = false;
        }
    }

    private static async Task<string> WritePdfReviewManifestAsync(string sourcePath, byte[] manifestBytes)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var defaultPath = sourcePath + ".xms-review.json";
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = attempt == 0 && !File.Exists(defaultPath)
                ? defaultPath
                : Path.Combine(directory, Path.GetFileName(sourcePath) + ".xms-review-"
                    + Guid.NewGuid().ToString("N") + ".json");
            var temporaryPath = Path.Combine(directory, ".xms-review-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    await output.WriteAsync(manifestBytes);
                    output.Flush(flushToDisk: true);
                }
                try
                {
                    File.Move(temporaryPath, candidate);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Keep existing review artifacts immutable and retry with a unique name.
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        throw new IOException("Could not create a unique PDF review sidecar without replacing an existing review.");
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
            var schema = await _sqliteSchemaLoader(connection, CancellationToken.None);
            if (generation != _sourceLoadGeneration || table != SelectedSourceTable || connection.Path != _sqliteSourcePath) return;
            var descriptor = SqliteSourceAdapter.Describe(connection, schema);
            SourceColumns.Clear();
            ReplaceItems(SourceColumns, schema.Columns.Select(column => column.Name));
            binding = binding with { Kind = "sqlite", Locator = Path.GetRelativePath(WorkspaceRoot(), _sqliteSourcePath),
                Configuration = config.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal),
                SchemaFingerprint = schema.Fingerprint };
            Session.SetBinding(binding);
            SelectedSourceBindingId = binding.BindingId;
            Raise(nameof(SourceColumns));
            var primaryKeyText = descriptor.PrimaryKeyColumns.Count > 0
                ? string.Join(", ", descriptor.PrimaryKeyColumns)
                : "none; row identity is positional and may shift between reads";
            var decimalText = descriptor.DecimalOverrides.Count > 0
                ? string.Join(", ", descriptor.DecimalOverrides)
                : "none configured";
            SourceInspectionSummary = $"SQLite table: {descriptor.Table}. Primary key: {primaryKeyText}. "
                + $"Row identity: {descriptor.RowIdentityStrategy}. Exact decimal overrides: {decimalText}. "
                + $"Decimal text culture: {descriptor.DecimalCulture}; SQLite REAL values are refused. "
                + "Capabilities: schema inspection, sampling, reading and lineage; writing is unsupported."
                + (descriptor.UnknownDecimalOverrides.Count > 0
                    ? $" Unknown decimal override columns: {string.Join(", ", descriptor.UnknownDecimalOverrides)}."
                    : string.Empty);
            StatusText = descriptor.HasStableRowIdentity
                ? $"Loaded {schema.Columns.Count} fields from {table}. Primary key: {string.Join(", ", descriptor.PrimaryKeyColumns)}; stable row identity enabled."
                : $"Loaded {schema.Columns.Count} fields from {table}. No primary key detected; SQLite does not guarantee row ordering, so positional row provenance may shift between reads.";
        }
        catch (Exception ex)
        {
            SourceInspectionSummary = $"SQLite source inspection failed: {ex.Message}";
            StatusText = $"SQLite table load failed: {ex.Message}";
        }
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
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        if (SelectedConcept is null || SelectedNode is null)
        { StatusText = "Select a taxonomy concept and an XBRL Fact node first."; return; }
        if (SelectedNode.NodeType != NodeTypeConstants.FactConstruct)
        { StatusText = "The selected node is not an XBRL Fact node."; return; }
        if (!SelectedConcept.IsFactTargetable)
        { StatusText = "Abstract concepts cannot be used as fact targets."; return; }
        if (profile.FindConcept(SelectedConcept.QualifiedName) is null)
        { StatusText = "The selected concept is not part of this project's filing profile."; return; }
        var concept = profile.FindConcept(SelectedConcept.QualifiedName)!;
        if (!StringComparer.Ordinal.Equals(concept.DataType, "monetary"))
        {
            StatusText = $"XBR011: this fact builder emits numeric values and cannot target '{concept.QualifiedName}' ({concept.DataType}).";
            return;
        }
        var factNode = Session.Project.Nodes.FirstOrDefault(node => node.NodeId == SelectedNode.NodeId);
        var graphPeriodType = factNode is null ? null : GetFactGraphPeriodType(factNode);
        if (graphPeriodType is not null && !StringComparer.Ordinal.Equals(graphPeriodType, concept.PeriodType))
        {
            StatusText = $"XBR004: the connected {graphPeriodType} period source cannot target a {concept.PeriodType} concept.";
            return;
        }
        Session.SetNodeParameterValues(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            [SelectedNode.NodeId] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["concept"] = SelectedConcept.QualifiedName,
                ["periodType"] = concept.PeriodType
            }
        });
        StatusText = $"Proposed target {SelectedConcept.QualifiedName} for {SelectedNodeDisplayName}. Review its unit and period before execution.";
    }

    private void ApplySelectedFactProperties()
    {
        if (SelectedNode is null || !CanEditSelectedFact) return;
        var resolution = _profileResolver.Resolve(Session.Project, allowSyntheticProfile: true);
        if (!resolution.Success || resolution.Profile is null)
        {
            StatusText = "Fact properties cannot be validated because the project's filing profile is unavailable.";
            return;
        }

        var factNode = Session.Project.Nodes.FirstOrDefault(node => node.NodeId == SelectedNode.NodeId);
        if (factNode is null) { StatusText = "The selected fact node no longer exists."; return; }
        var profile = resolution.Profile;
        var conceptName = factNode.Parameters.GetValueOrDefault("concept")?.ToString();
        var concept = conceptName is null ? null : profile.FindConcept(conceptName);
        if (concept is null || concept.IsAbstract)
        {
            StatusText = $"XBR001: concept '{conceptName ?? "(unset)"}' is unavailable or cannot carry facts in profile '{profile.ProfileId}'.";
            return;
        }
        if (!StringComparer.Ordinal.Equals(concept.DataType, "monetary"))
        {
            StatusText = $"XBR011: this fact builder emits numeric values and cannot target '{concept.QualifiedName}' ({concept.DataType}).";
            return;
        }

        var unit = SelectedFactUnit.Trim();
        var periodType = SelectedFactPeriodType.Trim().ToLowerInvariant();
        var decimals = SelectedFactDecimals.Trim();
        if (unit.Length > 0 && !profile.SupportedUnits.Contains(unit))
        {
            StatusText = $"XBR002: unit '{unit}' is not supported by filing profile '{profile.ProfileId}'.";
            return;
        }
        if (concept.DataType == "monetary" && unit.Length == 0)
        {
            StatusText = $"XBR002: monetary concept '{concept.QualifiedName}' requires a unit.";
            return;
        }
        if (periodType is not ("instant" or "duration")
            || !StringComparer.Ordinal.Equals(periodType, concept.PeriodType))
        {
            StatusText = $"XBR004: concept '{concept.QualifiedName}' requires a {concept.PeriodType} period type.";
            return;
        }
        var graphPeriodType = GetFactGraphPeriodType(factNode);
        if (graphPeriodType is not null && !StringComparer.Ordinal.Equals(graphPeriodType, concept.PeriodType))
        {
            StatusText = $"XBR004: the connected {graphPeriodType} period source cannot target a {concept.PeriodType} concept.";
            return;
        }

        string normalizedDecimals;
        if (StringComparer.OrdinalIgnoreCase.Equals(decimals, "INF"))
            normalizedDecimals = "INF";
        else if (int.TryParse(decimals, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDecimals)
                 && parsedDecimals is >= -30 and <= 30)
            normalizedDecimals = parsedDecimals.ToString(CultureInfo.InvariantCulture);
        else
        {
            StatusText = "XBR008: decimals must be INF or an integer in the supported range [-30, 30].";
            return;
        }

        if (!FactDimensionParser.TryParse(SelectedFactDimensions, profile.DimensionDomains,
                out var dimensions, out var dimensionError))
        {
            StatusText = $"XBR013: {dimensionError}";
            return;
        }

        var factUpdates = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["unit"] = unit,
            ["periodType"] = periodType,
            ["decimals"] = normalizedDecimals
        };
        if (dimensions.Count > 0 || factNode.Parameters.ContainsKey("dimensions"))
            factUpdates["dimensions"] = FactDimensionParser.Format(dimensions);

        var updates = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            [factNode.NodeId] = factUpdates
        };
        if (CanEditSelectedFactEntity)
        {
            var identifier = SelectedFactEntityIdentifier.Trim();
            if (identifier.Length == 0 || identifier.Any(char.IsControl))
            {
                StatusText = "XBR005: entity identifier must contain at least one non-control character.";
                return;
            }

            var entityEdge = Session.Project.Edges.FirstOrDefault(edge =>
                edge.TargetNodeId == factNode.NodeId && edge.TargetPortName == "entity");
            var entityNode = entityEdge is null
                ? null
                : Session.Project.Nodes.FirstOrDefault(node => node.NodeId == entityEdge.SourceNodeId);
            if (entityNode?.NodeType == NodeTypeConstants.Constant)
                updates[entityNode.NodeId] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["literal"] = identifier
                };
        }

        var changed = Session.SetNodeParameterValues(updates);
        StatusText = changed ? "Fact properties updated as one undoable change." : "No fact properties changed.";
    }

    private string? GetFactGraphPeriodType(MappingNode factNode)
    {
        var edge = Session.Project.Edges.FirstOrDefault(candidate =>
            candidate.TargetNodeId == factNode.NodeId
            && candidate.TargetPortName is "period" or "instantDate");
        var source = edge is null
            ? null
            : Session.Project.Nodes.FirstOrDefault(node => node.NodeId == edge.SourceNodeId);
        return source?.NodeType switch
        {
            NodeTypeConstants.BuildDurationDate => "duration",
            NodeTypeConstants.BuildInstantDate => "instant",
            _ => null
        };
    }

    private void RefreshFilteredSuggestions()
    {
        var query = _suggestionFilter.Trim();
        var filtered = AiSuggestions.Where(suggestion => string.IsNullOrEmpty(query)
            || suggestion.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
            || suggestion.TaxonomyEvidenceDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
            || suggestion.RecommendationDisplay.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ReplaceItems(FilteredAiSuggestions, filtered);
        if (SelectedSuggestion is not null && !FilteredAiSuggestions.Contains(SelectedSuggestion))
            SelectedSuggestion = null;
    }

    public async Task SuggestMappingsAsync()
    {
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        if (SelectedSourceBindingId is null || string.IsNullOrWhiteSpace(SelectedSourceColumn))
        {
            StatusText = "Choose a source file and select a source field first.";
            return;
        }
        if (SelectedNode is null || SelectedNode.NodeType != NodeTypeConstants.FactConstruct)
        {
            StatusText = "Select the target XBRL Fact node before requesting suggestions.";
            return;
        }

        try
        {
            var sourceBindingId = SelectedSourceBindingId;
            var sourceColumn = SelectedSourceColumn;
            var id = $"{sourceBindingId}:{sourceColumn}";
            var targetNodeId = SelectedNode.NodeId;
            var sourceNodeId = Session.Project.Nodes
                .Where(node => node.NodeType == NodeTypeConstants.ExcelField
                    && StringComparer.Ordinal.Equals(node.Parameters.GetValueOrDefault("bindingId")?.ToString(), sourceBindingId))
                .Select(node => node.NodeId)
                .SingleOrDefault() ?? "";
            var targetFact = Session.Project.Nodes.Single(node => node.NodeId == targetNodeId);
            var contextUnit = targetFact.Parameters.GetValueOrDefault("unit")?.ToString();
            var contextPeriod = targetFact.Parameters.GetValueOrDefault("periodType")?.ToString();
            var projectDigest = Session.Project.ComputeSemanticDigest();
            var service = new AiSuggestionService(AiPolicy.Offline, _taxonomy!);
            var batch = await service.SuggestAsync(
                new[] { new SourceFieldDescriptor(id, sourceColumn, contextUnit, contextPeriod) }, CancellationToken.None);
            if (!string.Equals(SelectedSourceBindingId, sourceBindingId, StringComparison.Ordinal)
                || !string.Equals(SelectedSourceColumn, sourceColumn, StringComparison.Ordinal)
                || !string.Equals(SelectedNode?.NodeId, targetNodeId, StringComparison.Ordinal)
                || !string.Equals(Session.Project.ComputeSemanticDigest(), projectDigest, StringComparison.Ordinal))
            {
                StatusText = "The project or mapping selection changed while suggestions were generated. Request fresh suggestions.";
                return;
            }
            var sourceIdentity = id;
            var sessionSuggestions = batch.Suggestions
                .Select(suggestion => suggestion with
                {
                    SourceField = sourceIdentity,
                    Draft = suggestion.Draft with { SourceField = sourceIdentity }
                })
                .ToArray();
            var session = new SuggestionSession(
                Session.Project,
                new DraftValidator(_taxonomy!, profile),
                _auditSinkFactory?.Invoke(),
                new SuggestionSessionContext(
                    Actor: Environment.UserName,
                    Method: "offline-retrieval",
                    ProviderConfigId: "local-retrieval"));
            session.LoadSuggestions(sessionSuggestions);
            _suggestionSession = session;
            SuggestionFilter = "";
            ReplaceSuggestionRows(sessionSuggestions.Select(s => new SuggestionRow
            {
                SuggestionId = s.SuggestionId,
                SourceFieldId = id,
                SourceNodeId = sourceNodeId,
                TargetNodeId = targetNodeId,
                ProjectDigest = projectDigest,
                Concept = s.Draft.Concept ?? "",
                Score = s.Score,
                Basis = s.Basis,
                TaxonomyEvidence = s.TaxonomyEvidence,
                Recommendation = s.Recommendation,
                IsAnalystEdited = s.IsAnalystEdited,
                ReviewState = session.StateOf(s.SuggestionId)
            }));
            SelectedSuggestion = FilteredAiSuggestions.FirstOrDefault();
            StatusText = batch.Abstentions.Count > 0
                ? string.Join(" ", batch.Abstentions.Select(abstention =>
                    $"No match for '{abstention.SourceFieldName}' ({abstention.SourceFieldId}): {abstention.Reason}"))
                : batch.Suggestions.Count == 0
                ? "No local concept candidates matched this field."
                : $"Found {batch.Suggestions.Count} local candidates. No source data was sent over the network.";
        }
        catch (Exception ex) { StatusText = $"Local suggestions failed: {ex.Message}"; }
    }

    public async Task SuggestConnectedMappingsAsync()
    {
        if (IsBusy || IsSourceLoading)
        {
            StatusText = "Wait for the current operation or source inspection to finish before generating batch suggestions.";
            return;
        }
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;

        var project = Session.Project;
        var projectDigest = project.ComputeSemanticDigest();
        var factNodes = project.Nodes.Where(node => node.NodeType == NodeTypeConstants.FactConstruct).ToArray();
        var sourceNodes = project.Nodes.Where(node => node.NodeType == NodeTypeConstants.ExcelField)
            .OrderBy(node => node.NodeId, StringComparer.Ordinal)
            .ToArray();
        var scopeDiagnostics = new List<Diagnostic>();
        var identities = sourceNodes.Select(node =>
        {
            var bindingId = node.Parameters.GetValueOrDefault("bindingId")?.ToString() ?? "";
            var column = node.Parameters.GetValueOrDefault("column")?.ToString() ?? "";
            return (Node: node, BindingId: bindingId, Column: column, FieldId: $"{bindingId}:{column}");
        }).ToArray();
        var duplicateIdentities = identities
            .Where(identity => !string.IsNullOrWhiteSpace(identity.BindingId) && !string.IsNullOrWhiteSpace(identity.Column))
            .GroupBy(identity => identity.FieldId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var targetsByFieldId = new Dictionary<string, (string SourceNodeId, string TargetNodeId)>(StringComparer.Ordinal);
        var descriptors = new List<SourceFieldDescriptor>();

        void AddScopeWarning(string message, string? nodeId = null)
            => scopeDiagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.AiInvalidDraft,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Warning,
                IsBlocking = false,
                Message = message,
                NodeId = nodeId
            });

        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity.BindingId) || string.IsNullOrWhiteSpace(identity.Column))
            {
                AddScopeWarning($"Source node '{identity.Node.DisplayName}' has no binding or field name; it was excluded from batch suggestions.", identity.Node.NodeId);
                continue;
            }
            if (duplicateIdentities.Contains(identity.FieldId))
            {
                AddScopeWarning($"Source field '{identity.FieldId}' is represented by multiple graph nodes; it was excluded as ambiguous.", identity.Node.NodeId);
                continue;
            }
            if (!project.Bindings.Any(binding => StringComparer.Ordinal.Equals(binding.BindingId, identity.BindingId)))
            {
                AddScopeWarning($"Source field '{identity.FieldId}' references a missing source binding; it was excluded.", identity.Node.NodeId);
                continue;
            }

            var targetFacts = factNodes.Where(factNode => IsUpstreamToFactValue(identity.Node.NodeId, factNode.NodeId)).ToArray();
            if (targetFacts.Length != 1)
            {
                AddScopeWarning(targetFacts.Length == 0
                    ? $"Source field '{identity.FieldId}' is not connected to a fact value input; it remains unassigned."
                    : $"Source field '{identity.FieldId}' reaches multiple fact nodes; choose one mapping path before batch suggestions.",
                    identity.Node.NodeId);
                continue;
            }

            var target = targetFacts[0];
            descriptors.Add(new SourceFieldDescriptor(
                identity.FieldId,
                identity.Column,
                target.Parameters.GetValueOrDefault("unit")?.ToString(),
                target.Parameters.GetValueOrDefault("periodType")?.ToString()));
            targetsByFieldId.Add(identity.FieldId, (identity.Node.NodeId, target.NodeId));
        }

        if (descriptors.Count == 0)
        {
            _suggestionSession = null;
            ReplaceSuggestionRows(Array.Empty<SuggestionRow>());
            ReplaceItems(Diagnostics, scopeDiagnostics.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));
            SuggestionBatchSummary = "No uniquely connected source-field to fact-node pairs are ready for batch suggestions.";
            StatusText = "Connect each source field to exactly one fact node before generating a batch.";
            return;
        }

        try
        {
            var suggestions = new List<Suggestion>();
            var abstentions = new List<SuggestionAbstention>();
            var diagnostics = new List<Diagnostic>(scopeDiagnostics);
            var service = new AiSuggestionService(AiPolicy.Offline, _taxonomy!);
            foreach (var chunk in descriptors.Chunk(100))
            {
                var batch = await service.SuggestAsync(chunk, CancellationToken.None);
                suggestions.AddRange(batch.Suggestions);
                abstentions.AddRange(batch.Abstentions);
                diagnostics.AddRange(batch.Diagnostics);
            }

            if (!ReferenceEquals(project, Session.Project)
                || !StringComparer.Ordinal.Equals(projectDigest, Session.Project.ComputeSemanticDigest()))
            {
                StatusText = "The project changed while batch suggestions were generated. No candidates were applied; request fresh suggestions.";
                return;
            }

            var session = new SuggestionSession(
                project,
                new DraftValidator(_taxonomy!, profile),
                _auditSinkFactory?.Invoke(),
                new SuggestionSessionContext(
                    Actor: Environment.UserName,
                    Method: "offline-retrieval",
                    ProviderConfigId: "local-retrieval"));
            var sessionSuggestions = suggestions.Select(suggestion =>
            {
                var fieldId = suggestion.Recommendation?.SourceFieldId;
                if (string.IsNullOrWhiteSpace(fieldId) || !targetsByFieldId.ContainsKey(fieldId))
                    throw new InvalidOperationException("A local candidate has no matching connected source-field identity.");
                return suggestion with
                {
                    SourceField = fieldId,
                    Draft = suggestion.Draft with { SourceField = fieldId }
                };
            }).ToArray();
            session.LoadSuggestions(sessionSuggestions);
            _suggestionSession = session;
            SuggestionFilter = "";
            ReplaceSuggestionRows(sessionSuggestions.Select(suggestion =>
            {
                var fieldId = suggestion.Draft.SourceField;
                var target = targetsByFieldId[fieldId];
                return new SuggestionRow
                {
                    SuggestionId = suggestion.SuggestionId,
                    SourceFieldId = fieldId,
                    SourceNodeId = target.SourceNodeId,
                    TargetNodeId = target.TargetNodeId,
                    ProjectDigest = projectDigest,
                    IsConnectedBatch = true,
                    Concept = suggestion.Draft.Concept ?? "",
                    Score = suggestion.Score,
                    Basis = suggestion.Basis,
                    TaxonomyEvidence = suggestion.TaxonomyEvidence,
                    Recommendation = suggestion.Recommendation,
                    IsAnalystEdited = suggestion.IsAnalystEdited,
                    ReviewState = session.StateOf(suggestion.SuggestionId)
                };
            }));
            SelectedSuggestion = FilteredAiSuggestions.FirstOrDefault();
            ReplaceItems(Diagnostics, diagnostics.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));

            var fieldsWithCandidates = sessionSuggestions.Select(suggestion => suggestion.Draft.SourceField)
                .Distinct(StringComparer.Ordinal).Count();
            SuggestionBatchSummary = suggestions.Count == 0
                ? $"No candidates were ranked for {descriptors.Count} connected field(s); all remain unassigned."
                : $"{suggestions.Count} candidate(s) for {fieldsWithCandidates} connected field(s). Check one candidate per field; confidence is not calibrated probability.";
            StatusText = suggestions.Count == 0
                ? string.Join(" ", abstentions.Select(abstention =>
                    $"No match for '{abstention.SourceFieldName}' ({abstention.SourceFieldId}): {abstention.Reason}"))
                : $"Found {suggestions.Count} offline candidates across {fieldsWithCandidates} connected source field(s). "
                    + $"{abstentions.Count} field(s) remain unassigned. No source data was sent over the network.";
        }
        catch (Exception ex)
        {
            StatusText = $"Offline batch suggestions failed: {ex.Message}";
        }
    }

    private void ApplySelectedSuggestion()
    {
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        if (!TryGetCurrentSuggestionContext(out var selected, out var suggestion, out var sourceIdentity)) return;
        var sourceNode = Session.Project.Nodes.SingleOrDefault(node => node.NodeId == selected.SourceNodeId
            && node.NodeType == NodeTypeConstants.ExcelField);
        if (sourceNode is null)
        {
            StatusText = "The suggestion's source field node is missing or ambiguous; request fresh suggestions.";
            return;
        }
        if (!selected.IsConnectedBatch)
        {
            var sourceNodes = Session.Project.Nodes.Where(node => node.NodeType == NodeTypeConstants.ExcelField
                && node.Parameters.TryGetValue("bindingId", out var bindingId)
                && string.Equals(bindingId?.ToString(), SelectedSourceBindingId, StringComparison.Ordinal)).ToList();
            if (sourceNodes.Count != 1)
            {
                StatusText = "A suggested mapping requires exactly one source field node for the selected binding.";
                return;
            }
        }
        var targetFactNode = Session.Project.Nodes.SingleOrDefault(node => node.NodeId == selected.TargetNodeId
            && node.NodeType == NodeTypeConstants.FactConstruct);
        if (targetFactNode is null || !IsUpstreamToFactValue(sourceNode.NodeId, targetFactNode.NodeId))
        {
            StatusText = "The suggestion's source field is no longer connected to its target fact node.";
            return;
        }

        var concept = _taxonomy!.Get(SelectedSuggestionConcept.Trim());
        if (concept is null || !TaxonomyIndex.IsFactTargetable(concept))
        {
            StatusText = "The edited candidate is not a valid fact concept in the selected taxonomy.";
            return;
        }
        var fact = targetFactNode;
        var decimalsText = fact.Parameters.GetValueOrDefault("decimals")?.ToString();
        int? decimals = int.TryParse(decimalsText, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedDecimals) ? parsedDecimals : null;
        var editedDraft = suggestion.Draft with
        {
            SourceField = sourceIdentity,
            Concept = concept.QualifiedName,
            Unit = string.IsNullOrWhiteSpace(SelectedSuggestionUnit) ? null : SelectedSuggestionUnit.Trim(),
            PeriodType = SelectedSuggestionPeriodType.Trim(),
            Decimals = decimals
        };

        var candidate = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(Session.Project));
        var sourceColumn = selected.IsConnectedBatch
            ? sourceNode.Parameters.GetValueOrDefault("column")?.ToString() ?? ""
            : SelectedSourceColumn;
        candidate.Nodes.Single(node => node.NodeId == sourceNode.NodeId).Parameters["column"] = sourceColumn;
        var candidateFact = candidate.Nodes.Single(node => node.NodeId == targetFactNode.NodeId);
        candidateFact.Parameters["concept"] = concept.QualifiedName;
        candidateFact.Parameters["periodType"] = editedDraft.PeriodType;
        candidateFact.Parameters["unit"] = editedDraft.Unit;
        var compile = MappingJobService.ValidateProject(candidate, profile);
        if (!compile.Success)
        {
            Diagnostics.Clear();
            ReplaceItems(Diagnostics, compile.Diagnostics.Items.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));
            StatusText = "Suggestion failed graph validation; the project was not changed.";
            return;
        }

        if (suggestion.Draft != editedDraft)
        {
            var editOutcome = _suggestionSession!.Edit(suggestion.SuggestionId, editedDraft);
            if (!editOutcome.Applied)
            {
                StatusText = editOutcome.Reason ?? "The suggestion edit could not be recorded.";
                return;
            }
            selected.Concept = concept.QualifiedName;
        }

        var digestBefore = Session.Project.ComputeSemanticDigest();
        var acceptOutcome = _suggestionSession!.Accept(suggestion.SuggestionId);
        if (!acceptOutcome.Applied)
        {
            ShowSuggestionValidation(acceptOutcome.Validation);
            StatusText = acceptOutcome.Reason ?? "Suggestion acceptance failed; the project was not changed.";
            return;
        }

        bool graphChanged;
        try
        {
            graphChanged = Session.SetNodeParameterValues(
                new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
                {
                    [sourceNode.NodeId] = new Dictionary<string, object?> { ["column"] = sourceColumn },
                    [targetFactNode.NodeId] = new Dictionary<string, object?>
                    {
                        ["concept"] = concept.QualifiedName,
                        ["unit"] = editedDraft.Unit,
                        ["periodType"] = editedDraft.PeriodType
                    }
                });
        }
        catch (Exception ex)
        {
            var rollback = _suggestionSession.Undo();
            StatusText = rollback.Applied
                ? $"The mapping graph could not be updated after review: {ex.Message}"
                : "The mapping graph update failed and the accepted review could not be rolled back; inspect the audit and project before continuing.";
            return;
        }

        var digestAfter = Session.Project.ComputeSemanticDigest();
        _acceptedSuggestionUndo.Push(new AcceptedSuggestionUndo(
            _suggestionSession, new[] { suggestion.SuggestionId }, digestBefore, digestAfter, graphChanged, IsBatch: false));
        selected.ReviewState = SuggestionState.Accepted;
        selected.IsBatchSelected = false;
        StatusText = $"Accepted {concept.QualifiedName} after draft and graph validation. Review entity and dimensions before execution.";
    }

    private void AcceptSelectedSuggestionBatch()
    {
        CaptureSelectedSuggestionDraft();
        if (_suggestionSession is null)
        {
            StatusText = "Generate suggestions before accepting a batch.";
            return;
        }
        var selectedRows = AiSuggestions.Where(row => row.IsBatchSelected).ToArray();
        if (selectedRows.Length == 0)
        {
            StatusText = "Check at least one suggestion for batch acceptance.";
            return;
        }
        if (selectedRows.Any(row => row.ReviewState != SuggestionState.Pending))
        {
            StatusText = "Only pending suggestions can be accepted; uncheck accepted or rejected rows.";
            return;
        }
        if (selectedRows.GroupBy(row => row.SourceFieldId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            StatusText = "Choose one candidate per source field. No mappings changed.";
            return;
        }
        if (selectedRows.Where(row => !string.IsNullOrWhiteSpace(row.TargetNodeId))
            .GroupBy(row => row.TargetNodeId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            StatusText = "Two checked candidates target the same fact node. Resolve the target conflict; no mappings changed.";
            return;
        }

        if (selectedRows.Any(row => _unsavedSuggestionDrafts.ContainsKey(row.SuggestionId)))
        {
            StatusText = "Save the checked candidate edits before batch acceptance. No mappings changed.";
            return;
        }

        var currentProject = Session.Project;
        var digestBefore = currentProject.ComputeSemanticDigest();
        if (selectedRows.Any(row => !StringComparer.Ordinal.Equals(row.ProjectDigest, digestBefore)))
        {
            StatusText = "The project changed after these suggestions were generated. No mappings changed; request fresh suggestions.";
            return;
        }
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;

        var selectedIds = selectedRows.Select(row => row.SuggestionId).ToArray();
        var suggestionsById = _suggestionSession.Suggestions.ToDictionary(
            suggestion => suggestion.SuggestionId, StringComparer.Ordinal);
        var candidate = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(currentProject));
        var changes = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var row in selectedRows)
        {
            if (!suggestionsById.TryGetValue(row.SuggestionId, out var suggestion)
                || !StringComparer.Ordinal.Equals(suggestion.Draft.SourceField, row.SourceFieldId))
            {
                StatusText = "A checked candidate no longer belongs to the active review session. No mappings changed.";
                return;
            }
            var sourceNode = currentProject.Nodes.FirstOrDefault(node => node.NodeId == row.SourceNodeId
                && node.NodeType == NodeTypeConstants.ExcelField);
            var targetNode = currentProject.Nodes.FirstOrDefault(node => node.NodeId == row.TargetNodeId
                && node.NodeType == NodeTypeConstants.FactConstruct);
            if (sourceNode is null || targetNode is null || !IsUpstreamToFactValue(sourceNode.NodeId, targetNode.NodeId))
            {
                StatusText = "A checked source-to-fact path changed after suggestion generation. No mappings changed.";
                return;
            }
            var bindingId = sourceNode.Parameters.GetValueOrDefault("bindingId")?.ToString();
            var column = sourceNode.Parameters.GetValueOrDefault("column")?.ToString();
            if (string.IsNullOrWhiteSpace(bindingId) || string.IsNullOrWhiteSpace(column)
                || !StringComparer.Ordinal.Equals(row.SourceFieldId, $"{bindingId}:{column}"))
            {
                StatusText = "A checked source field identity changed after suggestion generation. No mappings changed.";
                return;
            }

            var concept = _taxonomy!.Get(suggestion.Draft.Concept ?? "");
            if (concept is null || !TaxonomyIndex.IsFactTargetable(concept))
            {
                StatusText = $"Candidate '{suggestion.Draft.Concept ?? "(none)"}' is not a targetable concept; no mappings changed.";
                return;
            }
            var candidateFact = candidate.Nodes.Single(node => node.NodeId == targetNode.NodeId);
            candidateFact.Parameters["concept"] = concept.QualifiedName;
            candidateFact.Parameters["periodType"] = suggestion.Draft.PeriodType;
            candidateFact.Parameters["unit"] = suggestion.Draft.Unit;
            if (suggestion.Draft.Decimals is { } decimals)
                candidateFact.Parameters["decimals"] = decimals.ToString(CultureInfo.InvariantCulture);
            var factChanges = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["concept"] = concept.QualifiedName,
                ["periodType"] = suggestion.Draft.PeriodType,
                ["unit"] = suggestion.Draft.Unit
            };
            if (suggestion.Draft.Decimals is { } changedDecimals)
                factChanges["decimals"] = changedDecimals.ToString(CultureInfo.InvariantCulture);
            changes[targetNode.NodeId] = factChanges;
        }

        var compile = MappingJobService.ValidateProject(candidate, profile);
        if (!compile.Success)
        {
            ReplaceItems(Diagnostics, compile.Diagnostics.Items.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));
            StatusText = "The checked batch failed graph validation; no mappings changed.";
            return;
        }

        var acceptance = _suggestionSession.AcceptSelectedValid(selectedIds);
        if (!acceptance.Applied)
        {
            ShowSuggestionValidation(acceptance.Validation);
            StatusText = acceptance.Reason ?? "Batch validation or audit persistence failed; no mappings changed.";
            return;
        }

        bool graphChanged;
        try
        {
            graphChanged = Session.SetNodeParameterValues(changes);
        }
        catch (Exception ex)
        {
            var rollback = _suggestionSession.Undo();
            StatusText = rollback.Applied
                ? $"The mapping batch could not be applied and was rolled back: {ex.Message}"
                : "The mapping batch failed and its review rollback was not durable; inspect the audit and project before continuing.";
            return;
        }

        var suggestionIds = selectedRows.Select(row => row.SuggestionId).ToArray();
        var digestAfter = Session.Project.ComputeSemanticDigest();
        _acceptedSuggestionUndo.Push(new AcceptedSuggestionUndo(
            _suggestionSession, suggestionIds, digestBefore, digestAfter, graphChanged, IsBatch: true));
        foreach (var row in selectedRows)
        {
            row.ReviewState = SuggestionState.Accepted;
            row.IsBatchSelected = false;
        }
        StatusText = $"Accepted {selectedRows.Length} mapping(s) as one validated, audited transaction. Review entity and dimensions before execution.";
    }

    private void EditSelectedSuggestion()
    {
        if (!TryGetCurrentSuggestionContext(out var selected, out var suggestion, out var sourceIdentity)) return;
        var editedDraft = BuildSelectedSuggestionDraft(suggestion, sourceIdentity);
        var outcome = _suggestionSession!.Edit(suggestion.SuggestionId, editedDraft);
        if (!outcome.Applied)
        {
            StatusText = outcome.Reason ?? "The suggestion edit could not be recorded.";
            return;
        }
        selected.Concept = editedDraft.Concept ?? "";
        selected.IsAnalystEdited = true;
        _unsavedSuggestionDrafts.Remove(selected.SuggestionId);
        LoadSelectedSuggestionEditor();
        StatusText = "Suggestion edit saved to the review session and audit trail. Acceptance will validate it against the current mapping.";
    }

    private void RejectSelectedSuggestion()
    {
        if (SelectedSuggestion is null || _suggestionSession is null)
        {
            StatusText = "Select a suggestion to reject.";
            return;
        }
        var outcome = _suggestionSession.Reject(SelectedSuggestion.SuggestionId);
        if (!outcome.Applied)
        {
            StatusText = outcome.Reason ?? "The suggestion could not be rejected.";
            return;
        }
        SelectedSuggestion.ReviewState = SuggestionState.Rejected;
        SelectedSuggestion.IsBatchSelected = false;
        _unsavedSuggestionDrafts.Remove(SelectedSuggestion.SuggestionId);
        LoadSelectedSuggestionEditor();
        StatusText = "Suggestion rejected and recorded in the audit trail.";
    }

    private DraftMapping BuildSelectedSuggestionDraft(Suggestion suggestion, string sourceIdentity)
    {
        var fact = SelectedSuggestion is null
            ? null
            : Session.Project.Nodes.FirstOrDefault(node => node.NodeId == SelectedSuggestion.TargetNodeId);
        var decimalsText = fact?.Parameters.GetValueOrDefault("decimals")?.ToString();
        int? decimals = int.TryParse(decimalsText, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedDecimals) ? parsedDecimals : suggestion.Draft.Decimals;
        return suggestion.Draft with
        {
            SourceField = sourceIdentity,
            Concept = SelectedSuggestionConcept.Trim(),
            Unit = string.IsNullOrWhiteSpace(SelectedSuggestionUnit) ? null : SelectedSuggestionUnit.Trim(),
            PeriodType = SelectedSuggestionPeriodType.Trim(),
            Decimals = decimals
        };
    }

    private bool HasUnsavedSuggestionEditorChanges(SuggestionRow row)
        => StringComparer.Ordinal.Equals(row.SuggestionId, _suggestionEditorBaselineId)
            && (!StringComparer.Ordinal.Equals(SelectedSuggestionConcept, _suggestionEditorBaselineConcept)
                || !StringComparer.Ordinal.Equals(SelectedSuggestionUnit, _suggestionEditorBaselineUnit)
                || !StringComparer.Ordinal.Equals(SelectedSuggestionPeriodType, _suggestionEditorBaselinePeriodType));

    private void CaptureSelectedSuggestionDraft()
    {
        var selected = SelectedSuggestion;
        if (selected is null || _suggestionSession is null) return;
        var suggestion = _suggestionSession.Suggestions.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.SuggestionId, selected.SuggestionId));
        if (suggestion is null) return;

        if (!HasUnsavedSuggestionEditorChanges(selected))
        {
            _unsavedSuggestionDrafts.Remove(selected.SuggestionId);
            return;
        }

        _unsavedSuggestionDrafts[selected.SuggestionId] = BuildSelectedSuggestionDraft(suggestion, selected.SourceFieldId);
    }

    private bool TryGetCurrentSuggestionContext(
        out SuggestionRow selected, out Suggestion suggestion, out string sourceIdentity)
    {
        selected = SelectedSuggestion!;
        suggestion = null!;
        sourceIdentity = "";
        if (selected is null || _suggestionSession is null)
        {
            StatusText = "Select a local suggestion from an active review session first.";
            return false;
        }

        var selectedRow = selected;
        var sourceNode = Session.Project.Nodes.FirstOrDefault(node => node.NodeId == selectedRow.SourceNodeId
            && node.NodeType == NodeTypeConstants.ExcelField);
        var targetNode = Session.Project.Nodes.FirstOrDefault(node => node.NodeId == selectedRow.TargetNodeId
            && node.NodeType == NodeTypeConstants.FactConstruct);
        if (sourceNode is null || targetNode is null)
        {
            StatusText = "The suggestion's source or target mapping node is no longer present.";
            return false;
        }
        var sourceBindingId = sourceNode.Parameters.GetValueOrDefault("bindingId")?.ToString();
        var sourceColumn = sourceNode.Parameters.GetValueOrDefault("column")?.ToString();
        if (string.IsNullOrWhiteSpace(sourceBindingId) || string.IsNullOrWhiteSpace(sourceColumn))
        {
            StatusText = "The suggestion's source field no longer has a binding and column identity.";
            return false;
        }
        var currentSourceIdentity = $"{sourceBindingId}:{sourceColumn}";
        if (selected.IsConnectedBatch
            && !string.Equals(selected.SourceFieldId, currentSourceIdentity, StringComparison.Ordinal))
        {
            StatusText = "The source field changed after these suggestions were generated. Request fresh suggestions.";
            return false;
        }
        sourceIdentity = selected.IsConnectedBatch ? currentSourceIdentity : selected.SourceFieldId;
        if (!selected.IsConnectedBatch
            && !string.Equals(SelectedNode?.NodeId, targetNode.NodeId, StringComparison.Ordinal))
        {
            StatusText = "The target fact node changed after these suggestions were generated. Request fresh suggestions.";
            return false;
        }
        if (!selected.IsConnectedBatch
            && (!string.Equals(SelectedSourceBindingId, sourceBindingId, StringComparison.Ordinal)
                || !string.Equals(selected.SourceFieldId, $"{SelectedSourceBindingId}:{SelectedSourceColumn}", StringComparison.Ordinal)))
        {
            StatusText = "The source field selection changed after these suggestions were generated. Request fresh suggestions.";
            return false;
        }
        if (!IsUpstreamToFactValue(sourceNode.NodeId, targetNode.NodeId))
        {
            StatusText = "The source field is no longer connected to its suggested target fact node.";
            return false;
        }
        if (!string.Equals(selected.ProjectDigest, Session.Project.ComputeSemanticDigest(), StringComparison.Ordinal))
        {
            StatusText = "The mapping changed after these suggestions were generated. Request fresh suggestions.";
            return false;
        }
        var selectedSuggestionId = selected.SuggestionId;
        suggestion = _suggestionSession.Suggestions.FirstOrDefault(item =>
            string.Equals(item.SuggestionId, selectedSuggestionId, StringComparison.Ordinal))!;
        if (suggestion is null)
        {
            StatusText = "The selected suggestion is no longer part of the active review session.";
            return false;
        }
        return true;
    }

    private void LoadSelectedSuggestionEditor()
    {
        if (SelectedSuggestion is null || _suggestionSession is null) return;
        var suggestion = _suggestionSession.Suggestions.FirstOrDefault(item =>
            string.Equals(item.SuggestionId, SelectedSuggestion.SuggestionId, StringComparison.Ordinal));
        if (suggestion is null) return;
        var fact = Session.Project.Nodes.FirstOrDefault(node => node.NodeId == SelectedSuggestion.TargetNodeId);
        var savedConcept = suggestion.Draft.Concept ?? "";
        var savedUnit = suggestion.Draft.Unit
            ?? fact?.Parameters.GetValueOrDefault("unit")?.ToString()
            ?? "";
        var savedPeriodType = suggestion.Draft.PeriodType
            ?? fact?.Parameters.GetValueOrDefault("periodType")?.ToString()
            ?? "duration";
        _suggestionEditorBaselineId = suggestion.SuggestionId;
        _suggestionEditorBaselineConcept = savedConcept;
        _suggestionEditorBaselineUnit = savedUnit;
        _suggestionEditorBaselinePeriodType = savedPeriodType;

        if (_unsavedSuggestionDrafts.TryGetValue(suggestion.SuggestionId, out var unsaved))
        {
            SelectedSuggestionConcept = unsaved.Concept ?? "";
            SelectedSuggestionUnit = unsaved.Unit ?? "";
            SelectedSuggestionPeriodType = unsaved.PeriodType ?? "duration";
            return;
        }

        SelectedSuggestionConcept = savedConcept;
        SelectedSuggestionUnit = savedUnit;
        SelectedSuggestionPeriodType = savedPeriodType;
    }

    private void ClearSelectedSuggestionEditor()
    {
        _suggestionEditorBaselineId = null;
        _suggestionEditorBaselineConcept = "";
        _suggestionEditorBaselineUnit = "";
        _suggestionEditorBaselinePeriodType = "";
        SelectedSuggestionConcept = "";
        SelectedSuggestionUnit = "";
        SelectedSuggestionPeriodType = "duration";
    }

    private void ShowSuggestionValidation(DraftValidationResult? validation)
    {
        if (validation is null) return;
        Diagnostics.Clear();
        ReplaceItems(Diagnostics, validation.Diagnostics.Select(ToDiagnosticRow));
        Raise(nameof(Diagnostics));
    }

    private void Undo()
    {
        var currentDigest = Session.Project.ComputeSemanticDigest();
        if (_acceptedSuggestionUndo.TryPeek(out var accepted)
            && string.Equals(accepted.DigestAfter, currentDigest, StringComparison.Ordinal))
        {
            if (accepted.GraphChanged && !Session.CanUndo)
            {
                StatusText = "The accepted mapping can no longer be undone from the graph history.";
                return;
            }
            var outcome = accepted.Session.Undo();
            if (!outcome.Applied)
            {
                StatusText = outcome.Reason ?? "Undo was blocked because the audit decision was not durable.";
                return;
            }
            _isSuggestionHistoryMutation = true;
            try
            {
                if (accepted.GraphChanged) Session.Undo();
            }
            finally { _isSuggestionHistoryMutation = false; }
            _acceptedSuggestionUndo.Pop();
            _suggestionRedoState = new SuggestionRedoState(
                accepted.Session, accepted.SuggestionIds, accepted.DigestBefore, accepted.GraphChanged, accepted.IsBatch);
            foreach (var suggestionId in accepted.SuggestionIds)
            {
                var row = AiSuggestions.FirstOrDefault(item => item.SuggestionId == suggestionId);
                if (row is not null) row.ReviewState = SuggestionState.Pending;
            }
            StatusText = accepted.IsBatch
                ? "Accepted suggestion batch undone as one transaction and recorded. The project is back at its prior revision."
                : "Accepted suggestion undone and the decision recorded. The project is back at its prior revision.";
            RefreshUndoState();
            return;
        }
        if (!Session.CanUndo) return;
        Session.Undo();
        RefreshUndoState();
    }

    private void Redo()
    {
        var currentDigest = Session.Project.ComputeSemanticDigest();
        if (_suggestionRedoState is { } pending
            && (!pending.GraphChanged || Session.CanRedo)
            && string.Equals(pending.DigestBefore, currentDigest, StringComparison.Ordinal))
        {
            var outcome = pending.IsBatch
                ? pending.Session.AcceptSelectedValid(pending.SuggestionIds)
                : pending.Session.Accept(pending.SuggestionIds.Single());
            if (!outcome.Applied)
            {
                StatusText = outcome.Reason ?? "Redo was blocked because the audit decision was not durable.";
                return;
            }
            _isSuggestionHistoryMutation = true;
            try
            {
                if (pending.GraphChanged) Session.Redo();
            }
            finally { _isSuggestionHistoryMutation = false; }
            var digestAfter = Session.Project.ComputeSemanticDigest();
            _acceptedSuggestionUndo.Push(new AcceptedSuggestionUndo(
                pending.Session, pending.SuggestionIds, pending.DigestBefore, digestAfter, pending.GraphChanged, pending.IsBatch));
            _suggestionRedoState = null;
            foreach (var suggestionId in pending.SuggestionIds)
            {
                var row = AiSuggestions.FirstOrDefault(item => item.SuggestionId == suggestionId);
                if (row is not null) row.ReviewState = SuggestionState.Accepted;
            }
            StatusText = pending.IsBatch
                ? "Accepted suggestion batch redone and recorded in the audit trail."
                : "Accepted suggestion redone and recorded in the audit trail.";
            RefreshUndoState();
            return;
        }
        if (!Session.CanRedo) return;
        Session.Redo();
        RefreshUndoState();
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

    private bool IsUpstreamToFactValue(string sourceNodeId, string factNodeId)
        => Session.Project.Edges
            .Where(edge => edge.TargetNodeId == factNodeId
                && StringComparer.Ordinal.Equals(edge.TargetPortName, "value"))
            .Any(edge => IsUpstream(sourceNodeId, edge.SourceNodeId));

    private ISourceAdapter BuildSourceAdapter(SourceConnection connection) => connection.Kind switch
    {
        "excel" => ExcelAdapter.FromConnection(connection),
        "csv" => new CsvAdapter(CsvAdapter.Options.FromConfiguration(connection.Configuration)),
        "json" => new JsonAdapter(JsonAdapter.Options.FromConfiguration(connection.Configuration)),
        "xml" => new XmlAdapter(),
        "xbrl-report" => new XbrlReportAdapter(),
        "pdf-digital" => new PdfSourceAdapter(_pdfEvidenceStoreFactory()),
        _ => throw new NotSupportedException($"Adapter '{connection.Kind}' is not supported for source browsing.")
    };

    public void LoadProject(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var manager = new ProjectManager(fullPath);
        var project = manager.Load();
        var profileAvailable = ActivateProject(manager, project);
        if (profileAvailable) _ = LoadWorkspaceBindingsAsync(profileAvailable);
    }

    private bool ActivateProject(ProjectManager manager, MappingProject project)
    {
        var profileAvailable = RefreshProfileForProject(project);
        _session.Changed -= OnSessionChanged;
        _session.SelectionChanged -= OnSessionSelectionChanged;
        _session = new DesignerSession(project);
        _session.Changed += OnSessionChanged;
        _session.SelectionChanged += OnSessionSelectionChanged;
        SelectedNode = null;
        SelectedDestinationKind = SqliteDestinationKind;
        ProjectPath = manager.FullPath;
        _expectedLayoutGeneration = project.LayoutGeneration;
        ClearSourceAndRunResults();
        _savedProjectSnapshot = ProjectSerializer.Serialize(project);
        OnSessionChanged();
        RunSearch();
        StatusText = profileAvailable
            ? $"Opened {Path.GetFileName(manager.FullPath)}."
            : $"Opened {Path.GetFileName(manager.FullPath)}, but filing profile '{project.ProfileId}' "
                + $"({project.ProfileDigest}) is unresolved or owner-gated; saved bindings were not loaded.";
        return profileAvailable;
    }

    private async Task LoadWorkspaceBindingsAsync(bool profileAvailable)
    {
        if (!profileAvailable) return;
        await LoadSavedSourceAsync();
        await LoadSavedDestinationAsync();
    }

    private async Task LoadSavedSourceAsync()
    {
        var binding = Session.Project.Bindings.FirstOrDefault(candidate =>
            candidate.Kind != "param" && !string.IsNullOrWhiteSpace(candidate.Locator));
        if (binding is null || ProjectPath is null) return;

        var root = WorkspaceRoot();
        var path = Path.IsPathRooted(binding.Locator!)
            ? Path.GetFullPath(binding.Locator!)
            : Path.GetFullPath(Path.Combine(root, binding.Locator!));
        if (!File.Exists(path))
        {
            SourceInspectionSummary = $"Saved source is unavailable: {Path.GetFileName(path)}.";
            StatusText = $"Saved source file is missing from the project workspace: {Path.GetFileName(path)}.";
            return;
        }

        SelectedSourceBindingId = binding.BindingId;
        await LoadSourceAsync(path);
    }

    private void ClearSourceAndRunResults()
    {
        _suggestionSession = null;
        _acceptedSuggestionUndo.Clear();
        _suggestionRedoState = null;
        Interlocked.Increment(ref _sourceLoadGeneration);
        InvalidateMappingOperationIfStale();
        ClearDestinationSelection();
        IsSourceLoading = false;
        _sqliteSourcePath = null;
        _selectedSourceTable = null;
        ResetPdfReviewState();
        SelectedSourceBindingId = null;
        SelectedSourceColumn = null;
        SelectedSuggestion = null;
        SourceColumns.Clear();
        SourceTables.Clear();
        SourceInspectionSummary = "Choose a source file to inspect its schema, row identity and adapter capabilities.";
        ReplaceSuggestionRows(Array.Empty<SuggestionRow>());
        SuggestionFilter = "";
        PreviewFacts.Clear();
        SelectedPreviewFact = null;
        SelectedPreviewEvidence = null;
        _selectedPreviewPdfEvidence = null;
        PreviewResultSummary = "Run a preview or full execution to inspect generated facts and evidence.";
        Raise(nameof(SelectedPdfEvidence));
        Diagnostics.Clear();
        Raise(nameof(SourceColumns));
        Raise(nameof(SourceTables));
        Raise(nameof(HasSourceTables));
        Raise(nameof(SelectedSourceTable));
        Raise(nameof(AiSuggestions));
        Raise(nameof(FilteredAiSuggestions));
        Raise(nameof(PreviewFacts));
        Raise(nameof(Diagnostics));
    }

    private void ClearDestinationSelection()
    {
        Interlocked.Increment(ref _destinationLoadGeneration);
        InvalidateMappingOperationIfStale();
        _destinationPath = null;
        _destinationTable = null;
        _selectedDestinationKey = null;
        _destinationTableSchema = null;
        _preferredDestinationKey = null;
        DestinationTables.Clear();
        DestinationKeys.Clear();
        Raise(nameof(DestinationPath));
        Raise(nameof(DestinationPathDisplay));
        Raise(nameof(DestinationTables));
        Raise(nameof(HasDestinationTables));
        Raise(nameof(DestinationTable));
        Raise(nameof(DestinationKeys));
        Raise(nameof(CanSelectDestinationKey));
        Raise(nameof(SelectedDestinationKey));
        IsDestinationLoading = false;
        ResetDestinationPreviewSummary("Run a destination dry run to see its estimated changes.");
        ResetDestinationSchemaConfirmation(IsSqliteDestination
            ? "Choose an existing SQLite database to inspect its tables."
            : "Choose an output file in the workspace.");
    }

    private void ResetDestinationPreviewSummary(string message)
    {
        _destinationPreviewSemanticDigest = null;
        DestinationPreviewSummary = message;
    }

    private async Task LoadSavedDestinationAsync()
    {
        var plan = Session.Project.Destination;
        if (plan is null || ProjectPath is null) return;
        try
        {
            var destinationKind = DestinationKindOptions.FirstOrDefault(option =>
                StringComparer.Ordinal.Equals(option.Kind, plan.Kind));
            if (destinationKind is null)
            {
                ResetDestinationSchemaConfirmation($"Saved destination kind '{plan.Kind}' is unsupported by this desktop build.");
                return;
            }
            SelectedDestinationKind = destinationKind;
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
            var path = Path.IsPathRooted(plan.Locator)
                ? Path.GetFullPath(plan.Locator)
                : Path.GetFullPath(Path.Combine(projectDirectory, plan.Locator));
            if (DestinationPlanValidator.IsFileKind(plan.Kind))
                await LoadDestinationFileAsync(path);
            else
                await LoadDestinationDatabaseAsync(path, plan.Table, plan.BusinessKey?.ToArray());
        }
        catch (Exception ex)
        {
            ResetDestinationSchemaConfirmation($"Saved destination could not be inspected: {ex.Message}");
        }
    }

    private async Task<bool> SaveProjectAsync()
    {
        try
        {
            var path = ProjectPath;
            if (path is null)
            {
                if (_fileDialog is null) { StatusText = "Project file picker is unavailable."; return false; }
                path = await _fileDialog.SaveProjectAsync();
            }
            if (path is null) return false;
            SaveProjectTo(path);
            return true;
        }
        catch (Exception ex) { StatusText = $"Save failed: {ex.Message}"; return false; }
    }

    public async Task SaveProjectAsAsync()
    {
        if (_fileDialog is null) { StatusText = "Project file picker is unavailable."; return; }
        try
        {
            var path = await _fileDialog.SaveProjectAsync();
            if (path is null) return;
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && !PathsEqual(ProjectPath, fullPath))
            {
                StatusText = "Save As stopped because that path already contains another project. Choose a new path to avoid replacing it.";
                return;
            }
            SaveProjectTo(fullPath);
        }
        catch (Exception ex) { StatusText = $"Save As failed: {ex.Message}"; }
    }

    public void SaveProjectTo(string path)
    {
        var hadProjectPath = ProjectPath is not null;
        var fullPath = Path.GetFullPath(path);
        var manager = new ProjectManager(fullPath);
        var saved = manager.Save(Session.Project,
            PathsEqual(ProjectPath, manager.FullPath) ? _expectedLayoutGeneration : null);
        ProjectPath = manager.FullPath;
        _expectedLayoutGeneration = saved.NewLayoutGeneration;
        _savedProjectSnapshot = ProjectSerializer.Serialize(Session.Project);
        Raise(nameof(IsProjectDirty));
        RefreshTitle();
        StatusText = $"Saved {Path.GetFileName(fullPath)} (semantic revision {saved.NewSemanticRevision}).";
        if (!hadProjectPath && _destinationPath is null && Session.Project.Destination is not null)
            _ = LoadSavedDestinationAsync();
    }

    private void Validate()
    {
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        Diagnostics.Clear();
        var compiled = MappingJobService.ValidateProject(Session.Project, profile);
        if (compiled.Diagnostics.Items.Count == 0)
            Diagnostics.Add(new DiagnosticRow { Display = "Mapping graph and filing profile validated.", Severity = "info" });
        else
            ReplaceItems(Diagnostics, compiled.Diagnostics.Items.Select(ToDiagnosticRow));
        StatusText = compiled.Success ? "Validation complete." : "Validation found blocking issues.";
        Raise(nameof(Diagnostics));
    }

    private async Task PreviewAsync()
    {
        if (IsSourceLoading) { StatusText = "Wait for source loading to finish before previewing."; return; }
        if (IsBusy) return;
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        var project = Session.Project;
        var operation = BeginMappingOperation(profile, "Running bounded preview…");
        var cancellationToken = operation.Cancellation.Token;
        try
        {
            var root = WorkspaceRoot();
            var connections = BuildConnections(root);
            await using var worker = _workerFactory.Create(profile);
            using var result = await new MappingJobService(profile, worker).RunAsync(
                project, connections, ReadParameters(), preview: true, cancellationToken,
                CreateMappingProgress(operation));
            if (!IsMappingOperationCurrent(operation))
            {
                StatusText = OperationStoppedStatus(operation, "Preview");
                return;
            }
            ShowRunResult(result);
            StatusText = result.Manifest.Outcome switch
            {
                RunOutcome.Cancelled => "Preview canceled by the validation worker. Sampled output is unavailable.",
                RunOutcome.Success or RunOutcome.SuccessWithWarnings
                    => $"Preview complete: {result.Manifest.Outcome}. Sampled output is partial.",
                _ => $"Preview ended with {result.Manifest.Outcome}. No validated sample is available."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Preview");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Preview");
        }
        catch (Exception ex)
        {
            Diagnostics.Clear();
            Diagnostics.Add(new DiagnosticRow { Display = ex.Message, Severity = "error" });
            Raise(nameof(Diagnostics));
            StatusText = "Preview failed.";
        }
        finally { FinishMappingOperation(operation); }
    }

    private async Task ExecuteAsync()
    {
        if (IsSourceLoading) { StatusText = "Wait for source loading to finish before executing."; return; }
        if (IsBusy) return;
        if (!TryResolveProfile(AllowSyntheticProfile, out var profile)) return;
        var project = Session.Project;
        var projectDigest = project.ComputeSemanticDigest();
        var gate = PublicationGate.Evaluate(project, profile, AllowSyntheticProfile);
        if (gate.HasBlockingErrors)
        {
            Diagnostics.Clear();
            ReplaceItems(Diagnostics, gate.Items.Select(ToDiagnosticRow));
            Raise(nameof(Diagnostics));
            StatusText = "Execution blocked by the publication authorization gate.";
            return;
        }
        if (_fileDialog is null) { StatusText = "Report file picker is unavailable."; return; }

        string? outputPath;
        try { outputPath = await _fileDialog.SaveReportAsync(); }
        catch (Exception ex) { StatusText = $"Output selection failed: {ex.Message}"; return; }
        if (outputPath is null) return;
        if (!ReferenceEquals(project, Session.Project)
            || !StringComparer.Ordinal.Equals(projectDigest, project.ComputeSemanticDigest()))
        {
            StatusText = "Execution stopped because the mapping changed while choosing the output path. Review it and start a new run.";
            return;
        }

        var operation = BeginMappingOperation(profile, "Executing and validating report…");
        var cancellationToken = operation.Cancellation.Token;
        try
        {
            var root = WorkspaceRoot();
            var connections = BuildConnections(root);
            await using var worker = _workerFactory.Create(profile);
            var service = new MappingJobService(profile, worker);
            using var result = await service.RunAsync(
                project, connections, ReadParameters(), preview: false, cancellationToken,
                CreateMappingProgress(operation));
            if (!IsMappingOperationCurrent(operation))
            {
                StatusText = OperationStoppedStatus(operation, "Execution");
                return;
            }
            ShowRunResult(result);
            if (!result.IsPublishable)
            {
                StatusText = result.Manifest.Outcome == RunOutcome.Cancelled
                    ? "Execution canceled by the validation worker. No artifact was published."
                    : $"Execution ended with {result.Manifest.Outcome}. No artifact was published.";
                return;
            }
            var publisher = new PublicationService(new RunJournal());
            PublicationReceipt receipt;
            if (result.LargeArtifact is { } largeArtifact)
            {
                receipt = await publisher.PublishAuthorizedLargeArtifactAsync(
                    outputPath, largeArtifact, root, project, profile, AllowSyntheticProfile, result,
                    (stagedPath, token) => service.ValidateStagedArtifactAsync(
                        stagedPath, outputPath, result, token), cancellationToken);
            }
            else
            {
                var payload = System.Text.Encoding.UTF8.GetBytes(result.XbrlJson);
                receipt = await publisher.PublishAuthorizedAsync(
                    outputPath, payload, root,
                    project, profile, AllowSyntheticProfile, result,
                    (stagedPath, token) => service.ValidateStagedArtifactAsync(
                        stagedPath, outputPath, result, token), cancellationToken);
            }
            StatusText = $"Published {Path.GetFileName(receipt.DestinationPath)}; digest {receipt.FinalDigest}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Execution");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = OperationStoppedStatus(operation, "Execution");
        }
        catch (Exception ex)
        {
            Diagnostics.Clear();
            Diagnostics.Add(new DiagnosticRow { Display = ex.Message, Severity = "error" });
            Raise(nameof(Diagnostics));
            StatusText = "Execution failed; no unvalidated output was published.";
        }
        finally { FinishMappingOperation(operation); }
    }

    private bool TryResolveProfile(bool allowSyntheticProfile, out FilingProfile profile)
    {
        var resolution = _profileResolver.Resolve(Session.Project, allowSyntheticProfile);
        if (!resolution.Success)
        {
            ReplaceProfileResolutionDiagnostics(Session.Project, resolution);
            StatusText = "Operation blocked by filing-profile resolution.";
            profile = null!;
            return false;
        }

        profile = resolution.Profile!;
        return true;
    }

    private bool RefreshProfileForProject(MappingProject project)
    {
        InvalidateTaxonomySearch();
        SelectedConcept = null;
        ConceptResults.Clear();
        HasNextPage = false;
        Raise(nameof(ConceptResults));
        Raise(nameof(HasNextPage));
        var resolution = _profileResolver.Resolve(project, allowSyntheticProfile: true);
        if (resolution.Success)
        {
            _profile = resolution.Profile!;
            _taxonomy = CreateTaxonomyIndex(_profile);
            RefreshSelectedConceptOccurrences();
            return true;
        }

        // Keep the designer inspectable without substituting synthetic taxonomy data.
        // This identity-only profile contains no concepts or validation rules and is
        // never resolved by a profile-dependent operation.
        _profile = CreateUnresolvedProfileIdentity(project);
        _taxonomy = CreateUnresolvedDisplayTaxonomyIndex(_profile);
        RefreshSelectedConceptOccurrences();
        ReplaceProfileResolutionDiagnostics(project, resolution);
        StatusText = "The project's filing profile is unresolved or owner-gated; profile-dependent actions are blocked.";
        return false;
    }

    private void ReplaceProfileResolutionDiagnostics(MappingProject project, FilingProfileResolution resolution)
    {
        Diagnostics.Clear();
        ReplaceItems(Diagnostics, resolution.Diagnostics.Items.Select(ToDiagnosticRow));
        Diagnostics.Add(new DiagnosticRow
        {
            Display = $"Project profile identity: ProfileId='{FormatProfileIdentityPart(project.ProfileId)}', "
                + $"ProfileDigest='{FormatProfileIdentityPart(project.ProfileDigest)}'. No profile was substituted.",
            Severity = "error"
        });
        Raise(nameof(Diagnostics));
    }

    private static FilingProfile CreateUnresolvedProfileIdentity(MappingProject project)
        => new()
        {
            ProfileId = project.ProfileId ?? string.Empty,
            Digest = project.ProfileDigest ?? string.Empty,
            Concepts = Array.Empty<ConceptMetadata>(),
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal)
        };

    private static TaxonomyIndex? CreateUnresolvedDisplayTaxonomyIndex(FilingProfile profile)
        => string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.Digest)
            ? null
            : new TaxonomyIndex(profile, Array.Empty<PresentationLink>());

    private static string FormatProfileIdentityPart(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(missing)" : value;

    private TaxonomyIndex CreateTaxonomyIndex(FilingProfile profile)
        => _taxonomyIndexStoreFactory?.Invoke().LoadOrBuild(profile).Index
            ?? new TaxonomyIndex(profile);

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
        ReplaceItems(Diagnostics, result.Diagnostics.Items.Select(ToDiagnosticRow));
        PreviewFacts.Clear();
        SelectedPreviewFact = null;
        SelectedPreviewEvidence = null;
        _selectedPreviewPdfEvidence = null;
        Raise(nameof(SelectedPdfEvidence));
        foreach (var fact in result.Facts.Take(500))
        {
            var value = fact.IsNil ? "nil" : fact.Value?.ToString() ?? fact.TextValue ?? "(empty)";
            var unit = fact.Unit?.ToString() ?? fact.CompoundUnit?.CanonicalKey;
            var sourceNodes = Session.Project.Nodes
                .Where(node => node.NodeType == NodeTypeConstants.FactConstruct
                    && StringComparer.Ordinal.Equals(node.Parameters.GetValueOrDefault("concept")?.ToString(), fact.ConceptName))
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .ToArray();
            var stage = sourceNodes.Length switch
            {
                0 => "Fact construction stage could not be matched to a saved mapping node.",
                1 => $"Fact construction · {sourceNodes[0].DisplayName} ({sourceNodes[0].NodeId}). "
                    + DescribeFactInputSources(sourceNodes[0], "value"),
                _ => "Fact construction node is ambiguous for this concept; candidate paths: "
                    + string.Join(" | ", sourceNodes.Select(node =>
                        $"{node.DisplayName} ({node.NodeId}): {DescribeFactInputSources(node, "value")}"))
            };
            PreviewFacts.Add(new FactRow
            {
                SemanticFact = fact,
                Display = $"{fact.ConceptName} = {value}{(unit is null ? "" : " " + unit)}; {fact.Period}",
                StageSummary = stage,
                Evidence = fact.ContributingSources.Select(source => new PreviewEvidenceRow(source)).ToArray()
            });
        }
        var errors = result.Diagnostics.Items.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var warnings = result.Diagnostics.Items.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        var resultFactCount = result.LargeArtifact?.FactCount ?? result.Facts.Count;
        var truncationNote = resultFactCount > PreviewFacts.Count
            ? $" Showing the first {PreviewFacts.Count} of {resultFactCount} fact(s)."
            : "";
        PreviewResultSummary = result.Manifest.IsPreview
            ? $"Preview only · {PreviewFacts.Count} sampled fact(s). "
                + $"{result.SampledTotals.Count} sampled aggregate(s) are partial. This result is not a final report and cannot be published."
            : $"Full run · {resultFactCount} fact(s) · {errors} error(s), {warnings} warning(s).{truncationNote} See Diagnostics for details.";
        SelectedPreviewFact = PreviewFacts.FirstOrDefault();
        Raise(nameof(Diagnostics));
        Raise(nameof(PreviewFacts));
    }

    private void OpenSelectedPreviewEvidence()
    {
        if (SelectedPreviewEvidence?.PdfEvidence is not { } evidence || !CanOpenSelectedPreviewEvidence)
            return;

        _selectedPreviewPdfEvidence = evidence;
        CurrentPdfPageNumber = evidence.PageNumber;
        IsPdfReviewTabSelected = true;
        Raise(nameof(SelectedPdfEvidence));
        StatusText = $"Showing source evidence on PDF page {evidence.PageNumber} for the selected fact.";
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

    private void ResetTaxonomySearchForCriteriaChange()
    {
        InvalidateTaxonomySearch();
        _resultPage = 0;
        ConceptResults.Clear();
        HasNextPage = false;
        TaxonomySearchStatus = "Search criteria changed. Run Search to load matching concepts.";
        Raise(nameof(ConceptResults));
        Raise(nameof(ResultPage));
        Raise(nameof(HasNextPage));
        Raise(nameof(HasPreviousPage));
    }

    private void InvalidateTaxonomySearch()
    {
        lock (_taxonomySearchGate)
        {
            _taxonomySearchGeneration++;
            var previous = _taxonomySearchCancellation;
            _taxonomySearchCancellation = null;
            previous?.Cancel();
            IsTaxonomySearchBusy = false;
            CancelTaxonomySearchCommand?.NotifyCanExecuteChanged();
        }
    }

    private void CancelTaxonomySearch()
    {
        lock (_taxonomySearchGate)
        {
            if (_taxonomySearchCancellation is null) return;
            InvalidateTaxonomySearch();
            TaxonomySearchStatus = "Taxonomy search cancelled.";
        }
    }

    private void RunSearch(bool keepPage = false)
    {
        if (!TryResolveProfile(AllowSyntheticProfile, out _))
        {
            InvalidateTaxonomySearch();
            ConceptResults.Clear();
            HasNextPage = false;
            TaxonomySearchStatus = "Search is unavailable until the project's filing profile is installed.";
            Raise(nameof(ConceptResults));
            Raise(nameof(HasNextPage));
            return;
        }

        CancellationTokenSource? previous;
        CancellationTokenSource cancellation;
        int generation;
        TaxonomyIndex index;
        string query;
        string language;
        int page;
        lock (_taxonomySearchGate)
        {
            if (!keepPage) _resultPage = 0;
            previous = _taxonomySearchCancellation;
            cancellation = new CancellationTokenSource();
            generation = ++_taxonomySearchGeneration;
            index = _taxonomy!;
            query = SearchTerm;
            language = SearchLanguage;
            page = _resultPage;
            _taxonomySearchCancellation = cancellation;
            IsTaxonomySearchBusy = true;
            TaxonomySearchStatus = "Searching the loaded taxonomy…";
            Raise(nameof(ResultPage));
            Raise(nameof(HasPreviousPage));
            previous?.Cancel();
        }
        _ = CompleteTaxonomySearchAsync(index, query, language, page, generation, cancellation);
    }

    private async Task CompleteTaxonomySearchAsync(
        TaxonomyIndex index,
        string query,
        string language,
        int page,
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await index.SearchAsync(
                query, language, page, TaxonomyIndex.DefaultPageSize, cancellation.Token);
            lock (_taxonomySearchGate)
            {
                if (!IsCurrentTaxonomySearch(index, query, language, page, generation, cancellation)) return;

                ConceptResults.Clear();
                foreach (var concept in result.Items)
                {
                    ConceptResults.Add(new ConceptRow
                    {
                        QualifiedName = concept.QualifiedName,
                        Label = TaxonomyIndex.ResolveLabel(concept, language),
                        DataType = concept.DataType,
                        PeriodType = concept.PeriodType,
                        IsFactTargetable = TaxonomyIndex.IsFactTargetable(concept)
                    });
                }
                HasNextPage = result.HasNext;
                var countText = result.TotalCount == 1 ? "1 concept" : $"{result.TotalCount} concepts";
                TaxonomySearchStatus = $"{countText}, page {result.Page + 1}.";
                Raise(nameof(ConceptResults));
                Raise(nameof(ResultPage));
                Raise(nameof(HasNextPage));
                Raise(nameof(HasPreviousPage));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            lock (_taxonomySearchGate)
            {
                if (IsCurrentTaxonomySearch(index, query, language, page, generation, cancellation))
                    TaxonomySearchStatus = "Taxonomy search cancelled.";
            }
        }
        catch (Exception ex)
        {
            lock (_taxonomySearchGate)
            {
                if (IsCurrentTaxonomySearch(index, query, language, page, generation, cancellation))
                    TaxonomySearchStatus = $"Taxonomy search failed: {ex.Message}";
            }
        }
        finally
        {
            lock (_taxonomySearchGate)
            {
                if (ReferenceEquals(_taxonomySearchCancellation, cancellation))
                {
                    _taxonomySearchCancellation = null;
                    IsTaxonomySearchBusy = false;
                    CancelTaxonomySearchCommand?.NotifyCanExecuteChanged();
                }
                cancellation.Dispose();
            }
        }
    }

    private bool IsCurrentTaxonomySearch(
        TaxonomyIndex index,
        string query,
        string language,
        int page,
        int generation,
        CancellationTokenSource cancellation)
        => generation == _taxonomySearchGeneration
           && ReferenceEquals(index, _taxonomy)
           && ReferenceEquals(cancellation, _taxonomySearchCancellation)
           && StringComparer.Ordinal.Equals(query, SearchTerm)
           && StringComparer.Ordinal.Equals(language, SearchLanguage)
           && page == _resultPage;

    private void RefreshSelectedConceptOccurrences()
    {
        SelectedConceptOccurrences.Clear();
        if (SelectedConcept is null)
        {
            SelectedConceptOccurrencesStatus = "Select a concept to inspect its presentation paths.";
            Raise(nameof(SelectedConceptOccurrences));
            return;
        }

        if (_taxonomy is null)
        {
            SelectedConceptOccurrencesStatus = "Presentation paths are unavailable until the project's filing profile is resolved.";
            Raise(nameof(SelectedConceptOccurrences));
            return;
        }

        var occurrences = _taxonomy.PresentationOccurrencesFor(SelectedConcept.QualifiedName);
        foreach (var occurrence in occurrences)
        {
            var path = string.Join(" → ", occurrence.ConceptPath.Select(qualifiedName =>
            {
                var concept = _taxonomy.Get(qualifiedName);
                return concept is null
                    ? qualifiedName
                    : $"{TaxonomyIndex.ResolveLabel(concept, SearchLanguage)} ({qualifiedName})";
            }));
            SelectedConceptOccurrences.Add(new PresentationOccurrenceRow(occurrence.RoleUri, path));
        }

        SelectedConceptOccurrencesStatus = occurrences.Count == 0
            ? "No presentation path is loaded for this concept in the selected taxonomy profile."
            : occurrences.Count >= 512
                ? "Showing the first 512 presentation paths (bounded)."
                : $"{occurrences.Count} presentation path(s) in loaded taxonomy relationships.";
        Raise(nameof(SelectedConceptOccurrences));
    }

    private static void ReplaceItems<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    private void ReplaceSuggestionRows(IEnumerable<SuggestionRow> rows)
    {
        _unsavedSuggestionDrafts.Clear();
        if (_selectedSuggestion is not null)
        {
            _selectedSuggestion = null;
            Raise(nameof(SelectedSuggestion));
        }
        ClearSelectedSuggestionEditor();
        foreach (var existing in AiSuggestions)
            existing.PropertyChanged -= OnSuggestionRowPropertyChanged;

        var materialized = rows.ToArray();
        ReplaceItems(AiSuggestions, materialized);
        foreach (var row in materialized)
            row.PropertyChanged += OnSuggestionRowPropertyChanged;

        RefreshFilteredSuggestions();
        RefreshSuggestionBatchSummary();
        Raise(nameof(AiSuggestions));
        Raise(nameof(CanAcceptSelectedSuggestionBatch));
        AcceptSelectedSuggestionBatchCommand?.NotifyCanExecuteChanged();
    }

    private void OnSuggestionRowPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(SuggestionRow.IsBatchSelected)) return;
        RefreshSuggestionBatchSummary();
        Raise(nameof(CanAcceptSelectedSuggestionBatch));
        AcceptSelectedSuggestionBatchCommand?.NotifyCanExecuteChanged();
    }

    private void RefreshSuggestionBatchSummary()
    {
        var selected = AiSuggestions.Where(row => row.IsBatchSelected).ToArray();
        if (selected.Length == 0)
        {
            SuggestionBatchSummary = "Choose one candidate per source field; conflicting or stale selections are rejected as a whole.";
            return;
        }

        var sourceConflicts = selected.GroupBy(row => row.SourceFieldId, StringComparer.Ordinal)
            .Count(group => group.Count() > 1);
        var targetConflicts = selected.Where(row => !string.IsNullOrWhiteSpace(row.TargetNodeId))
            .GroupBy(row => row.TargetNodeId, StringComparer.Ordinal)
            .Count(group => group.Count() > 1);
        SuggestionBatchSummary = sourceConflicts > 0 || targetConflicts > 0
            ? $"{selected.Length} candidate(s) checked; resolve {sourceConflicts} source conflict(s) and {targetConflicts} target conflict(s). No partial apply is allowed."
            : $"{selected.Length} candidate(s) checked across {selected.Select(row => row.SourceFieldId).Distinct(StringComparer.Ordinal).Count()} source field(s). Batch acceptance validates all together and creates one undo step.";
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

    private static MappingProject BuildEmptyProject()
    {
        var profile = SyntheticProfile.Build();
        return new MappingProject
        {
            ProjectId = Id.New(),
            Name = "Untitled Mapping",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest
        };
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
