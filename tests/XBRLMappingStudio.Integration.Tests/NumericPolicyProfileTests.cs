using System.Text.Json;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class NumericPolicyProfileTests
{
    [Theory]
    [InlineData(6, "reject", true)]
    [InlineData(5, "reject", false)]
    [InlineData(6, "nearest", false)]
    public void CatalogValidatesVersionedNumericPolicy(int schemaVersion, string roundingMode, bool accepted)
    {
        var root = FilingProfileResolverTests.NewCatalogDirectory();
        try
        {
            var installed = FilingProfileResolverTests.WriteInstalledProfile(root, "numeric-fixture", "1.0.0", "Revenue");
            var path = Path.Combine(installed.InstallationDirectory, InstalledFilingProfileCatalog.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<FilingProfileManifest>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            manifest = manifest with {
                SchemaVersion = schemaVersion,
                Concepts = new() { manifest.Concepts[0] with {
                    NumericPolicy = new() { RoundingDecimals = -3, RoundingMode = roundingMode } } } };
            manifest = manifest with { ProfileDigest = manifest.ComputeDigest() };
            File.WriteAllText(path, manifest.ToJson());
            var catalog = InstalledFilingProfileCatalog.Load(root);
            Assert.Equal(accepted, catalog.Profiles.Count == 1);
            if (accepted)
                Assert.Equal(-3, catalog.Profiles.Single().Profile.Concepts.Single().NumericPolicy!.RoundingDecimals);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
