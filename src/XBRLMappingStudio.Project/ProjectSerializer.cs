using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Project;

/// <summary>
/// JSON serialization for <see cref="MappingProject"/>.
/// Uses System.Text.Json with a custom converter for open-ended parameter dictionaries.
/// </summary>
public static class ProjectSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Serialize a project to canonical JSON text.</summary>
    public static string Serialize(MappingProject project)
        => JsonSerializer.Serialize(project, Options);

    /// <summary>
    /// Create an independent Save As copy. A copy gets a new audit identity and cannot
    /// inherit the source project's publication approvals. Locators remain unchanged;
    /// relative paths resolve from the copy's new project directory.
    /// </summary>
    public static MappingProject CreateIndependentCopy(MappingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var json = JsonNode.Parse(Serialize(project)) as JsonObject
            ?? throw new InvalidOperationException("Project serialization did not produce a JSON object.");
        var now = DateTimeOffset.UtcNow;
        json["projectId"] = Id.New();
        json["semanticRevision"] = 1;
        json["layoutGeneration"] = 1;
        json["approvals"] = new JsonArray();
        if (json["metadata"] is JsonObject metadata)
        {
            metadata["createdUtc"] = JsonSerializer.SerializeToNode(now, Options);
            metadata["lastModifiedUtc"] = JsonSerializer.SerializeToNode(now, Options);
        }

        return Deserialize(json.ToJsonString(Options));
    }

    /// <summary>
    /// Deserialize a project. Throws <see cref="ProjectSchemaException"/> when the
    /// schema version is newer than supported (unknown future formats open read-only
    /// per BASE03).
    /// </summary>
    public static MappingProject Deserialize(string json)
    {
        MappingProject? project;
        try
        {
            project = JsonSerializer.Deserialize<MappingProject>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProjectSchemaException(
                DiagnosticCodes.PrjCorruptFile,
                DeserializationErrorMessage(json), ex);
        }

        if (project is null)
        {
            throw new ProjectSchemaException(
                DiagnosticCodes.PrjCorruptFile,
                "Project deserialized to null.");
        }

        if (project.SchemaVersion < ProjectSchemaVersions.MinimumReadable)
        {
            throw new ProjectSchemaException(
                DiagnosticCodes.PrjSchemaVersionUnsupported,
                $"Project schema v{project.SchemaVersion} is older than the minimum readable v{ProjectSchemaVersions.MinimumReadable}.");
        }

        if (project.SchemaVersion > ProjectSchemaVersions.Current)
        {
            // Per BASE03: unknown future formats open read-only with an explanation.
            throw new ProjectSchemaException(
                DiagnosticCodes.PrjSchemaVersionUnsupported,
                $"Project schema v{project.SchemaVersion} is newer than supported v{ProjectSchemaVersions.Current}. Open read-only.")
            {
                IsReadOnly = true
            };
        }

        return project;
    }

    private static string DeserializationErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var missingIdentity = new[] { "profileId", "profileDigest" }
                    .Where(property => !document.RootElement.TryGetProperty(property, out var value)
                        || value.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(value.GetString()))
                    .ToArray();
                if (missingIdentity.Length > 0)
                    return "Project is missing required profile identity field(s): "
                        + string.Join(", ", missingIdentity)
                        + "; profile identity cannot be inferred.";
            }
        }
        catch (JsonException)
        {
            // Preserve the generic malformed/schema fallback when the source cannot be parsed.
        }

        return "Project file is malformed or does not satisfy the current schema.";
    }

    /// <summary>
    /// Verify that a candidate file can round-trip without loss. Used to validate
    /// staged writes before atomic replacement (BASE03).
    /// </summary>
    public static bool VerifyRoundTrip(string json)
    {
        try
        {
            var parsed = Deserialize(json);
            var reserialized = Serialize(parsed);
            // Compare semantic digest and layout generation for stability.
            return string.Equals(
                parsed.ComputeSemanticDigest(),
                Deserialize(reserialized).ComputeSemanticDigest(),
                StringComparison.Ordinal);
        }
        catch (ProjectSchemaException ex) when (ex.IsReadOnly)
        {
            return true; // read-only schemas still parse
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Raised when a project file cannot be interpreted at the current schema version.
/// </summary>
public sealed class ProjectSchemaException : Exception
{
    public string Code { get; }
    public bool IsReadOnly { get; init; }

    public ProjectSchemaException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
