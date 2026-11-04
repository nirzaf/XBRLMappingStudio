using System.Security.Cryptography;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.XBRL.Tests;

public sealed class SyntheticProfileTests
{
    [Fact]
    public void ProfileIdentityBindsTheCheckedInArelleTaxonomyBytes()
    {
        var profile = SyntheticProfile.Build();
        var taxonomyPath = Path.Combine(
            FixturePaths.Directory,
            "taxonomy",
            SyntheticProfile.TaxonomyFileName);
        var actualDigest = "sha256:" + Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(taxonomyPath))).ToLowerInvariant();

        Assert.Equal(SyntheticProfile.TaxonomyPackageDigest, actualDigest);
        Assert.Equal(actualDigest, profile.PackageHashes[SyntheticProfile.TaxonomyFileName]);
        Assert.Contains(SyntheticProfile.TaxonomyFileName, profile.TaxonomyUrls);
        Assert.NotEqual(profile.Digest, profile.PackageHashes["profile-concept-table"]);
    }
}
