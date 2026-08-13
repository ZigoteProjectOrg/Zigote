using Zigote.Core.Native;

namespace Zigote.Core.Engine;

/// <summary>
///     The 3D scene half of the engine's surface, named and grouped: nodes, transforms, materials,
///     lights and cameras. The counterpart to <see cref="IAudioApi" /> — <c>engine.Scene.SetMeshColor</c>
///     rather than <c>engine.SceneSetMeshColor</c> — so the domains of a 2400-line native facade are
///     discoverable by dotting into them instead of by reading the whole class.
///     <para>
///         A <c>readonly struct</c> over the engine reference, so <c>engine.Scene</c> allocates
///         nothing and every call inlines to the same P/Invoke it always was. Pure forwarding on
///         purpose: the guards and marshalling stay in one place on <see cref="ZigoteEngine" />
///         rather than being reimplemented here where they could drift.
///     </para>
///     <para>
///         Deliberately <b>not</b> an interface. The audio seam is one because a music app must be
///         testable without a sound card; nothing tests a scene without a GPU, so an interface here
///         would be a vtable and a second implementation nobody writes.
///     </para>
/// </summary>
public readonly unsafe struct Scene3D(ZigoteEngine engine)
{
    /// <summary>Drop every node. The scene is rebuilt from scratch after this.</summary>
    public void Clear()
    {
        engine.SceneClear();
    }

    /// <summary>Add a child node and return its handle (0 = failure). <c>kind</c> is the node type code.</summary>
    public ulong AddChildNode(ulong parentHandle, string name, byte kind)
    {
        return engine.SceneAddChildNode(parentHandle, name, kind);
    }

    public void RemoveNode(ulong nodeHandle)
    {
        engine.SceneRemoveNode(nodeHandle);
    }

    /// <summary>Position, rotation quaternion and scale in one call — the per-frame transform push.</summary>
    public void UpdateNode(ulong nodeHandle, float x, float y, float z, float qx, float qy,
        float qz,
        float qw, float sx, float sy, float sz)
    {
        engine.SceneUpdateNode(
            nodeHandle,
            x,
            y,
            z,
            qx,
            qy,
            qz,
            qw,
            sx,
            sy,
            sz
        );
    }

    public void SetNodeVisible(ulong nodeHandle, bool visible)
    {
        engine.SceneSetNodeVisible(nodeHandle, visible);
    }

    /// <summary>Highlight a node with a rim glow. Pass 0 to clear.</summary>
    public void SetSelectedNode(ulong nodeHandle)
    {
        engine.SceneSetSelectedNode(nodeHandle);
    }

    // ── geometry ──────────────────────────────────────────────────────────────

    public void SetMeshBlob(ulong nodeHandle, ReadOnlySpan<byte> data)
    {
        engine.SceneSetMeshBlob(nodeHandle, data);
    }

    public void SetMeshPrimitive(ulong nodeHandle, byte primType)
    {
        engine.SceneSetMeshPrimitive(nodeHandle, primType);
    }

    /// <summary>Instanced draws: <paramref name="count" /> 4×4 matrices, row-major, one per instance.</summary>
    public void SetMeshInstances(ulong nodeHandle, ReadOnlySpan<float> matrices, uint count)
    {
        engine.SceneSetMeshInstances(nodeHandle, matrices, count);
    }

    // ── material ──────────────────────────────────────────────────────────────

    public void SetMeshColor(ulong nodeHandle, float r, float g, float b)
    {
        engine.SceneSetMeshColor(
            nodeHandle,
            r,
            g,
            b
        );
    }

    public void SetMeshRoughness(ulong nodeHandle, float metallic, float roughness)
    {
        engine.SceneSetMeshRoughness(nodeHandle, metallic, roughness);
    }

    public void SetMeshSurface(ulong nodeHandle, float clearcoat, float clearcoatRoughness,
        float specular)
    {
        engine.SceneSetMeshSurface(
            nodeHandle,
            clearcoat,
            clearcoatRoughness,
            specular
        );
    }

    public void SetMeshEmissive(ulong nodeHandle, float r, float g, float b)
    {
        engine.SceneSetMeshEmissive(
            nodeHandle,
            r,
            g,
            b
        );
    }

    public void SetMeshVolume(ulong nodeHandle, float ior, float transmission)
    {
        engine.SceneSetMeshVolume(nodeHandle, ior, transmission);
    }

    public void SetMeshOcclusionStrength(ulong nodeHandle, float strength)
    {
        engine.SceneSetMeshOcclusionStrength(nodeHandle, strength);
    }

    public void SetMeshAlphaMode(ulong nodeHandle, uint mode, float cutoff = 0.5f)
    {
        engine.SceneSetMeshAlphaMode(nodeHandle, mode, cutoff);
    }

    public void SetMeshDoubleSided(ulong nodeHandle, bool doubleSided)
    {
        engine.SceneSetMeshDoubleSided(nodeHandle, doubleSided);
    }

    public void SetMeshEffect(ulong nodeHandle, uint effect)
    {
        engine.SceneSetMeshEffect(nodeHandle, effect);
    }

    // ── textures ──────────────────────────────────────────────────────────────

    public void SetMeshTexturePath(ulong nodeHandle, string path)
    {
        engine.SceneSetMeshTexturePath(nodeHandle, path);
    }

    /// <summary>Base-colour map from an already null-terminated UTF-8 path — the batch loader's form.</summary>
    public void SetMeshTextureFile(ulong nodeHandle, byte* pathC)
    {
        engine.SceneSetMeshTextureFile(nodeHandle, pathC);
    }

    public void SetMeshMrTextureFile(ulong nodeHandle, byte* pathC)
    {
        engine.SceneSetMeshMrTextureFile(nodeHandle, pathC);
    }

    public void SetMeshNormalTextureFile(ulong nodeHandle, byte* pathC)
    {
        engine.SceneSetMeshNormalTextureFile(nodeHandle, pathC);
    }

    public void SetMeshEmissiveTextureFile(ulong nodeHandle, byte* pathC)
    {
        engine.SceneSetMeshEmissiveTextureFile(nodeHandle, pathC);
    }

    /// <summary>Load many maps in one native call — one round trip instead of one per texture.</summary>
    public void LoadTexturesBatch(ZgTextureLoadItem[] items)
    {
        engine.SceneLoadTexturesBatch(items);
    }

    // ── lights and cameras ────────────────────────────────────────────────────

    public void SetLightProperties(ulong nodeHandle, byte kind, float r, float g, float b,
        float intensity, float range, float innerAngle, float outerAngle, bool castShadows)
    {
        engine.SceneSetLightProperties(
            nodeHandle,
            kind,
            r,
            g,
            b,
            intensity,
            range,
            innerAngle,
            outerAngle,
            castShadows
        );
    }

    public void SetCameraParams(ulong nodeHandle, float fovyDegrees, float near, float far)
    {
        engine.SceneSetCameraParams(
            nodeHandle,
            fovyDegrees,
            near,
            far
        );
    }

    /// <summary>Render the scene at this size and return the target texture handle.</summary>
    public ulong Render(uint width, uint height)
    {
        return engine.Render3D(width, height);
    }
}
