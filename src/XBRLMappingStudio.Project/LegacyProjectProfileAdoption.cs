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
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                "Legacy project is not valid UTF-8 JSON and was not changed.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                    "Legacy project root must be a JSON object.");

            var existingProfileId = ReadIdentity(document.RootElement, "profileId");
            var existingProfileDigest = ReadIdentity(document.RootElement, "profileDigest");
            var missingFields = new List<string>(2);
            if (existingProfileId.IsMissing) missingFields.Add("profileId");
            if (existingProfileDigest.IsMissing) missingFields.Add("profileDigest");
            if (missingFields.Count == 0)
                throw new InvalidOperationException(
                    "Project already declares a complete profile identity; ordinary project loading must be used.");

            if (!existingProfileId.IsMissing
                && !StringComparer.Ordinal.Equals(existingProfileId.Value, selectedProfileId))
                throw new InvalidOperationException(
                    "The selected profile ID conflicts with the identity already declared by the project.");
            if (!existingProfileDigest.IsMissing
                && !StringComparer.Ordinal.Equals(existingProfileDigest.Value, selectedProfileDigest))
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
            return new LegacyProjectProfileAdoptionDraft(independentCopy, missingFields.AsReadOnly());
        }
    }

    private static IdentityValue ReadIdentity(JsonElement root, string propertyName)
    {
        var matches = root.EnumerateObject().Where(property =>
            StringComparer.Ordinal.Equals(property.Name, propertyName)).ToArray();
        if (matches.Length > 1)
            throw new ProjectSchemaException(DiagnosticCodes.PrjCorruptFile,
                $"Legacy project contains duplicate '{propertyName}' fields; profile adoption is ambiguous.");
        if (matches.Length == 0) return new IdentityValue(null, IsMissing: true);

        var value = matches[0].Value;
        return value.ValueKind switch
        {
            JsonValueKind.Null => new IdentityValue(null, IsMissing: true),
            JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString())
                => new IdentityValue(null, IsMissing: true),
            JsonValueKind.String => new IdentityValue(value.GetString(), IsMissing: false),
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

    private readonly record struct IdentityValue(string? Value, bool IsMissing);
}

public sealed record LegacyProjectProfileAdoptionDraft(
    MappingProject Project,
    IReadOnlyList<string> AddedIdentityFields);
