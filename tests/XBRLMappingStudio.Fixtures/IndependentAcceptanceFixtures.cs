using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XBRLMappingStudio.Fixtures;

/// <summary>
/// Immutable, hand-authored source and report fixtures shared by the application test
/// suites. The manifest digest is pinned in code so changing an expected file digest
/// also requires an explicit, reviewable fixture revision.
/// </summary>
public static class IndependentAcceptanceFixtures
{
    public const string ManifestSha256 = "sha256:67b357e40f52d280067fda89a966421850f81dacd3c7e6b27dea8728c388db60";
    private const string ResourcePrefix = "XBRLMappingStudio.Fixtures.AcceptanceFixtures.";

    public static byte[] ReadBytes(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Acceptance fixture names must be simple file names.", nameof(fileName));

        using var stream = typeof(IndependentAcceptanceFixtures).Assembly
            .GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new FileNotFoundException($"Acceptance fixture '{fileName}' is not embedded.", fileName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string ReadText(string fileName)
        => Encoding.UTF8.GetString(ReadBytes(fileName));

    public static string Materialize(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, ReadBytes(fileName));
        return path;
    }

    public static string Digest(ReadOnlySpan<byte> bytes)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static IReadOnlyList<string> VerifyIntegrity()
    {
        var errors = new List<string>();
        var manifestBytes = ReadBytes("fixture-manifest.json");
        if (!StringComparer.Ordinal.Equals(Digest(manifestBytes), ManifestSha256))
            errors.Add("fixture-manifest.json digest differs from its pinned value.");

        using var manifest = JsonDocument.Parse(manifestBytes);
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var actual = Digest(ReadBytes(entry.Name));
            if (!StringComparer.Ordinal.Equals(actual, entry.Value.GetString()))
                errors.Add($"{entry.Name} digest differs from fixture-manifest.json.");
        }

        return errors;
    }
}
