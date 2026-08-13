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
        int count = 0;
        Walk(
            n: root,
            projectRoot: projectRoot,
            warn: warn,
            count: ref count
        );
        return count;
    }

    /// <summary>Scene-level overload: also covers <see cref="SceneGraph.EnvironmentPath" />.</summary>
    public static int Normalize(SceneGraph scene, string projectRoot, Action<string>? warn = null)
    {
        int count = 0;
        scene.EnvironmentPath = Fix(
            p: scene.EnvironmentPath,
            what: "EnvironmentPath",
            projectRoot: projectRoot,
            warn: warn,
            count: ref count
        );
        Walk(
            n: scene.Root,
            projectRoot: projectRoot,
            warn: warn,
            count: ref count
        );
        return count;
    }

    private static void Walk(SceneNode n, string projectRoot, Action<string>? warn, ref int count)
    {
        n.MeshPath = Fix(
            p: n.MeshPath,
            what: $"{n.Name}.MeshPath",
            projectRoot: projectRoot,
            warn: warn,
            count: ref count
        );
        n.TexturePath = Fix(
            p: n.TexturePath,
            what: $"{n.Name}.TexturePath",
            projectRoot: projectRoot,
            warn: warn,
            count: ref count
        );
        n.MetallicRoughnessTexturePath =
            Fix(
                p: n.MetallicRoughnessTexturePath,
                what: $"{n.Name}.MetallicRoughnessTexturePath",
                projectRoot: projectRoot,
                warn: warn,
                count: ref count
            );
        n.NormalTexturePath = Fix(
            p: n.NormalTexturePath,
            what: $"{n.Name}.NormalTexturePath",
            projectRoot: projectRoot,
            warn: warn,
            count: ref count
        );
        n.AudioClipPath = Fix(
            p: n.AudioClipPath,
            what: $"{n.Name}.AudioClipPath",
            projectRoot: projectRoot,
            warn: warn,
            count: ref count
        );
        foreach (var c in n.Children)
        {
            Walk(
                n: c,
                projectRoot: projectRoot,
                warn: warn,
                count: ref count
            );
        }
    }

    private static string? Fix(string? p, string what, string projectRoot, Action<string>? warn,
        ref int count)
    {
        if (string.IsNullOrEmpty(p) || AssetPath.IsBuiltinPrimitive(p)) return p;

        // Rootedness must be checked before ToRelative — it trims the leading '/' from unix paths,
        // which would silently "relativize" a path that points outside the project.
        if (Path.IsPathRooted(p))
        {
            string full = Path.GetFullPath(p);
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!full.StartsWith(
                    value: root + Path.DirectorySeparatorChar,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ))
            {
                warn?.Invoke(
                    $"absolute path outside the project (unportable, left as-is): {what} = {p}"
                );
                return p;
            }
        }

        string relative = AssetPath.ToRelative(path: p, contentRoot: projectRoot);
        if (!string.Equals(a: relative, b: p, comparisonType: StringComparison.Ordinal)) count++;
        return relative;
    }
}
