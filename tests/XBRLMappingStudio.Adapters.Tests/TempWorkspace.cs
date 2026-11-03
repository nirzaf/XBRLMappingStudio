namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>
/// Per-test isolated scratch directory that cleans itself up. Each instance owns a
/// unique temp folder so concurrent test execution never collides on paths.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public string Directory { get; }

    public TempWorkspace()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-adapters-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string PathFor(string name) => System.IO.Path.Combine(Directory, name);

    public void Write(string name, string content) => File.WriteAllText(PathFor(name), content);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
