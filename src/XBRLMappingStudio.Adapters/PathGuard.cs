using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters;

/// <summary>
/// Canonicalizes candidate paths and rejects any that escape the approved workspace
/// root (OPS04). Symlinks in every existing path component are resolved, including a
/// parent of a not-yet-created output, so an internal link cannot redirect a write.
/// Adapters must route every user- or project-supplied path through this gate.
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Returns the canonical absolute path guaranteed to live under <paramref name="workspaceRoot"/>.
    /// Throws <see cref="AdapterException"/> with <see cref="DiagnosticCodes.IoPathEscapesWorkspace"/>
    /// on traversal (dot-dot), absolute paths outside the root, or symlink escape.
    /// </summary>
    public static string EnsureWithin(string workspaceRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            throw Escape(workspaceRoot, candidatePath, "empty path");

        var root = ResolveLinks(CanonicalizeFull(Full(workspaceRoot, null)), workspaceRoot, candidatePath);
        var candidate = ResolveLinks(CanonicalizeFull(Full(candidatePath, root)), workspaceRoot, candidatePath);

        if (!IsUnder(root, candidate))
            throw Escape(workspaceRoot, candidatePath, candidate);

        return candidate;
    }

    /// <summary>True when the path is inside the root without throwing (probe API for UIs).</summary>
    public static bool IsWithin(string workspaceRoot, string candidatePath)
    {
        try { EnsureWithin(workspaceRoot, candidatePath); return true; }
        catch (AdapterException) { return false; }
    }

    private static string Full(string path, string? root) =>
        Path.GetFullPath(path, root ?? Directory.GetCurrentDirectory());

    /// <summary>
    /// Normalizes the path text without touching the filesystem: collapses '.'/'..',
    /// trailing separators, and (on Windows-style comparisons) case is left to IsUnder.
    /// </summary>
    private static string CanonicalizeFull(string path)
    {
        // Path.GetFullPath already collapses dot/dot-dot segments and trims; normalize the
        // final separator so "/a/b" and "/a/b/" compare equal.
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }

    private static bool IsUnder(string root, string candidate)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(root, candidate, comparer)) return true;
        return candidate.Length > root.Length
            && candidate.StartsWith(root + Path.DirectorySeparatorChar, comparer);
    }

    private static string ResolveLinks(string path, string workspaceRoot, string candidatePath)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            foreach (FileSystemInfo info in new FileSystemInfo[] { new DirectoryInfo(current), new FileInfo(current) })
            {
                try
                {
                    if (info.LinkTarget is null) continue;
                    var target = info.ResolveLinkTarget(true)
                        ?? throw new IOException("Symbolic link has no resolvable target.");
                    current = Path.GetFullPath(target.FullName);
                    break;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException ex)
                {
                    throw Escape(workspaceRoot, candidatePath, $"symlink target cannot be resolved ({ex.Message})");
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw Escape(workspaceRoot, candidatePath, $"symlink target cannot be inspected ({ex.Message})");
                }
            }
        }
        return CanonicalizeFull(current);
    }

    private static AdapterException Escape(string root, string candidate, string detail) =>
        new(DiagnosticCodes.IoPathEscapesWorkspace,
            $"Path '{candidate}' resolves outside the approved workspace root '{root}' ({detail}).",
            DiagnosticStage.Validation);
}
