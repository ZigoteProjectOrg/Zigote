using Xunit;
using Zigote.Core.Math3D;
using Zigote.Graphs.Core;
using Zigote.Graphs.Shading;

namespace Zigote.Tests;

/// <summary>
///     Headless tests for the shader-graph codegen: lowering, the CPU evaluator (which drives the live
///     preview), value coercion, WGSL structure, and the constant-fold regression that proves the
///     rewrite
///     of <c>CompileMaterial</c> didn't change shipping material output. Pure logic — no native
///     engine.
/// </summary>
public class ShaderGraphTests
{
    private const float Eps = 1e-4f;

    // ── Graph builders ──────────────────────────────────────────────────────────

    private static (GraphDocument doc, GraphNode principled) NewMaterial()
    {
        var doc = new GraphDocument {
            DomainId = ShaderNodeLibrary.DomainId,
            SchemaId = ShaderNodeLibrary.MaterialSchema,
        };
        var p = Add(doc, ShaderNodeLibrary.Principled);
        var o = Add(doc, ShaderNodeLibrary.Output);
        Connect(
            doc,
            p,
            "out.bsdf",
            o,
            "in.surface"
        );
        return (doc, p);
    }

    private static GraphNode Add(GraphDocument doc, string defId)
    {
        var n = new GraphNode { DefinitionId = defId };
        doc.Nodes.Add(n);
        return n;
    }

    private static void Connect(GraphDocument doc, GraphNode from, string fromPin, GraphNode to,
        string toPin)
    {
        doc.Edges.Add(
            new GraphEdge {
                From = new GraphPinEndpoint(from.Id, fromPin),
                To = new GraphPinEndpoint(to.Id, toPin),
            }
        );
    }

    private static SurfaceConstants Constants(GraphDocument doc)
    {
        return ShaderGraphCompiler.Compile(doc).Constants;
    }

    // ── Constant-fold regression (the safety checkpoint) ────────────────────────

    [Fact]
    public void Default_Principled_HasShippingDefaults()
    {
        var (doc, _) = NewMaterial();
        var c = Constants(doc);
        Assert.Equal(0.8f, c.BaseR, Eps);
        Assert.Equal(0.8f, c.BaseG, Eps);
        Assert.Equal(0.8f, c.BaseB, Eps);
        Assert.Equal(0f, c.Metallic, Eps);
        Assert.Equal(0.5f, c.Roughness, Eps);
        Assert.Equal(1f, c.Specular, Eps);
        Assert.Equal(0f, c.Clearcoat, Eps);
        Assert.Equal(0.03f, c.ClearcoatRoughness, Eps);
        Assert.Equal(0f, c.EmissiveR, Eps);
    }

    [Fact]
    public void Principled_PropertyOverrides_ReadThrough()
    {
        var (doc, p) = NewMaterial();
        p.Properties["in.base_color"] = GraphValue.FromFloat4(
            0.72f,
            0.05f,
            0.06f,
            1f
        );
        p.Properties["in.metallic"] = GraphValue.FromFloat(0.9f);
        p.Properties["in.roughness"] = GraphValue.FromFloat(0.30f);
        p.Properties["in.clearcoat"] = GraphValue.FromFloat(1f);
        p.Properties["in.clearcoat_roughness"] = GraphValue.FromFloat(0.05f);

        var c = Constants(doc);
        Assert.Equal(0.72f, c.BaseR, Eps);
        Assert.Equal(0.05f, c.BaseG, Eps);
        Assert.Equal(0.06f, c.BaseB, Eps);
        Assert.Equal(0.9f, c.Metallic, Eps);
        Assert.Equal(0.30f, c.Roughness, Eps);
        Assert.Equal(1f, c.Clearcoat, Eps);
        Assert.Equal(0.05f, c.ClearcoatRoughness, Eps);
    }

    [Fact]
    public void RgbNode_DrivesBaseColor()
    {
        var (doc, p) = NewMaterial();
        var rgb = Add(doc, ShaderNodeLibrary.Rgb);
        rgb.Properties["color"] = GraphValue.FromFloat4(
            0.2f,
            0.4f,
            0.6f,
            1f
        );
        Connect(
            doc,
            rgb,
            "out.color",
            p,
            "in.base_color"
        );

        var c = Constants(doc);
        Assert.Equal(0.2f, c.BaseR, Eps);
        Assert.Equal(0.4f, c.BaseG, Eps);
        Assert.Equal(0.6f, c.BaseB, Eps);
    }

