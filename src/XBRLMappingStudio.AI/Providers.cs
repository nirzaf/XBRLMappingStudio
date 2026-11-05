using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;

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
    IReadOnlyList<SourceFieldDescriptor> SourceFields,
    int? MaxOutputTokens = null,
    IReadOnlyDictionary<string, IReadOnlyList<RankedCandidate>>? CandidatesBySourceField = null);

/// <summary>A stable source identity and bounded context safe to use for concept retrieval.</summary>
public sealed record SourceFieldDescriptor(
    string Id,
    string Name,
    string? Unit = null,
    string? PeriodType = null);

/// <summary>A provider proposal tied to the exact source identity it addresses.</summary>
public sealed record ProposedMapping(string SourceFieldId, string Concept);

/// <summary>
/// A raw provider completion. Refusals and oversized/truncated responses are surfaced as
/// first-class flags so the caller can refuse to mutate project state (AI03).
/// </summary>
public sealed record ChatResponse(
    IReadOnlyList<ProposedMapping> ProposedMappings,
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
        return Task.FromResult(new ChatResponse(Array.Empty<ProposedMapping>(), Refused: true, Truncated: false));
    }
}

/// <summary>
/// HTTP-backed provider for <see cref="AiMode.ManagedProvider"/>. Never instantiated in
/// offline mode. The <see cref="HttpMessageHandler"/> is injectable so an offline audit can
/// attach a counting spy and prove no request leaves the process.
/// </summary>
public sealed class HttpChatProvider : IChatClientProvider
{
    private const int MaxResponseBytes = 1_048_576;
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
            var body = await ReadBoundedBodyAsync(resp.Content, cancellationToken);
            if (body is null) return new ProbeResult(false, "Provider capabilities response exceeded 1 MiB.");
            var caps = JsonSerializer.Deserialize<ProviderCapabilities>(body);
            return caps is { SupportsChat: true }
                ? new ProbeResult(true, "ok", caps)
                : new ProbeResult(false, "Provider did not advertise chat support.", caps);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(false, $"Probe unavailable: {ex.Message}");
        }
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request);
        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (payloadBytes > MaxResponseBytes)
            return new ChatResponse(Array.Empty<ProposedMapping>(), Refused: false, Truncated: true,
                OutboundBytes: payloadBytes);
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_endpoint}/complete", content, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return new ChatResponse(Array.Empty<ProposedMapping>(), Refused: true, Truncated: false);
        var body = await ReadBoundedBodyAsync(resp.Content, cancellationToken);
        if (body is null)
            return new ChatResponse(Array.Empty<ProposedMapping>(), Refused: false, Truncated: true,
                OutboundBytes: payloadBytes, InboundBytes: MaxResponseBytes + 1);
        var parsed = JsonSerializer.Deserialize<ProposedMapping[]>(body) ?? Array.Empty<ProposedMapping>();
        return new ChatResponse(parsed, Refused: false, Truncated: false,
            OutboundBytes: payloadBytes,
            InboundBytes: System.Text.Encoding.UTF8.GetByteCount(body));
    }

    private static async Task<string?> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > MaxResponseBytes) return null;
            body.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(body.ToArray());
    }
}

/// <summary>Microsoft.Extensions.AI bridge for an explicitly configured chat client.</summary>
public sealed class MicrosoftAiChatProvider(IChatClient client, string name = "Microsoft.Extensions.AI") : IChatClientProvider
{
    public string Name { get; } = name;

    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProbeResult(true, "Chat client is configured.",
            new ProviderCapabilities(true, false, int.MaxValue, true)));
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var perFieldCandidates = JsonSerializer.Serialize(request.CandidatesBySourceField ??
            new Dictionary<string, IReadOnlyList<RankedCandidate>>(StringComparer.Ordinal));
        var prompt = $"{request.SystemPrompt}\nAllowed concepts by source field (JSON):\n{perFieldCandidates}\nSource fields:\n{request.UserPrompt}";
        try
        {
            var response = await client.GetResponseAsync<ProposedMapping[]>(prompt,
                new ChatOptions { MaxOutputTokens = request.MaxOutputTokens },
                cancellationToken: cancellationToken);
            var proposals = response.Result ?? Array.Empty<ProposedMapping>();
            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(proposals).Length;
            return new ChatResponse(proposals, Refused: false,
                Truncated: responseBytes > 1_048_576, InboundBytes: responseBytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return new ChatResponse(Array.Empty<ProposedMapping>(), Refused: true, Truncated: false);
        }
    }
}
