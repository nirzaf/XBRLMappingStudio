using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI;

/// <summary>Disposition of one suggestion inside the analyst workflow (AI05).</summary>
public enum SuggestionState
{
    Pending,
    Accepted,
    Rejected,
}

/// <summary>Result of a session mutation attempt; rejected mutations leave state untouched.</summary>
public sealed record SessionOutcome(bool Applied, string? Reason, DraftValidationResult? Validation);

/// <summary>
/// AI06 governed-actor context applied to every persisted audit row from one session.
/// In the local, single-workspace build the actor is captured from the desktop
/// credentials and is explicitly not an immutable trusted reviewer; the multi-user
/// requirement in section 9 is deferred. When <see cref="ProviderSnapshotLimitation"/>
/// is non-empty it is stored verbatim so a downstream reviewer sees the disclosure
/// required by spec §6.2 step 5 for a provider alias without a pinned deployment.
/// </summary>
public sealed record SuggestionSessionContext(
    string Actor,
    string Method = "retrieval",
    string ProviderConfigId = "local-retrieval",
    string PromptTemplateRevision = "",
    string ProviderSnapshotLimitation = "");

/// <summary>
/// The analyst-facing workflow (AI05): suggestions are applied to a *draft* project
/// only after <see cref="DraftValidator"/> accepts them. Accept / Edit / Reject /
/// batch accept are recorded in the audit log; Undo restores the full prior draft
/// snapshot (serializable round-trip) so an approved graph can always be walked back.
/// Nothing here ever touches the source-of-truth project passed in the constructor —
/// callers adopt <see cref="Draft"/> explicitly.
/// </summary>
public sealed class SuggestionSession
{
    private readonly DraftValidator _validator;
    private readonly List<Suggestion> _suggestions = new();
    private readonly Dictionary<string, SuggestionState> _states = new(StringComparer.Ordinal);
    private readonly Stack<(string Project, Dictionary<string, SuggestionState> States)> _undoSnapshots = new();
    // AI06: keyed by AuditRecord.Sequence so the sink's context factory can pair the
    // durable row with the exact proposal that was on screen when the analyst decided.
    private readonly Dictionary<int, (string Original, string Approved, IReadOnlyList<string> CandidateIds)> _pendingPayloads = new();
    private readonly SuggestionSessionContext _sessionContext;

    private MappingProject _draft;

    public AuditLog Audit { get; }

    /// <summary>The working draft that accepted suggestions mutate. Not the original project.</summary>
    public MappingProject Draft => _draft;

    public IReadOnlyList<Suggestion> Suggestions => _suggestions;

    public SuggestionState StateOf(string suggestionId) =>
        _states.TryGetValue(suggestionId, out var s) ? s : SuggestionState.Pending;

    public SuggestionSession(MappingProject project, DraftValidator validator,
        IAuditSink? auditSink = null, SuggestionSessionContext? sessionContext = null)
    {
        // Clone the project through the serializer so the original is structurally immutable.
        _draft = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        _validator = validator;
        _sessionContext = sessionContext ?? new SuggestionSessionContext(Actor: Environment.UserName);
        // Wire the sink when one is supplied so every recorded decision is durable.
        // The contextFactory reads the same DraftValidator view the UI saw and never
        // includes raw workbook content: candidate ids, source field, and concept only.
        Audit = auditSink is null
            ? new AuditLog()
            : new AuditLog(auditSink, record => BuildAuditContext(record));
    }

    private AuditContext? BuildAuditContext(AuditRecord record)
    {
        // Records without a paired payload (accept-blocked) still describe an action on
        // the current draft, so we emit a context; only pure-noise records are dropped.
        var payload = _pendingPayloads.TryGetValue(record.Sequence, out var p)
            ? p : (Original: "", Approved: "", CandidateIds: Array.Empty<string>());
        return new AuditContext(
            ProjectId: _draft.ProjectId,
            SemanticRevision: _draft.SemanticRevision,
            CandidateIds: payload.CandidateIds,
            ProviderConfigId: _sessionContext.ProviderConfigId,
            Method: _sessionContext.Method,
            Actor: _sessionContext.Actor,
            PromptTemplateRevision: _sessionContext.PromptTemplateRevision,
            OriginalProposalJson: payload.Original,
            ApprovedEditJson: payload.Approved,
            ProviderSnapshotLimitation: _sessionContext.ProviderSnapshotLimitation);
    }

    private static string SerializeDraft(DraftMapping draft) => System.Text.Json.JsonSerializer.Serialize(new
    {
        concept = AiMetadataSanitizer.Redact(draft.Concept),
        unit = AiMetadataSanitizer.Redact(draft.Unit),
        periodType = AiMetadataSanitizer.Redact(draft.PeriodType),
        decimals = draft.Decimals,
        language = AiMetadataSanitizer.Redact(draft.Language),
        sourceField = AiMetadataSanitizer.Redact(draft.SourceField),
    });

