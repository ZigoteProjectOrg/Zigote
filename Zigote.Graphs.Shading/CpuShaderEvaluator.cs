using Zigote.Core.Math3D;

namespace Zigote.Graphs.Shading;

/// <summary>
///     Evaluates a <see cref="ShaderGraphProgram" /> on the CPU, one surface sample at a time. Drives
///     the
///     live material-ball preview and produces the constant-fold (<see cref="Constants" />) the editor
///     maps onto the engine's fixed PBR material. Allocation-free per sample: a single register file
///     sized
///     once per program; instructions are evaluated front-to-back (topological order from the
///     lowerer).
///     Shares its op semantics + the noise/ramp math with <see cref="WgslShaderEmitter" /> for parity.
/// </summary>
public sealed class CpuShaderEvaluator
{
    // ── Procedural stdlib (mirrors WgslShaderEmitter.Prelude — keep both in lockstep) ───────────

    private const float Pi = MathF.PI;
    private readonly IReadOnlyList<ShaderInstr> _instr;
    private readonly ShaderGraphProgram _program;
    private readonly Vec4[] _reg;

    public CpuShaderEvaluator(ShaderGraphProgram program)
    {
        _program = program;
        _instr = program.Instructions;
        _reg = new Vec4[_instr.Count];
    }

    /// <summary>Evaluate every surface output at one sample point.</summary>
    public SurfaceSample Eval(Vec2 uv, Vec3 gen, Vec3 nrm)
    {
        for (var i = 0; i < _reg.Length; i++)
            _reg[i] = Compute(
                _instr[i],
                uv,
                gen,
                nrm
            );

        var baseColor = Root(
            _program.BaseColor,
            ShaderValueType.Vec4,
            new Vec4(
                0.8f,
                0.8f,
                0.8f,
                1f
            )
        );
        var emisColor = Root(
            _program.Emission,
            ShaderValueType.Vec4,
            new Vec4(
                0f,
                0f,
                0f,
                1f
            )
        );
        var emisStr = RootScalar(_program.EmissionStrength, 0f);

        var s = new SurfaceSample {
            BaseColor = baseColor,
            Metallic = RootScalar(_program.Metallic, 0f),
            Roughness = RootScalar(_program.Roughness, 0.5f),
            Specular = RootScalar(_program.Specular, 1f),
            Clearcoat = RootScalar(_program.Clearcoat, 0f),
            ClearcoatRoughness = RootScalar(_program.ClearcoatRoughness, 0.03f),
            Emission =
                new Vec3(emisColor.X * emisStr, emisColor.Y * emisStr, emisColor.Z * emisStr),
        };

        if (_program.Normal >= 0)
        {
            var n = Root(
                _program.Normal,
                ShaderValueType.Vec3,
                new Vec4(
                    nrm.X,
                    nrm.Y,
                    nrm.Z,
                    0f
                )
            );
            s.Normal = new Vec3(n.X, n.Y, n.Z);
            s.HasNormal = true;
        }
        else
        {
            s.Normal = nrm;
        }

        return s;
    }

    /// <summary>The graph evaluated at the reference point (uv=½, generated=0, normal=+Z) → fixed PBR.</summary>
    public SurfaceConstants Constants()
    {
        var s = Eval(new Vec2(0.5f, 0.5f), Vec3.Zero, new Vec3(0f, 0f, 1f));
        return new SurfaceConstants(
            s.BaseColor.X,
            s.BaseColor.Y,
            s.BaseColor.Z,
            s.BaseColor.W,
            s.Metallic,
            s.Roughness,
            s.Specular,
            s.Clearcoat,
            s.ClearcoatRoughness,
            s.Emission.X,
            s.Emission.Y,
            s.Emission.Z
        );
    }

    private Vec4 Root(int id, ShaderValueType expect, Vec4 fallback)
    {
        if (id < 0) return fallback;
        return ShaderCoerce.Eval(_reg[id], _instr[id].Type, expect);
    }

    private float RootScalar(int id, float fallback)
    {
        if (id < 0) return fallback;
        return ShaderCoerce.Scalar(_reg[id], _instr[id].Type);
    }

