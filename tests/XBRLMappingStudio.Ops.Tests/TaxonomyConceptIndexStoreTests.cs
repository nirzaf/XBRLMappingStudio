using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class TaxonomyConceptIndexStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "xms-taxonomy-index-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public TaxonomyConceptIndexStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "state.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void LoadOrBuild_ReusesVerifiedSnapshotAndSeparatesPackageDigests()
    {
        var database = new LocalApplicationDatabase(_databasePath);
        var store = new TaxonomyConceptIndexStore(database);
        var firstProfile = BuildProfile("sha256:taxonomy-v1", "2026.1", "Revenue");

        var first = store.LoadOrBuild(firstProfile);

        Assert.False(first.ReusedCachedMetadata);
        Assert.Equal("2026.1", first.Index.TaxonomyVersion);
        Assert.Equal("USD", Assert.Single(first.Index.SupportedUnits));
        Assert.Contains("ext:Domestic", first.Index.DimensionDomains["ext:RegionAxis"]);
        Assert.Contains("Revenue", first.Index.Get("synthetic:Revenue")!.Label);

        var reused = new TaxonomyConceptIndexStore(new LocalApplicationDatabase(_databasePath))
            .LoadOrBuild(firstProfile);
        Assert.True(reused.ReusedCachedMetadata);
        Assert.Equal("إيرادات", TaxonomyIndex.ResolveLabel(
            reused.Index.GetByExpandedName(SyntheticProfile.Namespace, "Revenue")!, "ar"));
        Assert.True(reused.Index.Get("synthetic:FinancialStatements")!.IsAbstract);

        using (var connection = database.OpenConnection())
        using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = """
                UPDATE taxonomy_profile_index SET snapshot_json='corrupt'
                WHERE profile_id=$profile_id AND profile_digest=$profile_digest;
                """;
            corrupt.Parameters.AddWithValue("$profile_id", firstProfile.ProfileId);
            corrupt.Parameters.AddWithValue("$profile_digest", firstProfile.Digest);
            Assert.Equal(1, corrupt.ExecuteNonQuery());
        }

        var recovered = store.LoadOrBuild(firstProfile);
        Assert.False(recovered.ReusedCachedMetadata);
        Assert.Equal("Revenue", recovered.Index.Get("synthetic:Revenue")!.Label);

        var secondProfile = BuildProfile("sha256:taxonomy-v2", "2026.2", "Sales Revenue");
        var changed = store.LoadOrBuild(secondProfile);
        Assert.False(changed.ReusedCachedMetadata);
        Assert.Equal(secondProfile.Digest, changed.Index.ProfileDigest);
        Assert.Equal("2026.2", changed.Index.TaxonomyVersion);
        Assert.Equal("Sales Revenue", changed.Index.Get("synthetic:Revenue")!.Label);

        using var check = database.OpenConnection();
        using var count = check.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM taxonomy_profile_index
            WHERE profile_id=$profile_id AND profile_digest IN ($v1, $v2);
            """;
        count.Parameters.AddWithValue("$profile_id", firstProfile.ProfileId);
        count.Parameters.AddWithValue("$v1", firstProfile.Digest);
        count.Parameters.AddWithValue("$v2", secondProfile.Digest);
        Assert.Equal(2L, count.ExecuteScalar());
    }

    private static FilingProfile BuildProfile(string digest, string version, string revenueLabel)
    {
        var baseProfile = SyntheticProfile.Build();
        var concepts = baseProfile.Concepts
            .Select(concept => concept.QualifiedName == "synthetic:Revenue"
                ? concept with
                {
                    Label = revenueLabel,
                    LabelsByLanguage = concept.LabelsByLanguage
                        .Where(pair => pair.Key != "en")
                        .Append(new KeyValuePair<string, string>("en", revenueLabel))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                }
                : concept)
            .Concat(new[]
            {
                new ConceptMetadata("ext:RegionAxis", "urn:example:extension", "RegionAxis",
                    "Region", "dimension", "duration", IsAbstract: true),
                new ConceptMetadata("ext:Domestic", "urn:example:extension", "Domestic",
                    "Domestic", "domain-item", "duration", IsAbstract: false),
                new ConceptMetadata("ext:International", "urn:example:extension", "International",
                    "International", "domain-item", "duration", IsAbstract: false)
            })
            .ToArray();
        var namespaceUris = new Dictionary<string, string>(baseProfile.NamespaceUris, StringComparer.Ordinal)
        {
            ["ext"] = "urn:example:extension"
        };
        return new FilingProfile
        {
            ProfileId = "indexed-taxonomy",
            Digest = digest,
            TaxonomyVersion = version,
            Concepts = concepts,
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD" },
            DimensionDomains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["ext:RegionAxis"] = new[] { "ext:Domestic", "ext:International" }
            },
            NamespaceUris = namespaceUris,
            TaxonomyUrls = new[] { "extension.xsd" },
            PackageHashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["extension.xsd"] = "sha256:fixture"
            },
            RequiredChecks = new HashSet<string>(StringComparer.Ordinal) { "concept-exists" }
        };
    }
}
