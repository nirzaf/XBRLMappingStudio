using XBRLMappingStudio.AI;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.AI.Tests;

/// <summary>
/// AI06 audit persistence hook. Every analyst decision — accept, reject, edit,
/// batch-accept, undo — must reach the injected sink with the governed actor,
/// method, prompt-template revision and a bounded rendering of the reviewed
/// payload. A sink that throws records a PersistenceFault and returns a failed,
/// rolled-back mutation so a governed caller can fail closed.
/// </summary>
public sealed class AuditPersistenceTests
{
    private sealed class RecordingSink : IAuditSink
    {
        public List<(AuditRecord Record, AuditContext Context)> Rows { get; } = new();
        public Exception? Failure;
        public bool Cancel;
        public void Append(AuditRecord record, AuditContext context)
        {
            if (Cancel) throw new OperationCanceledException("secret=/private/customer/seed 123456.78");
            if (Failure is not null) throw Failure;
            Rows.Add((record, context));
        }
    }

    private static SuggestionSession NewSession(IAuditSink sink, SuggestionSessionContext? ctx = null)
    {
        var taxonomy = TestProjects.Taxonomy();
        return new SuggestionSession(
            TestProjects.Empty(),
            new DraftValidator(taxonomy, TestProjects.Profile()),
            sink, ctx);
    }