    private Vec4 Compute(in ShaderInstr ins, Vec2 uv, Vec3 gen, Vec3 nrm)
    {
        switch (ins.Op)
        {
            case ShaderOp.ConstFloat:
                return new Vec4(
                    ins.P0,
                    0f,
                    0f,
                    0f
                );
            case ShaderOp.ConstVec3:
                return new Vec4(
                    ins.P0,
                    ins.P1,
                    ins.P2,
                    0f
                );
            case ShaderOp.ConstVec4:
                return new Vec4(
                    ins.P0,
                    ins.P1,
                    ins.P2,
                    ins.P3
                );
            case ShaderOp.InputUv:
                return new Vec4(
                    uv.X,
                    uv.Y,
                    0f,
                    0f
                );
            case ShaderOp.InputGenerated:
            case ShaderOp.InputObject:
            case ShaderOp.InputPosition:
                return new Vec4(
                    gen.X,
                    gen.Y,
                    gen.Z,
                    0f
                );
            case ShaderOp.InputNormal:
                return new Vec4(
                    nrm.X,
                    nrm.Y,
                    nrm.Z,
                    0f
                );
            case ShaderOp.Coerce:
                return ShaderCoerce.Eval(_reg[ins.Args[0]], _instr[ins.Args[0]].Type, ins.Type);
            case ShaderOp.Math:
                return new Vec4(
                    EvalMath((int)ins.P0, _reg[ins.Args[0]].X, _reg[ins.Args[1]].X),
                    0f,
                    0f,
                    0f
                );
            case ShaderOp.Clamp:
            {
                var lo = _reg[ins.Args[1]].X;
                var hi = _reg[ins.Args[2]].X;
                return new Vec4(
                    Math.Clamp(_reg[ins.Args[0]].X, lo, MathF.Max(lo, hi)),
                    0f,
                    0f,
                    0f
                );
            }
            case ShaderOp.MixColor:
                return EvalMix(
                    (int)ins.P0,
                    _reg[ins.Args[0]].X,
                    _reg[ins.Args[1]],
                    _reg[ins.Args[2]]
                );

            case ShaderOp.Mapping:
            {
                var v = V3(ins.Args[0]);
                var loc = V3(ins.Args[1]);
                var scl = V3(ins.Args[3]);
                return V4(new Vec3(v.X * scl.X + loc.X, v.Y * scl.Y + loc.Y, v.Z * scl.Z + loc.Z));
            }
            case ShaderOp.NoiseFac:
                return F(
                    NoiseFacImpl(
                        V3(ins.Args[0]),
                        Sc(ins.Args[1]),
                        Sc(ins.Args[2]),
                        Sc(ins.Args[3]),
                        Sc(ins.Args[4])
                    )
                );
            case ShaderOp.NoiseColor:
                return NoiseColorImpl(
                    V3(ins.Args[0]),
                    Sc(ins.Args[1]),
                    Sc(ins.Args[2]),
                    Sc(ins.Args[3]),
                    Sc(ins.Args[4])
                );
            case ShaderOp.GradientFac:
                return F(GradientImpl((int)ins.P0, V3(ins.Args[0])));
            case ShaderOp.GradientColor:
            {
                var f = GradientImpl((int)ins.P0, V3(ins.Args[0]));
                return new Vec4(
                    f,
                    f,
                    f,
                    1f
                );
            }
            case ShaderOp.CheckerFac:
                return F(CheckerFacImpl(V3(ins.Args[0]), Sc(ins.Args[3])));
            case ShaderOp.CheckerColor:
            {
                var fac = CheckerFacImpl(V3(ins.Args[0]), Sc(ins.Args[3]));
                return Lerp(_reg[ins.Args[2]], _reg[ins.Args[1]], fac); // even → color1
            }
            case ShaderOp.WaveFac:
                return F(
                    WaveFacImpl(
                        V3(ins.Args[0]),
                        Sc(ins.Args[1]),
                        Sc(ins.Args[2]),
                        Sc(ins.Args[3]),
                        (int)ins.P0,
                        (int)ins.P1
                    )
                );
            case ShaderOp.WaveColor:
            {
                var f = WaveFacImpl(
                    V3(ins.Args[0]),
                    Sc(ins.Args[1]),
                    Sc(ins.Args[2]),
                    Sc(ins.Args[3]),
                    (int)ins.P0,
                    (int)ins.P1
                );
                return new Vec4(
                    f,
                    f,
                    f,
                    1f
                );
            }
            case ShaderOp.VecMath:
                return V4(
                    VecMathVImpl(
                        (int)ins.P0,
                        V3(ins.Args[0]),
                        V3(ins.Args[1]),
                        Sc(ins.Args[2])
                    )
                );
            case ShaderOp.VecMathScalar:
                return F(VecMathSImpl((int)ins.P0, V3(ins.Args[0]), V3(ins.Args[1])));
            case ShaderOp.Combine:
                return new Vec4(
                    _reg[ins.Args[0]].X,
                    _reg[ins.Args[1]].X,
                    _reg[ins.Args[2]].X,
                    0f
                );
            case ShaderOp.SeparateX: return F(V3(ins.Args[0]).X);
            case ShaderOp.SeparateY: return F(V3(ins.Args[0]).Y);
            case ShaderOp.SeparateZ: return F(V3(ins.Args[0]).Z);
            case ShaderOp.MapRange:
                return F(
                    MapRangeImpl(
                        _reg[ins.Args[0]].X,
                        _reg[ins.Args[1]].X,
                        _reg[ins.Args[2]].X,
                        _reg[ins.Args[3]].X,
                        _reg[ins.Args[4]].X,
                        ins.P0 > 0.5f
                    )
                );
            case ShaderOp.ColorRampColor:
                return _program.Ramps[ins.Aux].Sample(_reg[ins.Args[0]].X);
            case ShaderOp.ColorRampAlpha:
                return F(_program.Ramps[ins.Aux].Sample(_reg[ins.Args[0]].X).W);

            default:
                throw new NotSupportedException($"CPU eval has no case for {ins.Op}.");
        }
    }

