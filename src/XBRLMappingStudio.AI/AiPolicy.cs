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
public sealed record AiPolicy(AiMode Mode = AiMode.Offline)
{
    public static AiPolicy Offline { get; } = new(AiMode.Offline);

    /// <summary>True only when outbound contact is permitted by policy.</summary>
    public bool AllowsOutbound => Mode != AiMode.Offline;

    public static AiPolicy Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "managed" or "managedprovider" => new AiPolicy(AiMode.ManagedProvider),
        "localmodel" => new AiPolicy(AiMode.LocalModel),
        _ => Offline
    };
}
