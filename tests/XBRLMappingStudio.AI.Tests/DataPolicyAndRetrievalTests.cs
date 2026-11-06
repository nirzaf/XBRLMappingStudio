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
        Assert.False(new AiPolicy(AiMode.ManagedProvider).AllowsOutbound);
        Assert.False(new AiPolicy(AiMode.LocalModel).AllowsOutbound);
        Assert.True(new AiPolicy(AiMode.ManagedProvider, "00000000-0000-0000-0000-000000000001", true, true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ai.example" }).AllowsOutbound);
    }

    [Fact]
    public void ProviderPolicy_RequiresApprovedManagedHost_AndKeepsLocalModelOnLoopback()
    {
        var managed = new AiPolicy(AiMode.ManagedProvider, "00000000-0000-0000-0000-000000000001", true, true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ai.example" });
        Assert.True(managed.AllowsEndpoint(new Uri("https://ai.example/v1")));
        Assert.False(managed.AllowsEndpoint(new Uri("https://other.example/v1")));
        Assert.False(managed.AllowsEndpoint(new Uri("http://ai.example/v1")));

        var local = new AiPolicy(AiMode.LocalModel, "00000000-0000-0000-0000-000000000005", true, true);
        Assert.True(local.AllowsEndpoint(new Uri("http://127.0.0.1:11434")));
        Assert.False(local.AllowsEndpoint(new Uri("https://public.example")));
    }

    [Fact]
    public async Task DisabledOrUnapprovedProvider_IsNotProbedOrContacted()
    {
        var disabled = new FakeProvider();
        var disabledService = new AiSuggestionService(
            new AiPolicy(AiMode.ManagedProvider, "00000000-0000-0000-0000-000000000002", ProviderEnabled: false, ProviderApproved: true),
            TestProjects.Taxonomy(), disabled);
        var disabledBatch = await disabledService.SuggestAsync(new[] { "Sales" }, default);

        Assert.False(disabledBatch.UsedProvider);
        Assert.Equal(0, disabled.ProbeCalls);
        Assert.IsType<NullProvider>(disabledService.ActiveProvider);

        var handler = new CountingHandler();
        var unapproved = new HttpChatProvider(new Uri("https://unapproved.example/v1"), handler);
        var approvedPolicy = new AiPolicy(AiMode.ManagedProvider, "00000000-0000-0000-0000-000000000003", ProviderEnabled: true,
            ProviderApproved: true,
            ApprovedEndpointHosts: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" });
        var unapprovedService = new AiSuggestionService(approvedPolicy, TestProjects.Taxonomy(), unapproved);
        var unapprovedBatch = await unapprovedService.SuggestAsync(new[] { "Sales" }, default);

        Assert.False(unapprovedBatch.UsedProvider);
        Assert.Equal(0, handler.RequestCount);
        Assert.IsType<NullProvider>(unapprovedService.ActiveProvider);

        var unbound = new UnboundFakeProvider();
        var unboundService = new AiSuggestionService(approvedPolicy, TestProjects.Taxonomy(), unbound);
        var unboundBatch = await unboundService.SuggestAsync(new[] { "Sales" }, default);

        Assert.False(unboundBatch.UsedProvider);
        Assert.Equal(0, unbound.ProbeCalls);
        Assert.IsType<NullProvider>(unboundService.ActiveProvider);
    }

    [Fact]
    public void Parse_DefaultsToOffline_ForUnknownOrNull()
    {
        Assert.Equal(AiMode.Offline, AiPolicy.Parse(null).Mode);
        Assert.Equal(AiMode.Offline, AiPolicy.Parse("garbage").Mode);
        var managedMode = AiPolicy.Parse("managed");
        Assert.Equal(AiMode.ManagedProvider, managedMode.Mode);
        Assert.False(managedMode.AllowsOutbound); // a mode string alone is not consent/configuration
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
            new XBRL.ConceptMetadata("ex:Statement", "urn:example", "Statement",
                "Statement", "string", "duration", IsAbstract: true),
            new XBRL.ConceptMetadata("ex:CashFlow", "urn:example", "CashFlow",
                "Cash Flow", "string", "duration", IsAbstract: true),
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
    public async Task UnanswerableSourceFieldReturnsTypedAbstentionWithoutGenericRevenue()
    {
        var service = new AiSuggestionService(AiPolicy.Offline, TestProjects.Taxonomy());

        var batch = await service.SuggestAsync(new[]
        {
            new SourceFieldDescriptor("internal-tag-id", "Internal compliance reference", "USD", "duration")
        }, default);

        Assert.Empty(batch.Suggestions);
        var abstention = Assert.Single(batch.Abstentions);
        Assert.Equal("internal-tag-id", abstention.SourceFieldId);
        Assert.Equal("Internal compliance reference", abstention.SourceFieldName);
        Assert.Contains("remains unassigned", abstention.Reason, StringComparison.Ordinal);
        Assert.Contains(batch.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("remains unassigned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousGrossAndNetRevenueReturnsAlternativesWithoutMutatingGraph()
    {
        var profile = new XBRL.FilingProfile
        {
            ProfileId = "gross-net-profile",
            Digest = "sha256:gross-net-v1",
            TaxonomyVersion = "2026.1",
            Concepts = new[]
            {
                new XBRL.ConceptMetadata("ex:GrossRevenue", "urn:example", "GrossRevenue", "Gross Revenue",
                    "monetary", "duration", IsAbstract: false),
                new XBRL.ConceptMetadata("ex:NetRevenue", "urn:example", "NetRevenue", "Net Revenue",
                    "monetary", "duration", IsAbstract: false),
                new XBRL.ConceptMetadata("ex:Revenue", "urn:example", "Revenue", "Revenue",
                    "monetary", "duration", IsAbstract: false)
            },
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" }
        };
        var taxonomy = new XBRL.TaxonomyIndex(profile);
        var service = new AiSuggestionService(AiPolicy.Offline, taxonomy);
        var batch = await service.SuggestAsync(new[]
        {
            new SourceFieldDescriptor("sales-field", "Gross or net revenue", "USD", "duration")
        }, default);

        Assert.Equal(3, batch.Suggestions.Count);
        Assert.Contains(batch.Suggestions, suggestion => suggestion.Draft.Concept == "ex:Revenue");
        Assert.All(batch.Suggestions, suggestion =>
            Assert.Equal(2, suggestion.Recommendation!.Alternatives.Count));

        var project = new XBRLMappingStudio.Project.MappingProject
        {
            ProjectId = "ambiguous-review",
            Name = "ambiguous-review",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest
        };
        var session = new SuggestionSession(project, new DraftValidator(taxonomy, profile));
        session.LoadSuggestions(batch.Suggestions);

        Assert.Empty(session.Draft.Nodes);
        Assert.All(batch.Suggestions, suggestion =>
            Assert.Equal(SuggestionState.Pending, session.StateOf(suggestion.SuggestionId)));
    }

    [Fact]
    public async Task Recommendation_SeparatesDraftListsAlternativesAndSurfacesUnresolvedAspects()
    {
        var profile = new XBRL.FilingProfile
        {
            ProfileId = "recommendation-profile",
            Digest = "sha256:recommendation-profile-v1",
            TaxonomyVersion = "2026.1",
            Concepts = new[]
            {
                new XBRL.ConceptMetadata("ex:Revenue", "urn:example", "Revenue", "Revenue",
                    "monetary", "duration", IsAbstract: false),
                new XBRL.ConceptMetadata("ext:Revenue", "urn:extension", "Revenue", "Revenue",
                    "monetary", "duration", IsAbstract: false)
            },
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" }
        };
        var taxonomy = new XBRL.TaxonomyIndex(profile);
        var service = new AiSuggestionService(AiPolicy.Offline, taxonomy);

        var batch = await service.SuggestAsync(new[]
        {
            new SourceFieldDescriptor("source-binding:Revenue", "Revenue")
        }, default);

        Assert.Equal(2, batch.Suggestions.Count);
        foreach (var suggestion in batch.Suggestions)
        {
            var recommendation = Assert.IsType<MappingRecommendation>(suggestion.Recommendation);
            Assert.Equal("source-binding:Revenue", recommendation.SourceFieldId);
            Assert.Equal(suggestion.Draft.Concept, recommendation.Candidate.QualifiedName);
            Assert.Single(recommendation.Alternatives);
            Assert.NotEqual(recommendation.Candidate.QualifiedName, recommendation.Alternatives[0].QualifiedName);
            Assert.Empty(recommendation.ProposedTransformations);
            Assert.Contains(recommendation.AspectAssessments, aspect =>
                aspect.Aspect == "unit" && aspect.Status == MappingAspectReviewStatus.NeedsReview);
            Assert.Contains(recommendation.AspectAssessments, aspect =>
                aspect.Aspect == "scale" && aspect.Status == MappingAspectReviewStatus.NeedsReview);
            Assert.Contains(recommendation.AspectAssessments, aspect =>
                aspect.Aspect == "period" && aspect.Status == MappingAspectReviewStatus.NeedsReview);
            Assert.Contains(recommendation.AspectAssessments, aspect =>
                aspect.Aspect == "entity" && aspect.Status == MappingAspectReviewStatus.RunContext);
            Assert.Contains(recommendation.AspectAssessments, aspect =>
                aspect.Aspect == "dimensions" && aspect.Status == MappingAspectReviewStatus.NeedsReview);
            Assert.Contains(recommendation.AspectAssessments, aspect =>
                aspect.Aspect == "transformation" && aspect.Status == MappingAspectReviewStatus.NotProposed);
            Assert.NotEmpty(recommendation.AllowedDeterministicTransformationIds);
            Assert.All(recommendation.AllowedDeterministicTransformationIds, id =>
                Assert.True(Engine.FunctionCatalog.Default.Find(id)?.IsDeterministic));
            Assert.DoesNotContain("runShellCommand", recommendation.AllowedDeterministicTransformationIds);
        }
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
