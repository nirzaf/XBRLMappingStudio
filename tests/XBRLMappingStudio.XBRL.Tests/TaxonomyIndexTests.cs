using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR03 — paged, language-aware taxonomy index and presentation networks. Verifies
/// stable paging, multilingual label resolution (including Arabic), that two concepts in
/// distinct namespaces sharing an identical label remain distinct rows, and that abstract
/// concepts are rejected as fact targets.
/// </summary>
public sealed class TaxonomyIndexTests
{
    private static FilingProfile Profile => SyntheticProfile.Build();

    [Fact]
    public void Search_EmptyQuery_ReturnsAllConcepts_PagedAtViewport()
    {
        var index = new TaxonomyIndex(Profile);
        var page = index.Search(query: "", page: 0, pageSize: TaxonomyIndex.DefaultPageSize);
        Assert.Equal(Profile.Concepts.Count, page.TotalCount);
        Assert.False(page.HasNext);
        Assert.Equal(Profile.Concepts.Count, page.Items.Count);
    }

    [Fact]
    public void Search_PagingIsStable_AndOrderedByNamespaceThenLocal()
    {
        var index = new TaxonomyIndex(Profile);
        // Page size 2 forces multiple windows across 6 concepts.
        var p0 = index.Search("", page: 0, pageSize: 2);
        var p1 = index.Search("", page: 1, pageSize: 2);
        Assert.True(p0.HasNext);
        Assert.Equal(2, p0.Items.Count);
        Assert.Equal(2, p1.Items.Count);

        // Ordering is ordinal by (namespace, local name) so pages never re-order.
        var all = index.Search("", page: 0, pageSize: 100).Items;
        var expected = all.Select(c => c.QualifiedName).ToList();
        var concatenated = p0.Items.Concat(p1.Items)
            .Concat(index.Search("", page: 2, pageSize: 2).Items)
            .Select(c => c.QualifiedName);
        Assert.Equal(expected.Take(6), concatenated);
    }

    [Fact]
    public void Search_MatchesByLabel()
    {
        var index = new TaxonomyIndex(Profile);
        var netIncome = index.Search("Net Income");
        Assert.Contains(netIncome.Items, c => c.QualifiedName == SyntheticProfile.Qualified("NetIncome"));
    }

    [Fact]
    public void Search_UsesRequestedLanguageLabel_Arabic()
    {
        var index = new TaxonomyIndex(Profile);
        // "إيرادات" is the Arabic label of synthetic:Revenue.
        var hits = index.Search("إيرادات", language: "ar");
        Assert.Contains(hits.Items, c => c.QualifiedName == SyntheticProfile.Qualified("Revenue"));
    }

    [Fact]
    public void ResolveLabel_FallsBackToDefault_WhenLanguageUnsupported()
    {
        var revenue = Profile.FindConcept(SyntheticProfile.Qualified("Revenue"))!;
        Assert.Equal("إيرادات", TaxonomyIndex.ResolveLabel(revenue, "ar"));
        Assert.Equal("Revenue", TaxonomyIndex.ResolveLabel(revenue, "de")); // no de → default
        Assert.Equal("Revenue", TaxonomyIndex.ResolveLabel(revenue, null));
    }

    [Fact]
    public void Search_KeepsDistinctNamespaces_WithIdenticalLabel()
    {
        var profile = TwoNamespaceSameLabelProfile();
        var index = new TaxonomyIndex(profile);
        var hits = index.Search("Revenue");
        Assert.Empty(index.Roles);
        // Both concepts carry the label "Revenue" but live in different namespaces.
        Assert.Equal(2, hits.Items.Count);
        Assert.Equal(
            new[] { "urn:a:finance", "urn:b:finance" },
            hits.Items.Select(c => c.Namespace).OrderBy(x => x, StringComparer.Ordinal));
        // Distinct qualified names are preserved as distinct rows.
        Assert.NotEqual(hits.Items[0].QualifiedName, hits.Items[1].QualifiedName);
    }

