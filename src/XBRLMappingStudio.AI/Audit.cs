using System.Text;
using System.Text.Json;

namespace XBRLMappingStudio.AI;

/// <summary>
/// One privacy-preserving audit entry per analyst-facing action (AI06). Records carry
/// sequence, action, field, concept, blocking-diagnostic count and codes — never raw
/// payloads — so the log can be shared for review without leaking workbook contents.
/// </summary>
public sealed record AuditRecord(
    int Sequence,
    string Action,
    string SourceField,
    string Concept,
    int BlockingDiagnostics,
    IReadOnlyList<string> DiagnosticCodes);

/// <summary>Findings from scanning rendered audit output for leaked secrets/canaries.</summary>
public sealed record CanaryScanResult(
    bool Clean,
    IReadOnlyList<string> Findings)
{
    public void EnsureClean()
    {
        if (!Clean)
            throw new InvalidOperationException(
                "Audit output contains prohibited material: " + string.Join("; ", Findings));
    }
}

/// <summary>
/// Append-only in-memory audit trail with a deterministic JSONL rendering and a
/// secret/canary scanner (AI06). The scanner looks for seeded credential markers
/// (password=/token=/secret=) and any registered canary amounts, so a test can seed
/// a fake secret in a provider payload and prove it never reaches the audit surface.
/// </summary>
public sealed class AuditLog
{
    private readonly List<AuditRecord> _records = new();
    private readonly List<string> _canaryValues = new();
    private int _sequence;

    /// <summary>Monotonic sequence used by records; exposed so callers build records inline.</summary>
    public int NextSequence => Volatile.Read(ref _sequence) + 1;

    public IReadOnlyList<AuditRecord> Records => _records;

    /// <summary>Register a canary value (e.g. a seeded amount) that must never appear in output.</summary>
    public void RegisterCanary(string value) => _canaryValues.Add(value);

    /// <summary>
    /// Append a record. The sequence is re-stamped here so callers may pass their
    /// pre-computed <see cref="NextSequence"/> hint without racing against duplicates.
    /// </summary>
    public void Record(AuditRecord record)
    {
        Interlocked.Increment(ref _sequence);
        _records.Add(record with { Sequence = _sequence });
    }

    /// <summary>Deterministic JSONL rendering; sorted by sequence, ordinal field order.</summary>
    public string RenderJsonL()
    {
        var sb = new StringBuilder();
        foreach (var r in _records.OrderBy(r => r.Sequence))
        {
            sb.Append(JsonSerializer.Serialize(new
            {
                seq = r.Sequence,
                action = r.Action,
                field = r.SourceField,
                concept = r.Concept,
                blocked = r.BlockingDiagnostics,
                codes = r.DiagnosticCodes.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = false }));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Scan the rendered audit surface (and any caller-supplied transcript lines) for
    /// prohibited content: credential markers and registered canary values. Case-insensitive
    /// for markers; ordinal for canaries to avoid culture surprises.
    /// </summary>
    public CanaryScanResult Scan(IEnumerable<string>? extraTranscriptLines = null)
    {
        var haystack = new StringBuilder(RenderJsonL());
        foreach (var line in extraTranscriptLines ?? Array.Empty<string>())
            haystack.Append(line).Append('\n');
        var text = haystack.ToString();

        var findings = new List<string>();
        foreach (var marker in new[] { "password=", "token=", "secret=" })
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                findings.Add($"credential marker '{marker}' present in audit output");
        }
        foreach (var canary in _canaryValues)
        {
            if (text.Contains(canary, StringComparison.Ordinal))
                findings.Add($"canary value '{canary}' present in audit output");
        }
        return new CanaryScanResult(findings.Count == 0, findings);
    }
}
