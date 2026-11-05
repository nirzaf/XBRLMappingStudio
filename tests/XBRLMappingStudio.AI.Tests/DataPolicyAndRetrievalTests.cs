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
    public void Rank_SeparatesAliasesBySelectedProfile_AndTokenizesSynonyms()
    {
        var synthetic = new LocalRetrievalRanker(TestProjects.Taxonomy());
        Assert.Equal("synthetic:NetIncome", synthetic.Rank("bottom line").First().QualifiedName);
        Assert.Equal("synthetic:Revenue", synthetic.Rank("income statement revenue").First().QualifiedName);

        var source = XBRL.SyntheticProfile.Build();
        var realProfile = new XBRL.FilingProfile
        {
            ProfileId = "licensed-profile",
            Digest = "sha256:licensed-profile",
            TaxonomyVersion = "2026.1",
            Concepts = source.Concepts,
            SupportedUnits = source.SupportedUnits
        };
        var real = new LocalRetrievalRanker(new XBRL.TaxonomyIndex(realProfile));

        // The synthetic-only alias "bottom line" cannot contaminate a different profile.
        Assert.Empty(real.Rank("bottom line"));
        Assert.Contains(real.Rank("Net Income"), candidate => candidate.QualifiedName == "synthetic:NetIncome");
    }

    [Fact]
    public void Rank_FiltersUnsuitableUnitPeriodAndConceptMetadata_ButRetainsProfileAndRoleEvidence()
    {
        var concepts = new[]
        {
            new XBRL.ConceptMetadata("ex:RevenueDuration", "urn:example", "RevenueDuration",
                "Revenue", "monetary", "duration", IsAbstract: false),
            new XBRL.ConceptMetadata("ex:RevenueInstant", "urn:example", "RevenueInstant",
                "Revenue", "monetary", "instant", IsAbstract: false),
            new XBRL.ConceptMetadata("ex:RevenueText", "urn:example", "RevenueText",
                "Revenue", "string", "duration", IsAbstract: false),
            new XBRL.ConceptMetadata("ex:RevenueUnknownPeriod", "urn:example", "RevenueUnknownPeriod",
                "Revenue", "monetary", "rolling", IsAbstract: false),
            new XBRL.ConceptMetadata("ex:RevenueAbstract", "urn:example", "RevenueAbstract",
                "Revenue", "monetary", "duration", IsAbstract: true)
        };
        var profile = new XBRL.FilingProfile
        {
            ProfileId = "selected-profile",
            Digest = "sha256:selected-profile-v2",
            TaxonomyVersion = "2026.2",
            Concepts = concepts,
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" }
        };
        var index = new XBRL.TaxonomyIndex(profile, new[]
        {
            new XBRL.PresentationLink("urn:role:income", "ex:Statement", "ex:RevenueDuration", 1),
            new XBRL.PresentationLink("urn:role:cash-flow", "ex:CashFlow", "ex:RevenueDuration", 2)
        });
        var ranker = new LocalRetrievalRanker(index);

        var compatible = Assert.Single(ranker.Rank(
            new SourceFieldDescriptor("field-1", "Revenue", "USD", "duration")));
        Assert.Equal("ex:RevenueDuration", compatible.QualifiedName);
        Assert.Equal(profile.ProfileId, compatible.Evidence.ProfileId);
        Assert.Equal(profile.Digest, compatible.Evidence.ProfileDigest);
        Assert.Equal("2026.2", compatible.Evidence.TaxonomyVersion);
        Assert.Equal("urn:example", compatible.Evidence.NamespaceUri);
        Assert.Equal(new[] { "urn:role:cash-flow", "urn:role:income" }, compatible.Evidence.PresentationRoles);
        Assert.Equal(2, compatible.Evidence.TotalPresentationRoleCount);

        Assert.Empty(ranker.Rank(new SourceFieldDescriptor("field-2", "Revenue", "JPY", "duration")));
        Assert.Equal("ex:RevenueInstant", Assert.Single(ranker.Rank(
            new SourceFieldDescriptor("field-3", "Revenue", "USD", "instant"))).QualifiedName);
    }

    [Fact]
    public void Rank_LargeIndex_ReturnsBoundedStableTopCandidates()
    {
        const int count = 20_000;
        var concepts = Enumerable.Range(0, count)
            .Select(index => new XBRL.ConceptMetadata(
                $"tax{index % 20:D2}:Revenue{index:D5}",
                $"urn:taxonomy:{index % 20:D2}",
                $"Revenue{index:D5}",
                "Revenue",
                "monetary",
                "duration",
                IsAbstract: false))
            .ToArray();
        var index = new XBRL.TaxonomyIndex(new XBRL.FilingProfile
        {
            ProfileId = "large-ranking-profile",
            Digest = "sha256:large-ranking-profile",
            TaxonomyVersion = "2026.1",
            Concepts = concepts,
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" }
        });
        var ranker = new LocalRetrievalRanker(index);

        var first = ranker.Rank("Revenue", int.MaxValue);
        var repeated = ranker.Rank("Revenue", int.MaxValue);

        Assert.Equal(LocalRetrievalRanker.MaximumResults, first.Count);
        Assert.Equal(first.Select(candidate => candidate.QualifiedName),
            repeated.Select(candidate => candidate.QualifiedName));
        Assert.Equal(first.Select(candidate => candidate.QualifiedName).Order(StringComparer.Ordinal),
            first.Select(candidate => candidate.QualifiedName));
    }

    [Fact]
    public void SuggestionService_RejectsRankerFromAnotherProfileDigest()
    {
        var profile = XBRL.SyntheticProfile.Build();
        var selectedIndex = new XBRL.TaxonomyIndex(profile);
        var staleIndex = new XBRL.TaxonomyIndex(new XBRL.FilingProfile
        {
            ProfileId = profile.ProfileId,
            Digest = "sha256:stale-package",
            Concepts = profile.Concepts,
            SupportedUnits = profile.SupportedUnits
        });

        Assert.Throws<ArgumentException>(() => new AiSuggestionService(
            AiPolicy.Offline,
            selectedIndex,
            ranker: new LocalRetrievalRanker(staleIndex)));
    }

    [Fact]
    public void Aliases_LoadFromProjectDirectory_OverrideDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Empty(LocalRetrievalRanker.LoadAliases(dir));
            Assert.Equal(LocalRetrievalRanker.DefaultAliases.Count,
                LocalRetrievalRanker.LoadAliases(dir, XBRL.SyntheticProfile.ProfileId).Count);
            Assert.Empty(LocalRetrievalRanker.LoadAliases(dir, "licensed-profile"));
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