    [Fact]
    public void PresentationNetwork_ChildrenAreOrdered_AndRootsAreDetected()
    {
        var index = new TaxonomyIndex(Profile);
        var network = index.Network(SyntheticProfile.PresentationRole);
        var roots = network.Roots().ToList();
        Assert.Contains(SyntheticProfile.Qualified("FinancialStatements"), roots);

        var children = network.ChildrenOf(SyntheticProfile.Qualified("FinancialStatements"))
            .Select(l => l.ChildQName).ToList();
        // Order follows declared Order: Revenue, Expenses, NetIncome, Assets, Liabilities.
        Assert.Equal(
            new[] { "Revenue", "Expenses", "NetIncome", "Assets", "Liabilities" }
                .Select(SyntheticProfile.Qualified),
            children);
    }

    [Fact]
    public void Concept_AppearsAcrossMultipleRoles()
    {
        var index = new TaxonomyIndex(Profile);
        var roles = index.Roles;
        Assert.Contains(roles, r => r == SyntheticProfile.PresentationRole);
        Assert.Contains(roles, r => r == SyntheticProfile.PresentationRole + "/income-statement");

        // Revenue appears under the summary role (as child of FinancialStatements)
        var summary = index.Network(SyntheticProfile.PresentationRole);
        Assert.Contains(summary.ChildrenOf(SyntheticProfile.Qualified("FinancialStatements")),
            l => l.ChildQName == SyntheticProfile.Qualified("Revenue"));

        // And under the income-statement role (as child of NetIncome).
        var income = index.Network(SyntheticProfile.PresentationRole + "/income-statement");
        Assert.Contains(income.ChildrenOf(SyntheticProfile.Qualified("NetIncome")),
            l => l.ChildQName == SyntheticProfile.Qualified("Revenue"));
    }

    [Fact]
    public void PresentationOccurrences_ReturnEveryRootToConceptPathAcrossRoles()
    {
        var index = new TaxonomyIndex(Profile);

        var occurrences = index.PresentationOccurrencesFor(SyntheticProfile.Qualified("Revenue"));

        Assert.Equal(2, occurrences.Count);
        Assert.Contains(occurrences, occurrence =>
            occurrence.RoleUri == SyntheticProfile.PresentationRole
            && occurrence.ConceptPath.SequenceEqual(new[]
            {
                SyntheticProfile.Qualified("FinancialStatements"),
                SyntheticProfile.Qualified("Revenue")
            }));
        Assert.Contains(occurrences, occurrence =>
            occurrence.RoleUri == SyntheticProfile.PresentationRole + "/income-statement"
            && occurrence.ConceptPath.SequenceEqual(new[]
            {
                SyntheticProfile.Qualified("NetIncome"),
                SyntheticProfile.Qualified("Revenue")
            }));
    }

    [Fact]
    public void ProfileRelationshipsRetainRoleOccurrencesAndPageOrderedChildren()
    {
        var source = Profile;
        var roleOne = "https://example.invalid/roles/statement";
        var roleTwo = "https://example.invalid/roles/income";
        var links = new[]
        {
            new PresentationLink(roleOne, SyntheticProfile.Qualified("FinancialStatements"),
                SyntheticProfile.Qualified("Revenue"), 2.5m),
            new PresentationLink(roleOne, SyntheticProfile.Qualified("FinancialStatements"),
                SyntheticProfile.Qualified("Expenses"), 1.25m),
            new PresentationLink(roleTwo, SyntheticProfile.Qualified("NetIncome"),
                SyntheticProfile.Qualified("Revenue"), 1m)
        };
        var profile = new FilingProfile
        {
            ProfileId = "manifest-relationships",
            Digest = "sha256:manifest-relationships-v1",
            Concepts = source.Concepts,
            SupportedUnits = source.SupportedUnits,
            NamespaceUris = source.NamespaceUris,
            PresentationLinks = links
        };
        var index = new TaxonomyIndex(profile);

        var firstPage = index.Network(roleOne).ChildrenPage(
            SyntheticProfile.Qualified("FinancialStatements"), page: 0, pageSize: 1);
        var secondPage = index.Network(roleOne).ChildrenPage(
            SyntheticProfile.Qualified("FinancialStatements"), page: 1, pageSize: 1);
        var revenueOccurrences = index.PresentationOccurrencesFor(SyntheticProfile.Qualified("Revenue"));

        Assert.Equal(2, firstPage.TotalCount);
        Assert.True(firstPage.HasNext);
        Assert.Equal(1.25m, Assert.Single(firstPage.Items).Order);
        Assert.False(secondPage.HasNext);
        Assert.Equal(2.5m, Assert.Single(secondPage.Items).Order);
        Assert.Equal(2, revenueOccurrences.Count);
        Assert.Contains(revenueOccurrences, occurrence => occurrence.RoleUri == roleOne);
        Assert.Contains(revenueOccurrences, occurrence => occurrence.RoleUri == roleTwo);
        Assert.Equal(source.Concepts.Count, index.AllConcepts.Count);
        Assert.Same(source.FindConcept(SyntheticProfile.Qualified("Revenue")),
            index.Get(SyntheticProfile.Qualified("Revenue")));
    }

