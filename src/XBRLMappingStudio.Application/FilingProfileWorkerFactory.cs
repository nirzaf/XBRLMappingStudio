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

    public FilingProfileWorkerFactory(Func<FilingProfile, IWorkerClient>? arelleFactory = null)
    {
        _arelleFactory = arelleFactory;
    }

    public IWorkerClient Create(FilingProfile profile, bool useArelle = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (useArelle)
        {
            return _arelleFactory?.Invoke(profile)
                ?? throw new InvalidOperationException(
                    $"Arelle is required for profile '{profile.ProfileId}', but no Arelle worker is configured.");
        }

        if (string.Equals(profile.ProfileId, SyntheticProfile.ProfileId, StringComparison.Ordinal))
            return new LocalSyntheticWorker(profile);

        throw new InvalidOperationException(
            $"Profile '{profile.ProfileId}' has no configured validation worker; execution is blocked.");
    }
}