    // ── Register accessors ──────────────────────────────────────────────────────

    private Vec3 V3(int id)
    {
        var r = _reg[id];
        return new Vec3(r.X, r.Y, r.Z);
    }

    private float Sc(int id)
    {
        return _reg[id].X;
    }

    private static Vec4 V4(Vec3 v)
    {
        return new Vec4(
            v.X,
            v.Y,
            v.Z,
            0f
        );
    }

    private static Vec4 F(float v)
    {
        return new Vec4(
            v,
            0f,
            0f,
            0f
        );
    }

    internal static float EvalMath(int op, float a, float b)
    {
        return (MathOp)op switch {
            MathOp.Add => a + b,
            MathOp.Subtract => a - b,
            MathOp.Multiply => a * b,
            MathOp.Divide => b != 0f ? a / b : 0f,
            MathOp.Power => MathF.Pow(MathF.Max(a, 0f), b),
            MathOp.Logarithm => a > 0f && b > 0f ? MathF.Log(a) / MathF.Log(b) : 0f,
            MathOp.Minimum => MathF.Min(a, b),
            MathOp.Maximum => MathF.Max(a, b),
            MathOp.Sqrt => MathF.Sqrt(MathF.Max(a, 0f)),
            MathOp.Absolute => MathF.Abs(a),
            MathOp.Sine => MathF.Sin(a),
            MathOp.Cosine => MathF.Cos(a),
            MathOp.Floor => MathF.Floor(a),
            MathOp.Fraction => a - MathF.Floor(a),
            MathOp.Modulo => b != 0f ? a - b * MathF.Truncate(a / b) : 0f,
            MathOp.GreaterThan => a > b ? 1f : 0f,
            MathOp.LessThan => a < b ? 1f : 0f,
            _ => a + b,
        };
    }

    private static Vec4 EvalMix(int mode, float fac, Vec4 a, Vec4 b)
    {
        fac = Math.Clamp(fac, 0f, 1f);
        var blended = (MixMode)mode switch {
            MixMode.Darken => Min(a, b),
            MixMode.Multiply => Mul(a, b),
            MixMode.Lighten => Max(a, b),
            MixMode.Screen => Sub(Vec4.One, Mul(Sub(Vec4.One, a), Sub(Vec4.One, b))),
            MixMode.Add => a + b,
            MixMode.Subtract => a - b,
            MixMode.Difference => Abs(a - b),
            _ => b, // Mix
        };
        var rgb = Lerp(a, blended, fac);
        return new Vec4(
            rgb.X,
            rgb.Y,
            rgb.Z,
            a.W + (b.W - a.W) * fac
        );
    }

    private static Vec4 Lerp(Vec4 a, Vec4 b, float t)
    {
        return a + (b - a) * t;
    }

