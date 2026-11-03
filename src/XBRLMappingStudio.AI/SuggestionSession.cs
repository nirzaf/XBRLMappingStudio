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

    private MappingProject _draft;

    public AuditLog Audit { get; } = new();

    /// <summary>The working draft that accepted suggestions mutate. Not the original project.</summary>
    public MappingProject Draft => _draft;

    public IReadOnlyList<Suggestion> Suggestions => _suggestions;

    public SuggestionState StateOf(string suggestionId) =>
        _states.TryGetValue(suggestionId, out var s) ? s : SuggestionState.Pending;

    public SuggestionSession(MappingProject project, DraftValidator validator)
    {
        // Clone the project through the serializer so the original is structurally immutable.
        _draft = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        _validator = validator;
    }

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

        PushSnapshot();
        ApplyDraftToProject(suggestion.Draft);
        _states[suggestionId] = SuggestionState.Accepted;
        Audit.Record(new AuditRecord(
            Audit.NextSequence, "accept", suggestion.SourceField,
            suggestion.Draft.Concept ?? "(none)", 0, Array.Empty<string>()));
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
        var updated = old with { Draft = edited };
        _suggestions[index] = updated;
        Audit.Record(new AuditRecord(
            Audit.NextSequence, "edit", edited.SourceField, edited.Concept ?? "(none)",
            0, Array.Empty<string>()));
        return new SessionOutcome(true, null, null);
    }

    public SessionOutcome Reject(string suggestionId)
    {
        var suggestion = Find(suggestionId);
        if (suggestion is null) return new SessionOutcome(false, "Unknown suggestion id.", null);
        if (_states[suggestionId] != SuggestionState.Pending)
            return new SessionOutcome(false, "Only pending suggestions can be rejected.", null);

        _states[suggestionId] = SuggestionState.Rejected;
        Audit.Record(new AuditRecord(
            Audit.NextSequence, "reject", suggestion.SourceField,
            suggestion.Draft.Concept ?? "(none)", 0, Array.Empty<string>()));
        return new SessionOutcome(true, null, null);
    }

    /// <summary>Batch accept: validates the whole surviving set together (cross-draft
    /// duplicates are caught), then applies as one transaction with a single undo point.</summary>
    public SessionOutcome AcceptAllValid()
    {
        var pending = _suggestions
            .Where(s => _states[s.SuggestionId] == SuggestionState.Pending)
            .ToList();
        if (pending.Count == 0) return new SessionOutcome(false, "Nothing pending.", null);

        var validation = _validator.Validate(pending.Select(p => p.Draft).ToList());
        if (!validation.Accepted)
            return new SessionOutcome(false, "Batch contains invalid drafts; nothing applied.", validation);

        PushSnapshot();
        foreach (var s in pending)
        {
            ApplyDraftToProject(s.Draft);
            _states[s.SuggestionId] = SuggestionState.Accepted;
        }
        Audit.Record(new AuditRecord(
            Audit.NextSequence, "batch-accept", "(multiple)", "(multiple)",
            0, Array.Empty<string>()));
        return new SessionOutcome(true, null, validation);
    }

    public SessionOutcome Undo()
    {
        if (_undoSnapshots.Count == 0)
            return new SessionOutcome(false, "Nothing to undo.", null);

        // Roll back the whole transaction: draft graph AND suggestion dispositions.
        var (payload, states) = _undoSnapshots.Pop();
        _draft = ProjectSerializer.Deserialize(payload);
        foreach (var (id, state) in states)
            _states[id] = state;
        Audit.Record(new AuditRecord(
            Audit.NextSequence, "undo", "(project)", "(snapshot)", 0, Array.Empty<string>()));
        return new SessionOutcome(true, null, null);
    }

    public bool CanUndo => _undoSnapshots.Count > 0;

    private void PushSnapshot() =>
        _undoSnapshots.Push((ProjectSerializer.Serialize(_draft),
            new Dictionary<string, SuggestionState>(_states, StringComparer.Ordinal)));

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
