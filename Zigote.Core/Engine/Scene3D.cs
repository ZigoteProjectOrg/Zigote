using Zigote.Core.Native;

namespace Zigote.Core.Engine;

/// <summary>
///     The 3D scene half of the engine's surface, named and grouped: nodes, transforms, materials,
///     lights and cameras. The counterpart to <see cref="IAudioApi" /> —
///     <c>engine.Scene.SetMeshColor</c>
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
    public void Clear() => engine.SceneClear();

    /// <summary>Add a child node and return its handle (0 = failure). <c>kind</c> is the node type code.</summary>
    public ulong AddChildNode(ulong parentHandle, string name, byte kind) =>
        engine.SceneAddChildNode(parentHandle: parentHandle, name: name, kind: kind);

    public void RemoveNode(ulong nodeHandle) => engine.SceneRemoveNode(nodeHandle);

    /// <summary>Position, rotation quaternion and scale in one call — the per-frame transform push.</summary>
    public void UpdateNode(ulong nodeHandle, float x, float y, float z, float qx, float qy,
        float qz,
        float qw, float sx, float sy, float sz)
    {
        engine.SceneUpdateNode(
            nodeHandle: nodeHandle,
            x: x,
            y: y,
            z: z,
            qx: qx,
            qy: qy,
            qz: qz,
            qw: qw,
            sx: sx,
            sy: sy,
            sz: sz
        );
    }

    public void SetNodeVisible(ulong nodeHandle, bool visible) =>
        engine.SceneSetNodeVisible(nodeHandle: nodeHandle, visible: visible);

    /// <summary>Highlight a node with a rim glow. Pass 0 to clear.</summary>
    public void SetSelectedNode(ulong nodeHandle) => engine.SceneSetSelectedNode(nodeHandle);

    // ── geometry ──────────────────────────────────────────────────────────────

    public void SetMeshBlob(ulong nodeHandle, ReadOnlySpan<byte> data) =>
        engine.SceneSetMeshBlob(nodeHandle: nodeHandle, data: data);

    public void SetMeshPrimitive(ulong nodeHandle, byte primType) =>
        engine.SceneSetMeshPrimitive(nodeHandle: nodeHandle, primType: primType);

    /// <summary>Instanced draws: <paramref name="count" /> 4×4 matrices, row-major, one per instance.</summary>
    public void SetMeshInstances(ulong nodeHandle, ReadOnlySpan<float> matrices, uint count) =>
        engine.SceneSetMeshInstances(nodeHandle: nodeHandle, matrices: matrices, count: count);

    // ── material ──────────────────────────────────────────────────────────────

    public void SetMeshColor(ulong nodeHandle, float r, float g, float b)
    {
        engine.SceneSetMeshColor(
            nodeHandle: nodeHandle,
            r: r,
            g: g,
            b: b
        );
    }

    public void SetMeshRoughness(ulong nodeHandle, float metallic, float roughness) =>
        engine.SceneSetMeshRoughness(
            nodeHandle: nodeHandle,
            metallic: metallic,
            roughness: roughness
        );

    public void SetMeshSurface(ulong nodeHandle, float clearcoat, float clearcoatRoughness,
        float specular)
    {
        engine.SceneSetMeshSurface(
            nodeHandle: nodeHandle,
            clearcoat: clearcoat,
            clearcoatRoughness: clearcoatRoughness,
            specular: specular
        );
    }

    public void SetMeshEmissive(ulong nodeHandle, float r, float g, float b)
    {
        engine.SceneSetMeshEmissive(
            nodeHandle: nodeHandle,
            r: r,
            g: g,
            b: b
        );
    }

    public void SetMeshVolume(ulong nodeHandle, float ior, float transmission) =>
        engine.SceneSetMeshVolume(nodeHandle: nodeHandle, ior: ior, transmission: transmission);

    public void SetMeshOcclusionStrength(ulong nodeHandle, float strength) =>
        engine.SceneSetMeshOcclusionStrength(nodeHandle: nodeHandle, strength: strength);

    public void SetMeshAlphaMode(ulong nodeHandle, uint mode, float cutoff = 0.5f) =>
        engine.SceneSetMeshAlphaMode(nodeHandle: nodeHandle, mode: mode, cutoff: cutoff);

    public void SetMeshDoubleSided(ulong nodeHandle, bool doubleSided) =>
        engine.SceneSetMeshDoubleSided(nodeHandle: nodeHandle, doubleSided: doubleSided);

    public void SetMeshEffect(ulong nodeHandle, uint effect) =>
        engine.SceneSetMeshEffect(nodeHandle: nodeHandle, effect: effect);

    // ── textures ──────────────────────────────────────────────────────────────

    public void SetMeshTexturePath(ulong nodeHandle, string path) =>
        engine.SceneSetMeshTexturePath(nodeHandle: nodeHandle, path: path);

    /// <summary>Base-colour map from an already null-terminated UTF-8 path — the batch loader's form.</summary>
    public void SetMeshTextureFile(ulong nodeHandle, byte* pathC) =>
        engine.SceneSetMeshTextureFile(nodeHandle: nodeHandle, pathC: pathC);

    public void SetMeshMrTextureFile(ulong nodeHandle, byte* pathC) =>
        engine.SceneSetMeshMrTextureFile(nodeHandle: nodeHandle, pathC: pathC);

    public void SetMeshNormalTextureFile(ulong nodeHandle, byte* pathC) =>
        engine.SceneSetMeshNormalTextureFile(nodeHandle: nodeHandle, pathC: pathC);

    public void SetMeshEmissiveTextureFile(ulong nodeHandle, byte* pathC) =>
        engine.SceneSetMeshEmissiveTextureFile(nodeHandle: nodeHandle, pathC: pathC);

    /// <summary>Load many maps in one native call — one round trip instead of one per texture.</summary>
    public void LoadTexturesBatch(ZgTextureLoadItem[] items) =>
        engine.SceneLoadTexturesBatch(items);

    // ── lights and cameras ────────────────────────────────────────────────────

    public void SetLightProperties(ulong nodeHandle, byte kind, float r, float g, float b,
        float intensity, float range, float innerAngle, float outerAngle, bool castShadows)
    {
        engine.SceneSetLightProperties(
            nodeHandle: nodeHandle,
            kind: kind,
            r: r,
            g: g,
            b: b,
            intensity: intensity,
            range: range,
            innerAngle: innerAngle,
            outerAngle: outerAngle,
            castShadows: castShadows
        );
    }

    public void SetCameraParams(ulong nodeHandle, float fovyDegrees, float near, float far)
    {
        engine.SceneSetCameraParams(
            nodeHandle: nodeHandle,
            fovyDegrees: fovyDegrees,
            near: near,
            far: far
        );
    }

    /// <summary>Render the scene at this size and return the target texture handle.</summary>
    public ulong Render(uint width, uint height) => engine.Render3D(width: width, height: height);
}
