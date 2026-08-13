using Zigote.Runtime.Scene;

namespace Zigote.Editor.Assets;

/// <summary>
///     Rewrites every scene reference to a renamed/moved asset path — the scene-side half of
///     rename-awareness (the registry side is <c>AssetRegistry.RenamePath</c>). Pure tree walk,
///     headless-testable; property setters push to native only for nodes with live handles.
/// </summary>
public static class AssetReferenceRewriter
{
    /// <summary>Rewrite all references in a scene (nodes + environment). Returns true if anything changed.</summary>
    public static bool RewriteScene(SceneGraph scene, string oldRelativePath,
        string newRelativePath)
    {
        var changed = Rewrite(scene.Root, oldRelativePath, newRelativePath);
        if (Matches(scene.EnvironmentPath, oldRelativePath))
        {
            scene.EnvironmentPath = newRelativePath;
            changed = true;
        }

        return changed;
    }

    /// <summary>Rewrite all references in a subtree. Returns true if anything changed.</summary>
    public static bool Rewrite(SceneNode node, string oldRelativePath, string newRelativePath)
    {
        var changed = false;

        if (Matches(node.MeshPath, oldRelativePath))
        {
            node.MeshPath = newRelativePath;
            changed = true;
        }

        if (Matches(node.TexturePath, oldRelativePath))
        {
            node.TexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(node.MetallicRoughnessTexturePath, oldRelativePath))
        {
            node.MetallicRoughnessTexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(node.NormalTexturePath, oldRelativePath))
        {
            node.NormalTexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(node.EmissiveTexturePath, oldRelativePath))
        {
            node.EmissiveTexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(node.AudioClipPath, oldRelativePath))
        {
            node.AudioClipPath = newRelativePath;
            changed = true;
        }

        if (Matches(node.SpriteShaderPath, oldRelativePath))
        {
            node.SpriteShaderPath = newRelativePath;
            changed = true;
        }

        if (Matches(node.ScriptPath, oldRelativePath))
        {
            node.ScriptPath = newRelativePath;
            changed = true;
        }

        for (var i = 0; i < node.Children.Count; i++)
            changed |= Rewrite(node.Children[i], oldRelativePath, newRelativePath);

        return changed;
    }

    private static bool Matches(string? candidate, string oldRelativePath)
    {
        return !string.IsNullOrEmpty(candidate) &&
               string.Equals(
                   candidate.Replace('\\', '/'),
                   oldRelativePath,
                   StringComparison.OrdinalIgnoreCase
               );
    }
}
