using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class FilingProfileResolverTests
{
    [Fact]
    public void SyntheticProfileRequiresExplicitOptIn()
    {
        var profile = SyntheticProfile.Build();
        var result = FilingProfileResolver.DevelopmentOnly().Resolve(Project(profile), allowSyntheticProfile: false);

        Assert.False(result.Success);
        Assert.Null(result.Profile);
        Assert.Contains(result.Diagnostics.Items, d =>
            d.Code == DiagnosticCodes.XbrSyntheticProfileNotAuthorized && d.IsBlocking);
    }

    [Fact]
    public void SyntheticProfileResolvesOnlyWhenIdentityMatchesAndCallerOptsIn()
    {
        var profile = SyntheticProfile.Build();
        var result = FilingProfileResolver.DevelopmentOnly().Resolve(Project(profile), allowSyntheticProfile: true);

        Assert.True(result.Success);
        Assert.Equal(profile.ProfileId, result.Profile?.ProfileId);
        Assert.Equal(profile.Digest, result.Profile?.Digest);
        Assert.Empty(result.Diagnostics.Items);
    }

    [Fact]
    public void UnknownProfileFailsWithoutSyntheticFallback()
    {
        var project = WithProfile(Project(SyntheticProfile.Build()), "uninstalled-profile-v1", "sha256:missing");
        var result = FilingProfileResolver.DevelopmentOnly().Resolve(project, allowSyntheticProfile: true);

        Assert.False(result.Success);
        Assert.Null(result.Profile);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == DiagnosticCodes.XbrProfileMismatch);
    }

    [Fact]
    public void KnownProfileWithStaleDigestFailsClosed()
    {
        var profile = SyntheticProfile.Build();
        var project = WithProfile(Project(profile), profile.ProfileId, "sha256:stale");
        var result = FilingProfileResolver.DevelopmentOnly().Resolve(project, allowSyntheticProfile: true);

        Assert.False(result.Success);
        Assert.Null(result.Profile);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == DiagnosticCodes.XbrProfileMismatch);
    }

    [Fact]
    public void ResolverCanUseAnExplicitlyInstalledNonSyntheticProfile()
    {
        var profile = new FilingProfile
        {
            ProfileId = "fixture-real-profile-v1",
            Digest = "sha256:fixture-profile-digest",
            Concepts = Array.Empty<ConceptMetadata>(),
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal)
        };
        var result = new FilingProfileResolver(new[] { profile })
            .Resolve(Project(profile), allowSyntheticProfile: false);

        Assert.True(result.Success);
        Assert.Same(profile, result.Profile);
    }

    [Fact]
    public void DuplicateInstalledProfileIdsAreRejected()
    {
        var profile = SyntheticProfile.Build();

        Assert.Throws<ArgumentException>(() => new FilingProfileResolver(new[] { profile, profile }));
    }

    [Fact]
    public async Task WorkerFactoryUsesLocalWorkerOnlyForExplicitSyntheticRuntime()
    {
        var profile = SyntheticProfile.Build();
        var factory = new FilingProfileWorkerFactory();

        var worker = factory.Create(profile);
        Assert.IsType<LocalSyntheticWorker>(worker);
        await worker.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => factory.Create(profile, useArelle: true));

        var realProfile = new FilingProfile
        {
            ProfileId = "fixture-real-profile-v1",
            Digest = "sha256:fixture-profile-digest",
            Concepts = Array.Empty<ConceptMetadata>(),
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal)
        };
        Assert.Throws<InvalidOperationException>(() => factory.Create(realProfile));
    }

    private static MappingProject Project(FilingProfile profile) => new()
    {
        ProjectId = "profile-resolution-test",
        Name = "Profile resolution test",
        ProfileId = profile.ProfileId,
        ProfileDigest = profile.Digest
    };

    private static MappingProject WithProfile(MappingProject source, string profileId, string digest) => new()
    {
        ProjectId = source.ProjectId,
        Name = source.Name,
        ProfileId = profileId,
        ProfileDigest = digest,
        SchemaVersion = source.SchemaVersion,
        FunctionCatalogVersion = source.FunctionCatalogVersion,
        SemanticRevision = source.SemanticRevision,
        LayoutGeneration = source.LayoutGeneration,
        Metadata = source.Metadata,
        Nodes = source.Nodes,
        Edges = source.Edges,
        Bindings = source.Bindings,
        Functions = source.Functions,
        Approvals = source.Approvals
    };
}
