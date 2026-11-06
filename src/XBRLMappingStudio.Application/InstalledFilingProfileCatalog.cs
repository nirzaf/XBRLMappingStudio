using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Versioned, offline profile metadata installed beside its hash-pinned taxonomy files.
/// The profile digest covers the metadata, package names, offline URI map and file hashes.
/// </summary>
public sealed record FilingProfileManifest
{
    public const int CurrentSchemaVersion = 3;

    public required int SchemaVersion { get; init; }
    public required string ProfileId { get; init; }
    public required string Version { get; init; }
    public required string EntryPoint { get; init; }
    public required string ProfileDigest { get; init; }
    public string OutputFormat { get; init; } = "xbrl-json";
    public required List<ConceptMetadata> Concepts { get; init; }
    public required List<string> SupportedUnits { get; init; }
    public Dictionary<string, string> NamespaceUris { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Dimension QName to allowed member QNames for the exact taxonomy version.</summary>
    public Dictionary<string, List<string>> DimensionDomains { get; init; } = new(StringComparer.Ordinal);
    public required List<string> PackageNames { get; init; }
    /// <summary>Relative package path to sha256 digest. Every file is verified before install.</summary>
    public required Dictionary<string, string> PackageFiles { get; init; }
    /// <summary>External taxonomy URI to its verified local package file. No URL is fetched.</summary>
    public Dictionary<string, string> OfflineReferences { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Required Arelle plugin IDs passed as separate worker arguments.</summary>
    public List<string> RequiredPlugins { get; init; } = new();
    public List<string> RequiredChecks { get; init; } = new();

    public string ComputeDigest() => FilingProfileManifestDigest.Compute(this);

    public string ToJson() => JsonSerializer.Serialize(this, FilingProfileManifestSerializer.Options);

    internal FilingProfile ToProfile() => new()
    {
        ProfileId = ProfileId,
        Digest = ProfileDigest,
        Concepts = Concepts.ToArray(),
        SupportedUnits = new HashSet<string>(SupportedUnits, StringComparer.Ordinal),
        TaxonomyVersion = Version,
        DimensionDomains = DimensionDomains.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal),
        OutputFormat = OutputFormat,
        NamespaceUris = new Dictionary<string, string>(NamespaceUris, StringComparer.Ordinal),
        TaxonomyUrls = OfflineReferences.Keys.Order(StringComparer.Ordinal).ToArray(),
        PackageHashes = new Dictionary<string, string>(PackageFiles, StringComparer.Ordinal),
        RequiredChecks = new HashSet<string>(RequiredChecks, StringComparer.Ordinal)
    };
}

public sealed record InstalledFilingProfile(
    FilingProfile Profile,
    string Version,
    int SchemaVersion,
    string InstallationDirectory,
    string EntryPointPath,
    IReadOnlyList<string> PackageNames,
    IReadOnlyDictionary<string, string> OfflineReferences,
    IReadOnlyList<string> RequiredPlugins,
    string ManifestFileDigest,
    bool IsEnabled = true);

public sealed record FilingProfileCatalogIssue(string? ProfileId, string? Version, string Message);

public sealed record FilingProfileCatalogLoadResult(
    IReadOnlyList<InstalledFilingProfile> Profiles,
    IReadOnlyList<FilingProfileCatalogIssue> Issues);

/// <summary>
/// Reads the user-installed profile catalog without network access. A profile is usable
/// only when every declared package file matches its pinned hash and its manifest digest
/// matches the exact profile identity recorded in a project.
/// </summary>
public static class InstalledFilingProfileCatalog
{
    public const string CatalogEnvironmentVariable = "XMS_PROFILE_CATALOG";
    public const string ManifestFileName = "profile.json";
    /// <summary>Presence of this regular file disables one installed profile version without changing its pinned taxonomy digest.</summary>
    public const string DisabledMarkerFileName = ".disabled";
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private const int MaxConcepts = 250_000;
    private const int MaxPackageFiles = 16_384;
    private const int MaxPackageNames = 512;
    private const long MaxPackageFileBytes = 512L * 1024 * 1024;
    private const long MaxProfileBytes = 2L * 1024 * 1024 * 1024;
    private const string SyntheticProfileId = "synthetic-oim-v1";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(CatalogEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XBRLMappingStudio", "profiles");
        }
    }

    public static FilingProfileCatalogLoadResult LoadDefault()
        => Load(DefaultDirectory);