    [Fact]
    public void PresentationOccurrences_IgnoreCyclesAndKeepOtherValidPaths()
    {
        var source = Profile;
        var role = "https://example.invalid/roles/cycle";
        var links = new[]
        {
            new PresentationLink(role, SyntheticProfile.Qualified("FinancialStatements"),
                SyntheticProfile.Qualified("Revenue"), 1),
            new PresentationLink(role, SyntheticProfile.Qualified("Revenue"),
                SyntheticProfile.Qualified("FinancialStatements"), 2),
            new PresentationLink(role, SyntheticProfile.Qualified("NetIncome"),
                SyntheticProfile.Qualified("FinancialStatements"), 3)
        };
        var profile = new FilingProfile
        {
            ProfileId = "cycle-relationships",
            Digest = "sha256:cycle-relationships-v1",
            Concepts = source.Concepts,
            SupportedUnits = source.SupportedUnits,
            PresentationLinks = links
        };

        var occurrences = new TaxonomyIndex(profile).PresentationOccurrencesFor(
            SyntheticProfile.Qualified("Revenue"));

        var occurrence = Assert.Single(occurrences);
        Assert.Equal(role, occurrence.RoleUri);
        Assert.Equal(new[]
        {
            SyntheticProfile.Qualified("NetIncome"),
            SyntheticProfile.Qualified("FinancialStatements"),
            SyntheticProfile.Qualified("Revenue")
        }, occurrence.ConceptPath);
    }

    [Fact]
    public void PresentationNetwork_ChildPagingObservesCancellationAndRejectsUnknownEndpoints()
    {
        var index = new TaxonomyIndex(Profile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => index.Network(SyntheticProfile.PresentationRole)
            .ChildrenPage(SyntheticProfile.Qualified("FinancialStatements"), cancellationToken: cancellation.Token));
        var invalidProfile = new FilingProfile
        {
            ProfileId = "invalid-relationships",
            Digest = "sha256:invalid-relationships",
            Concepts = Profile.Concepts,
            SupportedUnits = Profile.SupportedUnits,
            PresentationLinks = new[]
            {
                new PresentationLink("https://example.invalid/roles/bad", "missing:Parent",
                    SyntheticProfile.Qualified("Revenue"), 1)
            }
        };
        Assert.Throws<ArgumentException>(() => new TaxonomyIndex(invalidProfile));
    }

