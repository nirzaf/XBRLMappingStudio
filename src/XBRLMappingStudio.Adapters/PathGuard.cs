using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters;

/// <summary>
/// Canonicalizes candidate paths and rejects any that escape the approved workspace
/// root (OPS04). Symlinks are resolved when the target exists so a link planted inside
/// the root but pointing outside it cannot smuggle writes out of the sandbox.
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

        var root = CanonicalizeFull(Full(workspaceRoot, null));
        var candidate = CanonicalizeFull(Full(candidatePath, root));

        if (!IsUnder(root, candidate))
            throw Escape(workspaceRoot, candidatePath, candidate);

        // If the candidate exists (possibly via symlinks), its *final* target must also
        // live under the root — a mid-tree symlink can rewrite an external destination.
        var realTarget = ResolveFinalLinkTarget(candidate);
        if (realTarget is not null)
        {
            var realCanonical = CanonicalizeFull(realTarget);
            if (!IsUnder(root, realCanonical))
                throw Escape(workspaceRoot, candidatePath,
                    $"symlink resolves outside workspace to '{realCanonical}'");
        }

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

    private static string? ResolveFinalLinkTarget(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).ResolveLinkTarget(true)?.FullName;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName;
        }
        catch (IOException) { /* treat unresolvable as non-link */ }
        return null;
    }

    private static AdapterException Escape(string root, string candidate, string detail) =>
        new(DiagnosticCodes.IoPathEscapesWorkspace,
            $"Path '{candidate}' resolves outside the approved workspace root '{root}' ({detail}).",
            DiagnosticStage.Validation);
}
