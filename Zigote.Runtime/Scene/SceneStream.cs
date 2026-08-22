using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Zigote.Core.Native;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Accumulates scene changes as a flat command stream and hands the whole batch to native in one
///     call (<c>zigote_scene_apply</c>).
///     <para>
///         The scene used to be driven by fourteen per-node setters, each its own P/Invoke. The
///         per-property dirty gates mean a static node costs nothing per frame either way — but a
///         scene load or rebuild pays three to six transitions per node, and the API surface was
///         fourteen functions wide for one idea. Records are written straight from the generated
///         wire structs, so their layout is the Zig layout by construction.
///     </para>
///     <para>
///         Not thread-safe, and not meant to be: one instance per sync walk, flushed at the end.
///     </para>
/// </summary>
internal sealed class SceneStream
{
    private byte[] _buffer = new byte[8 * 1024];
    private int _length;

    /// <summary>Records written since the last flush. Zero means the flush is a no-op.</summary>
    public int Count { get; private set; }

    private Span<byte> Reserve(int size)
    {
        if (_length + size > _buffer.Length)
        {
            int grown = _buffer.Length;
            while (grown < _length + size) grown *= 2;
            Array.Resize(array: ref _buffer, newSize: grown);
        }

        var span = _buffer.AsSpan(start: _length, length: size);
        _length += size;
        Count++;
        return span;
    }

    /// <summary>Writes one record, stamping its header from the struct's own size.</summary>
    private void Write<T>(ZgSceneOp kind, T record) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        // Every record must be a multiple of 8 or the native decoder rejects the batch; the Zig
        // side asserts the same thing at comptime, so this can only trip if the two disagree.
        Debug.Assert(size % 8 == 0, $"scene op {kind} is {size} bytes, not a multiple of 8");

        var span = Reserve(size);
        MemoryMarshal.Write(destination: span, value: in record);
        // The header is at offset 0 of every op (asserted on the Zig side).
        var header = new ZgSceneOpHeader { Kind = (uint)kind, Size = (uint)size };
        MemoryMarshal.Write(destination: span, value: in header);
    }

    public void Transform(
        ulong node,
        float x, float y, float z,
        float qx, float qy, float qz, float qw,
        float sx, float sy, float sz
    ) =>
        Write(
            ZgSceneOp.Transform,
            new ZgSceneTransform
            {
                Node = node,
                X = x, Y = y, Z = z,
                Qx = qx, Qy = qy, Qz = qz, Qw = qw,
                Sx = sx, Sy = sy, Sz = sz,
            }
        );

    public void Light(
        ulong node, uint kind, bool castShadows,
        float r, float g, float b,
        float intensity, float range, float innerAngle, float outerAngle
    ) =>
        Write(
            ZgSceneOp.Light,
            new ZgSceneLight
            {
                Node = node, Kind = kind, CastShadows = castShadows ? 1u : 0u,
                R = r, G = g, B = b,
                Intensity = intensity, Range = range,
                InnerAngle = innerAngle, OuterAngle = outerAngle,
            }
        );

    public void Camera(ulong node, float fovyDegrees, float near, float far) =>
        Write(
            ZgSceneOp.Camera,
            new ZgSceneCamera { Node = node, FovyDegrees = fovyDegrees, Near = near, Far = far }
        );

    /// <summary>
    ///     The whole PBR factor set at once. Sent when ANY factor changed, which is why the native
    ///     side takes them as one record rather than eight calls that each touch adjacent fields.
    /// </summary>
    public void Material(
        ulong node,
        float colorR, float colorG, float colorB,
        float metallic, float roughness,
        float clearcoat, float clearcoatRoughness, float specular,
        float emissiveR, float emissiveG, float emissiveB,
        float ior, float transmission, float occlusionStrength,
        float alphaCutoff, uint effect, uint alphaMode, bool doubleSided
    ) =>
        Write(
            ZgSceneOp.Material,
            new ZgSceneMaterial
            {
                Node = node,
                ColorR = colorR, ColorG = colorG, ColorB = colorB,
                Metallic = metallic, Roughness = roughness,
                Clearcoat = clearcoat, ClearcoatRoughness = clearcoatRoughness, Specular = specular,
                EmissiveR = emissiveR, EmissiveG = emissiveG, EmissiveB = emissiveB,
                Ior = ior, Transmission = transmission, OcclusionStrength = occlusionStrength,
                AlphaCutoff = alphaCutoff, Effect = effect, AlphaMode = alphaMode,
                DoubleSided = doubleSided ? 1u : 0u,
            }
        );

    public void Visibility(ulong node, bool visible) =>
        Write(ZgSceneOp.Visibility, new ZgSceneVisibility { Node = node, Visible = visible ? 1u : 0u });

    public void Primitive(ulong node, uint primType) =>
        Write(ZgSceneOp.Primitive, new ZgScenePrimitive { Node = node, PrimType = primType });

    /// <summary>Hand the batch to native and reset. A batch native rejects is dropped whole.</summary>
    public unsafe void Flush()
    {
        if (_length == 0) return;

        fixed (byte* p = _buffer)
        {
            var status = NativeEngine.SceneApply(stream: p, len: (nuint)_length);
            if (status != ZgStatus.Ok)
            {
                Console.Error.WriteLine(
                    $"[scene] zigote_scene_apply rejected a {Count}-record batch: {status}"
                );
            }
        }

        _length = 0;
        Count = 0;
    }
}
