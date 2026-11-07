namespace XBRLMappingStudio.Application;

/// <summary>
/// Prevents a file publication target from replacing one of the files used as an
/// input to the same operation. Paths are compared after resolving existing
/// symbolic links so an alias cannot bypass the guard.
/// </summary>
public static class DestinationPathGuard
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string? FindSourceIdAtPath(
        string destinationPath,
        IEnumerable<(string SourceId, string? Path)> sources,
        string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var root = Path.GetFullPath(workspaceRoot);
        var target = NormalizePath(destinationPath, root);
        foreach (var (sourceId, sourcePath) in sources)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) continue;
            if (PathComparer.Equals(target, NormalizePath(sourcePath, root)))
                return sourceId;
        }

        return null;
    }

    public static bool AreSamePath(string leftPath, string rightPath, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var root = Path.GetFullPath(workspaceRoot);
        return PathComparer.Equals(
            NormalizePath(leftPath, root),
            NormalizePath(rightPath, root));
    }

    private static string NormalizePath(string path, string workspaceRoot)
    {
        var fullPath = Path.GetFullPath(path, workspaceRoot);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The path must have a filesystem root.", nameof(path));
        var current = pathRoot;
        var segments = fullPath[pathRoot.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            if (File.Exists(candidate))
            {
                var file = new FileInfo(candidate);
                current = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
            }
            else if (Directory.Exists(candidate))
            {
                var directory = new DirectoryInfo(candidate);
                current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
            }
            else
            {
                current = candidate;
            }
        }

        return Path.GetFullPath(current);
    }
}
