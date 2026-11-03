using XBRLMappingStudio.AI;

namespace XBRLMappingStudio.AI.Tests;

/// <summary>
/// AI01 — data-policy gate: when the policy is Offline the service must be structurally
/// incapable of outbound contact, and a full suggestion session must emit zero requests.
/// </summary>
public class DataPolicyTests
{
    [Fact]
    public async Task OfflinePolicy_EmptyProvider_StillProducesLocalSuggestions_ZeroOutbound()
    {
        var spy = new CountingHandler();
        var onlineProvider = new HttpChatProvider(new Uri("http://127.0.0.1:59/never"), spy);
        var taxonomy = TestProjects.Taxonomy();

        // Even when a real (spy-wired) provider is handed in, Offline discards it.
        var service = new AiSuggestionService(AiPolicy.Offline, taxonomy, onlineProvider);

        var batch = await service.SuggestAsync(new[] { "Revenue", "Total Assets" }, default);

        Assert.Equal(0, spy.RequestCount);                 // zero HTTP left the process
        Assert.False(batch.UsedProvider);
        Assert.IsType<NullProvider>(service.ActiveProvider); // structural, not convention
        Assert.NotEmpty(batch.Suggestions);                // local ranker still answered
    }

    [Fact]
    public void OfflinePolicy_DisallowsOutbound_ByConstruction()
    {
        Assert.False(AiPolicy.Offline.AllowsOutbound);
        Assert.True(new AiPolicy(AiMode.ManagedProvider).AllowsOutbound);
        Assert.True(new AiPolicy(AiMode.LocalModel).AllowsOutbound);
    }

    [Fact]
    public void Parse_DefaultsToOffline_ForUnknownOrNull()
    {
        Assert.Equal(AiMode.Offline, AiPolicy.Parse(null).Mode);
        Assert.Equal(AiMode.Offline, AiPolicy.Parse("garbage").Mode);
        Assert.Equal(AiMode.ManagedProvider, AiPolicy.Parse("managed").Mode);
    }
}

/// <summary>
/// AI02 — deterministic retrieval: identical ranking across repeats, distinct
/// same-label namespaces, and edge cases (abstract, unknown period, incompatible unit)
/// surfaced through the draft validator.
/// </summary>
public class RetrievalDeterminismTests
{
    [Fact]
    public void Rank_SameFixture_30Repeats_ProduceIdenticalOrderedIds()
    {
        var ranker = new LocalRetrievalRanker(TestProjects.Taxonomy());
        string[]? first = null;

        for (int i = 0; i < 30; i++)
        {
            var ids = ranker.Rank("Sales").Select(c => c.QualifiedName).ToArray();
            first ??= ids;
            Assert.Equal(first, ids); // byte-for-byte same order every repeat
        }

        // The canonical hit for "Sales" is Revenue (via alias "sales").
        Assert.Contains("synthetic:Revenue", first!);
    }

    [Fact]
    public void Rank_NeverProposesAbstractConcepts()
    {
        var ranker = new LocalRetrievalRanker(TestProjects.Taxonomy());
        var results = ranker.Rank("Financial Statements");
        Assert.DoesNotContain(results, c => c.QualifiedName == "synthetic:FinancialStatements");
    }

    [Fact]
    public void Rank_SameLabelInTwoNamespaces_RemainsDistinct()
    {
        // Build a profile with two concepts sharing the label "Revenue" across namespaces.
        var dupConcept = new XBRL.ConceptMetadata(
            "dup:Revenue", "urn:dup:finance:1.0", "Revenue", "Revenue",
            "monetary", "duration", IsAbstract: false);
        var concepts = TestProjects.Taxonomy().AllConcepts.Concat(new[] { dupConcept }).ToList();
        var profile = new XBRL.FilingProfile
        {
            ProfileId = "dup-test",
            Digest = "sha256:dup",
            Concepts = concepts,
            SupportedUnits = XBRL.SyntheticProfile.SupportedUnits,
        };
        var ranker = new LocalRetrievalRanker(new XBRL.TaxonomyIndex(profile));

        var hits = ranker.Rank("Revenue").Select(c => c.QualifiedName).ToList();
        Assert.Contains("synthetic:Revenue", hits);
        Assert.Contains("dup:Revenue", hits);
        Assert.Equal(hits.Count, hits.Distinct().Count()); // not collapsed
    }

    [Fact]
    public void Aliases_LoadFromProjectDirectory_OverrideDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, LocalRetrievalRanker.AliasFileName),
                "{\"synthetic:NetIncome\":[\"bottom_line_x\"]}");
            var aliases = LocalRetrievalRanker.LoadAliases(dir);
            Assert.Equal(new[] { "bottom_line_x" }, aliases["synthetic:NetIncome"]);
            // Concepts not in the file are simply absent (no default merge).
            Assert.False(aliases.ContainsKey("synthetic:Revenue"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
