using System.Globalization;
using System.Text;

namespace Zigote.Graphs.Shading;

/// <summary>
///     Generates WGSL from a <see cref="ShaderGraphProgram" />: a small stdlib prelude + a
///     <c>zg_surface(uv, gen, nrm) -> ZgSurface</c> function with one <c>let vN</c> per SSA
///     instruction.
///     Viewable today (the editor's "Generated WGSL" tab); the seam the future native per-material
///     pipeline will splice into <c>mesh_shader_source.wgsl</c>. Op semantics mirror
///     <see cref="CpuShaderEvaluator" /> so the preview matches the shader.
/// </summary>
public static class WgslShaderEmitter
{
    public static string Emit(ShaderGraphProgram program)
    {
        var sb = new StringBuilder();
        sb.Append(Prelude());
        sb.AppendLine();
        for (var i = 0; i < program.Ramps.Count; i++) sb.AppendLine(EmitRamp(i, program.Ramps[i]));
        sb.AppendLine(
            "fn zg_surface(uv: vec2<f32>, gen: vec3<f32>, nrm: vec3<f32>) -> ZgSurface {"
        );

        foreach (var ins in program.Instructions)
            sb.AppendLine(
                $"  let v{ins.Result}: {ShaderCoerce.WgslType(ins.Type)} = {Expr(ins, program)};"
            );

        var baseColor = Vec4Root(program, program.BaseColor, "vec4<f32>(0.8, 0.8, 0.8, 1.0)");
        var metallic = ScalarRoot(program, program.Metallic, 0f);
        var roughness = ScalarRoot(program, program.Roughness, 0.5f);
        var specular = ScalarRoot(program, program.Specular, 1f);
        var clearcoat = ScalarRoot(program, program.Clearcoat, 0f);
        var coatRough = ScalarRoot(program, program.ClearcoatRoughness, 0.03f);
        var emisColor = Vec4Root(program, program.Emission, "vec4<f32>(0.0, 0.0, 0.0, 1.0)");
        var emisStr = ScalarRoot(program, program.EmissionStrength, 0f);
        var normal = program.Normal >= 0
            ? Coerce(program, program.Normal, ShaderValueType.Vec3)
            : "nrm";

        sb.AppendLine("  var s: ZgSurface;");
        sb.AppendLine($"  s.base_color = {baseColor};");
        sb.AppendLine($"  s.metallic = {metallic};");
        sb.AppendLine($"  s.roughness = {roughness};");
        sb.AppendLine($"  s.specular = {specular};");
        sb.AppendLine($"  s.clearcoat = {clearcoat};");
        sb.AppendLine($"  s.clearcoat_roughness = {coatRough};");
        sb.AppendLine($"  s.emission = ({emisColor}).rgb * ({emisStr});");
        sb.AppendLine($"  s.normal = {normal};");
        sb.AppendLine("  return s;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Prelude()
    {
        return
            """
            struct ZgSurface {
              base_color: vec4<f32>,
              metallic: f32,
              roughness: f32,
              specular: f32,
              clearcoat: f32,
              clearcoat_roughness: f32,
              emission: vec3<f32>,
              normal: vec3<f32>,
            };

            const ZG_TAU: f32 = 6.2831853;

            fn zg_hash13(p: vec3<f32>) -> f32 {
              var p3 = fract(p * 0.1031);
              p3 = p3 + dot(p3, p3.zyx + vec3<f32>(31.32));
              return fract((p3.x + p3.y) * p3.z);
            }

            fn zg_vnoise(p: vec3<f32>) -> f32 {
              let i = floor(p);
              let f = fract(p);
              let u = f * f * (3.0 - 2.0 * f);
              let c000 = zg_hash13(i + vec3<f32>(0.0, 0.0, 0.0));
              let c100 = zg_hash13(i + vec3<f32>(1.0, 0.0, 0.0));
              let c010 = zg_hash13(i + vec3<f32>(0.0, 1.0, 0.0));
              let c110 = zg_hash13(i + vec3<f32>(1.0, 1.0, 0.0));
              let c001 = zg_hash13(i + vec3<f32>(0.0, 0.0, 1.0));
              let c101 = zg_hash13(i + vec3<f32>(1.0, 0.0, 1.0));
              let c011 = zg_hash13(i + vec3<f32>(0.0, 1.0, 1.0));
              let c111 = zg_hash13(i + vec3<f32>(1.0, 1.0, 1.0));
              let x00 = mix(c000, c100, u.x);
              let x10 = mix(c010, c110, u.x);
              let x01 = mix(c001, c101, u.x);
              let x11 = mix(c011, c111, u.x);
              return mix(mix(x00, x10, u.y), mix(x01, x11, u.y), u.z);
            }

            fn zg_fbm(p: vec3<f32>, detail: f32, rough: f32) -> f32 {
              var sum = 0.0;
              var amp = 1.0;
              var freq = 1.0;
              var norm = 0.0;
              let oct = clamp(i32(detail), 1, 8);
              for (var i = 0; i < oct; i = i + 1) {
                sum = sum + amp * zg_vnoise(p * freq);
                norm = norm + amp;
                amp = amp * rough;
                freq = freq * 2.0;
              }
              return select(sum, sum / norm, norm > 0.0);
            }

            fn zg_warp(p: vec3<f32>, scale: f32, dist: f32) -> vec3<f32> {
              let pp = p * scale;
              if (dist == 0.0) { return pp; }
              let w = dist * (zg_vnoise(pp + vec3<f32>(17.0)) - 0.5);
              return pp + vec3<f32>(w);
            }

            fn zg_noise_fac(p: vec3<f32>, scale: f32, detail: f32, rough: f32, dist: f32) -> f32 {
              return zg_fbm(zg_warp(p, scale, dist), detail, rough);
            }

            fn zg_noise_color(p: vec3<f32>, scale: f32, detail: f32, rough: f32, dist: f32) -> vec3<f32> {
              let pp = zg_warp(p, scale, dist);
              return vec3<f32>(zg_fbm(pp, detail, rough), zg_fbm(pp + vec3<f32>(13.5), detail, rough),
                               zg_fbm(pp + vec3<f32>(27.1), detail, rough));
            }

            fn zg_gradient(v: vec3<f32>, t: i32) -> f32 {
              if (t == 1) { let x = max(v.x, 0.0); return x * x; }
              if (t == 2) { let x = clamp(v.x, 0.0, 1.0); return x * x * (3.0 - 2.0 * x); }
              if (t == 3) { return (v.x + v.y) * 0.5; }
              if (t == 4) { return max(1.0 - length(v), 0.0); }
              if (t == 5) { let s = max(1.0 - length(v), 0.0); return s * s; }
              if (t == 6) { return atan2(v.y, v.x) / ZG_TAU + 0.5; }
              return v.x;
            }

            fn zg_checker_fac(v: vec3<f32>, scale: f32) -> f32 {
              let m = floor(v.x * scale) + floor(v.y * scale) + floor(v.z * scale);
              let parity = m - 2.0 * floor(m * 0.5);
              return select(0.0, 1.0, parity < 0.5);
            }

            fn zg_wave_fac(v: vec3<f32>, scale: f32, dist: f32, detail: f32, wtype: i32, profile: i32) -> f32 {
              var n = select((v.x + v.y + v.z) * scale, length(v) * scale, wtype == 1);
              if (dist != 0.0) { n = n + dist * zg_fbm(v * scale, detail, 0.5); }
              if (profile == 1) { return fract(n / ZG_TAU); }
              if (profile == 2) { return abs(fract(n / ZG_TAU) * 2.0 - 1.0); }
              return 0.5 + 0.5 * sin(n);
            }

            fn zg_vecmath_v(a: vec3<f32>, b: vec3<f32>, scale: f32, op: i32) -> vec3<f32> {
              if (op == 0) { return a + b; }
              if (op == 1) { return a - b; }
              if (op == 2) { return a * b; }
              if (op == 3) {
                return vec3<f32>(select(0.0, a.x / b.x, b.x != 0.0), select(0.0, a.y / b.y, b.y != 0.0),
                                 select(0.0, a.z / b.z, b.z != 0.0));
              }
              if (op == 4) { return cross(a, b); }
              if (op == 6) { let l = length(a); return select(vec3<f32>(0.0), a / l, l > 1e-6); }
              if (op == 8) { return a * scale; }
              if (op == 10) { return floor(a); }
              if (op == 11) { return fract(a); }
              return vec3<f32>(0.0);
            }

            fn zg_vecmath_s(a: vec3<f32>, b: vec3<f32>, op: i32) -> f32 {
              if (op == 5) { return dot(a, b); }
              if (op == 7) { return length(a); }
              if (op == 9) { return length(a - b); }
              return 0.0;
            }

            fn zg_map_range(v: f32, fmin: f32, fmax: f32, tmin: f32, tmax: f32, clamp_flag: i32) -> f32 {
              let d = fmax - fmin;
              let t = select(0.0, (v - fmin) / d, abs(d) > 1e-8);
              var r = tmin + t * (tmax - tmin);
              if (clamp_flag == 1) { r = clamp(r, min(tmin, tmax), max(tmin, tmax)); }
              return r;
            }

            fn zg_mapping(v: vec3<f32>, loc: vec3<f32>, scl: vec3<f32>) -> vec3<f32> {
              return v * scl + loc;
            }
            """;
    }

    // ── Per-instruction expression ──────────────────────────────────────────────

    private static string Expr(in ShaderInstr ins, ShaderGraphProgram p)
    {
        switch (ins.Op)
        {
            case ShaderOp.ConstFloat: return F(ins.P0);
            case ShaderOp.ConstVec3: return $"vec3<f32>({F(ins.P0)}, {F(ins.P1)}, {F(ins.P2)})";
            case ShaderOp.ConstVec4:
                return $"vec4<f32>({F(ins.P0)}, {F(ins.P1)}, {F(ins.P2)}, {F(ins.P3)})";
            case ShaderOp.InputUv: return "vec3<f32>(uv, 0.0)";
            case ShaderOp.InputGenerated:
            case ShaderOp.InputObject:
            case ShaderOp.InputPosition: return "gen";
            case ShaderOp.InputNormal: return "nrm";
            case ShaderOp.Coerce:
                return ShaderCoerce.Emit(
                    $"v{ins.Args[0]}",
                    p.Instructions[ins.Args[0]].Type,
                    ins.Type
                );
            case ShaderOp.Math:
                return EmitMath((int)ins.P0, $"v{ins.Args[0]}", $"v{ins.Args[1]}");
            case ShaderOp.Clamp:
                return
                    $"clamp(v{ins.Args[0]}, v{ins.Args[1]}, max(v{ins.Args[1]}, v{ins.Args[2]}))";
            case ShaderOp.MixColor:
                return EmitMix(
                    (int)ins.P0,
                    $"v{ins.Args[0]}",
                    $"v{ins.Args[1]}",
                    $"v{ins.Args[2]}"
                );

            case ShaderOp.Mapping:
                return $"zg_mapping(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[3]})";
            case ShaderOp.NoiseFac:
                return
                    $"zg_noise_fac(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[2]}, v{ins.Args[3]}, v{ins.Args[4]})";
            case ShaderOp.NoiseColor:
                return
                    $"vec4<f32>(zg_noise_color(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[2]}, v{ins.Args[3]}, v{ins.Args[4]}), 1.0)";
            case ShaderOp.GradientFac:
                return $"zg_gradient(v{ins.Args[0]}, {(int)ins.P0})";
            case ShaderOp.GradientColor:
                return $"vec4<f32>(vec3<f32>(zg_gradient(v{ins.Args[0]}, {(int)ins.P0})), 1.0)";
            case ShaderOp.CheckerFac:
                return $"zg_checker_fac(v{ins.Args[0]}, v{ins.Args[3]})";
            case ShaderOp.CheckerColor:
                return
                    $"mix(v{ins.Args[2]}, v{ins.Args[1]}, zg_checker_fac(v{ins.Args[0]}, v{ins.Args[3]}))";
            case ShaderOp.WaveFac:
                return
                    $"zg_wave_fac(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[2]}, v{ins.Args[3]}, {(int)ins.P0}, {(int)ins.P1})";
            case ShaderOp.WaveColor:
                return
                    $"vec4<f32>(vec3<f32>(zg_wave_fac(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[2]}, v{ins.Args[3]}, {(int)ins.P0}, {(int)ins.P1})), 1.0)";
            case ShaderOp.VecMath:
                return
                    $"zg_vecmath_v(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[2]}, {(int)ins.P0})";
            case ShaderOp.VecMathScalar:
                return $"zg_vecmath_s(v{ins.Args[0]}, v{ins.Args[1]}, {(int)ins.P0})";
            case ShaderOp.Combine:
                return $"vec3<f32>(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[2]})";
            case ShaderOp.SeparateX: return $"(v{ins.Args[0]}).x";
            case ShaderOp.SeparateY: return $"(v{ins.Args[0]}).y";
            case ShaderOp.SeparateZ: return $"(v{ins.Args[0]}).z";
            case ShaderOp.MapRange:
                return
                    $"zg_map_range(v{ins.Args[0]}, v{ins.Args[1]}, v{ins.Args[2]}, v{ins.Args[3]}, v{ins.Args[4]}, {(ins.P0 > 0.5f ? 1 : 0)})";
            case ShaderOp.ColorRampColor:
                return $"zg_ramp_{ins.Aux}(v{ins.Args[0]})";
            case ShaderOp.ColorRampAlpha:
                return $"zg_ramp_{ins.Aux}(v{ins.Args[0]}).a";

            default:
                throw new NotSupportedException($"WGSL emit has no case for {ins.Op}.");
        }
    }

    /// <summary>
    ///     Emit a per-ramp WGSL function with the stops baked in. Mirrors
    ///     <see cref="ShaderColorRamp.Sample" />.
    /// </summary>
    private static string EmitRamp(int index, ShaderColorRamp ramp)
    {
        var stops = ramp.Stops;
        var last = stops.Count - 1;
        var sb = new StringBuilder();
        sb.AppendLine($"fn zg_ramp_{index}(t: f32) -> vec4<f32> {{");
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            sb.AppendLine($"  let c{i} = vec4<f32>({F(s.R)}, {F(s.G)}, {F(s.B)}, {F(s.A)});");
        }

        sb.AppendLine($"  if (t <= {F(stops[0].Pos)}) {{ return c0; }}");
        sb.AppendLine($"  if (t >= {F(stops[last].Pos)}) {{ return c{last}; }}");
        for (var i = 0; i < last; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            var span = b.Pos - a.Pos;
            var fExpr = span > 1e-6f ? $"clamp((t - {F(a.Pos)}) / {F(span)}, 0.0, 1.0)" : "0.0";
            var g = ramp.Interpolation switch {
                RampInterpolation.Constant => "0.0",
                RampInterpolation.Ease => "f * f * (3.0 - 2.0 * f)",
                _ => "f",
            };
            sb.AppendLine(
                $"  if (t < {F(b.Pos)}) {{ let f = {fExpr}; return mix(c{i}, c{i + 1}, {g}); }}"
            );
        }

        sb.AppendLine($"  return c{last};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EmitMath(int op, string a, string b)
    {
        return (MathOp)op switch {
            MathOp.Add => $"({a} + {b})",
            MathOp.Subtract => $"({a} - {b})",
            MathOp.Multiply => $"({a} * {b})",
            MathOp.Divide => $"select(0.0, {a} / {b}, {b} != 0.0)",
            MathOp.Power => $"pow(max({a}, 0.0), {b})",
            MathOp.Logarithm => $"select(0.0, log({a}) / log({b}), {a} > 0.0 && {b} > 0.0)",
            MathOp.Minimum => $"min({a}, {b})",
            MathOp.Maximum => $"max({a}, {b})",
            MathOp.Sqrt => $"sqrt(max({a}, 0.0))",
            MathOp.Absolute => $"abs({a})",
            MathOp.Sine => $"sin({a})",
            MathOp.Cosine => $"cos({a})",
            MathOp.Floor => $"floor({a})",
            MathOp.Fraction => $"({a} - floor({a}))",
            MathOp.Modulo => $"select(0.0, {a} - {b} * trunc({a} / {b}), {b} != 0.0)",
            MathOp.GreaterThan => $"select(0.0, 1.0, {a} > {b})",
            MathOp.LessThan => $"select(0.0, 1.0, {a} < {b})",
            _ => $"({a} + {b})",
        };
    }

    private static string EmitMix(int mode, string fac, string a, string b)
    {
        var one = "vec4<f32>(1.0)";
        var blended = (MixMode)mode switch {
            MixMode.Darken => $"min({a}, {b})",
            MixMode.Multiply => $"({a} * {b})",
            MixMode.Lighten => $"max({a}, {b})",
            MixMode.Screen => $"({one} - ({one} - {a}) * ({one} - {b}))",
            MixMode.Add => $"({a} + {b})",
            MixMode.Subtract => $"({a} - {b})",
            MixMode.Difference => $"abs({a} - {b})",
            _ => b,
        };
        var f = $"clamp({fac}, 0.0, 1.0)";
        return $"vec4<f32>(mix(({a}).rgb, ({blended}).rgb, {f}), mix(({a}).a, ({b}).a, {f}))";
    }

    // ── Surface-root expressions ────────────────────────────────────────────────

    private static string ScalarRoot(ShaderGraphProgram p, int id, float fallback)
    {
        return id < 0 ? F(fallback) : Coerce(p, id, ShaderValueType.Float);
    }

    private static string Vec4Root(ShaderGraphProgram p, int id, string fallback)
    {
        return id < 0 ? fallback : Coerce(p, id, ShaderValueType.Vec4);
    }

    private static string Coerce(ShaderGraphProgram p, int id, ShaderValueType target)
    {
        return ShaderCoerce.Emit($"v{id}", p.Instructions[id].Type, target);
    }

    /// <summary>Format a float as a WGSL f32 literal (always with a decimal point).</summary>
    internal static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
        var s = v.ToString("0.0######", CultureInfo.InvariantCulture);
        return s;
    }
}