    public static FilingProfileCatalogLoadResult Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var profiles = new List<InstalledFilingProfile>();
        var issues = new List<FilingProfileCatalogIssue>();
        string root;
        try
        {
            root = Path.GetFullPath(directory);
            if (!Directory.Exists(root)) return new FilingProfileCatalogLoadResult(profiles, issues);
            EnsureNotReparsePoint(root, "Profile catalog root");
        }
        catch (Exception ex) when (IsCatalogError(ex))
        {
            issues.Add(new FilingProfileCatalogIssue(null, null,
                $"The profile catalog could not be inspected: {ex.Message}"));
            return new FilingProfileCatalogLoadResult(profiles, issues);
        }

        try
        {
            foreach (var profileDirectory in Directory.EnumerateDirectories(root)
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var profileId = Path.GetFileName(profileDirectory);
                try
                {
                    EnsureSafeIdentifier(profileId, "profile directory name");
                    EnsureNotReparsePoint(profileDirectory, "Profile directory");
                    foreach (var versionDirectory in Directory.EnumerateDirectories(profileDirectory)
                                 .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    {
                        var version = Path.GetFileName(versionDirectory);
                        try
                        {
                            profiles.Add(LoadVersion(root, versionDirectory, profileId, version));
                        }
                        catch (Exception ex) when (IsCatalogError(ex))
                        {
                            issues.Add(new FilingProfileCatalogIssue(profileId, version,
                                $"Profile '{profileId}' version '{version}' was not installed: {ex.Message}"));
                        }
                    }
                }
                catch (Exception ex) when (IsCatalogError(ex))
                {
                    issues.Add(new FilingProfileCatalogIssue(profileId, null,
                        $"Profile directory '{profileId}' was not installed: {ex.Message}"));
                }
            }
        }
        catch (Exception ex) when (IsCatalogError(ex))
        {
            issues.Add(new FilingProfileCatalogIssue(null, null,
                $"The profile catalog could not be enumerated: {ex.Message}"));
        }

        return new FilingProfileCatalogLoadResult(profiles, issues);
    }

    private static InstalledFilingProfile LoadVersion(
        string root, string versionDirectory, string directoryProfileId, string directoryVersion)
    {
        EnsureSafeIdentifier(directoryVersion, "profile version directory name");
        EnsureNotReparsePoint(versionDirectory, "Profile version directory");
        EnsureWithin(root, versionDirectory, "Profile version directory");

        var manifestPath = Path.Combine(versionDirectory, ManifestFileName);
        EnsureNotReparsePoint(manifestPath, "Profile manifest");
        byte[] bytes;
        using (var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                   64 * 1024, FileOptions.SequentialScan))
        {
            if (manifestStream.Length is <= 0 or > MaxManifestBytes)
                throw new InvalidDataException($"Profile manifest size must be between 1 byte and {MaxManifestBytes} bytes.");
            bytes = new byte[checked((int)manifestStream.Length)];
            manifestStream.ReadExactly(bytes);
        }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        EnsureUniqueJsonProperties(document.RootElement, "$manifest");
        var manifest = JsonSerializer.Deserialize<FilingProfileManifest>(bytes, SerializerOptions)
            ?? throw new InvalidDataException("Profile manifest is empty.");

        ValidateManifestShape(manifest, directoryProfileId, directoryVersion);
        var computedDigest = manifest.ComputeDigest();
        if (!StringComparer.Ordinal.Equals(manifest.ProfileDigest, computedDigest))
            throw new InvalidDataException("Profile digest does not match the versioned manifest contents.");

