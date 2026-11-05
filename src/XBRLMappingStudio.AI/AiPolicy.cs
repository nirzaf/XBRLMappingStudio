using System.Net;

namespace XBRLMappingStudio.AI;

/// <summary>Data-handling mode for the suggestion service (AI01).</summary>
public enum AiMode
{
    /// <summary>No network I/O of any kind. Suggestions come only from the local ranker.</summary>
    Offline,
    /// <summary>A managed (cloud) chat provider may be contacted.</summary>
    ManagedProvider,
    /// <summary>A locally-hosted model endpoint may be contacted.</summary>
    LocalModel
}

/// <summary>
/// Declares the outbound-data posture. When <see cref="Mode"/> is <see cref="AiMode.Offline"/>
/// the service is forbidden from making any outbound call; the acceptance criterion
/// "cloud disabled ⇒ zero outbound" is enforced structurally (the provider is replaced by
/// <see cref="NullProvider"/>) rather than by convention alone.
/// </summary>
public sealed record AiPolicy(
    AiMode Mode = AiMode.Offline,
    string? ProviderConfigId = null,
    bool ProviderEnabled = false,
    bool ProviderApproved = false,
    IReadOnlySet<string>? ApprovedEndpointHosts = null)
{
    public static AiPolicy Offline { get; } = new(AiMode.Offline);

    /// <summary>True only when a named, enabled provider has explicit approval.</summary>
    public bool AllowsOutbound => Mode != AiMode.Offline
        && ProviderEnabled
        && ProviderApproved
        && Guid.TryParseExact(ProviderConfigId, "D", out _);

    /// <summary>Check the actual HTTP endpoint against the selected provider policy.</summary>
    public bool AllowsEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!AllowsOutbound || !endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo))
            return false;

        if (Mode == AiMode.LocalModel)
            return (endpoint.Scheme is "http" or "https")
                && (IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address)
                    || StringComparer.OrdinalIgnoreCase.Equals(endpoint.Host, "localhost"));

        return Mode == AiMode.ManagedProvider
            && StringComparer.OrdinalIgnoreCase.Equals(endpoint.Scheme, Uri.UriSchemeHttps)
            && ApprovedEndpointHosts?.Any(host => StringComparer.OrdinalIgnoreCase.Equals(host, endpoint.IdnHost)) == true;
    }

    public static AiPolicy Parse(string? s) => s?.ToLowerInvariant() switch
    {
        // Parsing a mode never grants consent or chooses a provider configuration.
        "managed" or "managedprovider" => new AiPolicy(AiMode.ManagedProvider),
        "localmodel" => new AiPolicy(AiMode.LocalModel),
        _ => Offline
    };
}
