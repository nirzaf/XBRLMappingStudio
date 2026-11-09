using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Versioned, offline profile metadata installed beside its hash-pinned taxonomy files.
/// The profile digest covers the metadata, package names, offline URI map and file hashes.
/// </summary>
public sealed record FilingProfileManifest
{
    public const int CurrentSchemaVersion = 7;
    public const int MaximumRelationships = TaxonomyIndex.MaximumRelationships;
    public const int MaximumPresentationRelationships = MaximumRelationships;

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
    /// <summary>Typed dimension QName to global typed-domain element and supported simple type.</summary>
    public Dictionary<string, TypedDimensionDomainMetadata> TypedDimensionDomains { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Ordered presentation arcs; concept identity remains separate from its role-specific occurrences.</summary>
    public List<PresentationLink> PresentationLinks { get; init; } = new();
    /// <summary>Ordered summation-item arcs in the calculation linkbase.</summary>
    public List<CalculationLink> CalculationLinks { get; init; } = new();
    /// <summary>Ordered, arcrole-specific relationships in the definition linkbase.</summary>
    public List<DefinitionLink> DefinitionLinks { get; init; } = new();
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
        TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(TypedDimensionDomains, StringComparer.Ordinal),
        PresentationLinks = PresentationLinks.ToArray(),
        CalculationLinks = CalculationLinks.ToArray(),
        DefinitionLinks = DefinitionLinks.ToArray(),
        OutputFormat = OutputFormat,
        NamespaceUris = new Dictionary<string, string>(NamespaceUris, StringComparer.Ordinal),
        TaxonomyUrls = OfflineReferences.Keys.Order(StringComparer.Ordinal).ToArray(),
        PackageHashes = new Dictionary<string, string>(PackageFiles, StringComparer.Ordinal),
        RequiredPlugins = new HashSet<string>(RequiredPlugins, StringComparer.Ordinal),
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
    private const string StagingDirectoryPrefix = ".installing-";
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

    /// <summary>
    /// Installs one already-downloaded, offline profile package from a local version
    /// directory. The package is validated before and after copying, committed with an
    /// atomic same-volume directory move, and always left disabled pending authorization.
    /// An existing profile version is never replaced.
    /// </summary>
    public static async Task<InstalledFilingProfile> InstallAsync(
        string sourceVersionDirectory,
        string catalogDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);

        var sourceVersionPath = Path.GetFullPath(sourceVersionDirectory);
        var sourceVersionInfo = new DirectoryInfo(sourceVersionPath);
        var sourceProfileInfo = sourceVersionInfo.Parent
            ?? throw new InvalidDataException("The profile source must use <profile-id>/<version> directories.");
        var sourceRootInfo = sourceProfileInfo.Parent
            ?? throw new InvalidDataException("The profile source must use <profile-id>/<version> directories.");
        var profileId = sourceProfileInfo.Name;
        var version = sourceVersionInfo.Name;
        var sourceRoot = sourceRootInfo.FullName;

        EnsureSafeIdentifier(profileId, "profile source directory name");
        EnsureSafeIdentifier(version, "profile version directory name");
        if (!Directory.Exists(sourceRoot) || !Directory.Exists(sourceProfileInfo.FullName)
            || !Directory.Exists(sourceVersionPath))
            throw new DirectoryNotFoundException("The profile source must use an existing <profile-id>/<version> directory.");
        EnsureNotReparsePoint(sourceRoot, "Profile source root");
        EnsureNotReparsePoint(sourceProfileInfo.FullName, "Profile source directory");
        EnsureNotReparsePoint(sourceVersionPath, "Profile source version directory");

        // Refuse malformed or modified sources before making any catalog changes.
        _ = LoadVersion(sourceRoot, sourceVersionPath, profileId, version);

        var manifestPath = Path.Combine(sourceVersionPath, ManifestFileName);
        EnsureNotReparsePoint(manifestPath, "Profile manifest");
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException($"Profile manifest size must be between 1 byte and {MaxManifestBytes} bytes.");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (manifestBytes.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException($"Profile manifest size must be between 1 byte and {MaxManifestBytes} bytes.");
        using var manifestDocument = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 64 });
        EnsureUniqueJsonProperties(manifestDocument.RootElement, "$manifest");
        var manifest = JsonSerializer.Deserialize<FilingProfileManifest>(manifestBytes, SerializerOptions)
            ?? throw new InvalidDataException("Profile manifest is empty.");
        ValidateManifestShape(manifest, profileId, version);
        if (!StringComparer.Ordinal.Equals(manifest.ProfileDigest, manifest.ComputeDigest()))
            throw new InvalidDataException("Profile digest does not match the versioned manifest contents.");
        foreach (var relativePath in manifest.PackageFiles.Keys)
        {
            if (PathComparer.Equals(relativePath, ManifestFileName)
                || PathComparer.Equals(relativePath, DisabledMarkerFileName))
                throw new InvalidDataException($"Package file '{relativePath}' conflicts with catalog metadata.");
        }