    /// <summary>Load a batch of suggestions produced by <see cref="AiSuggestionService"/>.</summary>
    public void LoadSuggestions(IEnumerable<Suggestion> suggestions)
    {
        foreach (var s in suggestions)
        {
            if (_states.ContainsKey(s.SuggestionId)) continue;
            _suggestions.Add(s);
            _states[s.SuggestionId] = SuggestionState.Pending;
        }
    }

    public SessionOutcome Accept(string suggestionId)
    {
        var suggestion = Find(suggestionId);
        if (suggestion is null)
            return new SessionOutcome(false, "Unknown suggestion id.", null);
        if (_states[suggestionId] != SuggestionState.Pending)
            return new SessionOutcome(false, $"Suggestion already {_states[suggestionId]}.", null);

        var validation = _validator.Validate(new[] { suggestion.Draft });
        if (!validation.Accepted)
        {
            Audit.Record(new AuditRecord(
                Audit.NextSequence, "accept-blocked", suggestion.SourceField,
                suggestion.Draft.Concept ?? "(none)", validation.Diagnostics.Count,
                validation.Diagnostics.Select(d => d.Code).ToList()));
            return new SessionOutcome(false, "Draft failed validation; project untouched.", validation);
        }

        var previousFaultCount = Audit.PersistenceFaults.Count;
        PushSnapshot();
        ApplyDraftToProject(suggestion.Draft);
        _states[suggestionId] = SuggestionState.Accepted;
        // AI06: pre-register the payload the sink will pick up on this sequence so the
        // durable row can reconstruct what was proposed and what was approved.
        var seq = Audit.NextSequence;
        _pendingPayloads[seq] = (SerializeDraft(suggestion.Draft), SerializeDraft(suggestion.Draft),
            new[] { suggestion.SuggestionId });
        Audit.Record(new AuditRecord(
            seq, "accept", suggestion.SourceField,
            suggestion.Draft.Concept ?? "(none)", 0, Array.Empty<string>()));
        if (Audit.PersistenceFaults.Count > previousFaultCount)
        {
            RestoreTopSnapshot();
            return new SessionOutcome(false,
                "Durable audit persistence failed; the accepted draft was rolled back.", validation);
        }
        return new SessionOutcome(true, null, validation);
    }

    /// <summary>Replace a pending suggestion's payload (analyst edit) before acceptance.</summary>
    public SessionOutcome Edit(string suggestionId, DraftMapping edited)
    {
        var index = _suggestions.FindIndex(s => s.SuggestionId == suggestionId);
        if (index < 0) return new SessionOutcome(false, "Unknown suggestion id.", null);
        if (_states[suggestionId] != SuggestionState.Pending)
            return new SessionOutcome(false, "Only pending suggestions can be edited.", null);

        var old = _suggestions[index];
        var previousFaultCount = Audit.PersistenceFaults.Count;
        var updated = old with { Draft = edited, IsAnalystEdited = true };
        _suggestions[index] = updated;
        var seq = Audit.NextSequence;
        _pendingPayloads[seq] = (SerializeDraft(old.Draft), SerializeDraft(edited),
            new[] { old.SuggestionId });
        Audit.Record(new AuditRecord(
            seq, "edit", edited.SourceField, edited.Concept ?? "(none)",
            0, Array.Empty<string>()));
        if (Audit.PersistenceFaults.Count > previousFaultCount)
        {
            _suggestions[index] = old;
            return new SessionOutcome(false,
                "Durable audit persistence failed; the suggestion edit was rolled back.", null);
        }
        return new SessionOutcome(true, null, null);
    }

    public SessionOutcome Reject(string suggestionId)
    {
        var suggestion = Find(suggestionId);
        if (suggestion is null) return new SessionOutcome(false, "Unknown suggestion id.", null);
        if (_states[suggestionId] != SuggestionState.Pending)
            return new SessionOutcome(false, "Only pending suggestions can be rejected.", null);

        var previousFaultCount = Audit.PersistenceFaults.Count;
        _states[suggestionId] = SuggestionState.Rejected;
        var seq = Audit.NextSequence;
        _pendingPayloads[seq] = (SerializeDraft(suggestion.Draft), "", new[] { suggestion.SuggestionId });
        Audit.Record(new AuditRecord(
            seq, "reject", suggestion.SourceField,
            suggestion.Draft.Concept ?? "(none)", 0, Array.Empty<string>()));
        if (Audit.PersistenceFaults.Count > previousFaultCount)
        {
            _states[suggestionId] = SuggestionState.Pending;
            return new SessionOutcome(false,
                "Durable audit persistence failed; the rejection was rolled back.", null);
        }
        return new SessionOutcome(true, null, null);
    }

