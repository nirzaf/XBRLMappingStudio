using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Redacts credential-looking values and builds a shareable diagnostic bundle (OPS04,
/// OPS08). Any <c>password=</c>, <c>token=</c> or <c>secret=</c> assignment in
/// a rendered line is masked before it leaves the process; the bundle carries the run
/// journal receipt, a project summary and bounded, redacted log lines — never raw payloads.
/// Scheduler deployment remains an operator-owned environment qualification.
/// </summary>
public static partial class SupportBundle
{
    public const int MaximumLogTailBytes = 256 * 1024;
    public const int MaximumLogLines = 200;
    public const int MaximumLogInputLines = 4096;
    public const int MaximumLogLineCharacters = 8 * 1024;
    public const int MaximumLogOutputCharacters = 64 * 1024;

    [GeneratedRegex(@"\b(password|token|secret)\b\s*[=:]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    /// <summary>Mask credential assignments in a single line of text.</summary>
    public static string Redact(string line) =>
        SecretPattern().Replace(line, "$1=<redacted>");

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

        // Project summary (metadata only — the mapping graph itself stays private).
        try
        {
            var pm = new ProjectManager(projectPath);
            var project = pm.Load();
            sb.Append("project: ").Append(project.Name)
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
        var entry = journal.Load(runId);
        sb.Append("run: ").Append(runId).Append('\n');
        if (entry is null)
            sb.Append("journal: no entry for run\n");
        else
        {
            sb.Append("journal-state: ").Append(entry.State).Append('\n');
            sb.Append("destination: ").Append(entry.DestinationPath).Append('\n');
            sb.Append("staged-digest: ").Append(entry.StagedDigest ?? "-").Append('\n');
            sb.Append("final-digest: ").Append(entry.FinalDigest ?? "-").Append('\n');
            sb.Append("lock-token: ").Append(entry.LockToken ?? "-").Append('\n');
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
}
