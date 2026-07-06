namespace Zigote.Core.Assets;

/// <summary>
///     Canonicalises asset paths to the form <see cref="AssetRegistry" /> keys on:
///     <b>project-relative, forward-slashed</b>. The registry compares case-insensitively, so
///     the only thing that must be pinned is relative-vs-absolute and the separator — otherwise
///     the same file registered once as an absolute path and once as a relative one mints two
///     distinct <see cref="AssetId" />s for identical content.
///     <para>
///         Built-in primitive sentinels (<c>#cube</c>/<c>#quad</c>/<c>#sphere</c>/<c>#cylinder</c>)
///         are not files and must never be registered — <see cref="IsBuiltinPrimitive" /> guards that.
///     </para>
/// </summary>
public static class AssetPath
{
    /// <summary>True for the <c>#name</c> built-in mesh primitives that have no backing file.</summary>
    public static bool IsBuiltinPrimitive(string? path)
    {
        return !string.IsNullOrEmpty(path) && path[0] == '#';
    }

    /// <summary>
    ///     Normalise <paramref name="path" /> to a project-relative, forward-slashed key. Absolute
    ///     paths under <paramref name="contentRoot" /> are made relative to it; paths already
    ///     relative are only separator-normalised. Built-in primitives pass through unchanged.
    /// </summary>
    public static string ToRelative(string path, string? contentRoot)
    {
        if (IsBuiltinPrimitive(path)) return path;

        var normalized = path.Replace('\\', '/');

        if (Path.IsPathRooted(normalized) && !string.IsNullOrEmpty(contentRoot))
        {
            var rootFull = Path.GetFullPath(contentRoot).Replace('\\', '/').TrimEnd('/');
            var pathFull = Path.GetFullPath(path).Replace('\\', '/');
            if (pathFull.StartsWith(rootFull + "/", StringComparison.OrdinalIgnoreCase))
                normalized = pathFull[(rootFull.Length + 1)..];
        }

        return normalized.TrimStart('/');
    }

    /// <summary>
    ///     Resolve a project-relative key back to an absolute filesystem path under
    ///     <paramref name="contentRoot" />. Built-in primitives and already-absolute paths pass through.
    /// </summary>
    public static string ToAbsolute(string relativePath, string? contentRoot)
    {
        if (IsBuiltinPrimitive(relativePath)) return relativePath;
        if (Path.IsPathRooted(relativePath) || string.IsNullOrEmpty(contentRoot))
            return relativePath;
        return Path.GetFullPath(Path.Combine(contentRoot, relativePath));
    }
}