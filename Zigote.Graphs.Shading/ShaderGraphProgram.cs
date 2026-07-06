using System.Text;
using Zigote.Core.Math3D;

namespace Zigote.Graphs.Shading;

/// <summary>
///     The instruction set of the shader-graph IR. Each <see cref="ShaderInstr" /> produces exactly
///     one
///     SSA value. Multi-output graph nodes (Noise → Fac+Color, Checker → Color+Fac, …) lower to one op
///     per consumed output. The CPU evaluator (<see cref="CpuShaderEvaluator" />) and the WGSL emitter
///     (<see cref="WgslShaderEmitter" />) both switch on this enum — add a case to both when
///     extending.
/// </summary>
public enum ShaderOp : byte
{
    // ── Leaves ────────────────────────────────────────────────────────────────
    ConstFloat, // P0
    ConstVec3, // P0,P1,P2
    ConstVec4, // P0,P1,P2,P3
    InputUv, // generated UV (x,y,0)
    InputGenerated, // object-space position remapped to a preview box
    InputObject, // object-space position
    InputNormal, // shading normal
    InputPosition, // world position (== object position in the preview)

    // ── Conversion ────────────────────────────────────────────────────────────
    Coerce, // Args[0] = source; Type = target; source type read from the producing instr

    // ── Scalar / converter ────────────────────────────────────────────────────
    Math, // P0 = MathOp; Args = a,b → float
    Clamp, // Args = value,min,max → float
    MapRange, // Args = value,fromMin,fromMax,toMin,toMax; P0 = clamp(0/1) → float

    // ── Vector ────────────────────────────────────────────────────────────────
    VecMath, // P0 = VecMathOp; Args = a,b,scale → vec3
    VecMathScalar, // P0 = VecMathOp; Args = a,b → float (Dot/Length/Distance)
    Combine, // Args = x,y,z → vec3
    SeparateX, // Args = v → float
    SeparateY,
    SeparateZ,

    // ── Colour ────────────────────────────────────────────────────────────────
    MixColor, // P0 = MixMode; Args = fac,a,b → vec4

    // ── Vector transform ──────────────────────────────────────────────────────
    Mapping, // P0 = MappingType; Args = vector,location,rotation,scale → vec3

    // ── Procedural textures ───────────────────────────────────────────────────
    NoiseFac, // P0 = dimensions; Args = vector,scale,detail,roughness,distortion → float
    NoiseColor, // → vec4
    GradientFac, // P0 = GradientType; Args = vector → float
    GradientColor, // → vec4
    CheckerColor, // Args = vector,color1,color2,scale → vec4
    CheckerFac, // → float
    WaveFac, // P0 = WaveType, P1 = WaveProfile; Args = vector,scale,distortion,detail → float
    WaveColor, // → vec4
    ColorRampColor, // Aux = ramp index; Args = fac → vec4
    ColorRampAlpha, // → float
}

// Operation indices — MUST match the EnumLabels order on the corresponding node property.
public enum MathOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Power,
    Logarithm,
    Minimum,
    Maximum,
    Sqrt,
    Absolute,
    Sine,
    Cosine,
    Floor,
    Fraction,
    Modulo,
    GreaterThan,
    LessThan,
}

public enum VecMathOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Cross,
    Dot,
    Normalize,
    Length,
    Scale,
    Distance,
    Floor,
    Fraction,
}

public enum MixMode
{
    Mix,
    Darken,
    Multiply,
    Lighten,
    Screen,
    Add,
    Subtract,
    Difference,
}

public enum MappingType
{
    Point,
    Texture,
    Vector,
    Normal,
}

public enum NoiseDimensions
{
    D1,
    D2,
    D3,
    D4,
}

public enum GradientType
{
    Linear,
    Quadratic,
    Easing,
    Diagonal,
    Spherical,
    QuadraticSphere,
    Radial,
}

public enum WaveType
{
    Bands,
    Rings,
}

public enum WaveProfile
{
    Sine,
    Saw,
    Triangle,
}

public enum RampInterpolation
{
    Linear,
    Constant,
    Ease,
}

public enum TextureSlot : byte
{
    BaseColor,
    Normal,
}

/// <summary>
///     One IR instruction. <see cref="Args" /> hold operand SSA ids; <see cref="P0" />–
///     <see cref="P3" />
///     hold inline scalar params (const values, op indices); <see cref="Aux" /> indexes a side table
///     (ramps).
/// </summary>
public readonly record struct ShaderInstr(
    int Result,
    ShaderOp Op,
    ShaderValueType Type,
    int[] Args,
    float P0 = 0f,
    float P1 = 0f,
    float P2 = 0f,
    float P3 = 0f,
    int Aux = -1)
{
    public string Dump()
    {
        var args = Args is { Length: > 0 } ? string.Join(",", Args) : "";
        return $"v{Result} = {Op}:{Type}({args}) [{P0:G},{P1:G},{P2:G},{P3:G}]" +
               (Aux >= 0 ? $" aux={Aux}" : "");
    }
}

