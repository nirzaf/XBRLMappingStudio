using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Engine;
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

    [Fact]
    public async Task SelectedConceptShowsRoleSpecificPathsFromResolvedProfileRelationships()
    {
        var source = SyntheticProfile.Build();
        var profile = new FilingProfile
        {
            ProfileId = "desktop-profile-relationships",
            Digest = "sha256:desktop-profile-relationships-v1",
            Concepts = source.Concepts,
            SupportedUnits = source.SupportedUnits,
            NamespaceUris = source.NamespaceUris,
            PresentationLinks = new[]
            {
                new PresentationLink("https://example.invalid/roles/statement",
                    "synthetic:FinancialStatements", "synthetic:Revenue", 2.5m),
                new PresentationLink("https://example.invalid/roles/income",
                    "synthetic:NetIncome", "synthetic:Revenue", 1m)
            }
        };
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "Profile relationship explorer test",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest
        };
        var viewModel = new MainWindowViewModel(
            project,
            null,
            (_, _) => throw new NotSupportedException("No source adapter is needed."),
            new FilingProfileResolver(new[] { profile }));

        viewModel.SearchTerm = "Revenue";
        viewModel.SearchTaxonomyCommand.Execute(null);
        await WaitForTaxonomySearchAsync(viewModel);
        viewModel.SelectedConcept = Assert.Single(viewModel.ConceptResults);

        Assert.Equal(2, viewModel.SelectedConceptOccurrences.Count);
        Assert.Contains(viewModel.SelectedConceptOccurrences, row =>
            row.RoleUri == "https://example.invalid/roles/statement");
        Assert.Contains(viewModel.SelectedConceptOccurrences, row =>
            row.RoleUri == "https://example.invalid/roles/income");
        Assert.Contains("2 presentation path(s)", viewModel.SelectedConceptOccurrencesStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationPanelCreatesSeparateDraftAndCanRestorePinnedSource()
    {
        var sourceProfile = MigrationProfile("sha256:migration-v1", "Revenue", "2025.1");
        var targetProfile = MigrationProfile("sha256:migration-v2", "Sales revenue", "2026.1");
        var project = new MappingProject
        {
            ProjectId = "desktop-migration-project",
            Name = "Migration panel test",
            ProfileId = sourceProfile.ProfileId,
            ProfileDigest = sourceProfile.Digest,
            Nodes = new List<MappingNode>
            {
                new()
                {
                    NodeId = "fact-1",
                    NodeType = NodeTypeConstants.FactConstruct,
                    DisplayName = "Revenue fact",
                    Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["concept"] = "ex:Revenue"
                    }
                }
            }
        };
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "reviewer@example.invalid",
            ApprovedUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            ProfileDigest = sourceProfile.Digest
        });
        var root = Path.Combine(Path.GetTempPath(), "xms-taxonomy-migration-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.xmap");
        try
        {
            new ProjectManager(path).Save(project);
            var viewModel = new MainWindowViewModel(
                project,
                null,
                (_, _) => throw new NotSupportedException("No source adapter is needed."),
                new FilingProfileResolver(new[] { sourceProfile, targetProfile }));
            viewModel.LoadProject(path);

            viewModel.SelectedTaxonomyMigrationTarget = Assert.Single(viewModel.TaxonomyMigrationTargets);
            viewModel.PreviewTaxonomyMigrationCommand.Execute(null);
            Assert.True(viewModel.CanApplyTaxonomyMigration);
            Assert.Contains("Profile digest", viewModel.TaxonomyMigrationSummary, StringComparison.Ordinal);

            viewModel.CreateTaxonomyMigrationDraftCommand.Execute(null);
            Assert.Equal(targetProfile.Digest, viewModel.Session.Project.ProfileDigest);
            Assert.Null(viewModel.ProjectPath);
            Assert.True(viewModel.IsProjectDirty);
            Assert.True(viewModel.CanRollbackTaxonomyMigration);
            Assert.Throws<InvalidOperationException>(() => viewModel.SaveProjectTo(path));
            Assert.Equal(sourceProfile.Digest, new ProjectManager(path).Load().ProfileDigest);
            var draftPath = Path.Combine(root, "upgraded-draft.xmap");
            viewModel.SaveProjectTo(draftPath);
            Assert.Equal(targetProfile.Digest, new ProjectManager(draftPath).Load().ProfileDigest);
            Assert.True(viewModel.CanRollbackTaxonomyMigration);

            viewModel.RollbackTaxonomyMigrationCommand.Execute(null);

            Assert.Equal(sourceProfile.Digest, viewModel.Session.Project.ProfileDigest);
            Assert.Equal(path, viewModel.ProjectPath);
            Assert.False(viewModel.IsProjectDirty);
            Assert.Single(viewModel.Session.Project.Approvals);
            Assert.Equal(targetProfile.Digest, new ProjectManager(draftPath).Load().ProfileDigest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FilingProfile MigrationProfile(string digest, string label, string version)
        => new()
        {
            ProfileId = "desktop-migration-profile",
            Digest = digest,
            TaxonomyVersion = version,
            Concepts = new[]
            {
                new ConceptMetadata("ex:Revenue", "urn:example", "Revenue", label,
                    "monetary", "duration", IsAbstract: false)
            },
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" },
            NamespaceUris = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ex"] = "urn:example"
            }
        };

    private static async Task WaitForTaxonomySearchAsync(MainWindowViewModel viewModel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (viewModel.IsTaxonomySearchBusy)
            await Task.Delay(5, timeout.Token);
    }
}
