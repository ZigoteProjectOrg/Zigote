using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;
using Zigote.Graphs.Vfx;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Vfx;

/// <summary>
///     Editor-side glue between a <see cref="SceneNode" /> VFX emitter and the node-graph framework — the
///     VFX counterpart of <c>ShaderMaterialDomain</c>. Loads/saves the node's persisted graph
///     (<see cref="SceneNode.VfxGraphJson" />) and compiles it to a runtime <c>VfxEmitterAsset</c>.
/// </summary>
public static class VfxNodeEditor
{
    public static GraphDomainRegistry CreateRegistry()
    {
        return VfxDomain.CreateRegistry();
    }

    /// <summary>The node's authored graph, or the default preset when it has none (or a corrupt string).</summary>
    public static GraphDocument LoadGraph(SceneNode node)
    {
        if (!string.IsNullOrEmpty(node.VfxGraphJson))
            try
            {
                return VfxGraphSerializer.Deserialize(node.VfxGraphJson);
            }
            catch
            {
                // Fall through to a default graph if the stored string is corrupt/legacy.
            }

        return VfxPresets.CreateDefault(node.Name);
    }

    public static void SaveGraph(SceneNode node, GraphDocument graph)
    {
        node.VfxGraphJson = VfxGraphSerializer.Serialize(graph);
    }

    public static CompiledVfxGraph Compile(SceneNode node)
    {
        return VfxGraphCompiler.Compile(LoadGraph(node));
    }

    /// <summary>Seed a freshly-created VFX node with a preset graph so it shows particles immediately.</summary>
    public static void SeedDefault(SceneNode node, string preset = "Sparks")
    {
        if (string.IsNullOrEmpty(node.VfxGraphJson))
            SaveGraph(node, VfxPresets.Create(preset, node.Name));
    }
}
