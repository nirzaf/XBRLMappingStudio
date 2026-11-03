using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Worker;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR08 — worker/conversion resilience. Cooperative cancellation aborts before any
/// publication; a corrupted intermediate is refused so an existing accepted output
/// survives untouched; retrying the same inputs yields a semantically equivalent fact
/// set under a new traceable run id. All assertions are offline and deterministic.
/// </summary>
public sealed class ConversionResilienceTests : IDisposable
{
    private static FilingProfile Profile => SyntheticProfile.Build();
    private readonly string _dir;

    public ConversionResilienceTests()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-xbrl08", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static IReadOnlyList<XbrlFact> Facts() => new[]
    {
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 1_250_000m,
            Decimals = -3
        }
    };

    /// <summary>
    /// Simulates the two-phase conversion+publication used by the pipeline: serialize to a
    /// staged file, verify it reopens, then atomically move into place. Returns the run id.
    /// </summary>
    private string PublishAccepted(string dest, IReadOnlyList<XbrlFact> facts, Action<string>? beforeMove = null)
    {
        var runId = Guid.NewGuid().ToString("n");
        var staged = dest + ".staged";
        var json = OimSerializer.Serialize(Profile, facts);
        File.WriteAllText(staged, json, new UTF8Encoding(false));

        beforeMove?.Invoke(staged); // injection seam (corruption / cancellation)

        // Verify staged content reopens before touching the destination.
        var reopened = OimSerializer.ReadFacts(File.ReadAllText(staged));
        if (reopened.Count == 0)
            throw new InvalidDataException("staged artifact failed reconciliation");

        File.Move(staged, dest, overwrite: true);
        return runId;
    }

    [Fact]
    public async Task Cancellation_DoesNotPublishOutput()
    {
        var dest = System.IO.Path.Combine(_dir, "out.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var worker = new LocalSyntheticWorker(Profile);
        var request = new WorkerRequest(
            WorkerOperations.ValidateReport,
            OperationId: "op1",
            JobId: "job1",
            ProfileDigest: Profile.Digest,
            DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(30),
            Payload: JsonSerializer.SerializeToElement(OimSerializer.Serialize(Profile, Facts())));

        // Cooperative cancellation aborts the request; no destination file is produced.
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await worker.RequestAsync(request, cts.Token));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void ExistingAcceptedOutput_Survives_CorruptedIntermediate()
    {
        var dest = System.IO.Path.Combine(_dir, "out.json");
        var firstRun = PublishAccepted(dest, Facts());
        var acceptedBytes = File.ReadAllBytes(dest);

        // A later run injects a truncated intermediate before the move.
        Assert.ThrowsAny<JsonException>(() => PublishAccepted(dest, Facts(), staged =>
        {
            // Simulate a crash mid-write: overwrite the staged file with truncated bytes.
            var text = File.ReadAllText(staged);
            File.WriteAllText(staged, text.Substring(0, text.Length / 2));
        }));

        // The previously accepted output is byte-for-byte intact.
        Assert.Equal(acceptedBytes, File.ReadAllBytes(dest));
        Assert.False(string.IsNullOrEmpty(firstRun));
    }

    [Fact]
    public void RetrySameInputs_YieldsEquivalentFacts_AndNewRunId()
    {
        var dest = System.IO.Path.Combine(_dir, "out.json");
        var runA = PublishAccepted(dest, Facts());
        var factsA = OimSerializer.ReadFacts(File.ReadAllText(dest));

        // Retry with identical inputs.
        var runB = PublishAccepted(dest, Facts());
        var factsB = OimSerializer.ReadFacts(File.ReadAllText(dest));

        // New, distinct, traceable run id.
        Assert.NotEqual(runA, runB);
        // Semantically equivalent fact multiset.
        Assert.Equal(
            factsA.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal),
            factsB.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkerRejectsUnknownConcept_WithBlockingDiagnostic()
    {
        var bad = new[]
        {
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Nonexistent"),
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "ABC-Corp",
                Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
                Unit = new XbrlUnit("QAR"),
                Value = 1m,
                Decimals = 0
            }
        };
        await using var worker = new LocalSyntheticWorker(Profile);
        var request = new WorkerRequest(
            WorkerOperations.ValidateReport, "op", "job", Profile.Digest,
            DateTimeOffset.UtcNow.AddSeconds(30),
            JsonSerializer.SerializeToElement(OimSerializer.Serialize(Profile, bad)));

        var response = await worker.RequestAsync(request, CancellationToken.None);
        Assert.Equal(WorkerStatus.DomainFailure, response.Status);
        Assert.Contains(response.Diagnostics!, d => d.Code == DiagnosticCodes.XbrConceptUnknown && d.IsBlocking);
    }
}
