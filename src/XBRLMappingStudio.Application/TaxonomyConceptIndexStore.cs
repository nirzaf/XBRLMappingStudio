using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

public sealed record TaxonomyIndexLoadResult(TaxonomyIndex Index, bool ReusedCachedMetadata);

/// <summary>
/// Persists the verified offline concept metadata needed by the taxonomy explorer.
/// Cache identity includes the exact profile digest; a changed package can never reuse
/// rows from an older taxonomy version. SQLite FTS is intentionally not used until
/// measurements show that the bounded in-memory search needs it.
/// </summary>
public sealed class TaxonomyConceptIndexStore
{
    private const int CacheSchema = 1;
    private const int MaximumConceptCount = 250_000;
    private const int MaximumSnapshotBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64
    };

    private readonly LocalApplicationDatabase _database;

    private sealed record ConceptSnapshot(
        string QualifiedName,
        string Namespace,
        string LocalName,
        string Label,
        string DataType,
        string PeriodType,
        bool IsAbstract,
        string[] SupportedLanguages,
        SortedDictionary<string, string> LabelsByLanguage);

    private sealed record DimensionSnapshot(string DimensionQName, string[] MemberQNames);

    private sealed record ProfileSnapshot(
        int CacheSchema,
        string ProfileId,
        string ProfileDigest,
        string TaxonomyVersion,
        string OutputFormat,
        string[] SupportedUnits,
        SortedDictionary<string, string> NamespaceUris,
        string[] TaxonomyUrls,
        SortedDictionary<string, string> PackageHashes,
        string[] RequiredChecks,
        DimensionSnapshot[] DimensionDomains,
        ConceptSnapshot[] Concepts);

    public TaxonomyConceptIndexStore(LocalApplicationDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public TaxonomyIndexLoadResult LoadOrBuild(FilingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.Digest))
            throw new ArgumentException("A taxonomy metadata index requires an exact profile identity.", nameof(profile));
        if (profile.Concepts.Count > MaximumConceptCount)
            throw new InvalidDataException($"Taxonomy concept count exceeds the {MaximumConceptCount}-concept index limit.");

        // Constructing this validates that the incoming profile has unique stable identities.
        var incomingIndex = new TaxonomyIndex(profile);
        var snapshot = CreateSnapshot(profile);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumSnapshotBytes)
            throw new InvalidDataException($"Taxonomy metadata snapshot exceeds the {MaximumSnapshotBytes}-byte cache limit.");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var connection = _database.OpenConnection();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT cache_schema, taxonomy_version, concept_count, snapshot_json, snapshot_sha256
                FROM taxonomy_profile_index
                WHERE profile_id=$profile_id AND profile_digest=$profile_digest
                  AND length(CAST(snapshot_json AS BLOB)) <= $max_snapshot_bytes;
                """;
            read.Parameters.AddWithValue("$profile_id", profile.ProfileId);
            read.Parameters.AddWithValue("$profile_digest", profile.Digest);
            read.Parameters.AddWithValue("$max_snapshot_bytes", MaximumSnapshotBytes);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                var cacheSchema = reader.GetInt32(0);
                var taxonomyVersion = reader.GetString(1);
                var conceptCount = reader.GetInt32(2);
                var cachedJson = reader.GetString(3);
                var cachedDigest = reader.GetString(4);
                var cachedBytes = Encoding.UTF8.GetBytes(cachedJson);
                var actualCachedDigest = Convert.ToHexString(SHA256.HashData(cachedBytes)).ToLowerInvariant();
                if (cacheSchema == CacheSchema
                    && StringComparer.Ordinal.Equals(taxonomyVersion, snapshot.TaxonomyVersion)
                    && conceptCount == snapshot.Concepts.Length
                    && StringComparer.Ordinal.Equals(cachedDigest, actualCachedDigest)
                    && StringComparer.Ordinal.Equals(cachedDigest, digest)
                    && StringComparer.Ordinal.Equals(cachedJson, json))
                {
                    var cachedSnapshot = JsonSerializer.Deserialize<ProfileSnapshot>(cachedJson, JsonOptions)
                        ?? throw new InvalidDataException("Cached taxonomy metadata is empty.");
                    return new TaxonomyIndexLoadResult(new TaxonomyIndex(ToProfile(cachedSnapshot)), true);
                }
            }
        }

        using (var transaction = connection.BeginTransaction(deferred: false))
        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO taxonomy_profile_index (
                    profile_id, profile_digest, cache_schema, taxonomy_version,
                    concept_count, snapshot_json, snapshot_sha256, indexed_utc)
                VALUES (
                    $profile_id, $profile_digest, $cache_schema, $taxonomy_version,
                    $concept_count, $snapshot_json, $snapshot_sha256, $indexed_utc)
                ON CONFLICT(profile_id, profile_digest) DO UPDATE SET
                    cache_schema=excluded.cache_schema,
                    taxonomy_version=excluded.taxonomy_version,
                    concept_count=excluded.concept_count,
                    snapshot_json=excluded.snapshot_json,
                    snapshot_sha256=excluded.snapshot_sha256,
                    indexed_utc=excluded.indexed_utc;
                """;
            write.Parameters.AddWithValue("$profile_id", profile.ProfileId);
            write.Parameters.AddWithValue("$profile_digest", profile.Digest);
            write.Parameters.AddWithValue("$cache_schema", CacheSchema);
            write.Parameters.AddWithValue("$taxonomy_version", snapshot.TaxonomyVersion);
            write.Parameters.AddWithValue("$concept_count", snapshot.Concepts.Length);
            write.Parameters.AddWithValue("$snapshot_json", json);
            write.Parameters.AddWithValue("$snapshot_sha256", digest);
            write.Parameters.AddWithValue("$indexed_utc", DateTimeOffset.UtcNow.ToString("O"));
            write.ExecuteNonQuery();
            transaction.Commit();
        }

        return new TaxonomyIndexLoadResult(incomingIndex, false);
    }

    private static ProfileSnapshot CreateSnapshot(FilingProfile profile) => new(
        CacheSchema,
        profile.ProfileId,
        profile.Digest,
        string.IsNullOrWhiteSpace(profile.TaxonomyVersion) ? profile.Digest : profile.TaxonomyVersion,
        profile.OutputFormat,
        profile.SupportedUnits.Order(StringComparer.Ordinal).ToArray(),
        Sorted(profile.NamespaceUris),
        profile.TaxonomyUrls.Order(StringComparer.Ordinal).ToArray(),
        Sorted(profile.PackageHashes),
        profile.RequiredChecks.Order(StringComparer.Ordinal).ToArray(),
        profile.DimensionDomains.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DimensionSnapshot(
                pair.Key,
                pair.Value.Order(StringComparer.Ordinal).ToArray()))
            .ToArray(),
        profile.Concepts.OrderBy(concept => concept.Namespace, StringComparer.Ordinal)
            .ThenBy(concept => concept.LocalName, StringComparer.Ordinal)
            .Select(concept => new ConceptSnapshot(
                concept.QualifiedName,
                concept.Namespace,
                concept.LocalName,
                concept.Label,
                concept.DataType,
                concept.PeriodType,
                concept.IsAbstract,
                concept.SupportedLanguages.Order(StringComparer.Ordinal).ToArray(),
                Sorted(concept.LabelsByLanguage)))
            .ToArray());

    private static FilingProfile ToProfile(ProfileSnapshot snapshot) => new()
    {
        ProfileId = snapshot.ProfileId,
        Digest = snapshot.ProfileDigest,
        TaxonomyVersion = snapshot.TaxonomyVersion,
        Concepts = snapshot.Concepts.Select(concept => new ConceptMetadata(
                concept.QualifiedName,
                concept.Namespace,
                concept.LocalName,
                concept.Label,
                concept.DataType,
                concept.PeriodType,
                concept.IsAbstract)
            {
                SupportedLanguages = concept.SupportedLanguages,
                LabelsByLanguage = concept.LabelsByLanguage
            })
            .ToArray(),
        SupportedUnits = new HashSet<string>(snapshot.SupportedUnits, StringComparer.Ordinal),
        DimensionDomains = snapshot.DimensionDomains.ToDictionary(
            domain => domain.DimensionQName,
            domain => (IReadOnlyList<string>)domain.MemberQNames,
            StringComparer.Ordinal),
        OutputFormat = snapshot.OutputFormat,
        NamespaceUris = snapshot.NamespaceUris,
        TaxonomyUrls = snapshot.TaxonomyUrls,
        PackageHashes = snapshot.PackageHashes,
        RequiredChecks = new HashSet<string>(snapshot.RequiredChecks, StringComparer.Ordinal)
    };

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> values)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            result.Add(key, value);
        return result;
    }
}