    /// <summary>Batch accept: validates the whole surviving set together (cross-draft
    /// duplicates are caught), then applies as one transaction with a single undo point.</summary>
    public SessionOutcome AcceptAllValid()
    {
        var pendingIds = _suggestions
            .Where(suggestion => _states[suggestion.SuggestionId] == SuggestionState.Pending)
            .Select(suggestion => suggestion.SuggestionId)
            .ToArray();
        return pendingIds.Length == 0
            ? new SessionOutcome(false, "Nothing pending.", null)
            : AcceptSelectedValid(pendingIds);
    }

    /// <summary>
    /// Batch accept an analyst-selected subset of pending candidates. All selected
    /// drafts are validated together, then applied with one undo snapshot and one
    /// durable batch audit record. A conflict or audit failure leaves the whole set
    /// unchanged.
    /// </summary>
    public SessionOutcome AcceptSelectedValid(IReadOnlyCollection<string> suggestionIds)
    {
        ArgumentNullException.ThrowIfNull(suggestionIds);
        if (suggestionIds.Count == 0)
            return new SessionOutcome(false, "Nothing selected for batch acceptance.", null);
        if (suggestionIds.Any(string.IsNullOrWhiteSpace))
            return new SessionOutcome(false,
                "Every selected suggestion must be a distinct pending item in the active review session.", null);

        var selectedIds = new HashSet<string>(suggestionIds, StringComparer.Ordinal);
        if (selectedIds.Count != suggestionIds.Count
            || selectedIds.Any(id => !_states.TryGetValue(id, out var state) || state != SuggestionState.Pending))
            return new SessionOutcome(false,
                "Every selected suggestion must be a distinct pending item in the active review session.", null);

        var pending = _suggestions
            .Where(suggestion => selectedIds.Contains(suggestion.SuggestionId))
            .ToList();
        if (pending.Count == 0) return new SessionOutcome(false, "Nothing pending.", null);

        var competing = pending
            .GroupBy(suggestion => suggestion.Draft.SourceField, StringComparer.Ordinal)
            .Where(group => group.Select(suggestion => (
                suggestion.Draft.Concept,
                suggestion.Draft.Unit,
                suggestion.Draft.PeriodType,
                suggestion.Draft.Decimals,
                suggestion.Draft.Language,
                suggestion.Draft.DimensionName,
                suggestion.Draft.DimensionMember)).Distinct().Count() > 1)
            .ToArray();
        if (competing.Length > 0)
        {
            var conflicts = competing.Select(group => new Diagnostic
            {
                Code = DiagnosticCodes.AiInvalidDraft,
                Subsystem = DiagnosticSubsystem.AI,
                Stage = DiagnosticStage.Validation,
                Severity = DiagnosticSeverity.Error,
                IsBlocking = true,
                Message = $"Source field '{group.Key}' has competing pending mapping alternatives; keep one candidate before batch acceptance."
            }).ToArray();
            return new SessionOutcome(false,
                "Batch contains competing suggestions for one or more source fields; nothing applied.",
                new DraftValidationResult(false, conflicts));
        }

        var validation = _validator.Validate(pending.Select(p => p.Draft).ToList());
        if (!validation.Accepted)
            return new SessionOutcome(false, "Batch contains invalid drafts; nothing applied.", validation);

        var previousFaultCount = Audit.PersistenceFaults.Count;
        PushSnapshot();
        foreach (var s in pending)
        {
            ApplyDraftToProject(s.Draft);
            _states[s.SuggestionId] = SuggestionState.Accepted;
        }
        var batchSeq = Audit.NextSequence;
        _pendingPayloads[batchSeq] = (
            System.Text.Json.JsonSerializer.Serialize(pending.Select(s => s.Draft).Select(SerializeDraft).ToArray()),
            System.Text.Json.JsonSerializer.Serialize(pending.Select(s => s.Draft).Select(SerializeDraft).ToArray()),
            pending.Select(s => s.SuggestionId).ToArray());
        Audit.Record(new AuditRecord(
            batchSeq, "batch-accept", "(multiple)", "(multiple)",
            0, Array.Empty<string>()));
        if (Audit.PersistenceFaults.Count > previousFaultCount)
        {
            RestoreTopSnapshot();
            return new SessionOutcome(false,
                "Durable audit persistence failed; the batch was rolled back.", validation);
        }
        return new SessionOutcome(true, null, validation);
    }

