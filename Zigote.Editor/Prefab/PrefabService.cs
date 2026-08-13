using Zigote.Core.Assets;
using Zigote.Runtime.Prefab;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Prefab;

/// <summary>
///     Editor-facing prefab asset operations: turn a <see cref="SceneNode" /> subtree into a
///     <c>.prefab</c> asset (registered for a stable <see cref="AssetId" />) and instantiate one back
///     into the scene. The structural/string template lives in the <c>.prefab</c> file; the numeric
///     override engine is <see cref="ScenePrefabLibrary" /> (flecs <c>EcsPrefab</c>). Pure file +
///     clone +
///     registry work — no native/ECS — so it is headless-testable.
/// </summary>
public sealed class PrefabService
{
    public const string PrefabDir = "assets/prefabs";

    private readonly Func<AssetRegistry> _assets;
    private readonly Func<string?> _projectDir;

    public PrefabService(Func<AssetRegistry> assets, Func<string?> projectDir)
    {
        _assets = assets;
        _projectDir = projectDir;
    }

    /// <summary>
    ///     Write <paramref name="source" /> (deep-cloned) to <c>assets/prefabs/&lt;name&gt;.prefab</c>,
    ///     register it, and return its <see cref="AssetId" />. Does not mutate <paramref name="source" />
    ///     — the caller (a command) marks it as an instance so the operation is undoable.
    /// </summary>
    public AssetId CreatePrefab(SceneNode source, string? name = null)
    {
        var dir = _projectDir();
        var prefabName = string.IsNullOrWhiteSpace(name) ? source.Name : name!;
        var rel = $"{PrefabDir}/{MakeFileSafe(prefabName)}{PrefabDocument.Extension}";
        var abs = dir is null ? Path.GetFullPath(rel) : Path.GetFullPath(Path.Combine(dir, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        var template = source.DeepClone();
        template.PrefabSource = AssetId.Empty; // a template is not itself an instance
        new PrefabDocument {
            Name = prefabName,
            Template = template,
        }.Save(abs);

        return _assets().Register(AssetPath.ToRelative(abs, dir));
    }

    /// <summary>Load the <c>.prefab</c> document behind an asset id (null if unresolved/missing).</summary>
    public PrefabDocument? Load(AssetId id)
    {
        if (_assets().Resolve(id) is not { } rel) return null;
        return PrefabDocument.Load(AssetPath.ToAbsolute(rel, _projectDir()));
    }

    /// <summary>
    ///     Build a fresh instance subtree from a prefab asset, tagged with
    ///     <see cref="SceneNode.PrefabSource" />
    ///     so the inspector/serializer know it is an instance. Null if the prefab can't be resolved.
    /// </summary>
    public SceneNode? InstantiateNode(AssetId id)
    {
        if (Load(id) is not { } doc) return null;
        var node = doc.InstantiateNode();
        node.PrefabSource = id;
        return node;
    }

    private static string MakeFileSafe(string name)
    {
        var chars = name
            .Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrEmpty(safe) ? "Prefab" : safe;
    }
}
