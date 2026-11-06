namespace XBRLMappingStudio.XBRL.Tests;

internal static class TestFilingProfiles
{
    public const string UnitsPerShareConcept = "synthetic:UnitsPerShare";

    public static FilingProfile WithNumericCompoundUnitConcept()
    {
        var source = SyntheticProfile.Build();
        var compoundUnitConcept = new ConceptMetadata(
            UnitsPerShareConcept,
            SyntheticProfile.Namespace,
            "UnitsPerShare",
            "Units per share",
            "decimal",
            "duration",
            IsAbstract: false);
        return new FilingProfile
        {
            ProfileId = source.ProfileId,
            Digest = source.Digest,
            Concepts = source.Concepts.Append(compoundUnitConcept).ToArray(),
            SupportedUnits = source.SupportedUnits,
            TaxonomyVersion = source.TaxonomyVersion,
            DimensionDomains = source.DimensionDomains,
            PresentationLinks = source.PresentationLinks,
            OutputFormat = source.OutputFormat,
            NamespaceUris = source.NamespaceUris,
            TaxonomyUrls = source.TaxonomyUrls,
            PackageHashes = source.PackageHashes,
            RequiredPlugins = source.RequiredPlugins,
            RequiredChecks = source.RequiredChecks
        };
    }
}