    public SessionOutcome Undo()
    {
        if (_undoSnapshots.Count == 0)
            return new SessionOutcome(false, "Nothing to undo.", null);

        // Roll back the whole transaction only when the undo decision can be persisted.
        var (payload, states) = _undoSnapshots.Pop();
        var currentDraft = ProjectSerializer.Serialize(_draft);
        var currentStates = new Dictionary<string, SuggestionState>(_states, StringComparer.Ordinal);
        _draft = ProjectSerializer.Deserialize(payload);
        foreach (var (id, state) in states)
            _states[id] = state;
        var previousFaultCount = Audit.PersistenceFaults.Count;
        var undoSeq = Audit.NextSequence;
        _pendingPayloads[undoSeq] = ("", "", Array.Empty<string>());
        Audit.Record(new AuditRecord(
            undoSeq, "undo", "(project)", "(snapshot)", 0, Array.Empty<string>()));
        if (Audit.PersistenceFaults.Count > previousFaultCount)
        {
            _draft = ProjectSerializer.Deserialize(currentDraft);
            foreach (var (id, state) in currentStates)
                _states[id] = state;
            _undoSnapshots.Push((payload, states));
            return new SessionOutcome(false,
                "Durable audit persistence failed; the undo was rolled back.", null);
        }
        return new SessionOutcome(true, null, null);
    }

    public bool CanUndo => _undoSnapshots.Count > 0;

    private void PushSnapshot() =>
        _undoSnapshots.Push((ProjectSerializer.Serialize(_draft),
            new Dictionary<string, SuggestionState>(_states, StringComparer.Ordinal)));

    private void RestoreTopSnapshot()
    {
        if (_undoSnapshots.Count == 0) return;
        var (payload, states) = _undoSnapshots.Pop();
        _draft = ProjectSerializer.Deserialize(payload);
        foreach (var (id, state) in states)
            _states[id] = state;
    }

    private Suggestion? Find(string id) =>
        _suggestions.FirstOrDefault(s => s.SuggestionId == id);

    /// <summary>
    /// Materialize one accepted draft as excelField → factConstruct nodes and edges on
    /// the draft project. Node/edge IDs are derived from the mapping id so re-adding the
    /// same draft is idempotent (mirrors the structural shape the DraftValidator proved).
    /// </summary>
    private void ApplyDraftToProject(DraftMapping d)
    {
        var factNodeId = "fact-" + d.MappingId;
        if (_draft.Nodes.Any(n => n.NodeId == factNodeId)) return;

        if (!_draft.Nodes.Any(n => n.NodeId == "entity-param"))
        {
            _draft.Nodes.Add(new MappingNode
            {
                NodeId = "entity-param",
                NodeType = Engine.NodeTypeConstants.RunParameter,
                DisplayName = "draft entity",
                Parameters = { ["name"] = "entityIdentifier", ["shape"] = "String" },
                OutputPorts = { new Project.PortDefinition("value", Project.PortShape.String, Project.PortCardinality.Single) },
            });
        }

        _draft.Nodes.Add(new MappingNode
        {
            NodeId = "src-" + d.MappingId,
            NodeType = Engine.NodeTypeConstants.ExcelField,
            DisplayName = d.SourceField,
            Parameters = { ["bindingId"] = "draft-binding", ["column"] = d.SourceField },
            OutputPorts = { new Project.PortDefinition("value", Project.PortShape.Decimal, Project.PortCardinality.Single) },
        });

        var fact = new MappingNode
        {
            NodeId = factNodeId,
            NodeType = Engine.NodeTypeConstants.FactConstruct,
            DisplayName = $"fact {d.Concept}",
            InputPorts =
            {
                new Project.PortDefinition("value", Project.PortShape.Decimal, Project.PortCardinality.Single, IsRequired: false),
                new Project.PortDefinition("entity", Project.PortShape.String, Project.PortCardinality.Single, IsRequired: true),
            },
            OutputPorts = { new Project.PortDefinition("fact", Project.PortShape.Fact, Project.PortCardinality.Single) },
        };
        if (d.Concept is not null) fact.Parameters["concept"] = d.Concept;
        if (d.Unit is not null) fact.Parameters["unit"] = d.Unit;
        if (d.PeriodType is not null) fact.Parameters["periodType"] = d.PeriodType;
        if (d.Decimals is not null) fact.Parameters["decimals"] = d.Decimals;
        if (d.Language is not null) fact.Parameters["language"] = d.Language;
        _draft.Nodes.Add(fact);

        _draft.Edges.Add(new MappingEdge
        {
            EdgeId = "e-src-" + d.MappingId,
            SourceNodeId = "src-" + d.MappingId,
            SourcePortName = "value",
            TargetNodeId = factNodeId,
            TargetPortName = "value",
        });
        _draft.Edges.Add(new MappingEdge
        {
            EdgeId = "e-ent-" + d.MappingId,
            SourceNodeId = "entity-param",
            SourcePortName = "value",
            TargetNodeId = factNodeId,
            TargetPortName = "entity",
        });
    }
}
