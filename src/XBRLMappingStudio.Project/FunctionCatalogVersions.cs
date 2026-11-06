namespace XBRLMappingStudio.Project;

/// <summary>Version identifiers and compatibility rules for the declarative function catalog.</summary>
public static class FunctionCatalogVersions
{
    public const string Current = "1.0";

    /// <summary>
    /// Older projects defaulted to "1" before the catalog adopted semantic-version text.
    /// Treat that persisted value as the current 1.0 catalog; other mismatches fail closed.
    /// </summary>
    public static bool IsCompatible(string declaredVersion, string availableVersion)
        => string.Equals(declaredVersion, availableVersion, StringComparison.Ordinal)
            || (string.Equals(declaredVersion, "1", StringComparison.Ordinal)
                && string.Equals(availableVersion, Current, StringComparison.Ordinal));
}
