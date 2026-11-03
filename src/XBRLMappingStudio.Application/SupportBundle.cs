using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Redacts credential-looking values and builds a shareable diagnostic bundle (OPS04,
/// OPS08-partial). Any <c>password=</c>, <c>token=</c> or <c>secret=</c> assignment in
/// a rendered line is masked before it leaves the process; the bundle carries the run
/// journal receipt, a project summary and redacted log lines — never raw payloads.
/// Full scheduler integration remains owner-gated (needs the customer scheduler).
/// </summary>
public static partial class SupportBundle
{
    [GeneratedRegex(@"\b(password|token|secret)\b\s*[=:]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    /// <summary>Mask credential assignments in a single line of text.</summary>
    public static string Redact(string line) =>
        SecretPattern().Replace(line, "$1=<redacted>");

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

        // Redacted log tail.
        sb.Append("--- logs (redacted) ---\n");
        foreach (var line in logLines ?? Array.Empty<string>())
            sb.Append(Redact(line)).Append('\n');

        return sb.ToString();
    }
}
