using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Desktop.Tests;

public sealed class TaxonomyExplorerTests
{
    [Fact]
    public async Task RapidSearchCriteriaChanges_LeaveOnlyTheNewestTwentyThousandConceptQuery()
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
                    $"Disclosure label {index:D5}",
                    "monetary",
                    "duration",
                    IsAbstract: false);
            })
            .ToArray();
        var profile = new FilingProfile
        {
            ProfileId = "desktop-large-taxonomy",
            Digest = "sha256:desktop-large-taxonomy-v1",
            Concepts = concepts,
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" }
        };
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "Large taxonomy explorer test",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest
        };
        var viewModel = new MainWindowViewModel(
            project,
            null,
            (_, _) => throw new NotSupportedException("No source adapter is needed."),
            new FilingProfileResolver(new[] { profile }));

        viewModel.SearchTerm = "Disclosure00000";
        viewModel.SearchTaxonomyCommand.Execute(null);

        viewModel.SearchTerm = "Disclosure19999";
        viewModel.SearchTaxonomyCommand.Execute(null);

        await WaitForTaxonomySearchAsync(viewModel);

        var result = Assert.Single(viewModel.ConceptResults);
        Assert.Equal("t3:Disclosure19999", result.QualifiedName);
        Assert.DoesNotContain(viewModel.ConceptResults, row => row.QualifiedName == "t0:Disclosure00000");
        Assert.Contains("1 concept", viewModel.TaxonomySearchStatus, StringComparison.Ordinal);
        Assert.False(viewModel.IsTaxonomySearchBusy);
    }

    private static async Task WaitForTaxonomySearchAsync(MainWindowViewModel viewModel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (viewModel.IsTaxonomySearchBusy)
            await Task.Delay(5, timeout.Token);
    }
}