    [Fact]
    public void Accept_PersistsOneRow_WithActorMethodAndTemplate()
    {
        var sink = new RecordingSink();
        var session = NewSession(sink, new SuggestionSessionContext(
            Actor: "analyst-1",
            Method: "retrieval+provider",
            ProviderConfigId: "local-retrieval",
            PromptTemplateRevision: "pt-v3",
            ProviderSnapshotLimitation: "provider alias 'default' has no immutable snapshot"));
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales",
                new DraftMapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
        });

        var outcome = session.Accept("s1");

        Assert.True(outcome.Applied, outcome.Reason);
        var row = Assert.Single(sink.Rows);
        Assert.Equal("accept", row.Record.Action);
        Assert.Equal("analyst-1", row.Context.Actor);
        Assert.Equal("retrieval+provider", row.Context.Method);
        Assert.Equal("pt-v3", row.Context.PromptTemplateRevision);
        Assert.Equal(new[] { "s1" }, row.Context.CandidateIds);
        Assert.Contains("provider alias", row.Context.ProviderSnapshotLimitation);
        Assert.Contains("synthetic:Revenue", row.Context.ApprovedEditJson);
        Assert.Equal(TestProjects.Empty().ProjectId, row.Context.ProjectId);
    }

    [Fact]
    public void AuditPersistence_RedactsValueLikeAndCredentialLikeSourceText()
    {
        var sink = new RecordingSink();
        var session = NewSession(sink);
        const string sourceName = "Revenue secret=do-not-store-8822 987654321.09";
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", sourceName,
                new DraftMapping("s1", sourceName, "synthetic:Revenue", "USD", "duration"), "t", 9),
        });

        Assert.True(session.Accept("s1").Applied);

        var row = Assert.Single(sink.Rows);
        var persistedAudit = string.Join("\n", row.Record.SourceField, row.Context.OriginalProposalJson,
            row.Context.ApprovedEditJson);
        Assert.DoesNotContain("do-not-store-8822", persistedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321.09", persistedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=", persistedAudit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", persistedAudit, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_Reject_Undo_EachPersistOneRow()
    {
        var sink = new RecordingSink();
        var session = NewSession(sink, new SuggestionSessionContext(Actor: "analyst-2"));
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales",
                new DraftMapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
            new Suggestion("s2", "Cost",
                new DraftMapping("s2", "Cost", "synthetic:Expenses", "USD", "duration"), "t", 7),
        });

        session.Edit("s1", new DraftMapping("s1", "Sales", "synthetic:NetIncome", "USD", "duration"));
        session.Reject("s2");
        session.Accept("s1");
        session.Undo();

        var decisions = sink.Rows.Select(r => r.Record.Action).ToList();
        Assert.Contains("edit", decisions);
        Assert.Contains("reject", decisions);
        Assert.Contains("accept", decisions);
        Assert.Contains("undo", decisions);
        // Edit row's ApprovedEditJson differs from OriginalProposalJson so a reviewer
        // can see exactly what the analyst changed before accepting.
        var edit = sink.Rows.First(r => r.Record.Action == "edit").Context;
        Assert.Contains("synthetic:Revenue", edit.OriginalProposalJson);
        Assert.Contains("synthetic:NetIncome", edit.ApprovedEditJson);
    }

    [Fact]
    public void SinkFault_IsCapturedOnLog_AndRollsBackTheAcceptedDraft()
    {
        var sink = new RecordingSink { Failure = new IOException("simulated sink failure") };
        var session = NewSession(sink);
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales",
                new DraftMapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
        });

        var outcome = session.Accept("s1");

        // Governed desktop acceptance fails closed when its durable audit append fails.
        Assert.False(outcome.Applied);
        Assert.Equal(SuggestionState.Pending, session.StateOf("s1"));
        Assert.False(session.CanUndo);
        Assert.Contains("rolled back", outcome.Reason!.ToLowerInvariant());
        Assert.Single(session.Audit.PersistenceFaults);
        Assert.Equal(nameof(IOException), session.Audit.PersistenceFaults[0]);
    }

    [Fact]
    public void SinkCancellation_IsCapturedAndRollsBackTheAcceptedDraft()
    {
        var sink = new RecordingSink { Cancel = true };
        var session = NewSession(sink);
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales",
                new DraftMapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
        });

        var outcome = session.Accept("s1");

        Assert.False(outcome.Applied);
        Assert.Equal(SuggestionState.Pending, session.StateOf("s1"));
        Assert.Empty(session.Draft.Nodes);
        Assert.False(session.CanUndo);
        Assert.Contains("rolled back", outcome.Reason!.ToLowerInvariant());
        Assert.Single(session.Audit.PersistenceFaults);
        Assert.Equal(nameof(OperationCanceledException), session.Audit.PersistenceFaults[0]);
        Assert.DoesNotContain("/private/customer/seed", session.Audit.PersistenceFaults[0], StringComparison.Ordinal);
        Assert.DoesNotContain("123456.78", session.Audit.PersistenceFaults[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BatchSinkFault_RollsBackEveryDraftAndDisposition()
    {
        var sink = new RecordingSink { Failure = new IOException("simulated sink failure") };
        var session = NewSession(sink);
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales",
                new DraftMapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
            new Suggestion("s2", "Costs",
                new DraftMapping("s2", "Costs", "synthetic:Expenses", "USD", "duration"), "t", 8),
        });

        var outcome = session.AcceptAllValid();

        Assert.False(outcome.Applied);
        Assert.Contains("rolled back", outcome.Reason!.ToLowerInvariant());
        Assert.Equal(SuggestionState.Pending, session.StateOf("s1"));
        Assert.Equal(SuggestionState.Pending, session.StateOf("s2"));
        Assert.Empty(session.Draft.Nodes);
        Assert.False(session.CanUndo);
        Assert.Single(session.Audit.PersistenceFaults);
    }

    [Fact]
    public void NoSink_SessionPreservesInMemoryBehaviour()
    {
        // Existing analyst tests rely on the default (no-sink) path; guard against
        // regressions in the optional sink wiring.
        var taxonomy = TestProjects.Taxonomy();
        var session = new SuggestionSession(
            TestProjects.Empty(),
            new DraftValidator(taxonomy, TestProjects.Profile()));
        session.LoadSuggestions(new[]
        {
            new Suggestion("s1", "Sales",
                new DraftMapping("s1", "Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
        });
        Assert.True(session.Accept("s1").Applied);
        Assert.Empty(session.Audit.PersistenceFaults);
        Assert.Contains(session.Audit.Records, r => r.Action == "accept");
    }
}