        long totalBytes = 0;
        foreach (var (relativePath, expectedDigest) in manifest.PackageFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var fullPath = ResolvePackageFile(versionDirectory, relativePath);
            EnsureNotReparsePoint(fullPath, "Taxonomy package file");
            var info = new FileInfo(fullPath);
            if (!info.Exists) throw new InvalidDataException($"Required package file '{relativePath}' is missing.");
            if (info.Length > MaxPackageFileBytes)
                throw new InvalidDataException($"Package file '{relativePath}' exceeds the {MaxPackageFileBytes}-byte limit.");
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaxProfileBytes)
                throw new InvalidDataException($"Profile package files exceed the {MaxProfileBytes}-byte total limit.");
            var actualDigest = ComputeFileDigest(fullPath);
            if (!StringComparer.Ordinal.Equals(actualDigest, expectedDigest))
                throw new InvalidDataException($"Package hash mismatch for '{relativePath}'.");
        }

        var entryPointPath = ResolvePackageFile(versionDirectory, manifest.EntryPoint);
        var profile = manifest.ToProfile();
        var isEnabled = !HasDisabledMarker(versionDirectory);
        return new InstalledFilingProfile(
            profile,
            manifest.Version,
            manifest.SchemaVersion,
            Path.GetFullPath(versionDirectory),
            entryPointPath,
            manifest.PackageNames.ToArray(),
            new Dictionary<string, string>(manifest.OfflineReferences, StringComparer.Ordinal),
            manifest.RequiredPlugins.ToArray(),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            isEnabled);
    }

    private static bool HasDisabledMarker(string versionDirectory)
    {
        var markerPath = Path.Combine(versionDirectory, DisabledMarkerFileName);
        try
        {
            var attributes = File.GetAttributes(markerPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("A disabled-profile marker cannot be a symbolic link or reparse point.");
            if (attributes.HasFlag(FileAttributes.Directory))
                throw new InvalidDataException("A disabled-profile marker must be a regular file.");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void ValidateManifestShape(FilingProfileManifest manifest,
        string directoryProfileId, string directoryVersion)
    {
        if (manifest.SchemaVersion is < 2 or > FilingProfileManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported profile manifest schema version '{manifest.SchemaVersion}'.");
        EnsureSafeIdentifier(manifest.ProfileId, "profile ID");
        EnsureSafeIdentifier(manifest.Version, "profile version");
        if (!StringComparer.Ordinal.Equals(directoryProfileId, manifest.ProfileId)
            || !StringComparer.Ordinal.Equals(directoryVersion, manifest.Version))
            throw new InvalidDataException("Profile ID/version must match their installation directory names.");
        if (StringComparer.Ordinal.Equals(manifest.ProfileId, SyntheticProfileId))
            throw new InvalidDataException("The built-in synthetic profile ID is reserved and cannot be installed.");
        if (string.IsNullOrWhiteSpace(manifest.OutputFormat))
            throw new InvalidDataException("Profile output format is required.");
        if (manifest.Concepts is null || manifest.Concepts.Count > MaxConcepts)
            throw new InvalidDataException($"Concept count exceeds the {MaxConcepts}-concept limit or is missing.");
        if (manifest.SupportedUnits is null || manifest.PackageNames is null
            || manifest.PackageFiles is null || manifest.NamespaceUris is null
            || manifest.DimensionDomains is null || manifest.OfflineReferences is null || manifest.RequiredPlugins is null
            || manifest.RequiredChecks is null)
            throw new InvalidDataException("Profile collections cannot be null.");
        if (manifest.SchemaVersion < 3 && manifest.DimensionDomains.Count > 0)
            throw new InvalidDataException("Dimension domains require profile manifest schema version 3.");
        if (manifest.PackageNames.Count is 0 or > MaxPackageNames)
            throw new InvalidDataException($"A profile must declare between 1 and {MaxPackageNames} taxonomy package names.");
        if (manifest.PackageFiles.Count is 0 or > MaxPackageFiles)
            throw new InvalidDataException($"A profile must declare between 1 and {MaxPackageFiles} package files.");
        if (manifest.RequiredPlugins.Count > 128)
            throw new InvalidDataException("A profile can declare at most 128 required Arelle plugins.");
        EnsureUnique(manifest.PackageNames, "package name");
        EnsureUnique(manifest.SupportedUnits, "supported unit");
        EnsureUnique(manifest.RequiredPlugins, "required plugin");
        EnsureUnique(manifest.RequiredChecks, "required check");
        foreach (var plugin in manifest.RequiredPlugins)
            if (plugin.Length > 256 || plugin.StartsWith("/", StringComparison.Ordinal)
                || plugin.Contains('\\') || plugin.Contains(',') || plugin.Any(char.IsWhiteSpace)
                || plugin.Split('/').Any(segment => segment is "" or "." or "..")
                || plugin.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '+' or '.' or '/')))
                throw new InvalidDataException($"Required plugin ID '{plugin}' contains unsupported characters.");

        foreach (var (prefix, namespaceUri) in manifest.NamespaceUris)
        {
            EnsureSafePrefix(prefix);
            if (!Uri.TryCreate(namespaceUri, UriKind.Absolute, out _))
                throw new InvalidDataException($"Namespace prefix '{prefix}' must map to an absolute URI.");
        }

        var concepts = new HashSet<(string Namespace, string LocalName)>();
        foreach (var concept in manifest.Concepts)
        {
            if (concept is null || string.IsNullOrWhiteSpace(concept.QualifiedName)
                || string.IsNullOrWhiteSpace(concept.Namespace) || string.IsNullOrWhiteSpace(concept.LocalName)
                || string.IsNullOrWhiteSpace(concept.Label) || string.IsNullOrWhiteSpace(concept.DataType)
                || string.IsNullOrWhiteSpace(concept.PeriodType) || concept.SupportedLanguages is null
                || concept.LabelsByLanguage is null || !concepts.Add((concept.Namespace, concept.LocalName)))
                throw new InvalidDataException("Profile concepts must have unique expanded names and complete metadata.");
            var colon = concept.QualifiedName.IndexOf(':');
            if (colon <= 0 || colon != concept.QualifiedName.LastIndexOf(':')
                || !StringComparer.Ordinal.Equals(concept.QualifiedName[(colon + 1)..], concept.LocalName)
                || !manifest.NamespaceUris.TryGetValue(concept.QualifiedName[..colon], out var namespaceUri)
                || !StringComparer.Ordinal.Equals(namespaceUri, concept.Namespace))
                throw new InvalidDataException($"Concept '{concept.QualifiedName}' must match its namespace prefix and local name.");
            EnsureUnique(concept.SupportedLanguages, $"language for concept '{concept.QualifiedName}'");
            if (concept.LabelsByLanguage.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)
                || !concept.SupportedLanguages.Contains(pair.Key, StringComparer.Ordinal)))
                throw new InvalidDataException($"Localized labels for concept '{concept.QualifiedName}' must use a supported language and non-empty text.");
        }
        foreach (var (dimensionQName, memberQNames) in manifest.DimensionDomains)
        {
            if (!manifest.Concepts.Any(concept => StringComparer.Ordinal.Equals(concept.QualifiedName, dimensionQName))
                || memberQNames is null || memberQNames.Count == 0)
                throw new InvalidDataException($"Dimension domain '{dimensionQName}' must name an indexed taxonomy concept and at least one member.");
            EnsureUnique(memberQNames, $"dimension member for '{dimensionQName}'");
            foreach (var memberQName in memberQNames)
                if (!manifest.Concepts.Any(concept => StringComparer.Ordinal.Equals(concept.QualifiedName, memberQName)))
                    throw new InvalidDataException($"Dimension domain '{dimensionQName}' refers to unindexed member '{memberQName}'.");
        }

        EnsureSafeRelativePath(manifest.EntryPoint, "taxonomy entry point");
        if (!manifest.PackageFiles.ContainsKey(manifest.EntryPoint))
            throw new InvalidDataException("The taxonomy entry point must be one of the hash-pinned package files.");
        foreach (var (relativePath, digest) in manifest.PackageFiles)
        {
            EnsureSafeRelativePath(relativePath, "package file path");
            if (!IsSha256Digest(digest))
                throw new InvalidDataException($"Package file '{relativePath}' must have a lowercase sha256 digest.");
        }
        EnsureUnique(manifest.PackageFiles.Keys, "package file path", PathComparer);

        foreach (var (uriText, localPath) in manifest.OfflineReferences)
        {
            if (uriText.Length > 2048 || !Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)
                || uri.UserInfo.Length > 0)
                throw new InvalidDataException($"Offline taxonomy reference '{uriText}' must be an HTTP(S) URI without credentials.");
            EnsureSafeRelativePath(localPath, "offline reference target");
            if (!manifest.PackageFiles.ContainsKey(localPath))
                throw new InvalidDataException($"Offline reference '{uriText}' targets unpinned file '{localPath}'.");
        }
    }

    private static string ResolvePackageFile(string versionDirectory, string relativePath)
    {
        EnsureSafeRelativePath(relativePath, "package file path");
        var path = Path.GetFullPath(Path.Combine(versionDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(versionDirectory, path, "Package file");
        var current = Path.GetFullPath(versionDirectory);
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new InvalidDataException($"Required package path '{relativePath}' is missing.");
            EnsureNotReparsePoint(current, "Package path");
        }
        return path;
    }

    private static void EnsureSafeRelativePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\')
            || path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':')
            || path.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*', '\0' }) >= 0
            || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"{description} must be a safe relative path using forward slashes.");
    }

    private static void EnsureSafeIdentifier(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '+')))
            throw new InvalidDataException($"The {description} must contain only letters, digits, '.', '_', '+' or '-'.");
    }

    private static void EnsureSafePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 128
            || !(char.IsAsciiLetter(prefix[0]) || prefix[0] == '_')
            || prefix.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')))
            throw new InvalidDataException($"Namespace prefix '{prefix}' is not a valid XML name prefix.");
    }

    private static void EnsureWithin(string root, string path, string description)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison())
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison()))
            throw new InvalidDataException($"{description} escapes its installed profile directory.");
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"{description} cannot be a symbolic link or reparse point.");
    }

    private static bool IsSha256Digest(string? value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)) return false;
        foreach (var ch in value.AsSpan(7))
            if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static string ComputeFileDigest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureUniqueJsonProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate JSON property '{property.Name}' at {path}.");
                EnsureUniqueJsonProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                EnsureUniqueJsonProperties(item, $"{path}[{index++}]");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string description,
        StringComparer? comparer = null)
    {
        var seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        foreach (var value in values)
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                throw new InvalidDataException($"Profile {description}s must be non-empty and unique.");
    }

    private static bool IsCatalogError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or NotSupportedException or OverflowException;

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static JsonSerializerOptions SerializerOptionsForManifest => SerializerOptions;
}