    [Fact]
    public void ValueNode_DrivesRoughness()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc, ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(0.7f);
        Connect(
            doc,
            v,
            "out.value",
            p,
            "in.roughness"
        );
        Assert.Equal(0.7f, Constants(doc).Roughness, Eps);
    }

    [Fact]
    public void MathNode_Add_FeedsRoughness()
    {
        var (doc, p) = NewMaterial();
        var m = Add(doc, ShaderNodeLibrary.Math);
        m.Properties["op"] = GraphValue.FromInt((int)MathOp.Add);
        m.Properties["in.a"] = GraphValue.FromFloat(0.25f);
        m.Properties["in.b"] = GraphValue.FromFloat(0.5f);
        Connect(
            doc,
            m,
            "out.result",
            p,
            "in.roughness"
        );
        Assert.Equal(0.75f, Constants(doc).Roughness, Eps);
    }

    [Fact]
    public void MixColor_HalfFactor_BlendsBaseColor()
    {
        var (doc, p) = NewMaterial();
        var mix = Add(doc, ShaderNodeLibrary.MixColor);
        mix.Properties["in.factor"] = GraphValue.FromFloat(0.5f);
        mix.Properties["in.a"] = GraphValue.FromFloat4(
            0f,
            0f,
            0f,
            1f
        );
        mix.Properties["in.b"] = GraphValue.FromFloat4(
            1f,
            1f,
            1f,
            1f
        );
        Connect(
            doc,
            mix,
            "out.result",
            p,
            "in.base_color"
        );

        var c = Constants(doc);
        Assert.Equal(0.5f, c.BaseR, Eps);
        Assert.Equal(0.5f, c.BaseG, Eps);
        Assert.Equal(0.5f, c.BaseB, Eps);
    }

    [Fact]
    public void MultiplyColor_MultipliesChannels()
    {
        var (doc, p) = NewMaterial();
        var mul = Add(doc, ShaderNodeLibrary.MulColor);
        mul.Properties["in.a"] = GraphValue.FromFloat4(
            0.5f,
            0.5f,
            0.5f,
            1f
        );
        mul.Properties["in.b"] = GraphValue.FromFloat4(
            0.5f,
            1f,
            1f,
            1f
        );
        Connect(
            doc,
            mul,
            "out.result",
            p,
            "in.base_color"
        );

        var c = Constants(doc);
        Assert.Equal(0.25f, c.BaseR, Eps);
        Assert.Equal(0.5f, c.BaseG, Eps);
        Assert.Equal(0.5f, c.BaseB, Eps);
    }

    [Fact]
    public void Clamp_ClampsConnectedValue()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc, ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(2f);
        var clamp = Add(doc, ShaderNodeLibrary.Clamp);
        clamp.Properties["in.min"] = GraphValue.FromFloat(0f);
        clamp.Properties["in.max"] = GraphValue.FromFloat(1f);
        Connect(
            doc,
            v,
            "out.value",
            clamp,
            "in.value"
        );
        Connect(
            doc,
            clamp,
            "out.result",
            p,
            "in.roughness"
        );
        Assert.Equal(1f, Constants(doc).Roughness, Eps);
    }

    [Fact]
    public void Emission_PremultipliesColorByStrength()
    {
        var (doc, p) = NewMaterial();
        p.Properties["in.emission"] = GraphValue.FromFloat4(
            1f,
            0f,
            0f,
            1f
        );
        p.Properties["in.emission_strength"] = GraphValue.FromFloat(2f);
        var c = Constants(doc);
        Assert.Equal(2f, c.EmissiveR, Eps);
        Assert.Equal(0f, c.EmissiveG, Eps);
    }

    // ── Coercion ────────────────────────────────────────────────────────────────

    [Fact]
    public void Coerce_ColorToFloat_IsLuminance()
    {
        var (doc, p) = NewMaterial();
        var rgb = Add(doc, ShaderNodeLibrary.Rgb);
        rgb.Properties["color"] = GraphValue.FromFloat4(
            0.2f,
            0.4f,
            0.6f,
            1f
        );
        Connect(
            doc,
            rgb,
            "out.color",
            p,
            "in.roughness"
        ); // Color → Float
        var expected = 0.2f * ShaderCoerce.LumR + 0.4f * ShaderCoerce.LumG +
                       0.6f * ShaderCoerce.LumB;
        Assert.Equal(expected, Constants(doc).Roughness, Eps);
    }

    [Fact]
    public void Coerce_FloatToColor_Splats()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc, ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(0.3f);
        Connect(
            doc,
            v,
            "out.value",
            p,
            "in.base_color"
        ); // Float → Color
        var c = Constants(doc);
        Assert.Equal(0.3f, c.BaseR, Eps);
        Assert.Equal(0.3f, c.BaseG, Eps);
        Assert.Equal(0.3f, c.BaseB, Eps);
    }

    [Fact]
    public void Coerce_Matrix_RoundTrips()
    {
        var v3 = new Vec4(
            0.2f,
            0.4f,
            0.6f,
            0f
        );
        // Float → Vec4 splats rgb, alpha 1.
        var f2V4 = ShaderCoerce.Eval(
            new Vec4(
                0.5f,
                0f,
                0f,
                0f
            ),
            ShaderValueType.Float,
            ShaderValueType.Vec4
        );
        Assert.Equal(
            new Vec4(
                0.5f,
                0.5f,
                0.5f,
                1f
            ),
            f2V4
        );
        // Vec3 → Vec4 appends 1.
        var v3V4 = ShaderCoerce.Eval(v3, ShaderValueType.Vec3, ShaderValueType.Vec4);
        Assert.Equal(
            new Vec4(
                0.2f,
                0.4f,
                0.6f,
                1f
            ),
            v3V4
        );
        // Vec4 → Vec3 drops alpha.
        var v4V3 = ShaderCoerce.Eval(
            new Vec4(
                0.2f,
                0.4f,
                0.6f,
                0.9f
            ),
            ShaderValueType.Vec4,
            ShaderValueType.Vec3
        );
        Assert.Equal(0f, v4V3.W, Eps);
        // Vec3 → Float luminance.
        var lum = ShaderCoerce.Scalar(v3, ShaderValueType.Vec3);
        Assert.Equal(
            0.2f * ShaderCoerce.LumR + 0.4f * ShaderCoerce.LumG + 0.6f * ShaderCoerce.LumB,
            lum,
            Eps
        );
    }

    // ── Diagnostics / structure ─────────────────────────────────────────────────

    [Fact]
    public void NoOutput_FailsWithSG0001()
    {
        var doc = new GraphDocument { DomainId = ShaderNodeLibrary.DomainId };
        var compiled = ShaderGraphCompiler.Compile(doc);
        Assert.False(compiled.Success);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "SG0001");
    }

    [Fact]
    public void Cycle_IsDiagnosed_WithoutThrowing()
    {
        var (doc, p) = NewMaterial();
        var m = Add(doc, ShaderNodeLibrary.Math);
        Connect(
            doc,
            m,
            "out.result",
            m,
            "in.a"
        ); // self-loop
        Connect(
            doc,
            m,
            "out.result",
            p,
            "in.roughness"
        );
        var compiled = ShaderGraphCompiler.Compile(doc);
        Assert.True(compiled.Success); // warning, not error
        Assert.Contains(compiled.Diagnostics, d => d.Code == "SG0010");
    }

    [Fact]
    public void Wgsl_HasSurfaceFunction_AndIsBalanced()
    {
        var (doc, p) = NewMaterial();
        var rgb = Add(doc, ShaderNodeLibrary.Rgb);
        rgb.Properties["color"] = GraphValue.FromFloat4(
            0.2f,
            0.4f,
            0.6f,
            1f
        );
        Connect(
            doc,
            rgb,
            "out.color",
            p,
            "in.base_color"
        );

        var wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains("struct ZgSurface", wgsl);
        Assert.Contains(
            "fn zg_surface(uv: vec2<f32>, gen: vec3<f32>, nrm: vec3<f32>) -> ZgSurface",
            wgsl
        );
        Assert.Contains("return s;", wgsl);
        Assert.Equal(CountChar(wgsl, '{'), CountChar(wgsl, '}'));
        AssertSsaDeclaredBeforeUse(wgsl);
    }

    [Fact]
    public void Wgsl_DefaultGraph_EmitsConstantSurface()
    {
        var (doc, _) = NewMaterial();
        var wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains("vec4<f32>(0.8, 0.8, 0.8, 1.0)", wgsl); // base colour
        Assert.Contains("let v", wgsl);
        Assert.Contains("s.roughness =", wgsl);
        Assert.Contains("s.emission = (", wgsl); // emission = colour.rgb * strength
        Assert.DoesNotContain("NaN", wgsl);
    }

    [Fact]
    public void Constant_Factory_RoundTripsThroughEvaluator()
    {
        var c = new SurfaceConstants(
            0.1f,
            0.2f,
            0.3f,
            1f,
            0.4f,
            0.6f,
            1.2f,
            0.5f,
            0.07f,
            1f,
            0f,
            0f
        );
        var compiled = CompiledShaderGraph.Constant(c);
        var eval = new CpuShaderEvaluator(compiled.Program).Constants();
        Assert.Equal(0.1f, eval.BaseR, Eps);
        Assert.Equal(0.4f, eval.Metallic, Eps);
        Assert.Equal(0.6f, eval.Roughness, Eps);
        Assert.Equal(1.2f, eval.Specular, Eps);
        Assert.Equal(0.5f, eval.Clearcoat, Eps);
        Assert.Equal(1f, eval.EmissiveR, Eps);
    }

    [Fact]
    public void TextureNode_RecordedAsTextureRef_BaseColorWhite()
    {
        var (doc, p) = NewMaterial();
        var tex = Add(doc, ShaderNodeLibrary.TexImage);
        tex.Properties["path"] = GraphValue.FromString("textures/stone.png");
        Connect(
            doc,
            tex,
            "out.color",
            p,
            "in.base_color"
        );

        var compiled = ShaderGraphCompiler.Compile(doc);
        Assert.Equal("textures/stone.png", compiled.TexturePath(TextureSlot.BaseColor));
        Assert.Equal(1f, compiled.Constants.BaseR, Eps); // texture drives colour → factor white
    }

    // ── Procedural nodes ────────────────────────────────────────────────────────

    private static CpuShaderEvaluator.SurfaceSample EvalAt(GraphDocument doc, Vec3 gen)
    {
        var program = ShaderGraphCompiler.Compile(doc).Program;
        return new CpuShaderEvaluator(program).Eval(new Vec2(0f, 0f), gen, new Vec3(0f, 0f, 1f));
    }

    [Fact]
    public void Library_ContainsAllProceduralNodes()
    {
        var ids = ShaderNodeLibrary.Definitions.Select(d => d.Id).ToHashSet();
        foreach (var id in new[] {
                     ShaderNodeLibrary.TexCoord,
                     ShaderNodeLibrary.Mapping,
                     ShaderNodeLibrary.Noise,
                     ShaderNodeLibrary.Gradient,
                     ShaderNodeLibrary.Checker,
                     ShaderNodeLibrary.Wave,
                     ShaderNodeLibrary.VecMath,
                     ShaderNodeLibrary.MapRange,
                     ShaderNodeLibrary.SeparateXyz,
                     ShaderNodeLibrary.CombineXyz,
                 })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void Checker_AlternatesByCell()
    {
        var (doc, p) = NewMaterial();
        var checker = Add(doc, ShaderNodeLibrary.Checker);
        Connect(
            doc,
            checker,
            "out.color",
            p,
            "in.base_color"
        ); // vector defaults to Generated coords
        Assert.Equal(
            0.8f,
            EvalAt(doc, new Vec3(0f, 0f, 0f)).BaseColor.X,
            1e-3f
        ); // even cell → color1
        Assert.Equal(
            0.2f,
            EvalAt(doc, new Vec3(0.3f, 0f, 0f)).BaseColor.X,
            1e-3f
        ); // floor(1.5)=1 → odd → color2
    }

    [Fact]
    public void Gradient_Linear_IsXCoordinate()
    {
        var (doc, p) = NewMaterial();
        var grad = Add(doc, ShaderNodeLibrary.Gradient);
        Connect(
            doc,
            grad,
            "out.fac",
            p,
            "in.roughness"
        );
        Assert.Equal(0.4f, EvalAt(doc, new Vec3(0.4f, 0f, 0f)).Roughness, 1e-3f);
    }

    [Fact]
    public void VectorMath_Dot_FeedsScalarOutput()
    {
        var (doc, p) = NewMaterial();
        var vm = Add(doc, ShaderNodeLibrary.VecMath);
        vm.Properties["op"] = GraphValue.FromInt((int)VecMathOp.Dot);
        vm.Properties["in.a"] = GraphValue.FromFloat3(1f, 2f, 3f);
        vm.Properties["in.b"] = GraphValue.FromFloat3(0.5f, 0.5f, 0.5f);
        Connect(
            doc,
            vm,
            "out.value",
            p,
            "in.roughness"
        );
        Assert.Equal(3f, EvalAt(doc, Vec3.Zero).Roughness, 1e-3f); // (1+2+3)*0.5
    }

    [Fact]
    public void MapRange_ClampsWhenEnabled()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc, ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(2f);
        var mr = Add(doc, ShaderNodeLibrary.MapRange);
        mr.Properties["clamp"] = GraphValue.FromBool(true);
        mr.Properties["in.from_max"] = GraphValue.FromFloat(1f);
        mr.Properties["in.to_max"] = GraphValue.FromFloat(10f);
        Connect(
            doc,
            v,
            "out.value",
            mr,
            "in.value"
        );
        Connect(
            doc,
            mr,
            "out.result",
            p,
            "in.roughness"
        );
        Assert.Equal(
            10f,
            EvalAt(doc, Vec3.Zero).Roughness,
            1e-3f
        ); // 2→20 unclamped, clamped to to_max=10
    }

    [Fact]
    public void SeparateCombine_RoundTrips()
    {
        var (doc, p) = NewMaterial();
        var comb = Add(doc, ShaderNodeLibrary.CombineXyz);
        comb.Properties["in.x"] = GraphValue.FromFloat(0.1f);
        comb.Properties["in.y"] = GraphValue.FromFloat(0.2f);
        comb.Properties["in.z"] = GraphValue.FromFloat(0.3f);
        var sep = Add(doc, ShaderNodeLibrary.SeparateXyz);
        Connect(
            doc,
            comb,
            "out.vector",
            sep,
            "in.vector"
        );
        Connect(
            doc,
            sep,
            "out.y",
            p,
            "in.roughness"
        );
        Assert.Equal(0.2f, EvalAt(doc, Vec3.Zero).Roughness, 1e-3f);
    }

    [Fact]
    public void Noise_IsDeterministic_AndBounded()
    {
        var (doc, p) = NewMaterial();
        var noise = Add(doc, ShaderNodeLibrary.Noise);
        Connect(
            doc,
            noise,
            "out.fac",
            p,
            "in.roughness"
        );
        var at = new Vec3(0.31f, 0.42f, 0.53f);
        var r1 = EvalAt(doc, at).Roughness;
        var r2 = EvalAt(doc, at).Roughness;
        Assert.Equal(r1, r2, 1e-5f);
        Assert.InRange(r1, -0.01f, 1.01f);
    }

    [Fact]
    public void Wgsl_NoiseGraph_IncludesStdlib_AndIsWellFormed()
    {
        var (doc, p) = NewMaterial();
        var noise = Add(doc, ShaderNodeLibrary.Noise);
        Connect(
            doc,
            noise,
            "out.fac",
            p,
            "in.roughness"
        );
        var wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains("fn zg_hash13", wgsl);
        Assert.Contains("fn zg_noise_fac", wgsl);
        Assert.Contains("zg_noise_fac(", wgsl); // a call site inside zg_surface
        Assert.Equal(CountChar(wgsl, '{'), CountChar(wgsl, '}'));
        AssertSsaDeclaredBeforeUse(wgsl);
    }

    // ── Color Ramp ──────────────────────────────────────────────────────────────

    [Fact]
    public void ColorRamp_DefaultBlackToWhite_SamplesByFac()
    {
        foreach (var (fac, expect) in new[] {
                     (0f, 0f),
                     (0.5f, 0.5f),
                     (1f, 1f),
                 })
        {
            var (doc, p) = NewMaterial();
            var ramp = Add(doc, ShaderNodeLibrary.ColorRamp);
            ramp.Properties["in.fac"] = GraphValue.FromFloat(fac);
            Connect(
                doc,
                ramp,
                "out.color",
                p,
                "in.base_color"
            );
            Assert.Equal(expect, Constants(doc).BaseR, 1e-3f);
        }
    }

    [Fact]
    public void ColorRamp_CustomStops_DriveColor()
    {
        var (doc, p) = NewMaterial();
        var ramp = Add(doc, ShaderNodeLibrary.ColorRamp);
        ramp.Properties["ramp"] = GraphValue.FromString(
            ShaderRampJson.Serialize(
                [
                    new ShaderRampStop(
                        0f,
                        1f,
                        0f,
                        0f,
                        1f
                    ),
                    new ShaderRampStop(
                        1f,
                        0f,
                        0f,
                        1f,
                        1f
                    ),
                ]
            )
        );
        ramp.Properties["in.fac"] = GraphValue.FromFloat(0f);
        Connect(
            doc,
            ramp,
            "out.color",
            p,
            "in.base_color"
        );
        var c = Constants(doc);
        Assert.Equal(1f, c.BaseR, 1e-3f); // first stop is red
        Assert.Equal(0f, c.BaseB, 1e-3f);
    }

    [Fact]
    public void ShaderRampJson_RoundTrips()
    {
        IReadOnlyList<ShaderRampStop> stops = [
            new(
                0f,
                1f,
                0f,
                0f,
                1f
            ),
            new(
                0.5f,
                0f,
                1f,
                0f,
                1f
            ),
            new(
                1f,
                0f,
                0f,
                1f,
                1f
            ),
        ];
        var parsed = ShaderRampJson.Parse(ShaderRampJson.Serialize(stops));
        Assert.Equal(3, parsed.Count);
        Assert.Equal(0.5f, parsed[1].Pos, 1e-3f);
        Assert.Equal(1f, parsed[1].G, 1e-3f);
        Assert.Equal(1f, parsed[2].B, 1e-3f);
    }

    [Fact]
    public void Wgsl_ColorRamp_EmitsRampFunction()
    {
        var (doc, p) = NewMaterial();
        var ramp = Add(doc, ShaderNodeLibrary.ColorRamp);
        Connect(
            doc,
            ramp,
            "out.color",
            p,
            "in.base_color"
        );
        var wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains("fn zg_ramp_0", wgsl);
        Assert.Contains("zg_ramp_0(", wgsl);
        Assert.Equal(CountChar(wgsl, '{'), CountChar(wgsl, '}'));
        AssertSsaDeclaredBeforeUse(wgsl);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static int CountChar(string s, char c)
    {
        var n = 0;
        foreach (var ch in s)
            if (ch == c)
                n++;
        return n;
    }

    private static void AssertSsaDeclaredBeforeUse(string wgsl)
    {
        var declared = new HashSet<int>();
        foreach (var line in wgsl.Split('\n'))
        {
            var declId = DeclIndex(line);
            foreach (var use in FindRefs(line))
            {
                if (use == declId) continue; // the LHS being declared on this line
                Assert.True(
                    declared.Contains(use),
                    $"WGSL uses v{use} before it is declared:\n{line}"
                );
            }

            if (declId >= 0) declared.Add(declId);
        }
    }

    private static IEnumerable<int> FindRefs(string line)
    {
        for (var i = 0; i + 1 < line.Length; i++)
            if (line[i] == 'v' && char.IsDigit(line[i + 1]) &&
                (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
            {
                var j = i + 1;
                while (j < line.Length && char.IsDigit(line[j])) j++;
                yield return int.Parse(line[(i + 1)..j]);
                i = j;
            }
    }

    private static int DeclIndex(string line)
    {
        var t = line.TrimStart();
        if (!t.StartsWith("let v", StringComparison.Ordinal)) return -1;
        var colon = t.IndexOf(':');
        if (colon < 5) return -1;
        return int.TryParse(t[5..colon], out var id) ? id : -1;
    }
}