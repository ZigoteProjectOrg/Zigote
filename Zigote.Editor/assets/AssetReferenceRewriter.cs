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
        bool changed = Rewrite(
            node: scene.Root,
            oldRelativePath: oldRelativePath,
            newRelativePath: newRelativePath
        );
        if (Matches(candidate: scene.EnvironmentPath, oldRelativePath: oldRelativePath))
        {
            scene.EnvironmentPath = newRelativePath;
            changed = true;
        }

        return changed;
    }

    /// <summary>Rewrite all references in a subtree. Returns true if anything changed.</summary>
    public static bool Rewrite(SceneNode node, string oldRelativePath, string newRelativePath)
    {
        bool changed = false;

        if (Matches(candidate: node.MeshPath, oldRelativePath: oldRelativePath))
        {
            node.MeshPath = newRelativePath;
            changed = true;
        }

        if (Matches(candidate: node.TexturePath, oldRelativePath: oldRelativePath))
        {
            node.TexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(candidate: node.MetallicRoughnessTexturePath, oldRelativePath: oldRelativePath))
        {
            node.MetallicRoughnessTexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(candidate: node.NormalTexturePath, oldRelativePath: oldRelativePath))
        {
            node.NormalTexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(candidate: node.EmissiveTexturePath, oldRelativePath: oldRelativePath))
        {
            node.EmissiveTexturePath = newRelativePath;
            changed = true;
        }

        if (Matches(candidate: node.AudioClipPath, oldRelativePath: oldRelativePath))
        {
            node.AudioClipPath = newRelativePath;
            changed = true;
        }

        if (Matches(candidate: node.SpriteShaderPath, oldRelativePath: oldRelativePath))
        {
            node.SpriteShaderPath = newRelativePath;
            changed = true;
        }

        if (Matches(candidate: node.ScriptPath, oldRelativePath: oldRelativePath))
        {
            node.ScriptPath = newRelativePath;
            changed = true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            changed |= Rewrite(
                node: node.Children[i],
                oldRelativePath: oldRelativePath,
                newRelativePath: newRelativePath
            );
        }

        return changed;
    }

    private static bool Matches(string? candidate, string oldRelativePath)
    {
        return !string.IsNullOrEmpty(candidate) &&
               string.Equals(
                   a: candidate.Replace(oldChar: '\\', newChar: '/'),
                   b: oldRelativePath,
                   comparisonType: StringComparison.OrdinalIgnoreCase
               );
    }
}