internal static class FilingProfileManifestSerializer
{
    public static JsonSerializerOptions Options => new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

internal static class FilingProfileManifestDigest
{
    private sealed record DigestPayload(
        int SchemaVersion,
        string ProfileId,
        string Version,
        string EntryPoint,
        string OutputFormat,
        IReadOnlyList<CanonicalConcept> Concepts,
        IReadOnlyList<string> SupportedUnits,
        IReadOnlyDictionary<string, string> NamespaceUris,
        IReadOnlyList<string> PackageNames,
        IReadOnlyDictionary<string, string> PackageFiles,
        IReadOnlyDictionary<string, string> OfflineReferences,
        IReadOnlyList<string> RequiredPlugins,
        IReadOnlyList<string> RequiredChecks);

    private sealed record DigestPayloadV3(
        int SchemaVersion,
        string ProfileId,
        string Version,
        string EntryPoint,
        string OutputFormat,
        IReadOnlyList<CanonicalConcept> Concepts,
        IReadOnlyList<string> SupportedUnits,
        IReadOnlyDictionary<string, string> NamespaceUris,
        IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionDomains,
        IReadOnlyList<string> PackageNames,
        IReadOnlyDictionary<string, string> PackageFiles,
        IReadOnlyDictionary<string, string> OfflineReferences,
        IReadOnlyList<string> RequiredPlugins,
        IReadOnlyList<string> RequiredChecks);

