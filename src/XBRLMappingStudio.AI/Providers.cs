using System.Net.Http.Json;
using System.Text.Json;

namespace XBRLMappingStudio.AI;

public sealed record ProviderCapabilities(
    bool SupportsChat,
    bool SupportsEmbeddings,
    int MaxContextTokens,
    bool SupportsStructuredOutput);

/// <summary>Result of a provider capability probe (AI03).</summary>
public sealed record ProbeResult(bool Available, string? Detail, ProviderCapabilities? Capabilities = null);

public sealed record ChatRequest(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<string> AllowedConcepts,
    int? MaxOutputTokens = null);

/// <summary>
/// A raw provider completion. Refusals and oversized/truncated responses are surfaced as
/// first-class flags so the caller can refuse to mutate project state (AI03).
/// </summary>
public sealed record ChatResponse(
    IReadOnlyList<string> ProposedMappings,
    bool Refused,
    bool Truncated,
    int OutboundBytes = 0,
    int InboundBytes = 0);

/// <summary>
/// Abstract chat provider. The suggestion service only ever talks to a model through this
/// contract, keeping the offline/online decision in one place.
/// </summary>
public interface IChatClientProvider
{
    string Name { get; }
    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken);
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Null-object provider used whenever outbound is disallowed. It records that it was asked
/// but performs no I/O, so tests can assert "zero outbound" by checking that the service
/// never even invokes a real transport.
/// </summary>
public sealed class NullProvider : IChatClientProvider
{
    public string Name => "null";
    public int InvokeCount { get; private set; }

    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probe);
        return Task.FromResult(new ProbeResult(false, "Outbound disabled by policy."));
    }
    private int _probe;

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        InvokeCount++;
        return Task.FromResult(new ChatResponse(Array.Empty<string>(), Refused: true, Truncated: false));
    }
}

/// <summary>
/// HTTP-backed provider for <see cref="AiMode.ManagedProvider"/>. Never instantiated in
/// offline mode. The <see cref="HttpMessageHandler"/> is injectable so an offline audit can
/// attach a counting spy and prove no request leaves the process.
/// </summary>
public sealed class HttpChatProvider : IChatClientProvider
{
    private readonly HttpClient _http;
    private readonly string _endpoint;

    public string Name => "http";

    public HttpChatProvider(Uri endpoint, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint.ToString();
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_endpoint}/capabilities", cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return new ProbeResult(false, $"Probe failed with HTTP {(int)resp.StatusCode}.");
            var caps = await resp.Content.ReadFromJsonAsync<ProviderCapabilities>(cancellationToken);
            return new ProbeResult(true, "ok", caps);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(false, $"Probe unavailable: {ex.Message}");
        }
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request);
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_endpoint}/complete", content, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return new ChatResponse(Array.Empty<string>(), Refused: true, Truncated: false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonSerializer.Deserialize<string[]>(body) ?? Array.Empty<string>();
        return new ChatResponse(parsed, Refused: false, Truncated: false,
            OutboundBytes: System.Text.Encoding.UTF8.GetByteCount(payload),
            InboundBytes: System.Text.Encoding.UTF8.GetByteCount(body));
    }
}