    [Fact]
    public async Task Search_ObservesPreCancelledTokens_SynchronouslyAndAsynchronously()
    {
        var index = new TaxonomyIndex(Profile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            index.Search("Revenue", cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            index.SearchAsync("Revenue", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void AbstractConcept_IsRejected_AsFactTarget()
    {
        var index = new TaxonomyIndex(Profile);
        var financial = index.Get(SyntheticProfile.Qualified("FinancialStatements"))!;
        var revenue = index.Get(SyntheticProfile.Qualified("Revenue"))!;
        Assert.True(financial.IsAbstract);
        Assert.False(TaxonomyIndex.IsFactTargetable(financial));
        Assert.True(TaxonomyIndex.IsFactTargetable(revenue));
    }

    [Fact]
    public void LargeIndex_UsesExpandedNamesAndStableDigestBoundPaging()
    {
        const int conceptCount = 20_000;
        var concepts = Enumerable.Range(0, conceptCount)
            .Select(index =>
            {
                var namespaceIndex = index % 4;
                var localName = $"Disclosure{index:D5}";
                return new ConceptMetadata(
                    $"t{namespaceIndex}:{localName}",
                    $"urn:finance:{namespaceIndex}",
                    localName,
                    "Revenue",
                    "monetary",
                    "duration",
                    IsAbstract: index % 97 == 0)
                {
                    SupportedLanguages = new[] { "en", "fr" },
                    LabelsByLanguage = new Dictionary<string, string>
                    {
                        ["en"] = "Revenue",
                        ["fr"] = "Produit"
                    }
                };
            })
            .ToArray();
        var profile = new FilingProfile
        {
            ProfileId = "large-taxonomy",
            Digest = "sha256:large-taxonomy-v1",
            TaxonomyVersion = "2026.1",
            Concepts = concepts,
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "EUR", "USD" },
            DimensionDomains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["t0:RegionAxis"] = new[] { "t0:Domestic", "t0:International" }
            }
        };
        var index = new TaxonomyIndex(profile, Array.Empty<PresentationLink>());

        var firstPage = index.Search("Produit", language: "fr", page: 0, pageSize: 37);
        var nextPage = index.Search("Produit", language: "fr", page: 1, pageSize: 37);

        Assert.Equal(conceptCount, firstPage.TotalCount);
        Assert.True(firstPage.HasNext);
        Assert.Equal(37, firstPage.Items.Count);
        Assert.Equal(37, nextPage.Items.Count);
        Assert.Equal(
            concepts.OrderBy(concept => concept.Namespace, StringComparer.Ordinal)
                .ThenBy(concept => concept.LocalName, StringComparer.Ordinal)
                .Take(74)
                .Select(concept => concept.QualifiedName),
            firstPage.Items.Concat(nextPage.Items).Select(concept => concept.QualifiedName));
        Assert.Same(concepts[19_999], index.GetByExpandedName(
            concepts[19_999].Namespace, concepts[19_999].LocalName));
        Assert.Equal("2026.1", index.TaxonomyVersion);
        Assert.Contains("USD", index.SupportedUnits);
        Assert.Contains("t0:Domestic", index.DimensionDomains["t0:RegionAxis"]);
        Assert.True(index.IsCurrentFor(profile));

        var changedProfile = new FilingProfile
        {
            ProfileId = profile.ProfileId,
            Digest = "sha256:large-taxonomy-v2",
            TaxonomyVersion = "2026.2",
            Concepts = concepts,
            SupportedUnits = profile.SupportedUnits,
            DimensionDomains = profile.DimensionDomains
        };
        Assert.False(index.IsCurrentFor(changedProfile));
        Assert.Empty(index.Search("", page: int.MaxValue, pageSize: int.MaxValue).Items);
    }

    [Fact]
    public void Search_NegativePage_Throws()
    {
        var index = new TaxonomyIndex(Profile);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("", page: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("", pageSize: 0));
    }

    private static FilingProfile TwoNamespaceSameLabelProfile()
    {
        var concepts = new[]
        {
            new ConceptMetadata("a:Revenue", "urn:a:finance", "Revenue", "Revenue",
                "monetary", "duration", IsAbstract: false),
            new ConceptMetadata("b:Revenue", "urn:b:finance", "Revenue", "Revenue",
                "monetary", "duration", IsAbstract: false),
        };
        return new FilingProfile
        {
            ProfileId = "two-ns",
            Digest = "sha256:two-ns",
            Concepts = concepts,
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" }
        };
    }
}
