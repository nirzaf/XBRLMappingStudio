namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// Resolves baseline fixtures copied next to the test assembly
/// (<c>fixtures/**/*.json</c> per the csproj <c>CopyToOutputDirectory</c>). Keeping
/// discovery here avoids hard-coding an absolute source-tree path that would not
/// survive a Release or CI output layout.
/// </summary>
internal static class FixturePaths
{
    public static string Directory { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string Resolve(string name) => System.IO.Path.Combine(Directory, name);

    public static string Read(string name) => File.ReadAllText(Resolve(name));
}