    private sealed record CanonicalConcept(
        string QualifiedName,
        string Namespace,
        string LocalName,
        string Label,
        string DataType,
        string PeriodType,
        bool IsAbstract,
        IReadOnlyList<string> SupportedLanguages,
        IReadOnlyDictionary<string, string> LabelsByLanguage);

    public static string Compute(FilingProfileManifest manifest)
    {
        var concepts = manifest.Concepts.OrderBy(concept => concept.QualifiedName, StringComparer.Ordinal)
            .Select(concept => new CanonicalConcept(
                concept.QualifiedName,
                concept.Namespace,
                concept.LocalName,
                concept.Label,
                concept.DataType,
                concept.PeriodType,
                concept.IsAbstract,
                concept.SupportedLanguages.Order(StringComparer.Ordinal).ToArray(),
                Sorted(concept.LabelsByLanguage)))
            .ToArray();
        object canonical = manifest.SchemaVersion < 3
            ? new DigestPayload(
                manifest.SchemaVersion,
                manifest.ProfileId,
                manifest.Version,
                manifest.EntryPoint,
                manifest.OutputFormat,
                concepts,
                manifest.SupportedUnits.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.NamespaceUris),
                manifest.PackageNames.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.PackageFiles),
                Sorted(manifest.OfflineReferences),
                manifest.RequiredPlugins.Order(StringComparer.Ordinal).ToArray(),
                manifest.RequiredChecks.Order(StringComparer.Ordinal).ToArray())
            : new DigestPayloadV3(
                manifest.SchemaVersion,
                manifest.ProfileId,
                manifest.Version,
                manifest.EntryPoint,
                manifest.OutputFormat,
                concepts,
                manifest.SupportedUnits.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.NamespaceUris),
                SortedDomains(manifest.DimensionDomains),
                manifest.PackageNames.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.PackageFiles),
                Sorted(manifest.OfflineReferences),
                manifest.RequiredPlugins.Order(StringComparer.Ordinal).ToArray(),
                manifest.RequiredChecks.Order(StringComparer.Ordinal).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical,
            InstalledFilingProfileCatalog.SerializerOptionsForManifest);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> values)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            result.Add(key, value);
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> SortedDomains(
        IReadOnlyDictionary<string, List<string>> values)
    {
        var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, members) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            result.Add(key, members.Order(StringComparer.Ordinal).ToArray());
        return result;
    }
}
