using System.Text.Json;
using System.Text.Json.Serialization;
using Zigote.Runtime.Scene;

namespace Zigote.Runtime.Prefab;

/// <summary>
///     The on-disk <c>.prefab</c> asset: a reusable <see cref="SceneNode" /> subtree template plus its
///     prefab name. The template captures the structural/string identity (name, kind, mesh path,
///     hierarchy) shared by every instance; the numeric per-property overrides live on the instances
///     (backed by the editor's <c>ScenePrefabLibrary</c> / <c>EcsPrefab</c>). Serialized with the same
///     <see cref="MathJson" /> options + <c>ReferenceHandler.Preserve</c> as a full scene, so a prefab
///     round-trips exactly like a subtree of a scene. Lives in the runtime (not the editor) so
///     exported
///     games can load and spawn prefabs at play time via the <c>World</c> scripting API.
/// </summary>
public sealed class PrefabDocument
{
    public const string Extension = ".prefab";

    [JsonInclude] public string Name { get; set; } = "Prefab";

    [JsonInclude] public SceneNode Template { get; set; } = new("Prefab");

    public void Save(string path) => File.WriteAllText(
        path: path,
        contents: JsonSerializer.Serialize(value: this, options: MathJson.SceneOptions(true))
    );

    public static PrefabDocument? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var doc = JsonSerializer.Deserialize<PrefabDocument>(
            json: File.ReadAllText(path),
            options: MathJson.SceneOptions(false)
        );
        if (doc is null) return null;
        RestoreParents(node: doc.Template, parent: null);
        return doc;
    }

    /// <summary>Build a fresh instance subtree from this template (a clean clone with no native handles).</summary>
    public SceneNode InstantiateNode() => Template.DeepClone();

    private static void RestoreParents(SceneNode node, SceneNode? parent)
    {
        node.Parent = parent;
        foreach (var child in node.Children)
            RestoreParents(node: child, parent: node);
    }
}
