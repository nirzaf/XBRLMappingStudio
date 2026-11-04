using System.Net;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.AI;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI.Tests;

/// <summary>Counting spy transport: proves whether any HTTP request left the process.</summary>
internal sealed class CountingHandler : HttpMessageHandler
{
    public int RequestCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RequestCount);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"supportsChat\":true,\"supportsEmbeddings\":false,\"maxContextTokens\":1024,\"supportsStructuredOutput\":true}",
                Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

/// <summary>Scriptable in-memory provider for the AI03 error matrix.</summary>
internal sealed class FakeProvider : IChatClientProvider
{
    public ProbeResult Probe { get; set; } = new(true, "ok");
    public Func<ChatRequest, ChatResponse>? Completer { get; set; }
    public ChatRequest? LastRequest { get; private set; }
    public int ProbeCalls;
    public int CompleteCalls;
    public string Name => "fake";

    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        ProbeCalls++;
        if (Probe.Available) return Task.FromResult(Probe);
        return Task.FromResult(Probe);
    }

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        CompleteCalls++;
        LastRequest = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Completer?.Invoke(request)
            ?? new ChatResponse(Array.Empty<ProposedMapping>(), Refused: true, Truncated: false));
    }
}

internal static class TestProjects
{
    public static MappingProject Empty() => new()
    {
        ProjectId = "ai-test-project",
        Name = "ai-test",
        ProfileId = SyntheticProfile.ProfileId,
        ProfileDigest = "test-digest",
    };

    public static FilingProfile Profile() => SyntheticProfile.Build();

    public static TaxonomyIndex Taxonomy() => new(SyntheticProfile.Build());
}