    private static Vec4 Mul(Vec4 a, Vec4 b)
    {
        return new Vec4(
            a.X * b.X,
            a.Y * b.Y,
            a.Z * b.Z,
            a.W * b.W
        );
    }

    private static Vec4 Sub(Vec4 a, Vec4 b)
    {
        return a - b;
    }

    private static Vec4 Min(Vec4 a, Vec4 b)
    {
        return new Vec4(
            MathF.Min(a.X, b.X),
            MathF.Min(a.Y, b.Y),
            MathF.Min(a.Z, b.Z),
            MathF.Min(a.W, b.W)
        );
    }

    private static Vec4 Max(Vec4 a, Vec4 b)
    {
        return new Vec4(
            MathF.Max(a.X, b.X),
            MathF.Max(a.Y, b.Y),
            MathF.Max(a.Z, b.Z),
            MathF.Max(a.W, b.W)
        );
    }

    private static Vec4 Abs(Vec4 a)
    {
        return new Vec4(
            MathF.Abs(a.X),
            MathF.Abs(a.Y),
            MathF.Abs(a.Z),
            MathF.Abs(a.W)
        );
    }

    private static float Fract(float x)
    {
        return x - MathF.Floor(x);
    }

    private static Vec3 Fract3(Vec3 v)
    {
        return new Vec3(Fract(v.X), Fract(v.Y), Fract(v.Z));
    }

