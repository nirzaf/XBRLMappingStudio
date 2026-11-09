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
    public void SchemaV5CatalogRoundTripsCalculationAndDefinitionLinksIntoProfileDigest()
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-linkbase-profile", "1.0.0", "Revenue");
            var manifestPath = Path.Combine(installed.InstallationDirectory,
                InstalledFilingProfileCatalog.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<FilingProfileManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var costs = new ConceptMetadata("ex:Costs", "urn:example", "Costs", "Costs",
                "monetary", "duration", IsAbstract: false);
            var calculation = new CalculationLink("https://example.invalid/roles/calculation",
                "ex:Revenue", "ex:Costs", -1m, 2m);
            var definition = new DefinitionLink("https://example.invalid/roles/dimensions",
                "https://xbrl.org/int/dim/arcrole/domain-member", "ex:Revenue", "ex:Costs",
                1m, Usable: false, ContextElement: "segment");
            manifest = manifest with
            {
                Concepts = manifest.Concepts.Append(costs).ToList(),
                CalculationLinks = new List<CalculationLink> { calculation },
                DefinitionLinks = new List<DefinitionLink> { definition },
                ProfileDigest = ""
            };
            manifest = manifest with { ProfileDigest = manifest.ComputeDigest() };
            var digestWithRelationships = manifest.ProfileDigest;
            var changedRelationshipDigest = (manifest with
            {
                CalculationLinks = new List<CalculationLink> { calculation with { Weight = 1m } },
                ProfileDigest = ""
            }).ComputeDigest();
            Assert.NotEqual(digestWithRelationships, changedRelationshipDigest);
            File.WriteAllText(manifestPath, manifest.ToJson());

            var profile = Assert.Single(InstalledFilingProfileCatalog.Load(root).Profiles).Profile;
            var index = new TaxonomyIndex(profile);
            var calculationLink = Assert.Single(index.CalculationNetwork(calculation.RoleUri).Links);
            var definitionLink = Assert.Single(index.DefinitionNetwork(definition.RoleUri, definition.ArcRoleUri).Links);

            Assert.Equal(manifest.ProfileDigest, profile.Digest);
            Assert.Equal(-1m, calculationLink.Weight);
            Assert.Equal("ex:Costs", calculationLink.ChildQName);
            Assert.Equal(false, definitionLink.Usable);
            Assert.Equal("segment", definitionLink.ContextElement);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineInstallerCopiesPinnedFilesAtomicallyAndLeavesProfileDisabled()
    {
        var sourceRoot = NewCatalogDirectory();
        var catalogRoot = NewCatalogDirectory();
        try
        {
            var source = WriteInstalledProfile(sourceRoot, "fixture-offline-profile", "1.2.0", "Revenue");

            var installed = await InstalledFilingProfileCatalog.InstallAsync(source.InstallationDirectory, catalogRoot);

            Assert.Equal("fixture-offline-profile", installed.Profile.ProfileId);
            Assert.Equal("1.2.0", installed.Version);
            Assert.False(installed.IsEnabled);
            Assert.Equal(Path.Combine(catalogRoot, installed.Profile.ProfileId, installed.Version),
                installed.InstallationDirectory);
            Assert.True(File.Exists(Path.Combine(installed.InstallationDirectory,
                InstalledFilingProfileCatalog.DisabledMarkerFileName)));
            Assert.Equal(File.ReadAllText(source.EntryPointPath), File.ReadAllText(installed.EntryPointPath));

            var profileDirectory = Path.GetDirectoryName(installed.InstallationDirectory)!;
            Directory.CreateDirectory(Path.Combine(profileDirectory, ".installing-abandoned"));
            var catalog = InstalledFilingProfileCatalog.Load(catalogRoot);
            Assert.Single(catalog.Profiles);
            Assert.Empty(catalog.Issues);
            Assert.False(Assert.Single(catalog.Profiles).IsEnabled);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineInstallerRejectsTamperedSourceBeforeCreatingProfileDirectory()
    {
        var sourceRoot = NewCatalogDirectory();
        var catalogRoot = NewCatalogDirectory();
        try
        {
            var source = WriteInstalledProfile(sourceRoot, "fixture-tampered-profile", "1.0.0", "Revenue");
            File.WriteAllText(source.EntryPointPath, "<schema changed='true'/>");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InstalledFilingProfileCatalog.InstallAsync(source.InstallationDirectory, catalogRoot));

            Assert.False(Directory.Exists(Path.Combine(catalogRoot, "fixture-tampered-profile")));
            Assert.Empty(InstalledFilingProfileCatalog.Load(catalogRoot).Profiles);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineInstallerRefusesToReplaceAnExistingVersion()
    {
        var sourceRoot = NewCatalogDirectory();
        var catalogRoot = NewCatalogDirectory();
        try
        {
            var source = WriteInstalledProfile(sourceRoot, "fixture-immutable-profile", "1.0.0", "Revenue");
            var first = await InstalledFilingProfileCatalog.InstallAsync(source.InstallationDirectory, catalogRoot);
            var markerPath = Path.Combine(first.InstallationDirectory,
                InstalledFilingProfileCatalog.DisabledMarkerFileName);
            const string markerContent = "keep existing version";
            File.WriteAllText(markerPath, markerContent);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InstalledFilingProfileCatalog.InstallAsync(source.InstallationDirectory, catalogRoot));

            Assert.Equal(markerContent, File.ReadAllText(markerPath));
            Assert.Single(InstalledFilingProfileCatalog.Load(catalogRoot).Profiles);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public void CatalogIndexesDimensionDomainsAndKeepsLegacyManifestDigestCompatible()
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-dimension-profile", "3.0.0", "Revenue");
            var manifestPath = Path.Combine(
                installed.InstallationDirectory, InstalledFilingProfileCatalog.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<FilingProfileManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var dimensionManifest = manifest with
            {
                Concepts = manifest.Concepts.Concat(new[]
                {
                    new ConceptMetadata("ex:RegionAxis", "urn:example", "RegionAxis",
                        "Region", "dimension", "duration", IsAbstract: true),
                    new ConceptMetadata("ex:NorthAmerica", "urn:example", "NorthAmerica",
                        "North America", "domain-item", "duration", IsAbstract: false),
                    new ConceptMetadata("ex:Statements", "urn:example", "Statements",
                        "Statements", "string", "duration", IsAbstract: true),
                    new ConceptMetadata("ex:Income", "urn:example", "Income",
                        "Income", "string", "duration", IsAbstract: true)
                }).ToList(),
                DimensionDomains = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["ex:RegionAxis"] = new() { "ex:NorthAmerica" }
                },
                PresentationLinks = new List<PresentationLink>
                {
                    new("https://example.invalid/roles/statement", "ex:Statements", "ex:Revenue", 2.5m),
                    new("https://example.invalid/roles/income", "ex:Income", "ex:Revenue", 1m)
                },
                ProfileDigest = ""
            };
            dimensionManifest = dimensionManifest with
            {
                ProfileDigest = dimensionManifest.ComputeDigest()
            };
            File.WriteAllText(manifestPath, dimensionManifest.ToJson());

            var loaded = Assert.Single(InstalledFilingProfileCatalog.Load(root).Profiles);
            Assert.Equal("3.0.0", loaded.Profile.TaxonomyVersion);
            Assert.Equal(new[] { "ex:NorthAmerica" }, loaded.Profile.DimensionDomains["ex:RegionAxis"]);
            Assert.Equal(2, new TaxonomyIndex(loaded.Profile)
                .PresentationOccurrencesFor("ex:Revenue").Count);
            Assert.NotEqual(dimensionManifest.ProfileDigest,
                (dimensionManifest with
                {
                    DimensionDomains = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                    {
                        ["ex:RegionAxis"] = new() { "ex:NorthAmerica", "ex:Asia" }
                    },
                    ProfileDigest = ""
                }).ComputeDigest());
            Assert.Equal(dimensionManifest.ProfileDigest,
                (dimensionManifest with
                {
                    PresentationLinks = dimensionManifest.PresentationLinks.AsEnumerable().Reverse().ToList(),
                    ProfileDigest = ""
                }).ComputeDigest());
            Assert.NotEqual(dimensionManifest.ProfileDigest,
                (dimensionManifest with
                {
                    PresentationLinks = new List<PresentationLink>
                    {
                        dimensionManifest.PresentationLinks[0] with { Order = 3m },
                        dimensionManifest.PresentationLinks[1]
                    },
                    ProfileDigest = ""
                }).ComputeDigest());

            var legacyV3 = dimensionManifest with
            {
                SchemaVersion = 3,
                PresentationLinks = new List<PresentationLink>(),
                ProfileDigest = ""
            };
            legacyV3 = legacyV3 with { ProfileDigest = legacyV3.ComputeDigest() };
            File.WriteAllText(manifestPath, legacyV3.ToJson());
            var reloadedV3 = Assert.Single(InstalledFilingProfileCatalog.Load(root).Profiles);
            Assert.Equal(3, reloadedV3.SchemaVersion);
            Assert.Empty(reloadedV3.Profile.PresentationLinks);
            Assert.Equal(new[] { "ex:NorthAmerica" }, reloadedV3.Profile.DimensionDomains["ex:RegionAxis"]);

            var legacy = manifest with
            {
                SchemaVersion = 2,
                ProfileDigest = ""
            };
            legacy = legacy with { ProfileDigest = legacy.ComputeDigest() };
            File.WriteAllText(manifestPath, legacy.ToJson());
            var reloadedLegacy = Assert.Single(InstalledFilingProfileCatalog.Load(root).Profiles);
            Assert.Equal(2, reloadedLegacy.SchemaVersion);
            Assert.Equal("fixture-dimension-profile", reloadedLegacy.Profile.ProfileId);
            Assert.Empty(reloadedLegacy.Profile.DimensionDomains);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SchemaV7IndexesDigestBoundTypedDimensionDomainMetadata()
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-typed-dimension-profile", "7.0.0", "Revenue");
            var manifestPath = Path.Combine(
                installed.InstallationDirectory, InstalledFilingProfileCatalog.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<FilingProfileManifest>(
                File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var typedManifest = manifest with
            {
                Concepts = manifest.Concepts.Append(new ConceptMetadata(
                    "ex:TypedAxis", "urn:example", "TypedAxis", "Typed axis", "dimension", "duration", true)).ToList(),
                NamespaceUris = new Dictionary<string, string>(manifest.NamespaceUris, StringComparer.Ordinal)
                {
                    ["xs"] = "http://www.w3.org/2001/XMLSchema"
                },
                TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
                {
                    ["ex:TypedAxis"] = new("ex:TypedMember", "xs:string")
                },
                ProfileDigest = ""
            };
            typedManifest = typedManifest with { ProfileDigest = typedManifest.ComputeDigest() };
            File.WriteAllText(manifestPath, typedManifest.ToJson());

            var loaded = Assert.Single(InstalledFilingProfileCatalog.Load(root).Profiles);
            Assert.Equal(new TypedDimensionDomainMetadata("ex:TypedMember", "xs:string"),
                loaded.Profile.TypedDimensionDomains["ex:TypedAxis"]);

            var changedElement = typedManifest with
            {
                TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
                {
                    ["ex:TypedAxis"] = new("ex:OtherMember", "xs:string")
                },
                ProfileDigest = ""
            };
            Assert.NotEqual(typedManifest.ProfileDigest, changedElement.ComputeDigest());
            var changedType = typedManifest with
            {
                TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
                {
                    ["ex:TypedAxis"] = new("ex:TypedMember", "xs:token")
                },
                ProfileDigest = ""
            };
            Assert.NotEqual(typedManifest.ProfileDigest, changedType.ComputeDigest());

            var legacy = typedManifest with { SchemaVersion = 6, ProfileDigest = "" };
            legacy = legacy with { ProfileDigest = legacy.ComputeDigest() };
            File.WriteAllText(manifestPath, legacy.ToJson());
            var rejected = InstalledFilingProfileCatalog.Load(root);
            Assert.Empty(rejected.Profiles);
            Assert.Contains(rejected.Issues, issue => issue.Message.Contains("schema version 7", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ex:Missing:bad", "xs:string")]
    [InlineData("ex:TypedMember", "xs:decimal")]
    [InlineData("ex:TypedMember", "fake:string")]
    public void CatalogRejectsUnsupportedTypedDimensionMetadata(string domainElementQName, string dataTypeQName)
    {
        var root = NewCatalogDirectory();
        try
        {
            WriteInvalidManifest(root, "fixture-invalid-typed-domain", "1.0.0", manifest => manifest with
            {
                Concepts = manifest.Concepts.Append(new ConceptMetadata(
                    "ex:TypedAxis", "urn:example", "TypedAxis", "Typed axis", "dimension", "duration", true)).ToList(),
                NamespaceUris = new Dictionary<string, string>(manifest.NamespaceUris, StringComparer.Ordinal)
                {
                    ["xs"] = "http://www.w3.org/2001/XMLSchema",
                    ["fake"] = "urn:not-xml-schema"
                },
                TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
                {
                    ["ex:TypedAxis"] = new(domainElementQName, dataTypeQName)
                }
            });

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue => issue.Message.Contains("supported XSD simple type", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsPresentationRelationshipsThatReferenceUnknownConcepts()
    {
        var root = NewCatalogDirectory();
        try
        {
            WriteInvalidManifest(root, "fixture-invalid-relationship", "1.0.0", manifest => manifest with
            {
                PresentationLinks = new List<PresentationLink>
                {
                    new("https://example.invalid/roles/invalid", "ex:Missing", "ex:Revenue", 1m)
                }
            });

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue =>
                issue.ProfileId == "fixture-invalid-relationship"
                && issue.Message.Contains("reference indexed concepts", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsDuplicatePresentationRelationships()
    {
        var root = NewCatalogDirectory();
        try
        {
            var relationship = new PresentationLink(
                "https://example.invalid/roles/duplicate", "ex:Revenue", "ex:Revenue", 1m);
            WriteInvalidManifest(root, "fixture-duplicate-relationship", "1.0.0", manifest => manifest with
            {
                PresentationLinks = new List<PresentationLink> { relationship, relationship }
            });

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue =>
                issue.ProfileId == "fixture-duplicate-relationship"
                && issue.Message.Contains("duplicate arcs", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsCalculationRelationshipsBeforeSchemaV5()
    {
        var root = NewCatalogDirectory();
        try
        {
            WriteInvalidManifest(root, "fixture-old-linkbase-schema", "1.0.0", manifest => manifest with
            {
                SchemaVersion = 4,
                CalculationLinks = new List<CalculationLink>
                {
                    new("https://example.invalid/roles/calculation", "ex:Revenue", "ex:Revenue", 1m)
                }
            });

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue =>
                issue.ProfileId == "fixture-old-linkbase-schema"
                && issue.Message.Contains("schema version 5", StringComparison.Ordinal));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmTaxonomyCacheCannotResolveMissingOrTamperedInstalledPackage(bool removePackage)
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-cached-profile", "1.0.0", "Revenue");
            var database = new LocalApplicationDatabase(Path.Combine(root, "state.db"));
            var cache = new TaxonomyConceptIndexStore(database);
            var warm = cache.LoadOrBuild(installed.Profile);
            Assert.False(warm.ReusedCachedMetadata);

            if (removePackage)
                File.Delete(installed.EntryPointPath);
            else
                File.AppendAllText(installed.EntryPointPath, "<!-- changed after cache warmup -->");

            var resolver = FilingProfileResolver.FromInstalledDirectory(root);
            var result = resolver.Resolve(Project(installed.Profile), allowSyntheticProfile: false);

            Assert.False(result.Success);
            Assert.Null(result.Profile);
            Assert.Contains(result.Diagnostics.Items, diagnostic =>
                diagnostic.Code == DiagnosticCodes.XbrProfileMismatch
                && (diagnostic.Message.Contains("Package hash mismatch", StringComparison.Ordinal)
                    || diagnostic.Message.Contains("is missing", StringComparison.Ordinal)));

            var missingIdentity = resolver.Resolve(
                WithProfile(Project(installed.Profile), "", ""), allowSyntheticProfile: false);
            Assert.False(missingIdentity.Success);
            Assert.Null(missingIdentity.Profile);
            Assert.Contains(missingIdentity.Diagnostics.Items,
                diagnostic => diagnostic.Code == DiagnosticCodes.XbrProfileMismatch);

            using var connection = database.OpenConnection();
            using var check = connection.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM taxonomy_profile_index
                WHERE profile_id=$profile_id AND profile_digest=$profile_digest;
                """;
            check.Parameters.AddWithValue("$profile_id", installed.Profile.ProfileId);
            check.Parameters.AddWithValue("$profile_digest", installed.Profile.Digest);
            Assert.Equal(1L, check.ExecuteScalar());
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
    public void DisabledInstalledProfileFailsClosedWithoutChangingItsIdentity()
    {
        var root = NewCatalogDirectory();
        try
        {
            var installed = WriteInstalledProfile(root, "fixture-disabled-profile", "1.0.0", "Revenue");
            File.WriteAllText(Path.Combine(installed.InstallationDirectory,
                InstalledFilingProfileCatalog.DisabledMarkerFileName), "disabled by local administrator");

            var loaded = InstalledFilingProfileCatalog.Load(root);
            var installation = Assert.Single(loaded.Profiles);
            Assert.False(installation.IsEnabled);
            Assert.Equal(installed.Profile.ProfileId, installation.Profile.ProfileId);
            Assert.Equal(installed.Profile.Digest, installation.Profile.Digest);

            var resolver = FilingProfileResolver.FromInstalledDirectory(root);
            var result = resolver.Resolve(Project(installed.Profile), allowSyntheticProfile: false);

            Assert.False(result.Success);
            Assert.Null(result.Profile);
            Assert.Contains(result.Diagnostics.Items, diagnostic =>
                diagnostic.Code == DiagnosticCodes.XbrProfileNotAuthorized
                && diagnostic.Message.Contains("disabled", StringComparison.Ordinal)
                && diagnostic.Message.Contains("no source data was read", StringComparison.Ordinal));

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

    [Theory]
    [InlineData("?version=1")]
    [InlineData("?")]
    [InlineData("#schema-fragment")]
    [InlineData("#")]
    public void CatalogRejectsOfflineReferenceQueriesAndFragments(string suffix)
    {
        var root = NewCatalogDirectory();
        try
        {
            WriteInvalidManifest(root, "fixture-reference-uri-profile", "1.0.0", manifest => manifest with
            {
                OfflineReferences = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["https://example.invalid/common.xsd" + suffix] = "imports/common.xsd"
                }
            });

            var result = InstalledFilingProfileCatalog.Load(root);

            Assert.Empty(result.Profiles);
            Assert.Contains(result.Issues, issue => issue.ProfileId == "fixture-reference-uri-profile"
                && issue.Message.Contains("without credentials, query, or fragment", StringComparison.Ordinal));
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
    public void CatalogRequiredCheckLimitsMatchWorkerCodePointAndCountRules()
    {
        var cases = new (IReadOnlyList<string> Checks, bool Accepted)[]
        {
            (new[] { "rule:" + string.Concat(Enumerable.Repeat("😀", 507)) }, true), // 512 code points
            (new[] { new string('x', 513) }, false),
            (new[] { "rule:bad\u0085id" }, false),
            (Enumerable.Range(0, 2049).Select(index => $"rule:{index}").ToArray(), false)
        };

        foreach (var (checks, accepted) in cases)
        {
            var root = NewCatalogDirectory();
            try
            {
                WriteInvalidManifest(root, "fixture-check-limit-profile", "1.0.0", manifest => manifest with
                {
                    RequiredChecks = checks.ToList()
                });

                var result = InstalledFilingProfileCatalog.Load(root);
                Assert.Equal(accepted, result.Profiles.Count == 1);
                if (!accepted)
                    Assert.Contains(result.Issues, issue => issue.Message.Contains(
                        "required validation check", StringComparison.OrdinalIgnoreCase));
                else
                    Assert.Equal(checks.Order(StringComparer.Ordinal),
                        result.Profiles.Single().Profile.RequiredChecks.Order(StringComparer.Ordinal));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
        Assert.False(invoked);
        await worker.HandshakeAsync(CancellationToken.None);
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
