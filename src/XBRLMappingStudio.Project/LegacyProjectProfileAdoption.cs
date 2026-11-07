using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Project;

/// <summary>
/// Explicitly binds a legacy project that lacks one or both profile identity fields
/// to a caller-selected profile. The original document is never modified.
/// </summary>
public static class LegacyProjectProfileAdoption
{
    public static LegacyProjectProfileIdentityInspection InspectIdentity(ReadOnlySpan<byte> originalBytes)
    {
        var json = DecodeUtf8(originalBytes);
        using var document = ParseDocument(json);
        return InspectIdentity(document.RootElement);
    }

    public static LegacyProjectProfileAdoptionDraft CreateDraft(
        ReadOnlySpan<byte> originalBytes,
        string selectedProfileId,
        string selectedProfileDigest)
    {
        if (string.IsNullOrWhiteSpace(selectedProfileId))
            throw new ArgumentException("An explicit profile ID is required.", nameof(selectedProfileId));
        if (string.IsNullOrWhiteSpace(selectedProfileDigest))
            throw new ArgumentException("An explicit profile digest is required.", nameof(selectedProfileDigest));

        var json = DecodeUtf8(originalBytes);
        using var document = ParseDocument(json);
        var identity = InspectIdentity(document.RootElement);
        if (identity.ExistingProfileId is not null
            && !StringComparer.Ordinal.Equals(identity.ExistingProfileId, selectedProfileId))
            throw new InvalidOperationException(
                "The selected profile ID conflicts with the identity already declared by the project.");
        if (identity.ExistingProfileDigest is not null
            && !StringComparer.Ordinal.Equals(identity.ExistingProfileDigest, selectedProfileDigest))
            throw new InvalidOperationException(
                "The selected profile digest conflicts with the identity already declared by the project.");

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                ?? throw new JsonException("Project root must be an object.");
        }
        catch (JsonException ex)
        {
            throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                "Legacy project could not be copied for profile adoption.", ex);
        }

        root["profileId"] = selectedProfileId;
        root["profileDigest"] = selectedProfileDigest;
        var identityBoundJson = root.ToJsonString(ProjectSerializer.Options);

        EnsureNoUnsupportedFields(identityBoundJson);
        var project = ProjectSerializer.Deserialize(identityBoundJson);
        var independentCopy = ProjectSerializer.CreateIndependentCopy(project);
        return new LegacyProjectProfileAdoptionDraft(independentCopy, identity.MissingFields);
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                "Legacy project is not valid UTF-8 JSON and was not changed.", ex);
        }
    }

    private static LegacyProjectProfileIdentityInspection InspectIdentity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                "Legacy project root must be a JSON object.");

        var existingProfileId = ReadIdentity(root, "profileId");
        var existingProfileDigest = ReadIdentity(root, "profileDigest");
        var missingFields = new List<string>(2);
        if (existingProfileId is null) missingFields.Add("profileId");
        if (existingProfileDigest is null) missingFields.Add("profileDigest");
        if (missingFields.Count == 0)
            throw new InvalidOperationException(
                "Project already declares a complete profile identity; ordinary project loading must be used.");

        return new LegacyProjectProfileIdentityInspection(
            existingProfileId,
            existingProfileDigest,
            missingFields.AsReadOnly());
    }

    private static string? ReadIdentity(JsonElement root, string propertyName)
    {
        var matches = root.EnumerateObject().Where(property =>
            StringComparer.Ordinal.Equals(property.Name, propertyName)).ToArray();
        if (matches.Length > 1)
            throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                $"Legacy project contains duplicate '{propertyName}' fields; profile adoption is ambiguous.");
        if (matches.Length == 0) return null;

        var value = matches[0].Value;
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString())
                => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                $"Legacy project field '{propertyName}' must be a string or absent; it was not changed.")
        };
    }

    private static void EnsureNoUnsupportedFields(string json)
    {
        var strictOptions = new JsonSerializerOptions(ProjectSerializer.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        try
        {
            _ = JsonSerializer.Deserialize<MappingProject>(json, strictOptions)
                ?? throw new JsonException("Project deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new ProjectSchemaException(DiagnosticCodes.PrjSchemaVersionUnsupported,
                "Legacy project contains fields this version cannot safely preserve; profile adoption was refused.", ex);
        }
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        var offset = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes[offset..]);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                "Legacy project is not valid UTF-8 and was not changed.", ex);
        }
    }
}

public sealed record LegacyProjectProfileIdentityInspection(
    string? ExistingProfileId,
    string? ExistingProfileDigest,
    IReadOnlyList<string> MissingFields);

public sealed record LegacyProjectProfileAdoptionDraft(
    MappingProject Project,
    IReadOnlyList<string> AddedIdentityFields);