        var catalogRoot = Path.GetFullPath(catalogDirectory);
        Directory.CreateDirectory(catalogRoot);
        EnsureNotReparsePoint(catalogRoot, "Profile catalog root");
        var profileDirectory = Path.Combine(catalogRoot, profileId);
        if (Directory.Exists(profileDirectory))
            EnsureNotReparsePoint(profileDirectory, "Profile catalog directory");
        else
            Directory.CreateDirectory(profileDirectory);
        EnsureNotReparsePoint(profileDirectory, "Profile catalog directory");
        EnsureWithin(catalogRoot, profileDirectory, "Profile catalog directory");

        var destinationVersionPath = Path.Combine(profileDirectory, version);
        if (Directory.Exists(destinationVersionPath) || File.Exists(destinationVersionPath))
            throw new InvalidOperationException($"Profile '{profileId}' version '{version}' is already present and will not be overwritten.");

        var stagingDirectory = Path.Combine(profileDirectory, StagingDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        EnsureNotReparsePoint(stagingDirectory, "Profile staging directory");
        EnsureWithin(catalogRoot, stagingDirectory, "Profile staging directory");

        try
        {
            var stagedManifestPath = Path.Combine(stagingDirectory, ManifestFileName);
            await using (var output = new FileStream(stagedManifestPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            }

            long totalBytes = 0;
            foreach (var relativePath in manifest.PackageFiles.Keys.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolvePackageFile(sourceVersionPath, relativePath);
                var sourceInfo = new FileInfo(sourcePath);
                if (!sourceInfo.Exists)
                    throw new InvalidDataException($"Required package file '{relativePath}' is missing.");
                if (sourceInfo.Length > MaxPackageFileBytes)
                    throw new InvalidDataException($"Package file '{relativePath}' exceeds the {MaxPackageFileBytes}-byte limit.");
                totalBytes = checked(totalBytes + sourceInfo.Length);
                if (totalBytes > MaxProfileBytes)
                    throw new InvalidDataException($"Profile package files exceed the {MaxProfileBytes}-byte total limit.");

                var stagedPath = Path.GetFullPath(Path.Combine(stagingDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                EnsureWithin(stagingDirectory, stagedPath, "Staged package file");
                var stagedParent = Path.GetDirectoryName(stagedPath)
                    ?? throw new InvalidDataException($"Package file '{relativePath}' has no parent directory.");
                Directory.CreateDirectory(stagedParent);
                EnsureNotReparsePoint(stagedParent, "Staged package directory");

                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var disabledMarkerPath = Path.Combine(stagingDirectory, DisabledMarkerFileName);
            await using (var marker = new FileStream(disabledMarkerPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            {
                var markerBytes = Encoding.UTF8.GetBytes("Installed disabled pending owner authorization.\n");
                await marker.WriteAsync(markerBytes, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = LoadVersion(catalogRoot, stagingDirectory, profileId, version);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, destinationVersionPath);
            stagingDirectory = destinationVersionPath;
            return LoadVersion(catalogRoot, destinationVersionPath, profileId, version);
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
                // Preserve the installation error; a later catalog scan ignores abandoned staging folders.
            }
            throw;
        }
    }

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
                                 .Where(path => !Path.GetFileName(path).StartsWith(StagingDirectoryPrefix, StringComparison.Ordinal))
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
        ValidateTypedDimensionDomainDeclarations(manifest, versionDirectory);

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
            || manifest.DimensionDomains is null || manifest.TypedDimensionDomains is null || manifest.PresentationLinks is null
            || manifest.CalculationLinks is null || manifest.DefinitionLinks is null
            || manifest.OfflineReferences is null || manifest.RequiredPlugins is null
            || manifest.RequiredChecks is null)
            throw new InvalidDataException("Profile collections cannot be null.");
        if (manifest.SchemaVersion < 3 && manifest.DimensionDomains.Count > 0)
            throw new InvalidDataException("Dimension domains require profile manifest schema version 3.");
        if (manifest.SchemaVersion < 7 && manifest.TypedDimensionDomains.Count > 0)
            throw new InvalidDataException("Typed dimension domains require profile manifest schema version 7.");
        if (manifest.TypedDimensionDomains.Count > 512)
            throw new InvalidDataException("A profile can declare at most 512 typed dimension domains.");
        if (manifest.SchemaVersion < 4 && manifest.PresentationLinks.Count > 0)
            throw new InvalidDataException("Presentation relationships require profile manifest schema version 4.");
        if (manifest.SchemaVersion < 5 && (manifest.CalculationLinks.Count > 0 || manifest.DefinitionLinks.Count > 0))
            throw new InvalidDataException("Calculation and definition relationships require profile manifest schema version 5.");
        var totalRelationships = (long)manifest.PresentationLinks.Count
            + manifest.CalculationLinks.Count + manifest.DefinitionLinks.Count;
        if (totalRelationships > FilingProfileManifest.MaximumRelationships)
            throw new InvalidDataException($"Taxonomy relationships exceed the {FilingProfileManifest.MaximumRelationships}-relationship limit.");
        if (manifest.PackageNames.Count is 0 or > MaxPackageNames)
            throw new InvalidDataException($"A profile must declare between 1 and {MaxPackageNames} taxonomy package names.");
        if (manifest.PackageFiles.Count is 0 or > MaxPackageFiles)
            throw new InvalidDataException($"A profile must declare between 1 and {MaxPackageFiles} package files.");
        if (manifest.RequiredPlugins.Count > 128)
            throw new InvalidDataException("A profile can declare at most 128 required Arelle plugins.");
        if (manifest.RequiredChecks.Count > 2048)
            throw new InvalidDataException("A profile can declare at most 2048 required validation checks.");
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
        foreach (var check in manifest.RequiredChecks)
            if (check.EnumerateRunes().Count() > 512 || check.Any(char.IsControl))
                throw new InvalidDataException("Required validation check IDs must be at most 512 characters and contain no control characters.");

        foreach (var (prefix, namespaceUri) in manifest.NamespaceUris)
        {
            EnsureSafePrefix(prefix);
            if (!Uri.TryCreate(namespaceUri, UriKind.Absolute, out _))
                throw new InvalidDataException($"Namespace prefix '{prefix}' must map to an absolute URI.");
        }

        var concepts = new HashSet<(string Namespace, string LocalName)>();
        var qualifiedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var concept in manifest.Concepts)
        {
            if (concept is null || string.IsNullOrWhiteSpace(concept.QualifiedName)
                || string.IsNullOrWhiteSpace(concept.Namespace) || string.IsNullOrWhiteSpace(concept.LocalName)
                || string.IsNullOrWhiteSpace(concept.Label) || string.IsNullOrWhiteSpace(concept.DataType)
                || string.IsNullOrWhiteSpace(concept.PeriodType) || concept.SupportedLanguages is null
                || concept.LabelsByLanguage is null || !concepts.Add((concept.Namespace, concept.LocalName))
                || !qualifiedNames.Add(concept.QualifiedName))
                throw new InvalidDataException("Profile concepts must have unique expanded names and complete metadata.");
            if (concept.NumericPolicy is { } policy
                && (manifest.SchemaVersion < 6 || !concept.IsNumeric || !policy.IsValid(out _)))
                throw new InvalidDataException("Numeric policies require schema version 6, a numeric concept, and supported constraints.");
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
        var relationshipKeys = new HashSet<(string RoleUri, string ParentQName, string ChildQName, decimal Order)>();
        foreach (var relationship in manifest.PresentationLinks)
        {
            if (relationship is null || string.IsNullOrWhiteSpace(relationship.RoleUri)
                || relationship.RoleUri.Length > 2048 || relationship.RoleUri.Any(char.IsWhiteSpace)
                || relationship.RoleUri.Any(char.IsControl)
                || !Uri.TryCreate(relationship.RoleUri, UriKind.Absolute, out var roleUri)
                || (roleUri.Scheme is "http" or "https"
                    && (string.IsNullOrEmpty(roleUri.Host) || roleUri.UserInfo.Length > 0))
                || !qualifiedNames.Contains(relationship.ParentQName)
                || !qualifiedNames.Contains(relationship.ChildQName))
                throw new InvalidDataException("Presentation relationships must have an absolute role URI and reference indexed concepts.");
            if (!relationshipKeys.Add((relationship.RoleUri, relationship.ParentQName,
                    relationship.ChildQName, relationship.Order)))
                throw new InvalidDataException("Profile presentation relationships cannot contain duplicate arcs.");
        }
        var calculationKeys = new HashSet<CalculationLink>();
        foreach (var relationship in manifest.CalculationLinks)
        {
            if (relationship is null || !IsValidRelationshipUri(relationship.RoleUri)
                || (relationship.TargetRoleUri is not null && !IsValidRelationshipUri(relationship.TargetRoleUri))
                || relationship.Weight is not (-1m or 1m)
                || !qualifiedNames.Contains(relationship.ParentQName)
                || !qualifiedNames.Contains(relationship.ChildQName))
                throw new InvalidDataException("Calculation relationships must have valid role URIs and indexed endpoints.");
            if (!calculationKeys.Add(relationship))
                throw new InvalidDataException("Profile calculation relationships cannot contain duplicate arcs.");
        }
        var definitionKeys = new HashSet<DefinitionLink>();
        foreach (var relationship in manifest.DefinitionLinks)
        {
            if (relationship is null || !IsValidRelationshipUri(relationship.RoleUri)
                || !IsValidRelationshipUri(relationship.ArcRoleUri)
                || (relationship.TargetRoleUri is not null && !IsValidRelationshipUri(relationship.TargetRoleUri))
                || !qualifiedNames.Contains(relationship.ParentQName)
                || !qualifiedNames.Contains(relationship.ChildQName)
                || (relationship.ContextElement is not null
                    && relationship.ContextElement is not ("segment" or "scenario")))
                throw new InvalidDataException("Definition relationships must have valid role URIs, supported attributes, and indexed endpoints.");
            if (!definitionKeys.Add(relationship))
                throw new InvalidDataException("Profile definition relationships cannot contain duplicate arcs.");
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
        foreach (var (dimensionQName, typedDomain) in manifest.TypedDimensionDomains)
        {
            var dimension = manifest.Concepts.FirstOrDefault(concept =>
                StringComparer.Ordinal.Equals(concept.QualifiedName, dimensionQName));
            if (dimension is null || !StringComparer.Ordinal.Equals(dimension.DataTypeLocalName, "dimension")
                || typedDomain is null
                || !IsDeclaredQName(typedDomain.DomainElementQName, manifest.NamespaceUris)
                || !IsSupportedTypedDimensionDataType(typedDomain.DataTypeQName, manifest.NamespaceUris))
                throw new InvalidDataException(
                    $"Typed dimension domain '{dimensionQName}' must reference an indexed dimension concept, a declared global domain element, and a supported XSD simple type (currently xs:string).");
            if (manifest.DimensionDomains.ContainsKey(dimensionQName))
                throw new InvalidDataException($"Dimension '{dimensionQName}' cannot declare both explicit members and typed-domain metadata.");
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
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
                || uri.AbsolutePath == "/" || uriText.Contains('?') || uriText.Contains('#')
                || uriText.Contains('\\') || uriText.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
                throw new InvalidDataException($"Offline taxonomy reference '{uriText}' must be a canonical HTTP(S) file URI without credentials, query, or fragment.");
            EnsureSafeRelativePath(localPath, "offline reference target");
            if (!manifest.PackageFiles.ContainsKey(localPath))
                throw new InvalidDataException($"Offline reference '{uriText}' targets unpinned file '{localPath}'.");
        }
    }

    private static bool IsValidRelationshipUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
            || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme is not ("http" or "https")
            || (!string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0);
    }

    private static bool IsDeclaredQName(string? value, IReadOnlyDictionary<string, string> namespaceUris)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator != value.LastIndexOf(':') || separator == value.Length - 1)
            return false;
        var prefix = value[..separator];
        var localName = value[(separator + 1)..];
        if (!namespaceUris.ContainsKey(prefix)) return false;
        try
        {
            System.Xml.XmlConvert.VerifyNCName(prefix);
            System.Xml.XmlConvert.VerifyNCName(localName);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool IsSupportedTypedDimensionDataType(
        string? value,
        IReadOnlyDictionary<string, string> namespaceUris)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator != value.LastIndexOf(':') || separator == value.Length - 1)
            return false;
        var prefix = value[..separator];
        var localName = value[(separator + 1)..];
        if (localName != "string"
            || !namespaceUris.TryGetValue(prefix, out var namespaceUri)
            || !StringComparer.Ordinal.Equals(namespaceUri, "http://www.w3.org/2001/XMLSchema"))
            return false;
        try
        {
            System.Xml.XmlConvert.VerifyNCName(prefix);
            System.Xml.XmlConvert.VerifyNCName(localName);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static void ValidateTypedDimensionDomainDeclarations(
        FilingProfileManifest manifest,
        string versionDirectory)
    {
        if (manifest.TypedDimensionDomains.Count == 0) return;

        XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
        XNamespace xbrldt = "http://xbrl.org/2005/xbrldt";
        var declarationsById = new Dictionary<(string PackagePath, string Id), List<(string NamespaceUri, string LocalName, bool IsString)>>();
        var dimensions = new Dictionary<(string NamespaceUri, string LocalName), List<(string PackagePath, string TypedDomainRef)>>();
        long inspectedSchemaBytes = 0;
        var inspectedSchemaCount = 0;
        foreach (var relativePath in manifest.PackageFiles.Keys
                     .Where(path => Path.GetExtension(path).Equals(".xsd", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            var fullPath = ResolvePackageFile(versionDirectory, relativePath);
            var info = new FileInfo(fullPath);
            inspectedSchemaCount++;
            inspectedSchemaBytes = checked(inspectedSchemaBytes + info.Length);
            if (info.Length > 16L * 1024 * 1024 || inspectedSchemaCount > 4096
                || inspectedSchemaBytes > 256L * 1024 * 1024)
                throw new InvalidDataException("Typed-domain schema inspection exceeds its file-count or byte limits.");

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16L * 1024 * 1024,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });
            XDocument document;
            try
            {
                document = XDocument.Load(reader, LoadOptions.None);
            }
            catch (System.Xml.XmlException exception)
            {
                throw new InvalidDataException($"Typed-domain schema '{relativePath}' is not safe, bounded XML.", exception);
            }

            var schema = document.Root;
            if (schema?.Name != xsd + "schema") continue;
            var targetNamespace = (string?)schema.Attribute("targetNamespace") ?? string.Empty;
            var normalizedPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            foreach (var element in schema.Elements(xsd + "element"))
            {
                var name = (string?)element.Attribute("name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var key = (targetNamespace, name);
                var typedDomainRef = (string?)element.Attribute(xbrldt + "typedDomainRef");
                if (typedDomainRef is not null)
                {
                    if (!dimensions.TryGetValue(key, out var matches))
                        dimensions.Add(key, matches = new List<(string, string)>());
                    matches.Add((normalizedPath, typedDomainRef));
                }
                var id = (string?)element.Attribute("id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var idKey = (normalizedPath, id);
                    if (!declarationsById.TryGetValue(idKey, out var matches))
                        declarationsById.Add(idKey, matches = new List<(string, string, bool)>());
                    matches.Add((targetNamespace, name, IsXsdStringElementDeclaration(element, xsd)));
                }
            }
        }

        foreach (var (dimensionQName, typedDomain) in manifest.TypedDimensionDomains)
        {
            var dimension = manifest.Concepts.Single(concept =>
                StringComparer.Ordinal.Equals(concept.QualifiedName, dimensionQName));
            var separator = typedDomain.DomainElementQName.IndexOf(':');
            var prefix = typedDomain.DomainElementQName[..separator];
            var localName = typedDomain.DomainElementQName[(separator + 1)..];
            var namespaceUri = manifest.NamespaceUris[prefix];
            var dimensionKey = (dimension.Namespace, dimension.LocalName);
            if (!dimensions.TryGetValue(dimensionKey, out var dimensionMatches)
                || dimensionMatches.Count != 1
                || !TryResolveTypedDomainReference(versionDirectory, dimensionMatches[0].PackagePath,
                    dimensionMatches[0].TypedDomainRef, manifest.OfflineReferences, manifest.PackageFiles,
                    out var targetPath, out var targetId)
                || !declarationsById.TryGetValue((targetPath, targetId), out var domainMatches)
                || domainMatches.Count != 1
                || !StringComparer.Ordinal.Equals(domainMatches[0].NamespaceUri, namespaceUri)
                || !StringComparer.Ordinal.Equals(domainMatches[0].LocalName, localName)
                || !domainMatches[0].IsString)
                throw new InvalidDataException(
                    $"Typed dimension '{dimensionQName}' must resolve its typedDomainRef URI to the declared global xs:string element '{typedDomain.DomainElementQName}' in the hash-verified package schemas.");
        }
    }

    private static bool TryResolveTypedDomainReference(
        string versionDirectory,
        string sourceSchemaPath,
        string typedDomainRef,
        IReadOnlyDictionary<string, string> offlineReferences,
        IReadOnlyDictionary<string, string> packageFiles,
        out string targetPackagePath,
        out string targetId)
    {
        targetPackagePath = string.Empty;
        targetId = string.Empty;
        var hash = typedDomainRef.IndexOf('#');
        if (hash < 0 || hash != typedDomainRef.LastIndexOf('#') || hash == typedDomainRef.Length - 1)
            return false;
        var documentUri = typedDomainRef[..hash];
        try { targetId = Uri.UnescapeDataString(typedDomainRef[(hash + 1)..]); }
        catch (UriFormatException) { return false; }
        if (string.IsNullOrWhiteSpace(targetId) || targetId.Any(char.IsControl)) return false;

        if (Uri.TryCreate(documentUri, UriKind.Absolute, out var absoluteUri))
        {
            var packageUri = absoluteUri.GetLeftPart(UriPartial.Path);
            if (!offlineReferences.TryGetValue(packageUri, out var mappedPackagePath))
                return false;
            targetPackagePath = mappedPackagePath;
        }
        else
        {
            try
            {
                var sourceDirectory = Path.GetDirectoryName(sourceSchemaPath) ?? string.Empty;
                var decodedDocumentPath = Uri.UnescapeDataString(documentUri).Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(versionDirectory, sourceDirectory, decodedDocumentPath));
                EnsureWithin(versionDirectory, fullPath, "Typed-domain schema reference");
                targetPackagePath = Path.GetRelativePath(versionDirectory, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
            }
            catch (Exception exception) when (exception is UriFormatException or InvalidDataException
                                               or ArgumentException or IOException or NotSupportedException)
            {
                return false;
            }
        }

        try
        {
            EnsureSafeRelativePath(targetPackagePath, "Typed-domain schema reference");
            return packageFiles.ContainsKey(targetPackagePath);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsXsdStringElementDeclaration(XElement element, XNamespace xsd)
    {
        var typeName = (string?)element.Attribute("type");
        if (!string.IsNullOrWhiteSpace(typeName))
            return ResolvesToXsdString(element, typeName);

        var simpleType = element.Element(xsd + "simpleType");
        var restriction = simpleType?.Element(xsd + "restriction");
        var baseName = (string?)restriction?.Attribute("base");
        var simpleTypeDefinitions = simpleType?.Elements()
            .Where(child => child.Name != xsd + "annotation")
            .ToArray();
        return !string.IsNullOrWhiteSpace(baseName)
            && ResolvesToXsdString(element, baseName)
            && restriction!.Elements().All(child => child.Name == xsd + "annotation")
            && simpleTypeDefinitions is { Length: 1 }
            && ReferenceEquals(simpleTypeDefinitions[0], restriction);
    }

    private static bool ResolvesToXsdString(XElement element, string qualifiedName)
    {
        return ResolveQName(element, qualifiedName) == "http://www.w3.org/2001/XMLSchema|string";
    }

    private static string? ResolveQName(XElement element, string qualifiedName)
    {
        var separator = qualifiedName.IndexOf(':');
        if (separator <= 0 || separator != qualifiedName.LastIndexOf(':')
            || separator == qualifiedName.Length - 1)
            return null;
        var prefix = qualifiedName[..separator];
        var localName = qualifiedName[(separator + 1)..];
        try
        {
            System.Xml.XmlConvert.VerifyNCName(prefix);
            System.Xml.XmlConvert.VerifyNCName(localName);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        var namespaceUri = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        return namespaceUri is null ? null : namespaceUri + "|" + localName;
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

    private sealed record DigestPayloadV4(
        int SchemaVersion,
        string ProfileId,
        string Version,
        string EntryPoint,
        string OutputFormat,
        IReadOnlyList<CanonicalConcept> Concepts,
        IReadOnlyList<string> SupportedUnits,
        IReadOnlyDictionary<string, string> NamespaceUris,
        IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionDomains,
        IReadOnlyList<PresentationLink> PresentationLinks,
        IReadOnlyList<string> PackageNames,
        IReadOnlyDictionary<string, string> PackageFiles,
        IReadOnlyDictionary<string, string> OfflineReferences,
        IReadOnlyList<string> RequiredPlugins,
        IReadOnlyList<string> RequiredChecks);

    private sealed record DigestPayloadV5(
        int SchemaVersion,
        string ProfileId,
        string Version,
        string EntryPoint,
        string OutputFormat,
        IReadOnlyList<CanonicalConcept> Concepts,
        IReadOnlyList<string> SupportedUnits,
        IReadOnlyDictionary<string, string> NamespaceUris,
        IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionDomains,
        IReadOnlyList<PresentationLink> PresentationLinks,
        IReadOnlyList<CalculationLink> CalculationLinks,
        IReadOnlyList<DefinitionLink> DefinitionLinks,
        IReadOnlyList<string> PackageNames,
        IReadOnlyDictionary<string, string> PackageFiles,
        IReadOnlyDictionary<string, string> OfflineReferences,
        IReadOnlyList<string> RequiredPlugins,
        IReadOnlyList<string> RequiredChecks);

    private sealed record DigestPayloadV7(
        int SchemaVersion,
        string ProfileId,
        string Version,
        string EntryPoint,
        string OutputFormat,
        IReadOnlyList<CanonicalConcept> Concepts,
        IReadOnlyList<string> SupportedUnits,
        IReadOnlyDictionary<string, string> NamespaceUris,
        IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionDomains,
        IReadOnlyDictionary<string, TypedDimensionDomainMetadata> TypedDimensionDomains,
        IReadOnlyList<PresentationLink> PresentationLinks,
        IReadOnlyList<CalculationLink> CalculationLinks,
        IReadOnlyList<DefinitionLink> DefinitionLinks,
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
        IReadOnlyDictionary<string, string> LabelsByLanguage,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConceptNumericPolicy? NumericPolicy);

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
                Sorted(concept.LabelsByLanguage), concept.NumericPolicy))
            .ToArray();
        object canonical = manifest.SchemaVersion switch
        {
            < 3 => new DigestPayload(
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
                manifest.RequiredChecks.Order(StringComparer.Ordinal).ToArray()),
            3 => new DigestPayloadV3(
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
                manifest.RequiredChecks.Order(StringComparer.Ordinal).ToArray()),
            4 => new DigestPayloadV4(
                manifest.SchemaVersion,
                manifest.ProfileId,
                manifest.Version,
                manifest.EntryPoint,
                manifest.OutputFormat,
                concepts,
                manifest.SupportedUnits.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.NamespaceUris),
                SortedDomains(manifest.DimensionDomains),
                SortedPresentationLinks(manifest.PresentationLinks),
                manifest.PackageNames.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.PackageFiles),
                Sorted(manifest.OfflineReferences),
                manifest.RequiredPlugins.Order(StringComparer.Ordinal).ToArray(),
                manifest.RequiredChecks.Order(StringComparer.Ordinal).ToArray()),
            5 or 6 => new DigestPayloadV5(
                manifest.SchemaVersion,
                manifest.ProfileId,
                manifest.Version,
                manifest.EntryPoint,
                manifest.OutputFormat,
                concepts,
                manifest.SupportedUnits.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.NamespaceUris),
                SortedDomains(manifest.DimensionDomains),
                SortedPresentationLinks(manifest.PresentationLinks),
                SortedCalculationLinks(manifest.CalculationLinks),
                SortedDefinitionLinks(manifest.DefinitionLinks),
                manifest.PackageNames.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.PackageFiles),
                Sorted(manifest.OfflineReferences),
                manifest.RequiredPlugins.Order(StringComparer.Ordinal).ToArray(),
                manifest.RequiredChecks.Order(StringComparer.Ordinal).ToArray()),
            _ => new DigestPayloadV7(
                manifest.SchemaVersion,
                manifest.ProfileId,
                manifest.Version,
                manifest.EntryPoint,
                manifest.OutputFormat,
                concepts,
                manifest.SupportedUnits.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.NamespaceUris),
                SortedDomains(manifest.DimensionDomains),
                SortedTypedDomains(manifest.TypedDimensionDomains),
                SortedPresentationLinks(manifest.PresentationLinks),
                SortedCalculationLinks(manifest.CalculationLinks),
                SortedDefinitionLinks(manifest.DefinitionLinks),
                manifest.PackageNames.Order(StringComparer.Ordinal).ToArray(),
                Sorted(manifest.PackageFiles),
                Sorted(manifest.OfflineReferences),
                manifest.RequiredPlugins.Order(StringComparer.Ordinal).ToArray(),
                manifest.RequiredChecks.Order(StringComparer.Ordinal).ToArray())
        };
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

    private static IReadOnlyDictionary<string, TypedDimensionDomainMetadata> SortedTypedDomains(
        IReadOnlyDictionary<string, TypedDimensionDomainMetadata> values)
    {
        var result = new SortedDictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal);
        foreach (var (key, domain) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            result.Add(key, domain);
        return result;
    }

    internal static IReadOnlyList<PresentationLink> SortedPresentationLinks(IEnumerable<PresentationLink> links)
        => links.OrderBy(link => link.RoleUri, StringComparer.Ordinal)
            .ThenBy(link => link.ParentQName, StringComparer.Ordinal)
            .ThenBy(link => link.Order)
            .ThenBy(link => link.ChildQName, StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<CalculationLink> SortedCalculationLinks(IEnumerable<CalculationLink> links)
        => links.OrderBy(link => link.RoleUri, StringComparer.Ordinal)
            .ThenBy(link => link.ParentQName, StringComparer.Ordinal)
            .ThenBy(link => link.Order)
            .ThenBy(link => link.ChildQName, StringComparer.Ordinal)
            .ThenBy(link => link.Weight)
            .ThenBy(link => link.TargetRoleUri, StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<DefinitionLink> SortedDefinitionLinks(IEnumerable<DefinitionLink> links)
        => links.OrderBy(link => link.RoleUri, StringComparer.Ordinal)
            .ThenBy(link => link.ArcRoleUri, StringComparer.Ordinal)
            .ThenBy(link => link.ParentQName, StringComparer.Ordinal)
            .ThenBy(link => link.Order)
            .ThenBy(link => link.ChildQName, StringComparer.Ordinal)
            .ThenBy(link => link.TargetRoleUri, StringComparer.Ordinal)
            .ThenBy(link => link.Usable)
            .ThenBy(link => link.Closed)
            .ThenBy(link => link.ContextElement, StringComparer.Ordinal)
            .ToArray();
}
