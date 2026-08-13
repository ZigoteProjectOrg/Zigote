using Zigote.Core.Math3D;

namespace Zigote.Graphs.Shading;

/// <summary>
///     The three runtime value kinds a shader-graph socket reduces to. Blender's Value/Vector/Color
///     sockets map onto Float/Vec3/Vec4; Vec2 (UV) folds into <see cref="Vec3" /> as <c>(x, y, 0)</c>
///     so both backends only ever switch on three cases.
/// </summary>
public enum ShaderValueType : byte
{
    Float,
    Vec3,
    Vec4,
}

/// <summary>An SSA result handle: which instruction produced the value and its static type.</summary>
public readonly record struct ShaderValueRef(int Id, ShaderValueType Type);

/// <summary>
///     The single source of truth for implicit socket conversions. The CPU evaluator and the WGSL
///     emitter call into the SAME rules so the live preview matches the generated shader. Conversions
///     mirror Blender: scalar→vector splats, vector→scalar takes luminance, colour drops/appends
///     alpha.
/// </summary>
public static class ShaderCoerce
{
    // Rec. 709 luminance — Blender's rgb_to_bw / Color→Value path.
    public const float LumR = 0.2126f;
    public const float LumG = 0.7152f;
    public const float LumB = 0.0722f;

    public static bool NeedsConversion(ShaderValueType from, ShaderValueType to)
    {
        return from != to;
    }

    /// <summary>Convert a value carried in the universal <see cref="Vec4" /> register to the target type.</summary>
    public static Vec4 Eval(Vec4 v, ShaderValueType from, ShaderValueType to)
    {
        if (from == to) return v;
        return to switch {
            ShaderValueType.Float => new Vec4(
                Scalar(v, from),
                0f,
                0f,
                0f
            ),
            ShaderValueType.Vec3 => from == ShaderValueType.Float
                ? new Vec4(
                    v.X,
                    v.X,
                    v.X,
                    0f
                ) // splat
                : new Vec4(
                    v.X,
                    v.Y,
                    v.Z,
                    0f
                ), // Vec4 → Vec3 drops alpha
            ShaderValueType.Vec4 => from == ShaderValueType.Float
                ? new Vec4(
                    v.X,
                    v.X,
                    v.X,
                    1f
                ) // splat rgb, opaque
                : new Vec4(
                    v.X,
                    v.Y,
                    v.Z,
                    1f
                ), // Vec3 → Vec4 appends 1
            _ => v,
        };
    }

    /// <summary>Scalar reduction of a register value (Float passthrough, Vec3/Vec4 → luminance of rgb).</summary>
    public static float Scalar(Vec4 v, ShaderValueType from)
    {
        return from == ShaderValueType.Float ? v.X : v.X * LumR + v.Y * LumG + v.Z * LumB;
    }

    /// <summary>
    ///     WGSL counterpart of <see cref="Eval" />: wrap a source expression so it reads as the
    ///     target type.
    /// </summary>
    public static string Emit(string expr, ShaderValueType from, ShaderValueType to)
    {
        if (from == to) return expr;
        return to switch {
            ShaderValueType.Float => from == ShaderValueType.Float ? expr : Lum(expr, from),
            ShaderValueType.Vec3 => from switch {
                ShaderValueType.Float => $"vec3<f32>({expr})",
                ShaderValueType.Vec4 => $"({expr}).xyz",
                _ => expr,
            },
            ShaderValueType.Vec4 => from switch {
                ShaderValueType.Float => $"vec4<f32>(vec3<f32>({expr}), 1.0)",
                ShaderValueType.Vec3 => $"vec4<f32>({expr}, 1.0)",
                _ => expr,
            },
            _ => expr,
        };
    }

    private static string Lum(string expr, ShaderValueType from)
    {
        var rgb = from == ShaderValueType.Vec4 ? $"({expr}).xyz" : $"({expr})";
        return $"dot({rgb}, vec3<f32>({LumR}, {LumG}, {LumB}))";
    }

    /// <summary>The WGSL scalar/vector type keyword for a value type.</summary>
    public static string WgslType(ShaderValueType t)
    {
        return t switch {
            ShaderValueType.Float => "f32",
            ShaderValueType.Vec3 => "vec3<f32>",
            ShaderValueType.Vec4 => "vec4<f32>",
            _ => "f32",
        };
    }
}
