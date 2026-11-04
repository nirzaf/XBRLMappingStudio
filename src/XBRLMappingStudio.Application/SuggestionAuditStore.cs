using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.AI;

namespace XBRLMappingStudio.Application;

/// <summary>
/// AI06 durable audit trail. One row per analyst decision (accept, reject, edit,
/// batch-accept, undo) keyed by event id and stamped with project id + semantic
/// revision + governed actor + prompt-template revision, plus a bounded rendering
/// of the original proposal and the approved edit. Raw payloads are never stored
/// here — the "OriginalProposalJson"/"ApprovedEditJson" columns carry only the
/// draft mapping fields the analyst saw (concept, unit, decimals, period type,
/// language). The rendered form is what <see cref="AuditLog.Scan"/> later scans
/// for canaries, so any prohibited value that leaked into this surface would be
/// caught by the same test that guards the in-memory log.
/// </summary>
public sealed class SuggestionAuditStore : IAuditSink
{
    private readonly LocalApplicationDatabase _database;

    public SuggestionAuditStore(LocalApplicationDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public void Append(AuditRecord record, AuditContext context)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.ProjectId))
            throw new ArgumentException("Audit row requires a project id.", nameof(context));

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO suggestion_audit (
                event_id, project_id, semantic_revision, source_field_ref,
                candidate_ids_json, provider_config_id, decision, accepted_revision,
                occurred_utc, method, actor, prompt_template_revision,
                original_proposal_json, approved_edit_json, provider_snapshot_limitation)
            VALUES (
                $event_id, $project_id, $semantic_revision, $source_field_ref,
                $candidate_ids_json, $provider_config_id, $decision, $accepted_revision,
                $occurred_utc, $method, $actor, $prompt_template_revision,
                $original_proposal_json, $approved_edit_json, $provider_snapshot_limitation);
            """;
        command.Parameters.AddWithValue("$event_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$project_id", context.ProjectId);
        command.Parameters.AddWithValue("$semantic_revision", context.SemanticRevision);
        command.Parameters.AddWithValue("$source_field_ref", record.SourceField);
        command.Parameters.AddWithValue("$candidate_ids_json",
            JsonSerializer.Serialize(context.CandidateIds ?? Array.Empty<string>()));
        command.Parameters.AddWithValue("$provider_config_id", context.ProviderConfigId ?? "");
        command.Parameters.AddWithValue("$decision", record.Action);
        command.Parameters.AddWithValue("$accepted_revision",
            record.Action == "accept" || record.Action == "batch-accept"
                ? (object?)context.SemanticRevision : DBNull.Value);
        command.Parameters.AddWithValue("$occurred_utc",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$method", context.Method ?? "retrieval");
        command.Parameters.AddWithValue("$actor", context.Actor ?? "");
        command.Parameters.AddWithValue("$prompt_template_revision",
            context.PromptTemplateRevision ?? "");
        command.Parameters.AddWithValue("$original_proposal_json",
            context.OriginalProposalJson ?? "");
        command.Parameters.AddWithValue("$approved_edit_json",
            context.ApprovedEditJson ?? "");
        command.Parameters.AddWithValue("$provider_snapshot_limitation",
            context.ProviderSnapshotLimitation ?? "");
        command.ExecuteNonQuery();
    }

    /// <summary>Read back the most recent audit rows for a project revision (bounded).</summary>
    public IReadOnlyList<SuggestionAuditRow> Read(string projectId, int semanticRevision, int limit = 100)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, project_id, semantic_revision, source_field_ref,
                   candidate_ids_json, provider_config_id, decision, accepted_revision,
                   occurred_utc, method, actor, prompt_template_revision,
                   original_proposal_json, approved_edit_json, provider_snapshot_limitation
            FROM suggestion_audit
            WHERE project_id = $project_id AND semantic_revision = $semantic_revision
            ORDER BY occurred_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project_id", projectId);
        command.Parameters.AddWithValue("$semantic_revision", semanticRevision);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<SuggestionAuditRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SuggestionAuditRow(
                EventId: reader.GetString(0),
                ProjectId: reader.GetString(1),
                SemanticRevision: reader.GetInt32(2),
                SourceFieldRef: reader.GetString(3),
                CandidateIds: ParseArray(reader.GetString(4)),
                ProviderConfigId: reader.GetString(5),
                Decision: reader.GetString(6),
                AcceptedRevision: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                OccurredUtc: reader.GetString(8),
                Method: reader.GetString(9),
                Actor: reader.GetString(10),
                PromptTemplateRevision: reader.GetString(11),
                OriginalProposalJson: reader.GetString(12),
                ApprovedEditJson: reader.GetString(13),
                ProviderSnapshotLimitation: reader.GetString(14)));
        }
        return rows;
    }

    /// <summary>Flatten every persisted row into the same text the in-memory log renders,
    /// so <see cref="AuditLog.Scan"/> canary checks apply to durable content too (AI06).</summary>
    public string RenderForScan()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT project_id, semantic_revision, source_field_ref, decision, method,
                   actor, prompt_template_revision, original_proposal_json,
                   approved_edit_json, provider_snapshot_limitation
            FROM suggestion_audit
            ORDER BY occurred_utc ASC;
            """;
        var builder = new System.Text.StringBuilder();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            builder.Append(JsonSerializer.Serialize(new
            {
                project = reader.GetString(0),
                revision = reader.GetInt32(1),
                field = reader.GetString(2),
                decision = reader.GetString(3),
                method = reader.GetString(4),
                actor = reader.GetString(5),
                template = reader.GetString(6),
                original = reader.GetString(7),
                approved = reader.GetString(8),
                limitation = reader.GetString(9),
            })).Append('\n');
        }
        return builder.ToString();
    }

    private static IReadOnlyList<string> ParseArray(string json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}

/// <summary>One persisted AI06 audit row.</summary>
public sealed record SuggestionAuditRow(
    string EventId,
    string ProjectId,
    int SemanticRevision,
    string SourceFieldRef,
    IReadOnlyList<string> CandidateIds,
    string ProviderConfigId,
    string Decision,
    int? AcceptedRevision,
    string OccurredUtc,
    string Method,
    string Actor,
    string PromptTemplateRevision,
    string OriginalProposalJson,
    string ApprovedEditJson,
    string ProviderSnapshotLimitation);
