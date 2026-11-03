using XBRLMappingStudio.AI;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.AI.Tests;

/// <summary>
/// AI05 — analyst workflow: accept / edit / reject / batch / undo operate only on the
/// session's draft; the caller's project object is never mutated; accepted graphs stay
/// structurally valid.
/// </summary>
public class AnalystWorkflowTests
{
    private static (SuggestionSession session, AiSuggestionService service) CreateSession()
    {
        var taxonomy = TestProjects.Taxonomy();
        var service = new AiSuggestionService(AiPolicy.Offline, taxonomy);
        var session = new SuggestionSession(
            TestProjects.Empty(),
            new DraftValidator(taxonomy, TestProjects.Profile()));
        return (session, service);
    }

    private static DraftMapping Mapping(string id, string field, string concept,
        string unit, string period) => new(id, field, concept, unit, period);

    [Fact]
    public async Task Accept_AddsNodesToDraft_OriginalProjectUntouched()
    {
        var taxonomy = TestProjects.Taxonomy();
        var service = new AiSuggestionService(AiPolicy.Offline, taxonomy);
        var original = TestProjects.Empty();
        var session = new SuggestionSession(
            original,
            new DraftValidator(taxonomy, TestProjects.Profile()));
        var batch = await service.SuggestAsync(new[] { "Sales" }, default);
        session.LoadSuggestions(batch.Suggestions);
        var target = batch.Suggestions.First(s => s.Draft.Concept == "synthetic:Revenue");

        var outcome = session.Accept(target.SuggestionId);

        Assert.True(outcome.Applied, outcome.Reason);
        Assert.Equal(SuggestionState.Accepted, session.StateOf(target.SuggestionId));
        Assert.Contains(session.Draft.Nodes, n => n.Parameters.Contains(new KeyValuePair<string, object?>("concept", "synthetic:Revenue")));
        // The caller's project object was cloned: nothing leaked back.
        Assert.Empty(original.Nodes);
        Assert.Empty(original.Edges);
        // Accepted draft still validates as a whole graph.
        var structural = new GraphValidator(FunctionCatalog.Default)
            .Validate(new MappingGraph(session.Draft));
        Assert.False(structural.HasBlockingErrors);
    }

    [Fact]
    public void Accept_BlockedDraft_DoesNotMutateAnything()
    {
        var (session, _) = CreateSession();
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales", Mapping("s1", "Sales", "synthetic:Cash", "USD", "duration"),
                "test", 9)
        });

        var outcome = session.Accept("s1");

        Assert.False(outcome.Applied);
        Assert.Equal(SuggestionState.Pending, session.StateOf("s1"));
        Assert.Empty(session.Draft.Nodes);
        Assert.NotNull(outcome.Validation);
        Assert.Contains(outcome.Validation!.Diagnostics, d => d.Code == DiagnosticCodes.AiInvalidDraft);
    }

    [Fact]
    public void Undo_RestoresPriorDraftAndStates()
    {
        var (session, _) = CreateSession();
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales", Mapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"),
                "test", 9)
        });

        Assert.True(session.Accept("s1").Applied);
        int afterAccept = session.Draft.Nodes.Count;
        Assert.True(session.CanUndo);

        Assert.True(session.Undo().Applied);

        Assert.True(session.Draft.Nodes.Count < afterAccept);
        Assert.Equal(SuggestionState.Pending, session.StateOf("s1")); // disposition rolled back
    }

    [Fact]
    public void Edit_ThenAccept_UsesEditedConcept()
    {
        var (session, _) = CreateSession();
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales", Mapping("s1", "Sales", "synthetic:Cash", "USD", "duration"),
                "test", 9) // originally invented → would be blocked
        });

        var edited = Mapping("s1", "Sales", "synthetic:Revenue", "USD", "duration");
        Assert.True(session.Edit("s1", edited).Applied);
        Assert.True(session.Accept("s1").Applied);
        Assert.Equal(SuggestionState.Accepted, session.StateOf("s1"));
    }

    [Fact]
    public void Reject_LeavesNoGraphChange_AndIsTerminal()
    {
        var (session, _) = CreateSession();
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales", Mapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"),
                "test", 9)
        });

        Assert.True(session.Reject("s1").Applied);
        Assert.Equal(SuggestionState.Rejected, session.StateOf("s1"));
        Assert.Empty(session.Draft.Nodes);
        Assert.False(session.Reject("s1").Applied); // not pending anymore
    }

    [Fact]
    public void AcceptAllValid_BatchesCompatibleDrafts()
    {
        var (session, _) = CreateSession();
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales", Mapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
            new Suggestion("s2", "Debt", Mapping("s2", "Debt", "synthetic:Liabilities", "USD", "instant"), "t", 8),
        });

        var outcome = session.AcceptAllValid();

        Assert.True(outcome.Applied, outcome.Reason);
        Assert.Equal(SuggestionState.Accepted, session.StateOf("s1"));
        Assert.Equal(SuggestionState.Accepted, session.StateOf("s2"));
    }

    [Fact]
    public void AcceptAllValid_RejectsBatchContainingClash_NothingApplied()
    {
        var (session, _) = CreateSession();
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales", Mapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
            // Same representative aspects under a different field → duplicate-fact clash in batch.
            new Suggestion("s2", "Turnover", Mapping("s2", "Turnover", "synthetic:Revenue", "USD", "duration"), "t", 8),
        });

        var outcome = session.AcceptAllValid();

        Assert.False(outcome.Applied);
        Assert.Empty(session.Draft.Nodes);
        Assert.Equal(SuggestionState.Pending, session.StateOf("s1"));
    }

    [Fact]
    public async Task ApprovedDraft_SerializesAndSurvivesRoundTrip_OfflineOnly()
    {
        // "Approved graph round-trips through the CLI with AiPolicy.Offline": the draft
        // must survive serialization byte-identically in semantic content.
        var (session, service) = CreateSession();
        var batch = await service.SuggestAsync(new[] { "Sales" }, default);
        session.LoadSuggestions(batch.Suggestions);
        foreach (var s in batch.Suggestions)
            session.Accept(s.SuggestionId);

        var restored = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(session.Draft));

        Assert.Equal(session.Draft.ComputeSemanticDigest(), restored.ComputeSemanticDigest());
        Assert.False(service.ActiveProvider is HttpChatProvider); // offline path never carried a transport
    }
}