/// <summary>A reference to an external texture the graph needs (deferred to the native stage).</summary>
public readonly record struct ShaderTextureRef(string Path, TextureSlot Slot);

public readonly record struct ShaderRampStop(float Pos, float R, float G, float B, float A);

/// <summary>
///     A colour ramp (gradient) sampled by a Color Ramp node. UI-free — the editor adapts it to
///     its <c>GradientEditor</c>; the WGSL emitter bakes the stops into the shader.
/// </summary>
public sealed class ShaderColorRamp
{
    public ShaderColorRamp(IReadOnlyList<ShaderRampStop> stops, RampInterpolation interp)
    {
        Stops = stops.Count > 0
            ? stops
            : [
                new ShaderRampStop(
                    0f,
                    0f,
                    0f,
                    0f,
                    1f
                ),
                new ShaderRampStop(
                    1f,
                    1f,
                    1f,
                    1f,
                    1f
                ),
            ];
        Interpolation = interp;
    }

    public IReadOnlyList<ShaderRampStop> Stops { get; }
    public RampInterpolation Interpolation { get; }

    /// <summary>
    ///     Sample the ramp at <paramref name="t" /> (clamped to the endpoints). Mirrors
    ///     <c>ColorGradient.Sample</c> so the preview matches the editor gradient widget.
    /// </summary>
    public Vec4 Sample(float t)
    {
        var n = Stops.Count;
        if (n == 0) return Vec4.One;
        if (t <= Stops[0].Pos) return ToVec4(Stops[0]);
        if (t >= Stops[n - 1].Pos) return ToVec4(Stops[n - 1]);
        for (var i = 0; i < n - 1; i++)
        {
            var a = Stops[i];
            var b = Stops[i + 1];
            if (t < a.Pos || t > b.Pos) continue;
            var span = b.Pos - a.Pos;
            var f = span > 1e-6f ? (t - a.Pos) / span : 0f;
            f = Interpolation switch {
                RampInterpolation.Constant => 0f,
                RampInterpolation.Ease => f * f * (3f - 2f * f),
                _ => f,
            };
            return ToVec4(a) + (ToVec4(b) - ToVec4(a)) * f;
        }

        return ToVec4(Stops[n - 1]);
    }

    private static Vec4 ToVec4(ShaderRampStop s)
    {
        return new Vec4(
            s.R,
            s.G,
            s.B,
            s.A
        );
    }
}

/// <summary>
///     Constant approximation of the surface (the graph evaluated at a reference point). Maps onto
///     the engine's fixed PBR material so the 3D scene viewport keeps showing something until the
///     native
///     per-material shader lands.
/// </summary>
public readonly record struct SurfaceConstants(
    float BaseR,
    float BaseG,
    float BaseB,
    float BaseA,
    float Metallic,
    float Roughness,
    float Specular,
    float Clearcoat,
    float ClearcoatRoughness,
    float EmissiveR,
    float EmissiveG,
    float EmissiveB)
{
    public static SurfaceConstants Default => new(
        0.8f,
        0.8f,
        0.8f,
        1f,
        0f,
        0.5f,
        1f,
        0f,
        0.03f,
        0f,
        0f,
        0f
    );
}

/// <summary>
///     The lowered, backend-agnostic shader program: a flat SSA instruction list plus side tables
///     and the SSA roots of each Principled-BSDF surface input. Built once per compile, consumed by
///     both
///     backends.
/// </summary>
public sealed class ShaderGraphProgram
{
    public static readonly ShaderGraphProgram Empty = new();

    public IReadOnlyList<ShaderInstr> Instructions { get; init; } = [];
    public IReadOnlyList<ShaderColorRamp> Ramps { get; init; } = [];
    public IReadOnlyList<ShaderTextureRef> Textures { get; init; } = [];

    // SSA ids of the Principled-BSDF inputs (-1 = unused → backend substitutes the documented default).
    public int BaseColor { get; init; } = -1;
    public int Metallic { get; init; } = -1;
    public int Roughness { get; init; } = -1;
    public int Specular { get; init; } = -1;
    public int Emission { get; init; } = -1;
    public int EmissionStrength { get; init; } = -1;
    public int Clearcoat { get; init; } = -1;
    public int ClearcoatRoughness { get; init; } = -1;
    public int Normal { get; init; } = -1;

    /// <summary>Deterministic textual dump — used by golden tests.</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        foreach (var i in Instructions) sb.AppendLine(i.Dump());
        sb.Append($"roots: base={BaseColor} metal={Metallic} rough={Roughness} spec={Specular} ");
        sb.Append(
            $"emis={Emission} emisStr={EmissionStrength} coat={Clearcoat} coatR={ClearcoatRoughness} norm={Normal}"
        );
        return sb.ToString();
    }
}