using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Vfx;

/// <summary>
///     Lowers a <see cref="VfxEmitterAsset" /> + per-frame state to the flat 112-float (28-vec4)
///     uniform
///     buffer the GPU compute kernel reads (see <c>particle_compute_source.wgsl</c> <c>Params</c>).
///     One
///     fixed kernel parameterised by this UBO runs all module types — variable-length ramps/curves are
///     baked into fixed 8-entry LUTs here. The layout is load-bearing: it must match the WGSL struct
///     field-for-field (guarded by <c>VfxGpuParamsTests</c>).
/// </summary>
public static class VfxGpuParams
{
    public const int FloatCount = 112;

    private const float Deg2Rad = MathF.PI / 180f;

    // Module bitmask bits (must match the kernel's `mask & N` checks).
    private const uint MaskGravity = 1;
    private const uint MaskDrag = 2;
    private const uint MaskTurbulence = 4;
    private const uint MaskVortex = 8;
    private const uint MaskColorOverLife = 16;
    private const uint MaskSizeOverLife = 32;
    private const uint MaskAlphaOverLife = 64;

    public static void Build(VfxEmitterAsset asset, int spawnCount, uint frameSeed, float dt,
        float time,
        Vec3 position, Quat orientation, Span<float> dst)
    {
        dst.Clear();

        uint mask = 0;
        var gravity = Vec3.Zero;
        float drag = 0f;
        float turbStrength = 0f, turbFreq = 0f;
        var vortexAxis = Vec3.Up;
        float vortexStrength = 0f;
        ColorRamp? ramp = null;
        FloatCurve? sizeCurve = null;
        FloatCurve? alphaCurve = null;

        foreach (var m in asset.UpdateModules)
        {
            switch (m)
            {
                case GravityModule g:
                    mask |= MaskGravity;
                    gravity = g.Gravity;
                    break;
                case DragModule d:
                    mask |= MaskDrag;
                    drag = d.Drag;
                    break;
                case TurbulenceModule t:
                    mask |= MaskTurbulence;
                    turbStrength = t.Strength;
                    turbFreq = t.Frequency;
                    break;
                case VortexModule v:
                    mask |= MaskVortex;
                    vortexAxis = v.Axis;
                    vortexStrength = v.Strength;
                    break;
                case ColorOverLifeModule c:
                    mask |= MaskColorOverLife;
                    ramp = c.Ramp;
                    break;
                case SizeOverLifeModule s:
                    mask |= MaskSizeOverLife;
                    sizeCurve = s.Curve;
                    break;
                case AlphaOverLifeModule a:
                    mask |= MaskAlphaOverLife;
                    alphaCurve = a.Curve;
                    break;
            }
        }

        dst[0] = asset.Capacity; // counts
        dst[1] = spawnCount;
        dst[2] = frameSeed;
        dst[3] = (int)asset.Shape;

        dst[4] = mask; // flags
        dst[5] = (int)asset.Space;

        dst[8] = dt; // timing
        dst[9] = time;

        dst[12] = position.X; // epos
        dst[13] = position.Y;
        dst[14] = position.Z;

        dst[16] = orientation.X; // erot
        dst[17] = orientation.Y;
        dst[18] = orientation.Z;
        dst[19] = orientation.W;

        dst[20] = asset.EmitDirection.X; // edir
        dst[21] = asset.EmitDirection.Y;
        dst[22] = asset.EmitDirection.Z;

        dst[24] = asset.ShapeRadius; // shape0
        dst[25] = MathF.Cos(asset.ConeAngleDegrees * Deg2Rad);

        dst[28] = asset.ShapeBoxHalfExtents.X; // boxext
        dst[29] = asset.ShapeBoxHalfExtents.Y;
        dst[30] = asset.ShapeBoxHalfExtents.Z;

        dst[32] = asset.StartLifetime.Min; // life
        dst[33] = asset.StartLifetime.Max;
        dst[34] = asset.StartSpeed.Min;
        dst[35] = asset.StartSpeed.Max;

        dst[36] = asset.StartSize.Min; // size
        dst[37] = asset.StartSize.Max;
        dst[38] = asset.StartRotation.Min;
        dst[39] = asset.StartRotation.Max;

        dst[40] = asset.StartAngularVelocity.Min; // spin
        dst[41] = asset.StartAngularVelocity.Max;

        dst[44] = asset.StartColor.R; // col0
        dst[45] = asset.StartColor.G;
        dst[46] = asset.StartColor.B;
        dst[47] = asset.StartColor.A;

        dst[48] = asset.StartColorVariation.R; // col1
        dst[49] = asset.StartColorVariation.G;
        dst[50] = asset.StartColorVariation.B;
        dst[51] = asset.StartColorVariation.A;

        dst[52] = gravity.X; // grav (+drag)
        dst[53] = gravity.Y;
        dst[54] = gravity.Z;
        dst[55] = drag;

        dst[56] = turbStrength; // turb
        dst[57] = turbFreq;

        dst[60] = vortexAxis.X; // vort (+strength)
        dst[61] = vortexAxis.Y;
        dst[62] = vortexAxis.Z;
        dst[63] = vortexStrength;

        for (int i = 0; i < 8; i++) // ramp (8 stops)
        {
            var c = ramp?.Evaluate(i / 7f) ?? Color.White;
            int o = 64 + (i * 4);
            dst[o] = c.R;
            dst[o + 1] = c.G;
            dst[o + 2] = c.B;
            dst[o + 3] = c.A;
        }

        for (int i = 0; i < 8; i++) dst[96 + i] = sizeCurve?.Evaluate(i / 7f) ?? 1f; // size curve
        for (int i = 0; i < 8; i++)
            dst[104 + i] = alphaCurve?.Evaluate(i / 7f) ?? 1f; // alpha curve
    }

    public static float[] Build(VfxEmitterAsset asset, int spawnCount, uint frameSeed, float dt,
        float time,
        Vec3 position, Quat orientation)
    {
        float[] dst = new float[FloatCount];
        Build(
            asset: asset,
            spawnCount: spawnCount,
            frameSeed: frameSeed,
            dt: dt,
            time: time,
            position: position,
            orientation: orientation,
            dst: dst
        );
        return dst;
    }
}
