using Zigote.Runtime.Scene;
using Zigote.Vfx;

namespace Zigote.Runtime.Vfx;

/// <summary>
///     Resolves the emitter asset for a <see cref="NodeKind.VfxEmitter" /> node without the runtime
///     knowing about node graphs. The editor wires <see cref="GraphCompiler" /> to the graph compiler
///     at startup (same static-provider pattern as <c>Physics.Backend</c>); an exported player ships
///     baked emitter JSON on the node (<see cref="SceneNode.VfxBakedJson" />) and leaves it null.
/// </summary>
public static class VfxAssets
{
    public static Func<SceneNode, VfxEmitterAsset>? GraphCompiler { get; set; }

    public static VfxEmitterAsset Resolve(SceneNode node)
    {
        if (!string.IsNullOrEmpty(node.VfxBakedJson))
            return VfxAssetJson.Deserialize(node.VfxBakedJson);
        return GraphCompiler?.Invoke(node) ?? new VfxEmitterAsset();
    }
}