using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;
using System.Security.Cryptography;
using System.Text.Json;

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
    public void CatalogLoadsCoexistingVersionsAndResolvesTheExactDigest()
    {
        var root = NewCatalogDirectory();
        try
        {
            var v1 = WriteInstalledProfile(root, "fixture-filing-profile", "1.0.0", "Revenue v1");
            var v2 = WriteInstalledProfile(root, "fixture-filing-profile", "2.0.0", "Revenue v2");
            var catalog = InstalledFilingProfileCatalog.Load(root);
            var resolver = new FilingProfileResolver(catalog.Profiles.Select(installation => installation.Profile));

            var first = resolver.Resolve(Project(v1.Profile), allowSyntheticProfile: false);
            var second = resolver.Resolve(Project(v2.Profile), allowSyntheticProfile: false);
            var stale = resolver.Resolve(WithProfile(Project(v1.Profile), v1.Profile.ProfileId, "sha256:stale"),
                allowSyntheticProfile: false);

            Assert.True(first.Success);
            Assert.Equal("1.0.0", catalog.Profiles.Single(profile => profile.Profile.Digest == v1.Profile.Digest).Version);
            Assert.Equal(FilingProfileManifest.CurrentSchemaVersion,
                catalog.Profiles.Single(profile => profile.Profile.Digest == v1.Profile.Digest).SchemaVersion);
            Assert.Equal(v1.EntryPointPath,
                catalog.Profiles.Single(profile => profile.Profile.Digest == v1.Profile.Digest).EntryPointPath);
            Assert.True(second.Success);
            Assert.Equal("2.0.0", catalog.Profiles.Single(profile => profile.Profile.Digest == v2.Profile.Digest).Version);
            Assert.False(stale.Success);
            Assert.Null(stale.Profile);
            Assert.Contains(stale.Diagnostics.Items, diagnostic => diagnostic.Code == DiagnosticCodes.XbrProfileMismatch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsTamperedPackageAndReportsTheAffectedProfile()
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-tamper-profile", "1.0.0", "Revenue");
            File.WriteAllText(installed.EntryPointPath, "<schema tampered='true'/>");

            var result = InstalledFilingProfileCatalog.Load(root);
            var resolution = FilingProfileResolver.FromInstalledDirectory(root)
                .Resolve(Project(installed.Profile), allowSyntheticProfile: false);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue => issue.ProfileId == installed.Profile.ProfileId
                && issue.Message.Contains("Package hash mismatch", StringComparison.Ordinal));
            Assert.False(resolution.Success);
            Assert.Contains(resolution.Diagnostics.Items, diagnostic => diagnostic.Message.Contains(
                "Package hash mismatch", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsMissingPackageDependency()
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-missing-profile", "1.0.0", "Revenue");
            File.Delete(Path.Combine(installed.InstallationDirectory, "imports", "common.xsd"));

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue => issue.ProfileId == installed.Profile.ProfileId
                && issue.Message.Contains("Required package path", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InstalledCatalogProfileIsBlockedUntilExternalGatesAreClosed()
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-unqualified-profile", "1.0.0", "Revenue");
            var resolver = FilingProfileResolver.FromInstalledDirectory(root);

            var result = resolver.Resolve(Project(installed.Profile), allowSyntheticProfile: false);

            Assert.False(result.Success);
            Assert.Null(result.Profile);
            Assert.Contains(result.Diagnostics.Items, diagnostic =>
                diagnostic.Code == DiagnosticCodes.XbrProfileNotAuthorized
                && diagnostic.Message.Contains("GAT-01 and GAT-02", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsTraversalAndOfflineReferencesToUnpinnedFiles()
    {
        var root = NewCatalogDirectory();
        try
        {
            WriteInvalidManifest(root, "fixture-traversal-profile", "1.0.0", manifest => manifest with
            {
                EntryPoint = "../escape.xsd",
                PackageFiles = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["../escape.xsd"] = "sha256:" + new string('a', 64)
                }
            });
            WriteInvalidManifest(root, "fixture-reference-profile", "1.0.0", manifest => manifest with
            {
                OfflineReferences = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["https://example.invalid/missing.xsd"] = "missing.xsd"
                }
            });

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue => issue.ProfileId == "fixture-traversal-profile"
                && issue.Message.Contains("safe relative path", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.ProfileId == "fixture-reference-profile"
                && issue.Message.Contains("unpinned file", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsDuplicateJsonPropertiesAndChangedManifestDigest()
    {
        var root = NewCatalogDirectory();
        try
        {
            var duplicate = WriteInstalledProfile(root, "fixture-duplicate-profile", "1.0.0", "Revenue");
            var manifestPath = Path.Combine(duplicate.InstallationDirectory, InstalledFilingProfileCatalog.ManifestFileName);
            var json = File.ReadAllText(manifestPath);
            File.WriteAllText(manifestPath,
                json.Replace($"\"schemaVersion\":{FilingProfileManifest.CurrentSchemaVersion},",
                    $"\"schemaVersion\":{FilingProfileManifest.CurrentSchemaVersion},\"SchemaVersion\":{FilingProfileManifest.CurrentSchemaVersion},",
                    StringComparison.Ordinal));

            var changed = WriteInstalledProfile(root, "fixture-changed-profile", "1.0.0", "Revenue");
            var changedManifestPath = Path.Combine(changed.InstallationDirectory, InstalledFilingProfileCatalog.ManifestFileName);
            File.WriteAllText(changedManifestPath,
                File.ReadAllText(changedManifestPath).Replace("Revenue", "Expenses", StringComparison.Ordinal));

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue => issue.ProfileId == "fixture-duplicate-profile"
                && issue.Message.Contains("Duplicate JSON property", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.ProfileId == "fixture-changed-profile"
                && issue.Message.Contains("Profile digest does not match", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsConceptNameThatDoesNotMatchItsNamespaceMapping()
    {
        var root = NewCatalogDirectory();
        try
        {
            WriteInvalidManifest(root, "fixture-qname-profile", "1.0.0", manifest => manifest with
            {
                Concepts = new List<ConceptMetadata>
                {
                    manifest.Concepts[0] with { QualifiedName = "other:Revenue" }
                }
            });

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue => issue.ProfileId == "fixture-qname-profile"
                && issue.Message.Contains("must match its namespace prefix", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DuplicateInstalledProfileIdentityIsRejected()
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

    [Fact]
    public async Task WorkerFactoryRoutesNonSyntheticProfilesToArelleWithoutSilentDowngrade()
    {
        var profile = new FilingProfile
        {
            ProfileId = "fixture-worker-profile",
            Digest = "sha256:fixture-worker-digest",
            Concepts = Array.Empty<ConceptMetadata>(),
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal)
        };
        var invoked = false;
        var factory = new FilingProfileWorkerFactory(selected =>
        {
            Assert.Same(profile, selected);
            invoked = true;
            return new LocalSyntheticWorker(SyntheticProfile.Build());
        });

        var worker = factory.Create(profile);
        await worker.DisposeAsync();

        Assert.True(invoked);
    }

    [Fact]
    public void InstalledProfileWorkerRejectsUnrelatedTaxonomyRoot()
    {
        var root = NewCatalogDirectory();
        var unrelatedRoot = Path.Combine(root, "unrelated");
        Directory.CreateDirectory(unrelatedRoot);
        var workerScript = Path.Combine(root, "worker.py");
        File.WriteAllText(workerScript, "# fixture");
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-bound-worker", "1.0.0", "Revenue");
            var resolver = FilingProfileResolver.FromInstalledDirectory(root);
            var blockedFactory = FilingProfileWorkerFactory.ForInstalledProfiles(
                resolver, "python", workerScript);
            var authorizationError = Assert.Throws<InvalidOperationException>(
                () => blockedFactory.Create(installed.Profile));
            Assert.Contains("GAT-01 and GAT-02", authorizationError.Message, StringComparison.Ordinal);

            var factory = FilingProfileWorkerFactory.ForInstalledProfiles(
                resolver, "python", workerScript, unrelatedRoot, authorizeInstallation: _ => true);

            var error = Assert.Throws<InvalidOperationException>(() => factory.Create(installed.Profile));

            Assert.Contains("verified from its installed profile directory", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MappingProject Project(FilingProfile profile) => new()
    {
        ProjectId = "profile-resolution-test",
        Name = "Profile resolution test",
        ProfileId = profile.ProfileId,
        ProfileDigest = profile.Digest
    };

    internal static string NewCatalogDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-profile-catalog", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static InstalledFilingProfile WriteInstalledProfile(
        string root, string profileId, string version, string label)
    {
        var directory = Path.Combine(root, profileId, version);
        Directory.CreateDirectory(Path.Combine(directory, "taxonomy"));
        Directory.CreateDirectory(Path.Combine(directory, "imports"));
        var entryText = $"<schema version='{version}'/>";
        var importText = $"<schema import='{version}'/>";
        var entryPath = Path.Combine(directory, "taxonomy", "entry.xsd");
        var importPath = Path.Combine(directory, "imports", "common.xsd");
        File.WriteAllText(entryPath, entryText);
        File.WriteAllText(importPath, importText);
        var manifest = new FilingProfileManifest
        {
            SchemaVersion = FilingProfileManifest.CurrentSchemaVersion,
            ProfileId = profileId,
            Version = version,
            EntryPoint = "taxonomy/entry.xsd",
            ProfileDigest = "",
            Concepts = new List<ConceptMetadata>
            {
                new("ex:Revenue", "urn:example", "Revenue", label, "monetary", "duration", false)
            },
            SupportedUnits = new List<string> { "USD" },
            NamespaceUris = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ex"] = "urn:example"
            },
            PackageNames = new List<string> { "example-taxonomy" },
            PackageFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["taxonomy/entry.xsd"] = FileDigest(entryPath),
                ["imports/common.xsd"] = FileDigest(importPath)
            },
            OfflineReferences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.invalid/common.xsd"] = "imports/common.xsd"
            }
        };
        manifest = manifest with { ProfileDigest = manifest.ComputeDigest() };
        File.WriteAllText(Path.Combine(directory, InstalledFilingProfileCatalog.ManifestFileName), manifest.ToJson());
        return InstalledFilingProfileCatalog.Load(root).Profiles.Single(profile =>
            profile.Profile.ProfileId == profileId && profile.Version == version);
    }

    private static void WriteInvalidManifest(string root, string profileId, string version,
        Func<FilingProfileManifest, FilingProfileManifest> update)
    {
        var installed = WriteInstalledProfile(root, profileId, version, "Fixture");
        var path = Path.Combine(installed.InstallationDirectory, InstalledFilingProfileCatalog.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<FilingProfileManifest>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest = update(manifest with { ProfileDigest = "" });
        manifest = manifest with { ProfileDigest = manifest.ComputeDigest() };
        File.WriteAllText(path, manifest.ToJson());
    }

    private static string FileDigest(string path)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static MappingProject WithProfile(MappingProject source, string profileId, string digest) => new()
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
