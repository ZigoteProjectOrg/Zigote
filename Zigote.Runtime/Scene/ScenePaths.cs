using Zigote.Core.Assets;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Rewrites a subtree's asset references to the canonical <see cref="AssetPath" /> form
///     (project-relative, forward-slashed) so scenes stay machine- and OS-agnostic. Runs after model
///     import — the one place absolute paths enter the system (the native manifest returns them).
/// </summary>
public static class ScenePaths
{
    /// <summary>
    ///     Normalize every asset path under <paramref name="root" />. Returns the number of
    ///     rewritten paths; absolute paths outside the project are reported and left unchanged.
    /// </summary>
    public static int Normalize(SceneNode root, string projectRoot, Action<string>? warn = null)
    {
        var count = 0;
        Walk(
            root,
            projectRoot,
            warn,
            ref count
        );
        return count;
    }

    /// <summary>Scene-level overload: also covers <see cref="SceneGraph.EnvironmentPath" />.</summary>
    public static int Normalize(SceneGraph scene, string projectRoot, Action<string>? warn = null)
    {
        var count = 0;
        scene.EnvironmentPath = Fix(
            scene.EnvironmentPath,
            "EnvironmentPath",
            projectRoot,
            warn,
            ref count
        );
        Walk(
            scene.Root,
            projectRoot,
            warn,
            ref count
        );
        return count;
    }

    private static void Walk(SceneNode n, string projectRoot, Action<string>? warn, ref int count)
    {
        n.MeshPath = Fix(
            n.MeshPath,
            $"{n.Name}.MeshPath",
            projectRoot,
            warn,
            ref count
        );
        n.TexturePath = Fix(
            n.TexturePath,
            $"{n.Name}.TexturePath",
            projectRoot,
            warn,
            ref count
        );
        n.MetallicRoughnessTexturePath =
            Fix(
                n.MetallicRoughnessTexturePath,
                $"{n.Name}.MetallicRoughnessTexturePath",
                projectRoot,
                warn,
                ref count
            );
        n.NormalTexturePath = Fix(
            n.NormalTexturePath,
            $"{n.Name}.NormalTexturePath",
            projectRoot,
            warn,
            ref count
        );
        n.AudioClipPath = Fix(
            n.AudioClipPath,
            $"{n.Name}.AudioClipPath",
            projectRoot,
            warn,
            ref count
        );
        foreach (var c in n.Children)
            Walk(
                c,
                projectRoot,
                warn,
                ref count
            );
    }

    private static string? Fix(string? p, string what, string projectRoot, Action<string>? warn,
        ref int count)
    {
        if (string.IsNullOrEmpty(p) || AssetPath.IsBuiltinPrimitive(p)) return p;

        // Rootedness must be checked before ToRelative — it trims the leading '/' from unix paths,
        // which would silently "relativize" a path that points outside the project.
        if (Path.IsPathRooted(p))
        {
            var full = Path.GetFullPath(p);
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!full.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                ))
            {
                warn?.Invoke(
                    $"absolute path outside the project (unportable, left as-is): {what} = {p}"
                );
                return p;
            }
        }

        var relative = AssetPath.ToRelative(p, projectRoot);
        if (!string.Equals(relative, p, StringComparison.Ordinal)) count++;
        return relative;
    }
}