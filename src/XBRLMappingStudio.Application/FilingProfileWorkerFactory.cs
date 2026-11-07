using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Selects a worker from the resolved filing profile. The development profile may use
/// the local deterministic worker; every other profile requires an explicitly configured
/// Arelle worker factory so a missing provider cannot silently downgrade validation.
/// </summary>
public sealed class FilingProfileWorkerFactory
{
    private readonly Func<FilingProfile, IWorkerClient>? _arelleFactory;
    private readonly Func<FilingProfile, Func<IWorkerClient>>? _deferredArelleFactory;

    public FilingProfileWorkerFactory(Func<FilingProfile, IWorkerClient>? arelleFactory = null)
    {
        _arelleFactory = arelleFactory;
    }

    private FilingProfileWorkerFactory(
        Func<FilingProfile, Func<IWorkerClient>> deferredArelleFactory,
        bool deferProcessCreation)
    {
        _deferredArelleFactory = deferredArelleFactory;
    }

    public IWorkerClient Create(FilingProfile profile, bool useArelle = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var isSynthetic = string.Equals(profile.ProfileId, SyntheticProfile.ProfileId, StringComparison.Ordinal);
        if (useArelle || !isSynthetic)
        {
            if (_deferredArelleFactory is not null)
                return new BoundedWorkerClient(_deferredArelleFactory(profile));

            var arelleFactory = _arelleFactory
                ?? throw new InvalidOperationException(
                    $"Profile '{profile.ProfileId}' requires Arelle, but no Arelle worker is configured.");
            return new BoundedWorkerClient(() => arelleFactory(profile));
        }

        return new LocalSyntheticWorker(profile);
    }

    /// <summary>
    /// Creates the same fail-closed worker routing for CLI and desktop clients. A
    /// non-synthetic profile is bound to its hash-verified installation and offline
    /// reference map. It also requires a caller-supplied owner-approved qualification
    /// policy; without the external GAT-01/GAT-02 decision, real-profile execution stays
    /// blocked. Synthetic profiles use the local worker unless Arelle is explicitly
    /// requested for them.
    /// </summary>
    public static FilingProfileWorkerFactory ForInstalledProfiles(
        FilingProfileResolver resolver,
        string? pythonExecutablePath = null,
        string? workerScriptPath = null,
        string? taxonomyRootOverride = null,
        Func<InstalledFilingProfile, bool>? authorizeInstallation = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new FilingProfileWorkerFactory(profile =>
        {
            var script = FindWorkerScript(workerScriptPath);
            if (script is null)
                throw new InvalidOperationException("The Arelle worker script is not installed with this application.");

            var root = taxonomyRootOverride;
            InstalledFilingProfile? installation = null;
            if (!StringComparer.Ordinal.Equals(profile.ProfileId, SyntheticProfile.ProfileId))
            {
                installation = resolver.FindInstallation(profile.ProfileId, profile.Digest)
                    ?? throw new InvalidOperationException(
                        $"The exact installation for profile '{profile.ProfileId}' is unavailable.");
                if (authorizeInstallation?.Invoke(installation) != true)
                    throw new InvalidOperationException(
                        $"Profile '{profile.ProfileId}' has not passed the owner, filing-authority, and redistribution-rights gates. "
                        + "Close GAT-01 and GAT-02 before starting Arelle.");
                if (root is not null && !PathsEqual(root, installation.InstallationDirectory))
                    throw new InvalidOperationException(
                        "A qualified profile can only use the taxonomy package verified from its installed profile directory.");
                root = installation.InstallationDirectory;
            }
            else
            {
                root ??= FindSyntheticTaxonomyRoot(script);
            }

            if (root is null || !Directory.Exists(root))
                throw new InvalidOperationException("Arelle requires an installed, offline taxonomy directory.");

            var args = new List<string> { script, "--taxonomy-root", Path.GetFullPath(root) };
            if (installation is not null)
            {
                args.Add("--profile-manifest");
                args.Add(Path.Combine(installation.InstallationDirectory, InstalledFilingProfileCatalog.ManifestFileName));
                args.Add("--profile-manifest-sha256");
                args.Add(installation.ManifestFileDigest);
                foreach (var package in profile.PackageHashes.Keys.Where(path =>
                             Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    args.Add("--package");
                    args.Add(Path.Combine(installation.InstallationDirectory,
                        package.Replace('/', Path.DirectorySeparatorChar)));
                }
                foreach (var plugin in installation.RequiredPlugins)
                {
                    args.Add("--plugin");
                    args.Add(plugin);
                }
            }

            var executable = pythonExecutablePath
                ?? Environment.GetEnvironmentVariable("XMS_PYTHON")
                ?? (OperatingSystem.IsWindows() ? "python" : "python3");
            return () => new ArelleWorker(executable, args.ToArray(),
                    expectedProfileId: installation?.Profile.ProfileId,
                    expectedProfileSchemaVersion: installation?.SchemaVersion,
                    expectedProfileVersion: installation?.Version,
                    expectedProfileDigest: installation?.Profile.Digest,
                    expectedTaxonomyEntryPoint: installation is null ? null
                        : Path.GetRelativePath(installation.InstallationDirectory, installation.EntryPointPath)
                            .Replace('\\', '/'),
                    expectedRequiredPlugins: installation?.RequiredPlugins,
                    expectedRequiredChecks: installation?.Profile.RequiredChecks);
        }, deferProcessCreation: true);

        static string? FindWorkerScript(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? Path.GetFullPath(configured) : null;
            var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "arelle_worker.py");
            if (File.Exists(bundled)) return bundled;
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tools", "worker", "arelle_worker.py");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static string? FindSyntheticTaxonomyRoot(string script)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "taxonomy");
            if (File.Exists(Path.Combine(bundled, SyntheticProfile.TaxonomyFileName))) return bundled;
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(script)!); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "taxonomy");
                if (File.Exists(Path.Combine(candidate, SyntheticProfile.TaxonomyFileName))) return candidate;
                candidate = Path.Combine(directory.FullName, "tools", "worker", "taxonomy");
                if (File.Exists(Path.Combine(candidate, SyntheticProfile.TaxonomyFileName))) return candidate;
            }
            return null;
        }

        static bool PathsEqual(string left, string right)
            => (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Equals(Path.GetFullPath(left), Path.GetFullPath(right));
    }
}