/// <summary>
/// AI06 — privacy/provenance audit: every analyst action is recorded, rendering is
/// deterministic, and seeded secrets/canaries never reach the audit surface.
/// </summary>
public class AuditTests
{
    [Fact]
    public void Records_AreSequencedAndDeterministic()
    {
        var log = new AuditLog();
        log.Record(new AuditRecord(0, "accept", "Sales", "synthetic:Revenue", 0, Array.Empty<string>()));
        log.Record(new AuditRecord(0, "reject", "Tax", "synthetic:Expenses", 1, new[] { "AI002" }));

        Assert.Equal(new[] { 1, 2 }, log.Records.Select(r => r.Sequence));
        Assert.Equal(log.RenderJsonL(), log.RenderJsonL()); // stable rendering
    }

    [Fact]
    public void SessionActions_AreAllAudited()
    {
        var taxonomy = TestProjects.Taxonomy();
        var session = new SuggestionSession(
            TestProjects.Empty(),
            new DraftValidator(taxonomy, TestProjects.Profile()));
        session.LoadSuggestions(new[]
        {
            new Suggestion("ok", "Sales", new DraftMapping("ok", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
            new Suggestion("bad", "Tax", new DraftMapping("bad", "Tax", "synthetic:Cash", "USD", "duration"), "t", 9),
        });

        session.Accept("ok");
        session.Accept("bad");   // blocked — still audited
        session.Reject("bad");
        session.Undo();

        var actions = session.Audit.Records.Select(r => r.Action).ToList();
        Assert.Contains("accept", actions);
        Assert.Contains("accept-blocked", actions);
        Assert.Contains("reject", actions);
        Assert.Contains("undo", actions);
    }

    [Fact]
    public void Scan_CatchesSeededSecretsAndCanaries_InExtraTranscript()
    {
        var log = new AuditLog();
        log.RegisterCanary("987654321.09");
        log.Record(new AuditRecord(0, "accept", "Sales", "synthetic:Revenue", 0, Array.Empty<string>()));
        Assert.True(log.Scan().Clean); // the audit surface itself is clean

        var dirty = log.Scan(new[]
        {
            "provider payload excerpt: password=hunter2",
            "canary total 987654321.09 carried in raw payload",
        });
        Assert.False(dirty.Clean);
        Assert.Contains(dirty.Findings, f => f.Contains("password="));
        Assert.Contains(dirty.Findings, f => f.Contains("canary"));
        Assert.Throws<InvalidOperationException>(() => dirty.EnsureClean());
    }

    [Fact]
    public void Scan_CanaryMatchesInRenderedAudit_Itself()
    {
        var log = new AuditLog();
        log.RegisterCanary("synthetic:Revenue");
        log.Record(new AuditRecord(0, "accept", "Sales", "synthetic:Revenue", 0, Array.Empty<string>()));

        var scan = log.Scan();
        Assert.False(scan.Clean); // proves the scanner actually reads the rendered surface
    }
}
