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
        for (int i = 0; i < _reg.Length; i++)
        {
            _reg[i] = Compute(
                ins: _instr[i],
                uv: uv,
                gen: gen,
                nrm: nrm
            );
        }

        var baseColor = Root(
            id: _program.BaseColor,
            expect: ShaderValueType.Vec4,
            fallback: new Vec4(
                x: 0.8f,
                y: 0.8f,
                z: 0.8f,
                w: 1f
            )
        );
        var emisColor = Root(
            id: _program.Emission,
            expect: ShaderValueType.Vec4,
            fallback: new Vec4(
                x: 0f,
                y: 0f,
                z: 0f,
                w: 1f
            )
        );
        float emisStr = RootScalar(id: _program.EmissionStrength, fallback: 0f);

        var s = new SurfaceSample {
            BaseColor = baseColor,
            Metallic = RootScalar(id: _program.Metallic, fallback: 0f),
            Roughness = RootScalar(id: _program.Roughness, fallback: 0.5f),
            Specular = RootScalar(id: _program.Specular, fallback: 1f),
            Clearcoat = RootScalar(id: _program.Clearcoat, fallback: 0f),
            ClearcoatRoughness = RootScalar(id: _program.ClearcoatRoughness, fallback: 0.03f),
            Emission =
                new Vec3(
                    x: emisColor.X * emisStr,
                    y: emisColor.Y * emisStr,
                    z: emisColor.Z * emisStr
                ),
        };

        if (_program.Normal >= 0)
        {
            var n = Root(
                id: _program.Normal,
                expect: ShaderValueType.Vec3,
                fallback: new Vec4(
                    x: nrm.X,
                    y: nrm.Y,
                    z: nrm.Z,
                    w: 0f
                )
            );
            s.Normal = new Vec3(x: n.X, y: n.Y, z: n.Z);
            s.HasNormal = true;
        }
        else
            s.Normal = nrm;

        return s;
    }

    /// <summary>The graph evaluated at the reference point (uv=½, generated=0, normal=+Z) → fixed PBR.</summary>
    public SurfaceConstants Constants()
    {
        var s = Eval(
            uv: new Vec2(x: 0.5f, y: 0.5f),
            gen: Vec3.Zero,
            nrm: new Vec3(x: 0f, y: 0f, z: 1f)
        );
        return new SurfaceConstants(
            BaseR: s.BaseColor.X,
            BaseG: s.BaseColor.Y,
            BaseB: s.BaseColor.Z,
            BaseA: s.BaseColor.W,
            Metallic: s.Metallic,
            Roughness: s.Roughness,
            Specular: s.Specular,
            Clearcoat: s.Clearcoat,
            ClearcoatRoughness: s.ClearcoatRoughness,
            EmissiveR: s.Emission.X,
            EmissiveG: s.Emission.Y,
            EmissiveB: s.Emission.Z
        );
    }

    private Vec4 Root(int id, ShaderValueType expect, Vec4 fallback)
    {
        if (id < 0) return fallback;
        return ShaderCoerce.Eval(v: _reg[id], from: _instr[id].Type, to: expect);
    }

    private float RootScalar(int id, float fallback)
    {
        if (id < 0) return fallback;
        return ShaderCoerce.Scalar(v: _reg[id], from: _instr[id].Type);
    }

    private Vec4 Compute(in ShaderInstr ins, Vec2 uv, Vec3 gen, Vec3 nrm)
    {
        switch (ins.Op)
        {
            case ShaderOp.ConstFloat:
                return new Vec4(
                    x: ins.P0,
                    y: 0f,
                    z: 0f,
                    w: 0f
                );
            case ShaderOp.ConstVec3:
                return new Vec4(
                    x: ins.P0,
                    y: ins.P1,
                    z: ins.P2,
                    w: 0f
                );
            case ShaderOp.ConstVec4:
                return new Vec4(
                    x: ins.P0,
                    y: ins.P1,
                    z: ins.P2,
                    w: ins.P3
                );
            case ShaderOp.InputUv:
                return new Vec4(
                    x: uv.X,
                    y: uv.Y,
                    z: 0f,
                    w: 0f
                );
            case ShaderOp.InputGenerated:
            case ShaderOp.InputObject:
            case ShaderOp.InputPosition:
                return new Vec4(
                    x: gen.X,
                    y: gen.Y,
                    z: gen.Z,
                    w: 0f
                );
            case ShaderOp.InputNormal:
                return new Vec4(
                    x: nrm.X,
                    y: nrm.Y,
                    z: nrm.Z,
                    w: 0f
                );
            case ShaderOp.Coerce:
                return ShaderCoerce.Eval(
                    v: _reg[ins.Args[0]],
                    from: _instr[ins.Args[0]].Type,
                    to: ins.Type
                );
            case ShaderOp.Math:
                return new Vec4(
                    x: EvalMath(op: (int)ins.P0, a: _reg[ins.Args[0]].X, b: _reg[ins.Args[1]].X),
                    y: 0f,
                    z: 0f,
                    w: 0f
                );
            case ShaderOp.Clamp:
            {
                float lo = _reg[ins.Args[1]].X;
                float hi = _reg[ins.Args[2]].X;
                return new Vec4(
                    x: Math.Clamp(
                        value: _reg[ins.Args[0]].X,
                        min: lo,
                        max: MathF.Max(x: lo, y: hi)
                    ),
                    y: 0f,
                    z: 0f,
                    w: 0f
                );
            }
            case ShaderOp.MixColor:
                return EvalMix(
                    mode: (int)ins.P0,
                    fac: _reg[ins.Args[0]].X,
                    a: _reg[ins.Args[1]],
                    b: _reg[ins.Args[2]]
                );

            case ShaderOp.Mapping:
            {
                var v = V3(ins.Args[0]);
                var loc = V3(ins.Args[1]);
                var scl = V3(ins.Args[3]);
                return V4(
                    new Vec3(
                        x: (v.X * scl.X) + loc.X,
                        y: (v.Y * scl.Y) + loc.Y,
                        z: (v.Z * scl.Z) + loc.Z
                    )
                );
            }
            case ShaderOp.NoiseFac:
                return F(
                    NoiseFacImpl(
                        p: V3(ins.Args[0]),
                        scale: Sc(ins.Args[1]),
                        detail: Sc(ins.Args[2]),
                        rough: Sc(ins.Args[3]),
                        dist: Sc(ins.Args[4])
                    )
                );
            case ShaderOp.NoiseColor:
                return NoiseColorImpl(
                    p: V3(ins.Args[0]),
                    scale: Sc(ins.Args[1]),
                    detail: Sc(ins.Args[2]),
                    rough: Sc(ins.Args[3]),
                    dist: Sc(ins.Args[4])
                );
            case ShaderOp.GradientFac:
                return F(GradientImpl(type: (int)ins.P0, v: V3(ins.Args[0])));
            case ShaderOp.GradientColor:
            {
                float f = GradientImpl(type: (int)ins.P0, v: V3(ins.Args[0]));
                return new Vec4(
                    x: f,
                    y: f,
                    z: f,
                    w: 1f
                );
            }
            case ShaderOp.CheckerFac:
                return F(CheckerFacImpl(v: V3(ins.Args[0]), scale: Sc(ins.Args[3])));
            case ShaderOp.CheckerColor:
            {
                float fac = CheckerFacImpl(v: V3(ins.Args[0]), scale: Sc(ins.Args[3]));
                return Lerp(a: _reg[ins.Args[2]], b: _reg[ins.Args[1]], t: fac); // even → color1
            }
            case ShaderOp.WaveFac:
                return F(
                    WaveFacImpl(
                        v: V3(ins.Args[0]),
                        scale: Sc(ins.Args[1]),
                        dist: Sc(ins.Args[2]),
                        detail: Sc(ins.Args[3]),
                        type: (int)ins.P0,
                        profile: (int)ins.P1
                    )
                );
            case ShaderOp.WaveColor:
            {
                float f = WaveFacImpl(
                    v: V3(ins.Args[0]),
                    scale: Sc(ins.Args[1]),
                    dist: Sc(ins.Args[2]),
                    detail: Sc(ins.Args[3]),
                    type: (int)ins.P0,
                    profile: (int)ins.P1
                );
                return new Vec4(
                    x: f,
                    y: f,
                    z: f,
                    w: 1f
                );
            }
            case ShaderOp.VecMath:
                return V4(
                    VecMathVImpl(
                        op: (int)ins.P0,
                        a: V3(ins.Args[0]),
                        b: V3(ins.Args[1]),
                        scale: Sc(ins.Args[2])
                    )
                );
            case ShaderOp.VecMathScalar:
                return F(VecMathSImpl(op: (int)ins.P0, a: V3(ins.Args[0]), b: V3(ins.Args[1])));
            case ShaderOp.Combine:
                return new Vec4(
                    x: _reg[ins.Args[0]].X,
                    y: _reg[ins.Args[1]].X,
                    z: _reg[ins.Args[2]].X,
                    w: 0f
                );
            case ShaderOp.SeparateX: return F(V3(ins.Args[0]).X);
            case ShaderOp.SeparateY: return F(V3(ins.Args[0]).Y);
            case ShaderOp.SeparateZ: return F(V3(ins.Args[0]).Z);
            case ShaderOp.MapRange:
                return F(
                    MapRangeImpl(
                        v: _reg[ins.Args[0]].X,
                        fmin: _reg[ins.Args[1]].X,
                        fmax: _reg[ins.Args[2]].X,
                        tmin: _reg[ins.Args[3]].X,
                        tmax: _reg[ins.Args[4]].X,
                        clamp: ins.P0 > 0.5f
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
        return new Vec3(x: r.X, y: r.Y, z: r.Z);
    }

    private float Sc(int id) => _reg[id].X;

    private static Vec4 V4(Vec3 v)
    {
        return new Vec4(
            x: v.X,
            y: v.Y,
            z: v.Z,
            w: 0f
        );
    }

    private static Vec4 F(float v)
    {
        return new Vec4(
            x: v,
            y: 0f,
            z: 0f,
            w: 0f
        );
    }

    internal static float EvalMath(int op, float a, float b)
    {
        return (MathOp)op switch {
            MathOp.Add => a + b,
            MathOp.Subtract => a - b,
            MathOp.Multiply => a * b,
            MathOp.Divide => b != 0f ? a / b : 0f,
            MathOp.Power => MathF.Pow(x: MathF.Max(x: a, y: 0f), y: b),
            MathOp.Logarithm => a > 0f && b > 0f ? MathF.Log(a) / MathF.Log(b) : 0f,
            MathOp.Minimum => MathF.Min(x: a, y: b),
            MathOp.Maximum => MathF.Max(x: a, y: b),
            MathOp.Sqrt => MathF.Sqrt(MathF.Max(x: a, y: 0f)),
            MathOp.Absolute => MathF.Abs(a),
            MathOp.Sine => MathF.Sin(a),
            MathOp.Cosine => MathF.Cos(a),
            MathOp.Floor => MathF.Floor(a),
            MathOp.Fraction => a - MathF.Floor(a),
            MathOp.Modulo => b != 0f ? a - (b * MathF.Truncate(a / b)) : 0f,
            MathOp.GreaterThan => a > b ? 1f : 0f,
            MathOp.LessThan => a < b ? 1f : 0f,
            _ => a + b,
        };
    }

    private static Vec4 EvalMix(int mode, float fac, Vec4 a, Vec4 b)
    {
        fac = Math.Clamp(value: fac, min: 0f, max: 1f);
        var blended = (MixMode)mode switch {
            MixMode.Darken => Min(a: a, b: b),
            MixMode.Multiply => Mul(a: a, b: b),
            MixMode.Lighten => Max(a: a, b: b),
            MixMode.Screen => Sub(
                a: Vec4.One,
                b: Mul(a: Sub(a: Vec4.One, b: a), b: Sub(a: Vec4.One, b: b))
            ),
            MixMode.Add => a + b,
            MixMode.Subtract => a - b,
            MixMode.Difference => Abs(a - b),
            _ => b, // Mix
        };
        var rgb = Lerp(a: a, b: blended, t: fac);
        return new Vec4(
            x: rgb.X,
            y: rgb.Y,
            z: rgb.Z,
            w: a.W + ((b.W - a.W) * fac)
        );
    }

    private static Vec4 Lerp(Vec4 a, Vec4 b, float t) => a + ((b - a) * t);

    private static Vec4 Mul(Vec4 a, Vec4 b)
    {
        return new Vec4(
            x: a.X * b.X,
            y: a.Y * b.Y,
            z: a.Z * b.Z,
            w: a.W * b.W
        );
    }

    private static Vec4 Sub(Vec4 a, Vec4 b) => a - b;

    private static Vec4 Min(Vec4 a, Vec4 b)
    {
        return new Vec4(
            x: MathF.Min(x: a.X, y: b.X),
            y: MathF.Min(x: a.Y, y: b.Y),
            z: MathF.Min(x: a.Z, y: b.Z),
            w: MathF.Min(x: a.W, y: b.W)
        );
    }

    private static Vec4 Max(Vec4 a, Vec4 b)
    {
        return new Vec4(
            x: MathF.Max(x: a.X, y: b.X),
            y: MathF.Max(x: a.Y, y: b.Y),
            z: MathF.Max(x: a.Z, y: b.Z),
            w: MathF.Max(x: a.W, y: b.W)
        );
    }

    private static Vec4 Abs(Vec4 a)
    {
        return new Vec4(
            x: MathF.Abs(a.X),
            y: MathF.Abs(a.Y),
            z: MathF.Abs(a.Z),
            w: MathF.Abs(a.W)
        );
    }

    private static float Fract(float x) => x - MathF.Floor(x);

    private static Vec3 Fract3(Vec3 v) => new(x: Fract(v.X), y: Fract(v.Y), z: Fract(v.Z));

    private static float Lerp1(float a, float b, float t) => a + ((b - a) * t);

    // Dave Hoskins' hash13 — identical float math on CPU and in WGSL.
    private static float Hash13(Vec3 p)
    {
        var p3 = Fract3(p * 0.1031f);
        float dot = (p3.X * (p3.Z + 31.32f)) + (p3.Y * (p3.Y + 31.32f)) + (p3.Z * (p3.X + 31.32f));
        p3 = new Vec3(x: p3.X + dot, y: p3.Y + dot, z: p3.Z + dot);
        return Fract((p3.X + p3.Y) * p3.Z);
    }

    private static float VNoise(Vec3 p)
    {
        Vec3 i = new(x: MathF.Floor(p.X), y: MathF.Floor(p.Y), z: MathF.Floor(p.Z));
        Vec3 f = new(x: p.X - i.X, y: p.Y - i.Y, z: p.Z - i.Z);
        Vec3 u = new(
            x: f.X * f.X * (3f - (2f * f.X)),
            y: f.Y * f.Y * (3f - (2f * f.Y)),
            z: f.Z * f.Z * (3f - (2f * f.Z))
        );

        float C(float dx, float dy, float dz) =>
            Hash13(new Vec3(x: i.X + dx, y: i.Y + dy, z: i.Z + dz));

        float x00 = Lerp1(a: C(dx: 0, dy: 0, dz: 0), b: C(dx: 1, dy: 0, dz: 0), t: u.X);
        float x10 = Lerp1(a: C(dx: 0, dy: 1, dz: 0), b: C(dx: 1, dy: 1, dz: 0), t: u.X);
        float x01 = Lerp1(a: C(dx: 0, dy: 0, dz: 1), b: C(dx: 1, dy: 0, dz: 1), t: u.X);
        float x11 = Lerp1(a: C(dx: 0, dy: 1, dz: 1), b: C(dx: 1, dy: 1, dz: 1), t: u.X);
        return Lerp1(a: Lerp1(a: x00, b: x10, t: u.Y), b: Lerp1(a: x01, b: x11, t: u.Y), t: u.Z);
    }

    private static float Fbm(Vec3 p, float detail, float rough)
    {
        float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
        int oct = Math.Clamp(value: (int)detail, min: 1, max: 8);
        for (int i = 0; i < oct; i++)
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
        float w = dist * (VNoise(new Vec3(x: pp.X + 17f, y: pp.Y + 17f, z: pp.Z + 17f)) - 0.5f);
        return new Vec3(x: pp.X + w, y: pp.Y + w, z: pp.Z + w);
    }

    private static float NoiseFacImpl(Vec3 p, float scale, float detail, float rough, float dist) =>
        Fbm(p: WarpCoord(p: p, scale: scale, dist: dist), detail: detail, rough: rough);

    private static Vec4 NoiseColorImpl(Vec3 p, float scale, float detail, float rough, float dist)
    {
        var pp = WarpCoord(p: p, scale: scale, dist: dist);
        float r = Fbm(p: pp, detail: detail, rough: rough);
        float g = Fbm(
            p: new Vec3(x: pp.X + 13.5f, y: pp.Y + 13.5f, z: pp.Z + 13.5f),
            detail: detail,
            rough: rough
        );
        float b = Fbm(
            p: new Vec3(x: pp.X + 27.1f, y: pp.Y + 27.1f, z: pp.Z + 27.1f),
            detail: detail,
            rough: rough
        );
        return new Vec4(
            x: r,
            y: g,
            z: b,
            w: 1f
        );
    }

    private static float GradientImpl(int type, Vec3 v)
    {
        switch ((GradientType)type)
        {
            case GradientType.Quadratic:
            {
                float t = MathF.Max(x: v.X, y: 0f);
                return t * t;
            }
            case GradientType.Easing:
            {
                float t = Math.Clamp(value: v.X, min: 0f, max: 1f);
                return t * t * (3f - (2f * t));
            }
            case GradientType.Diagonal: return (v.X + v.Y) * 0.5f;
            case GradientType.Spherical: return MathF.Max(x: 1f - v.Length(), y: 0f);
            case GradientType.QuadraticSphere:
            {
                float s = MathF.Max(x: 1f - v.Length(), y: 0f);
                return s * s;
            }
            case GradientType.Radial: return (MathF.Atan2(y: v.Y, x: v.X) / (2f * Pi)) + 0.5f;
            default: return v.X; // Linear
        }
    }

    private static float CheckerFacImpl(Vec3 v, float scale)
    {
        float m = MathF.Floor(v.X * scale) + MathF.Floor(v.Y * scale) + MathF.Floor(v.Z * scale);
        float parity = m - (2f * MathF.Floor(m * 0.5f));
        return parity < 0.5f ? 1f : 0f; // even cell → color1
    }

    private static float WaveFacImpl(Vec3 v, float scale, float dist, float detail, int type,
        int profile)
    {
        float n = type == 1 ? v.Length() * scale : (v.X + v.Y + v.Z) * scale; // Rings vs Bands
        if (dist != 0f) n += dist * Fbm(p: v * scale, detail: detail, rough: 0.5f);
        return (WaveProfile)profile switch {
            WaveProfile.Saw => Fract(n / (2f * Pi)),
            WaveProfile.Triangle => MathF.Abs((Fract(n / (2f * Pi)) * 2f) - 1f),
            _ => 0.5f + (0.5f * MathF.Sin(n)), // Sine
        };
    }

    private static Vec3 VecMathVImpl(int op, Vec3 a, Vec3 b, float scale)
    {
        return (VecMathOp)op switch {
            VecMathOp.Add => a + b,
            VecMathOp.Subtract => a - b,
            VecMathOp.Multiply => new Vec3(x: a.X * b.X, y: a.Y * b.Y, z: a.Z * b.Z),
            VecMathOp.Divide => new Vec3(
                x: SafeDiv(a: a.X, b: b.X),
                y: SafeDiv(a: a.Y, b: b.Y),
                z: SafeDiv(a: a.Z, b: b.Z)
            ),
            VecMathOp.Cross => a.Cross(b),
            VecMathOp.Normalize => a.LengthSq() > 1e-12f ? a.Normalize() : Vec3.Zero,
            VecMathOp.Scale => a * scale,
            VecMathOp.Floor => new Vec3(
                x: MathF.Floor(a.X),
                y: MathF.Floor(a.Y),
                z: MathF.Floor(a.Z)
            ),
            VecMathOp.Fraction => new Vec3(x: Fract(a.X), y: Fract(a.Y), z: Fract(a.Z)),
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
        float d = fmax - fmin;
        float t = MathF.Abs(d) > 1e-8f ? (v - fmin) / d : 0f;
        float r = tmin + (t * (tmax - tmin));
        if (clamp)
        {
            r = Math.Clamp(
                value: r,
                min: MathF.Min(x: tmin, y: tmax),
                max: MathF.Max(x: tmin, y: tmax)
            );
        }

        return r;
    }

    private static float SafeDiv(float a, float b) => b != 0f ? a / b : 0f;

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