    private static float Lerp1(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    // Dave Hoskins' hash13 — identical float math on CPU and in WGSL.
    private static float Hash13(Vec3 p)
    {
        var p3 = Fract3(p * 0.1031f);
        var dot = p3.X * (p3.Z + 31.32f) + p3.Y * (p3.Y + 31.32f) + p3.Z * (p3.X + 31.32f);
        p3 = new Vec3(p3.X + dot, p3.Y + dot, p3.Z + dot);
        return Fract((p3.X + p3.Y) * p3.Z);
    }

    private static float VNoise(Vec3 p)
    {
        Vec3 i = new(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        Vec3 f = new(p.X - i.X, p.Y - i.Y, p.Z - i.Z);
        Vec3 u = new(
            f.X * f.X * (3f - 2f * f.X),
            f.Y * f.Y * (3f - 2f * f.Y),
            f.Z * f.Z * (3f - 2f * f.Z)
        );

        float C(float dx, float dy, float dz)
        {
            return Hash13(new Vec3(i.X + dx, i.Y + dy, i.Z + dz));
        }

        var x00 = Lerp1(C(0, 0, 0), C(1, 0, 0), u.X);
        var x10 = Lerp1(C(0, 1, 0), C(1, 1, 0), u.X);
        var x01 = Lerp1(C(0, 0, 1), C(1, 0, 1), u.X);
        var x11 = Lerp1(C(0, 1, 1), C(1, 1, 1), u.X);
        return Lerp1(Lerp1(x00, x10, u.Y), Lerp1(x01, x11, u.Y), u.Z);
    }

    private static float Fbm(Vec3 p, float detail, float rough)
    {
        float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
        var oct = Math.Clamp((int)detail, 1, 8);
        for (var i = 0; i < oct; i++)
        {
            sum += amp * VNoise(p * freq);
            norm += amp;
            amp *= rough;
            freq *= 2f;
        }

        return norm > 0f ? sum / norm : sum;
    }

    private static Vec3 WarpCoord(Vec3 p, float scale, float dist)
    {
        var pp = p * scale;
        if (dist == 0f) return pp;
        var w = dist * (VNoise(new Vec3(pp.X + 17f, pp.Y + 17f, pp.Z + 17f)) - 0.5f);
        return new Vec3(pp.X + w, pp.Y + w, pp.Z + w);
    }

    private static float NoiseFacImpl(Vec3 p, float scale, float detail, float rough, float dist)
    {
        return Fbm(WarpCoord(p, scale, dist), detail, rough);
    }

    private static Vec4 NoiseColorImpl(Vec3 p, float scale, float detail, float rough, float dist)
    {
        var pp = WarpCoord(p, scale, dist);
        var r = Fbm(pp, detail, rough);
        var g = Fbm(new Vec3(pp.X + 13.5f, pp.Y + 13.5f, pp.Z + 13.5f), detail, rough);
        var b = Fbm(new Vec3(pp.X + 27.1f, pp.Y + 27.1f, pp.Z + 27.1f), detail, rough);
        return new Vec4(
            r,
            g,
            b,
            1f
        );
    }

    private static float GradientImpl(int type, Vec3 v)
    {
        switch ((GradientType)type)
        {
            case GradientType.Quadratic:
            {
                var t = MathF.Max(v.X, 0f);
                return t * t;
            }
            case GradientType.Easing:
            {
                var t = Math.Clamp(v.X, 0f, 1f);
                return t * t * (3f - 2f * t);
            }
            case GradientType.Diagonal: return (v.X + v.Y) * 0.5f;
            case GradientType.Spherical: return MathF.Max(1f - v.Length(), 0f);
            case GradientType.QuadraticSphere:
            {
                var s = MathF.Max(1f - v.Length(), 0f);
                return s * s;
            }
            case GradientType.Radial: return MathF.Atan2(v.Y, v.X) / (2f * Pi) + 0.5f;
            default: return v.X; // Linear
        }
    }

    private static float CheckerFacImpl(Vec3 v, float scale)
    {
        var m = MathF.Floor(v.X * scale) + MathF.Floor(v.Y * scale) + MathF.Floor(v.Z * scale);
        var parity = m - 2f * MathF.Floor(m * 0.5f);
        return parity < 0.5f ? 1f : 0f; // even cell → color1
    }

    private static float WaveFacImpl(Vec3 v, float scale, float dist, float detail, int type,
        int profile)
    {
        var n = type == 1 ? v.Length() * scale : (v.X + v.Y + v.Z) * scale; // Rings vs Bands
        if (dist != 0f) n += dist * Fbm(v * scale, detail, 0.5f);
        return (WaveProfile)profile switch {
            WaveProfile.Saw => Fract(n / (2f * Pi)),
            WaveProfile.Triangle => MathF.Abs(Fract(n / (2f * Pi)) * 2f - 1f),
            _ => 0.5f + 0.5f * MathF.Sin(n), // Sine
        };
    }

    private static Vec3 VecMathVImpl(int op, Vec3 a, Vec3 b, float scale)
    {
        return (VecMathOp)op switch {
            VecMathOp.Add => a + b,
            VecMathOp.Subtract => a - b,
            VecMathOp.Multiply => new Vec3(a.X * b.X, a.Y * b.Y, a.Z * b.Z),
            VecMathOp.Divide => new Vec3(SafeDiv(a.X, b.X), SafeDiv(a.Y, b.Y), SafeDiv(a.Z, b.Z)),
            VecMathOp.Cross => a.Cross(b),
            VecMathOp.Normalize => a.LengthSq() > 1e-12f ? a.Normalize() : Vec3.Zero,
            VecMathOp.Scale => a * scale,
            VecMathOp.Floor => new Vec3(MathF.Floor(a.X), MathF.Floor(a.Y), MathF.Floor(a.Z)),
            VecMathOp.Fraction => new Vec3(Fract(a.X), Fract(a.Y), Fract(a.Z)),
            _ => Vec3.Zero,
        };
    }

    private static float VecMathSImpl(int op, Vec3 a, Vec3 b)
    {
        return (VecMathOp)op switch {
            VecMathOp.Dot => a.Dot(b),
            VecMathOp.Length => a.Length(),
            VecMathOp.Distance => (a - b).Length(),
            _ => 0f,
        };
    }

    private static float MapRangeImpl(float v, float fmin, float fmax, float tmin, float tmax,
        bool clamp)
    {
        var d = fmax - fmin;
        var t = MathF.Abs(d) > 1e-8f ? (v - fmin) / d : 0f;
        var r = tmin + t * (tmax - tmin);
        if (clamp) r = Math.Clamp(r, MathF.Min(tmin, tmax), MathF.Max(tmin, tmax));
        return r;
    }

    private static float SafeDiv(float a, float b)
    {
        return b != 0f ? a / b : 0f;
    }

    /// <summary>Per-pixel surface parameters the preview shades with.</summary>
    public struct SurfaceSample
    {
        public Vec4 BaseColor;
        public float Metallic;
        public float Roughness;
        public float Specular;
        public float Clearcoat;
        public float ClearcoatRoughness;
        public Vec3 Emission;
        public Vec3 Normal;
        public bool HasNormal;
    }
}
