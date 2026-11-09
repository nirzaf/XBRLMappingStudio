using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Redacts credential-looking values, email addresses and local paths while building a
/// shareable diagnostic bundle (OPS04, OPS08, OPS09). Project names and destination
/// paths are omitted; the bundle retains profile identity, run state, receipt digests,
/// and bounded log lines, never executable graph content or raw report payloads.
/// Scheduler deployment remains an operator-owned environment qualification.
/// </summary>
public static partial class SupportBundle
{
    public const int MaximumLogTailBytes = 256 * 1024;
    public const int MaximumLogLines = 200;
    public const int MaximumLogInputLines = 4096;
    public const int MaximumLogLineCharacters = 8 * 1024;
    public const int MaximumLogOutputCharacters = 64 * 1024;
    private const int MaximumRunIdCharacters = 256;

    [GeneratedRegex(@"\b(password|token|secret)\b\s*[=:]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?:[A-Z]:[\\/]|\\\\[^\\/\r\n]+[\\/][^\\/\r\n]+[\\/]|(?<![A-Za-z0-9])/(?:Users|home|tmp|var/tmp|mnt|media)/)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex LocalPathPattern();

    /// <summary>Mask credentials, email addresses and common local filesystem paths in text.</summary>
    public static string Redact(string line)
    {
        var redacted = SecretPattern().Replace(line, "$1=<redacted>");
        redacted = EmailPattern().Replace(redacted, "<email>");
        return LocalPathPattern().Replace(redacted, "<local-path>");
    }

    /// <summary>Read a bounded UTF-8 tail without materializing the complete log or any unbounded line.</summary>
    public static IReadOnlyList<string> ReadBoundedLogTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var offset = Math.Max(0, stream.Length - MaximumLogTailBytes);
        stream.Position = offset;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: false);

        if (offset > 0)
        {
            stream.Position = offset - 1;
            var startsAtLineBoundary = stream.ReadByte() == '\n';
            stream.Position = offset;
            if (!startsAtLineBoundary)
            {
                int skipped;
                do { skipped = reader.Read(); } while (skipped >= 0 && skipped != '\n');
            }
        }

        var tail = new Queue<string>();
        var line = new StringBuilder(Math.Min(MaximumLogLineCharacters, 1024));
        var overlong = false;
        var totalCharacters = 0;
        void AddLine()
        {
            var value = overlong ? "<log line omitted: character limit exceeded>" : line.ToString().TrimEnd('\r');
            value = Redact(value);
            tail.Enqueue(value);
            totalCharacters += value.Length;
            while (tail.Count > MaximumLogLines || totalCharacters > MaximumLogOutputCharacters)
                totalCharacters -= tail.Dequeue().Length;
            line.Clear();
            overlong = false;
        }

        int character;
        while ((character = reader.Read()) >= 0)
        {
            if (character == '\n')
            {
                AddLine();
                continue;
            }
            if (!overlong)
            {
                if (line.Length == MaximumLogLineCharacters) overlong = true;
                else line.Append((char)character);
            }
        }
        if (line.Length > 0 || overlong) AddLine();
        return tail.ToArray();
    }

    /// <summary>Compose the bundle text for one run; safe to write to disk or attach.</summary>
    public static string Build(string projectPath, string runId, RunJournal journal,
        IEnumerable<string>? logLines = null)
    {
        var sb = new StringBuilder();
        sb.Append("XBRL Mapping Studio support bundle\n");
        sb.Append("generated-utc: ").Append(DateTimeOffset.UtcNow.ToString("O")).Append('\n');

        // Keep the selected profile/revision for useful diagnostics, but don't export
        // the customer-controlled project name or mapping graph.
        try
        {
            var pm = new ProjectManager(projectPath);
            var project = pm.Load();
            sb.Append("project: <redacted>")
              .Append(" profile: ").Append(project.ProfileId)
              .Append(" semantic-revision: ").Append(project.SemanticRevision)
              .Append('\n');
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                     or JsonException or Project.ProjectSchemaException)
        {
            sb.Append("project: UNREADABLE (").Append(Redact(ex.Message)).Append(")\n");
        }

        // Journal receipt for the requested run.
        var safeRunId = IsSafeJournalRunId(runId) ? SanitizeRunId(runId) : "<invalid>";
        var entry = StringComparer.Ordinal.Equals(safeRunId, runId) ? journal.Load(runId) : null;
        sb.Append("run: ").Append(safeRunId).Append('\n');
        if (entry is null)
            sb.Append("journal: no entry for run\n");
        else
        {
            sb.Append("journal-state: ").Append(entry.State).Append('\n');
            sb.Append("destination: <redacted>\n");
            sb.Append("staged-digest: ").Append(entry.StagedDigest ?? "-").Append('\n');
            sb.Append("final-digest: ").Append(entry.FinalDigest ?? "-").Append('\n');
            // Lock tokens are publication-control material. They are useful for
            // local recovery but must not leave the host through a shareable bundle.
            sb.Append("lock-token: ").Append(entry.LockToken is null ? "-" : "<redacted>").Append('\n');
        }

        // Redacted bounded log tail. Bound this API too, even when callers do not
        // use ReadBoundedLogTail to load the source file.
        sb.Append("--- logs (redacted) ---\n");
        var tail = new Queue<string>();
        var totalCharacters = 0;
        var inputLines = 0;
        var inputTruncated = false;
        foreach (var line in logLines ?? Array.Empty<string>())
        {
            if (inputLines == MaximumLogInputLines)
            {
                inputTruncated = true;
                break;
            }
            inputLines++;
            var safeLine = line.Length > MaximumLogLineCharacters
                ? "<log line omitted: character limit exceeded>"
                : Redact(line);
            tail.Enqueue(safeLine);
            totalCharacters += safeLine.Length;
            while (tail.Count > MaximumLogLines || totalCharacters > MaximumLogOutputCharacters)
                totalCharacters -= tail.Dequeue().Length;
        }
        if (inputTruncated)
        {
            const string truncationNotice = "<log input truncated: line limit exceeded>";
            tail.Enqueue(truncationNotice);
            totalCharacters += truncationNotice.Length;
            while (tail.Count > MaximumLogLines || totalCharacters > MaximumLogOutputCharacters)
                totalCharacters -= tail.Dequeue().Length;
        }
        foreach (var line in tail) sb.Append(line).Append('\n');

        return sb.ToString();
    }

    private static string SanitizeRunId(string? runId)
    {
        if (string.IsNullOrEmpty(runId)) return "-";
        var bounded = runId.Length > MaximumRunIdCharacters
            ? runId[..MaximumRunIdCharacters]
            : runId;
        var normalized = new string(bounded.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        var singleLine = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Redact(singleLine);
    }

    private static bool IsSafeJournalRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > MaximumRunIdCharacters
            || runId.IndexOfAny(['/', '\\']) >= 0)
            return false;

        var invalidFileNameCharacters = Path.GetInvalidFileNameChars()
            .Where(character => !char.IsControl(character)).ToArray();
        return runId.IndexOfAny(invalidFileNameCharacters) < 0;
    }
}
