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
