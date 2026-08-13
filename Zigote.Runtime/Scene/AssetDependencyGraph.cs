using Zigote.Core.Assets;
using Zigote.Vfx;

namespace Zigote.Runtime.Scene;

/// <summary>One scene reference to an asset file: which node needs it and in what role.</summary>
public sealed record AssetDependency(string Path, SceneNode? Node, string Role);

/// <summary>
///     The set of asset files a scene actually reaches, with reverse lookup (path → dependents).
///     Game export stages only these files instead of the whole asset tree; the editor can use
///     <see cref="DependentsOf" /> for safe-delete warnings and batch updates.
/// </summary>
public sealed class AssetDependencyGraph
{
    // Paths are kept exactly as the scene spells them (ordinal): the runtime opens files by that
    // string, so staging must preserve the spelling even on case-insensitive source filesystems.
    private readonly Dictionary<string, List<AssetDependency>>
        _byPath = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Files => _byPath.Keys;

    public IReadOnlyList<AssetDependency> DependentsOf(string path) =>
        _byPath.TryGetValue(key: path, value: out var list) ? list : [];

    /// <summary>Reverse lookup by stable asset id, resolved through the registry (rename-proof).</summary>
    public IReadOnlyList<AssetDependency> DependentsOf(AssetId id, AssetRegistry registry) =>
        registry.Resolve(id) is { } path ? DependentsOf(path) : [];

    public static AssetDependencyGraph Build(SceneGraph scene)
    {
        var graph = new AssetDependencyGraph();
        graph.Add(path: scene.EnvironmentPath, node: null, role: "environment");
        Walk(graph: graph, node: scene.Root);
        return graph;
    }

    /// <summary>
    ///     Add a detached subtree's references — e.g. a <c>.prefab</c> template, so export also stages
    ///     the assets of prefabs that are only spawned at runtime via the World scripting API.
    /// </summary>
    public void AddTree(SceneNode root) => Walk(graph: this, node: root);

    private static void Walk(AssetDependencyGraph graph, SceneNode node)
    {
        graph.Add(path: node.MeshPath, node: node, role: "mesh");
        graph.Add(
            path: node.TexturePath,
            node: node,
            role: "texture"
        ); // Mesh material map AND Sprite-node texture
        graph.Add(path: node.MetallicRoughnessTexturePath, node: node, role: "texture-mr");
        graph.Add(path: node.NormalTexturePath, node: node, role: "texture-normal");
        graph.Add(path: node.AudioClipPath, node: node, role: "audio");
        graph.Add(
            path: node.SpriteShaderPath,
            node: node,
            role: "sprite-shader"
        ); // custom 2D material WGSL

        // Baked VFX emitters may reference a sprite texture inside their emitter-asset JSON.
        if (!string.IsNullOrEmpty(node.VfxBakedJson))
        {
            try
            {
                graph.Add(
                    path: VfxAssetJson.Deserialize(node.VfxBakedJson).TexturePath,
                    node: node,
                    role: "vfx-texture"
                );
            }
            catch (Exception)
            {
                // A corrupt baked blob fails loudly at runtime; dependency collection stays best-effort.
            }
        }

        foreach (var child in node.Children) Walk(graph: graph, node: child);
    }

    private void Add(string? path, SceneNode? node, string role)
    {
        // '#'-prefixed paths are built-in primitives (#cube/#sphere/…), not files.
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('#')) return;
        if (!_byPath.TryGetValue(key: path, value: out var list)) _byPath[path] = list = [];
        list.Add(new AssetDependency(Path: path, Node: node, Role: role));
    }
}
