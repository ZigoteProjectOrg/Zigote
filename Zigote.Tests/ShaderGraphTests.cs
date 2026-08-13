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
        var p = Add(doc: doc, defId: ShaderNodeLibrary.Principled);
        var o = Add(doc: doc, defId: ShaderNodeLibrary.Output);
        Connect(
            doc: doc,
            from: p,
            fromPin: "out.bsdf",
            to: o,
            toPin: "in.surface"
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
                From = new GraphPinEndpoint(NodeId: from.Id, PinId: fromPin),
                To = new GraphPinEndpoint(NodeId: to.Id, PinId: toPin),
            }
        );
    }

    private static SurfaceConstants Constants(GraphDocument doc) =>
        ShaderGraphCompiler.Compile(doc).Constants;

    // ── Constant-fold regression (the safety checkpoint) ────────────────────────

    [Fact]
    public void Default_Principled_HasShippingDefaults()
    {
        var (doc, _) = NewMaterial();
        var c = Constants(doc);
        Assert.Equal(expected: 0.8f, actual: c.BaseR, tolerance: Eps);
        Assert.Equal(expected: 0.8f, actual: c.BaseG, tolerance: Eps);
        Assert.Equal(expected: 0.8f, actual: c.BaseB, tolerance: Eps);
        Assert.Equal(expected: 0f, actual: c.Metallic, tolerance: Eps);
        Assert.Equal(expected: 0.5f, actual: c.Roughness, tolerance: Eps);
        Assert.Equal(expected: 1f, actual: c.Specular, tolerance: Eps);
        Assert.Equal(expected: 0f, actual: c.Clearcoat, tolerance: Eps);
        Assert.Equal(expected: 0.03f, actual: c.ClearcoatRoughness, tolerance: Eps);
        Assert.Equal(expected: 0f, actual: c.EmissiveR, tolerance: Eps);
    }

    [Fact]
    public void Principled_PropertyOverrides_ReadThrough()
    {
        var (doc, p) = NewMaterial();
        p.Properties["in.base_color"] = GraphValue.FromFloat4(
            x: 0.72f,
            y: 0.05f,
            z: 0.06f,
            w: 1f
        );
        p.Properties["in.metallic"] = GraphValue.FromFloat(0.9f);
        p.Properties["in.roughness"] = GraphValue.FromFloat(0.30f);
        p.Properties["in.clearcoat"] = GraphValue.FromFloat(1f);
        p.Properties["in.clearcoat_roughness"] = GraphValue.FromFloat(0.05f);

        var c = Constants(doc);
        Assert.Equal(expected: 0.72f, actual: c.BaseR, tolerance: Eps);
        Assert.Equal(expected: 0.05f, actual: c.BaseG, tolerance: Eps);
        Assert.Equal(expected: 0.06f, actual: c.BaseB, tolerance: Eps);
        Assert.Equal(expected: 0.9f, actual: c.Metallic, tolerance: Eps);
        Assert.Equal(expected: 0.30f, actual: c.Roughness, tolerance: Eps);
        Assert.Equal(expected: 1f, actual: c.Clearcoat, tolerance: Eps);
        Assert.Equal(expected: 0.05f, actual: c.ClearcoatRoughness, tolerance: Eps);
    }

    [Fact]
    public void RgbNode_DrivesBaseColor()
    {
        var (doc, p) = NewMaterial();
        var rgb = Add(doc: doc, defId: ShaderNodeLibrary.Rgb);
        rgb.Properties["color"] = GraphValue.FromFloat4(
            x: 0.2f,
            y: 0.4f,
            z: 0.6f,
            w: 1f
        );
        Connect(
            doc: doc,
            from: rgb,
            fromPin: "out.color",
            to: p,
            toPin: "in.base_color"
        );

        var c = Constants(doc);
        Assert.Equal(expected: 0.2f, actual: c.BaseR, tolerance: Eps);
        Assert.Equal(expected: 0.4f, actual: c.BaseG, tolerance: Eps);
        Assert.Equal(expected: 0.6f, actual: c.BaseB, tolerance: Eps);
    }

    [Fact]
    public void ValueNode_DrivesRoughness()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc: doc, defId: ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(0.7f);
        Connect(
            doc: doc,
            from: v,
            fromPin: "out.value",
            to: p,
            toPin: "in.roughness"
        );
        Assert.Equal(expected: 0.7f, actual: Constants(doc).Roughness, tolerance: Eps);
    }

    [Fact]
    public void MathNode_Add_FeedsRoughness()
    {
        var (doc, p) = NewMaterial();
        var m = Add(doc: doc, defId: ShaderNodeLibrary.Math);
        m.Properties["op"] = GraphValue.FromInt((int)MathOp.Add);
        m.Properties["in.a"] = GraphValue.FromFloat(0.25f);
        m.Properties["in.b"] = GraphValue.FromFloat(0.5f);
        Connect(
            doc: doc,
            from: m,
            fromPin: "out.result",
            to: p,
            toPin: "in.roughness"
        );
        Assert.Equal(expected: 0.75f, actual: Constants(doc).Roughness, tolerance: Eps);
    }

    [Fact]
    public void MixColor_HalfFactor_BlendsBaseColor()
    {
        var (doc, p) = NewMaterial();
        var mix = Add(doc: doc, defId: ShaderNodeLibrary.MixColor);
        mix.Properties["in.factor"] = GraphValue.FromFloat(0.5f);
        mix.Properties["in.a"] = GraphValue.FromFloat4(
            x: 0f,
            y: 0f,
            z: 0f,
            w: 1f
        );
        mix.Properties["in.b"] = GraphValue.FromFloat4(
            x: 1f,
            y: 1f,
            z: 1f,
            w: 1f
        );
        Connect(
            doc: doc,
            from: mix,
            fromPin: "out.result",
            to: p,
            toPin: "in.base_color"
        );

        var c = Constants(doc);
        Assert.Equal(expected: 0.5f, actual: c.BaseR, tolerance: Eps);
        Assert.Equal(expected: 0.5f, actual: c.BaseG, tolerance: Eps);
        Assert.Equal(expected: 0.5f, actual: c.BaseB, tolerance: Eps);
    }

    [Fact]
    public void MultiplyColor_MultipliesChannels()
    {
        var (doc, p) = NewMaterial();
        var mul = Add(doc: doc, defId: ShaderNodeLibrary.MulColor);
        mul.Properties["in.a"] = GraphValue.FromFloat4(
            x: 0.5f,
            y: 0.5f,
            z: 0.5f,
            w: 1f
        );
        mul.Properties["in.b"] = GraphValue.FromFloat4(
            x: 0.5f,
            y: 1f,
            z: 1f,
            w: 1f
        );
        Connect(
            doc: doc,
            from: mul,
            fromPin: "out.result",
            to: p,
            toPin: "in.base_color"
        );

        var c = Constants(doc);
        Assert.Equal(expected: 0.25f, actual: c.BaseR, tolerance: Eps);
        Assert.Equal(expected: 0.5f, actual: c.BaseG, tolerance: Eps);
        Assert.Equal(expected: 0.5f, actual: c.BaseB, tolerance: Eps);
    }

    [Fact]
    public void Clamp_ClampsConnectedValue()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc: doc, defId: ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(2f);
        var clamp = Add(doc: doc, defId: ShaderNodeLibrary.Clamp);
        clamp.Properties["in.min"] = GraphValue.FromFloat(0f);
        clamp.Properties["in.max"] = GraphValue.FromFloat(1f);
        Connect(
            doc: doc,
            from: v,
            fromPin: "out.value",
            to: clamp,
            toPin: "in.value"
        );
        Connect(
            doc: doc,
            from: clamp,
            fromPin: "out.result",
            to: p,
            toPin: "in.roughness"
        );
        Assert.Equal(expected: 1f, actual: Constants(doc).Roughness, tolerance: Eps);
    }

    [Fact]
    public void Emission_PremultipliesColorByStrength()
    {
        var (doc, p) = NewMaterial();
        p.Properties["in.emission"] = GraphValue.FromFloat4(
            x: 1f,
            y: 0f,
            z: 0f,
            w: 1f
        );
        p.Properties["in.emission_strength"] = GraphValue.FromFloat(2f);
        var c = Constants(doc);
        Assert.Equal(expected: 2f, actual: c.EmissiveR, tolerance: Eps);
        Assert.Equal(expected: 0f, actual: c.EmissiveG, tolerance: Eps);
    }

    // ── Coercion ────────────────────────────────────────────────────────────────

    [Fact]
    public void Coerce_ColorToFloat_IsLuminance()
    {
        var (doc, p) = NewMaterial();
        var rgb = Add(doc: doc, defId: ShaderNodeLibrary.Rgb);
        rgb.Properties["color"] = GraphValue.FromFloat4(
            x: 0.2f,
            y: 0.4f,
            z: 0.6f,
            w: 1f
        );
        Connect(
            doc: doc,
            from: rgb,
            fromPin: "out.color",
            to: p,
            toPin: "in.roughness"
        ); // Color → Float
        float expected = (0.2f * ShaderCoerce.LumR) + (0.4f * ShaderCoerce.LumG) +
                         (0.6f * ShaderCoerce.LumB);
        Assert.Equal(expected: expected, actual: Constants(doc).Roughness, tolerance: Eps);
    }

    [Fact]
    public void Coerce_FloatToColor_Splats()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc: doc, defId: ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(0.3f);
        Connect(
            doc: doc,
            from: v,
            fromPin: "out.value",
            to: p,
            toPin: "in.base_color"
        ); // Float → Color
        var c = Constants(doc);
        Assert.Equal(expected: 0.3f, actual: c.BaseR, tolerance: Eps);
        Assert.Equal(expected: 0.3f, actual: c.BaseG, tolerance: Eps);
        Assert.Equal(expected: 0.3f, actual: c.BaseB, tolerance: Eps);
    }

    [Fact]
    public void Coerce_Matrix_RoundTrips()
    {
        var v3 = new Vec4(
            x: 0.2f,
            y: 0.4f,
            z: 0.6f,
            w: 0f
        );
        // Float → Vec4 splats rgb, alpha 1.
        var f2V4 = ShaderCoerce.Eval(
            v: new Vec4(
                x: 0.5f,
                y: 0f,
                z: 0f,
                w: 0f
            ),
            from: ShaderValueType.Float,
            to: ShaderValueType.Vec4
        );
        Assert.Equal(
            expected: new Vec4(
                x: 0.5f,
                y: 0.5f,
                z: 0.5f,
                w: 1f
            ),
            actual: f2V4
        );
        // Vec3 → Vec4 appends 1.
        var v3V4 = ShaderCoerce.Eval(v: v3, from: ShaderValueType.Vec3, to: ShaderValueType.Vec4);
        Assert.Equal(
            expected: new Vec4(
                x: 0.2f,
                y: 0.4f,
                z: 0.6f,
                w: 1f
            ),
            actual: v3V4
        );
        // Vec4 → Vec3 drops alpha.
        var v4V3 = ShaderCoerce.Eval(
            v: new Vec4(
                x: 0.2f,
                y: 0.4f,
                z: 0.6f,
                w: 0.9f
            ),
            from: ShaderValueType.Vec4,
            to: ShaderValueType.Vec3
        );
        Assert.Equal(expected: 0f, actual: v4V3.W, tolerance: Eps);
        // Vec3 → Float luminance.
        float lum = ShaderCoerce.Scalar(v: v3, from: ShaderValueType.Vec3);
        Assert.Equal(
            expected: (0.2f * ShaderCoerce.LumR) + (0.4f * ShaderCoerce.LumG) +
                      (0.6f * ShaderCoerce.LumB),
            actual: lum,
            tolerance: Eps
        );
    }

    // ── Diagnostics / structure ─────────────────────────────────────────────────

    [Fact]
    public void NoOutput_FailsWithSG0001()
    {
        var doc = new GraphDocument { DomainId = ShaderNodeLibrary.DomainId };
        var compiled = ShaderGraphCompiler.Compile(doc);
        Assert.False(compiled.Success);
        Assert.Contains(collection: compiled.Diagnostics, filter: d => d.Code == "SG0001");
    }

    [Fact]
    public void Cycle_IsDiagnosed_WithoutThrowing()
    {
        var (doc, p) = NewMaterial();
        var m = Add(doc: doc, defId: ShaderNodeLibrary.Math);
        Connect(
            doc: doc,
            from: m,
            fromPin: "out.result",
            to: m,
            toPin: "in.a"
        ); // self-loop
        Connect(
            doc: doc,
            from: m,
            fromPin: "out.result",
            to: p,
            toPin: "in.roughness"
        );
        var compiled = ShaderGraphCompiler.Compile(doc);
        Assert.True(compiled.Success); // warning, not error
        Assert.Contains(collection: compiled.Diagnostics, filter: d => d.Code == "SG0010");
    }

    [Fact]
    public void Wgsl_HasSurfaceFunction_AndIsBalanced()
    {
        var (doc, p) = NewMaterial();
        var rgb = Add(doc: doc, defId: ShaderNodeLibrary.Rgb);
        rgb.Properties["color"] = GraphValue.FromFloat4(
            x: 0.2f,
            y: 0.4f,
            z: 0.6f,
            w: 1f
        );
        Connect(
            doc: doc,
            from: rgb,
            fromPin: "out.color",
            to: p,
            toPin: "in.base_color"
        );

        string wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains(expectedSubstring: "struct ZgSurface", actualString: wgsl);
        Assert.Contains(
            expectedSubstring:
            "fn zg_surface(uv: vec2<f32>, gen: vec3<f32>, nrm: vec3<f32>) -> ZgSurface",
            actualString: wgsl
        );
        Assert.Contains(expectedSubstring: "return s;", actualString: wgsl);
        Assert.Equal(expected: CountChar(s: wgsl, c: '{'), actual: CountChar(s: wgsl, c: '}'));
        AssertSsaDeclaredBeforeUse(wgsl);
    }

    [Fact]
    public void Wgsl_DefaultGraph_EmitsConstantSurface()
    {
        var (doc, _) = NewMaterial();
        string wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains(
            expectedSubstring: "vec4<f32>(0.8, 0.8, 0.8, 1.0)",
            actualString: wgsl
        ); // base colour
        Assert.Contains(expectedSubstring: "let v", actualString: wgsl);
        Assert.Contains(expectedSubstring: "s.roughness =", actualString: wgsl);
        Assert.Contains(
            expectedSubstring: "s.emission = (",
            actualString: wgsl
        ); // emission = colour.rgb * strength
        Assert.DoesNotContain(expectedSubstring: "NaN", actualString: wgsl);
    }

    [Fact]
    public void Constant_Factory_RoundTripsThroughEvaluator()
    {
        var c = new SurfaceConstants(
            BaseR: 0.1f,
            BaseG: 0.2f,
            BaseB: 0.3f,
            BaseA: 1f,
            Metallic: 0.4f,
            Roughness: 0.6f,
            Specular: 1.2f,
            Clearcoat: 0.5f,
            ClearcoatRoughness: 0.07f,
            EmissiveR: 1f,
            EmissiveG: 0f,
            EmissiveB: 0f
        );
        var compiled = CompiledShaderGraph.Constant(c);
        var eval = new CpuShaderEvaluator(compiled.Program).Constants();
        Assert.Equal(expected: 0.1f, actual: eval.BaseR, tolerance: Eps);
        Assert.Equal(expected: 0.4f, actual: eval.Metallic, tolerance: Eps);
        Assert.Equal(expected: 0.6f, actual: eval.Roughness, tolerance: Eps);
        Assert.Equal(expected: 1.2f, actual: eval.Specular, tolerance: Eps);
        Assert.Equal(expected: 0.5f, actual: eval.Clearcoat, tolerance: Eps);
        Assert.Equal(expected: 1f, actual: eval.EmissiveR, tolerance: Eps);
    }

    [Fact]
    public void TextureNode_RecordedAsTextureRef_BaseColorWhite()
    {
        var (doc, p) = NewMaterial();
        var tex = Add(doc: doc, defId: ShaderNodeLibrary.TexImage);
        tex.Properties["path"] = GraphValue.FromString("textures/stone.png");
        Connect(
            doc: doc,
            from: tex,
            fromPin: "out.color",
            to: p,
            toPin: "in.base_color"
        );

        var compiled = ShaderGraphCompiler.Compile(doc);
        Assert.Equal(
            expected: "textures/stone.png",
            actual: compiled.TexturePath(TextureSlot.BaseColor)
        );
        Assert.Equal(
            expected: 1f,
            actual: compiled.Constants.BaseR,
            tolerance: Eps
        ); // texture drives colour → factor white
    }

    // ── Procedural nodes ────────────────────────────────────────────────────────

    private static CpuShaderEvaluator.SurfaceSample EvalAt(GraphDocument doc, Vec3 gen)
    {
        var program = ShaderGraphCompiler.Compile(doc).Program;
        return new CpuShaderEvaluator(program).Eval(
            uv: new Vec2(x: 0f, y: 0f),
            gen: gen,
            nrm: new Vec3(x: 0f, y: 0f, z: 1f)
        );
    }

    [Fact]
    public void Library_ContainsAllProceduralNodes()
    {
        var ids = ShaderNodeLibrary.Definitions.Select(d => d.Id).ToHashSet();
        foreach (string id in new[] {
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
            Assert.Contains(expected: id, set: ids);
    }

    [Fact]
    public void Checker_AlternatesByCell()
    {
        var (doc, p) = NewMaterial();
        var checker = Add(doc: doc, defId: ShaderNodeLibrary.Checker);
        Connect(
            doc: doc,
            from: checker,
            fromPin: "out.color",
            to: p,
            toPin: "in.base_color"
        ); // vector defaults to Generated coords
        Assert.Equal(
            expected: 0.8f,
            actual: EvalAt(doc: doc, gen: new Vec3(x: 0f, y: 0f, z: 0f)).BaseColor.X,
            tolerance: 1e-3f
        ); // even cell → color1
        Assert.Equal(
            expected: 0.2f,
            actual: EvalAt(doc: doc, gen: new Vec3(x: 0.3f, y: 0f, z: 0f)).BaseColor.X,
            tolerance: 1e-3f
        ); // floor(1.5)=1 → odd → color2
    }

    [Fact]
    public void Gradient_Linear_IsXCoordinate()
    {
        var (doc, p) = NewMaterial();
        var grad = Add(doc: doc, defId: ShaderNodeLibrary.Gradient);
        Connect(
            doc: doc,
            from: grad,
            fromPin: "out.fac",
            to: p,
            toPin: "in.roughness"
        );
        Assert.Equal(
            expected: 0.4f,
            actual: EvalAt(doc: doc, gen: new Vec3(x: 0.4f, y: 0f, z: 0f)).Roughness,
            tolerance: 1e-3f
        );
    }

    [Fact]
    public void VectorMath_Dot_FeedsScalarOutput()
    {
        var (doc, p) = NewMaterial();
        var vm = Add(doc: doc, defId: ShaderNodeLibrary.VecMath);
        vm.Properties["op"] = GraphValue.FromInt((int)VecMathOp.Dot);
        vm.Properties["in.a"] = GraphValue.FromFloat3(x: 1f, y: 2f, z: 3f);
        vm.Properties["in.b"] = GraphValue.FromFloat3(x: 0.5f, y: 0.5f, z: 0.5f);
        Connect(
            doc: doc,
            from: vm,
            fromPin: "out.value",
            to: p,
            toPin: "in.roughness"
        );
        Assert.Equal(
            expected: 3f,
            actual: EvalAt(doc: doc, gen: Vec3.Zero).Roughness,
            tolerance: 1e-3f
        ); // (1+2+3)*0.5
    }

    [Fact]
    public void MapRange_ClampsWhenEnabled()
    {
        var (doc, p) = NewMaterial();
        var v = Add(doc: doc, defId: ShaderNodeLibrary.Value);
        v.Properties["value"] = GraphValue.FromFloat(2f);
        var mr = Add(doc: doc, defId: ShaderNodeLibrary.MapRange);
        mr.Properties["clamp"] = GraphValue.FromBool(true);
        mr.Properties["in.from_max"] = GraphValue.FromFloat(1f);
        mr.Properties["in.to_max"] = GraphValue.FromFloat(10f);
        Connect(
            doc: doc,
            from: v,
            fromPin: "out.value",
            to: mr,
            toPin: "in.value"
        );
        Connect(
            doc: doc,
            from: mr,
            fromPin: "out.result",
            to: p,
            toPin: "in.roughness"
        );
        Assert.Equal(
            expected: 10f,
            actual: EvalAt(doc: doc, gen: Vec3.Zero).Roughness,
            tolerance: 1e-3f
        ); // 2→20 unclamped, clamped to to_max=10
    }

    [Fact]
    public void SeparateCombine_RoundTrips()
    {
        var (doc, p) = NewMaterial();
        var comb = Add(doc: doc, defId: ShaderNodeLibrary.CombineXyz);
        comb.Properties["in.x"] = GraphValue.FromFloat(0.1f);
        comb.Properties["in.y"] = GraphValue.FromFloat(0.2f);
        comb.Properties["in.z"] = GraphValue.FromFloat(0.3f);
        var sep = Add(doc: doc, defId: ShaderNodeLibrary.SeparateXyz);
        Connect(
            doc: doc,
            from: comb,
            fromPin: "out.vector",
            to: sep,
            toPin: "in.vector"
        );
        Connect(
            doc: doc,
            from: sep,
            fromPin: "out.y",
            to: p,
            toPin: "in.roughness"
        );
        Assert.Equal(
            expected: 0.2f,
            actual: EvalAt(doc: doc, gen: Vec3.Zero).Roughness,
            tolerance: 1e-3f
        );
    }

    [Fact]
    public void Noise_IsDeterministic_AndBounded()
    {
        var (doc, p) = NewMaterial();
        var noise = Add(doc: doc, defId: ShaderNodeLibrary.Noise);
        Connect(
            doc: doc,
            from: noise,
            fromPin: "out.fac",
            to: p,
            toPin: "in.roughness"
        );
        var at = new Vec3(x: 0.31f, y: 0.42f, z: 0.53f);
        float r1 = EvalAt(doc: doc, gen: at).Roughness;
        float r2 = EvalAt(doc: doc, gen: at).Roughness;
        Assert.Equal(expected: r1, actual: r2, tolerance: 1e-5f);
        Assert.InRange(actual: r1, low: -0.01f, high: 1.01f);
    }

    [Fact]
    public void Wgsl_NoiseGraph_IncludesStdlib_AndIsWellFormed()
    {
        var (doc, p) = NewMaterial();
        var noise = Add(doc: doc, defId: ShaderNodeLibrary.Noise);
        Connect(
            doc: doc,
            from: noise,
            fromPin: "out.fac",
            to: p,
            toPin: "in.roughness"
        );
        string wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains(expectedSubstring: "fn zg_hash13", actualString: wgsl);
        Assert.Contains(expectedSubstring: "fn zg_noise_fac", actualString: wgsl);
        Assert.Contains(
            expectedSubstring: "zg_noise_fac(",
            actualString: wgsl
        ); // a call site inside zg_surface
        Assert.Equal(expected: CountChar(s: wgsl, c: '{'), actual: CountChar(s: wgsl, c: '}'));
        AssertSsaDeclaredBeforeUse(wgsl);
    }

    // ── Color Ramp ──────────────────────────────────────────────────────────────

    [Fact]
    public void ColorRamp_DefaultBlackToWhite_SamplesByFac()
    {
        foreach ((float fac, float expect) in new[] {
                     (0f, 0f),
                     (0.5f, 0.5f),
                     (1f, 1f),
                 })
        {
            var (doc, p) = NewMaterial();
            var ramp = Add(doc: doc, defId: ShaderNodeLibrary.ColorRamp);
            ramp.Properties["in.fac"] = GraphValue.FromFloat(fac);
            Connect(
                doc: doc,
                from: ramp,
                fromPin: "out.color",
                to: p,
                toPin: "in.base_color"
            );
            Assert.Equal(expected: expect, actual: Constants(doc).BaseR, tolerance: 1e-3f);
        }
    }

    [Fact]
    public void ColorRamp_CustomStops_DriveColor()
    {
        var (doc, p) = NewMaterial();
        var ramp = Add(doc: doc, defId: ShaderNodeLibrary.ColorRamp);
        ramp.Properties["ramp"] = GraphValue.FromString(
            ShaderRampJson.Serialize(
                [
                    new ShaderRampStop(
                        Pos: 0f,
                        R: 1f,
                        G: 0f,
                        B: 0f,
                        A: 1f
                    ),
                    new ShaderRampStop(
                        Pos: 1f,
                        R: 0f,
                        G: 0f,
                        B: 1f,
                        A: 1f
                    ),
                ]
            )
        );
        ramp.Properties["in.fac"] = GraphValue.FromFloat(0f);
        Connect(
            doc: doc,
            from: ramp,
            fromPin: "out.color",
            to: p,
            toPin: "in.base_color"
        );
        var c = Constants(doc);
        Assert.Equal(expected: 1f, actual: c.BaseR, tolerance: 1e-3f); // first stop is red
        Assert.Equal(expected: 0f, actual: c.BaseB, tolerance: 1e-3f);
    }

    [Fact]
    public void ShaderRampJson_RoundTrips()
    {
        IReadOnlyList<ShaderRampStop> stops = [
            new(
                Pos: 0f,
                R: 1f,
                G: 0f,
                B: 0f,
                A: 1f
            ),
            new(
                Pos: 0.5f,
                R: 0f,
                G: 1f,
                B: 0f,
                A: 1f
            ),
            new(
                Pos: 1f,
                R: 0f,
                G: 0f,
                B: 1f,
                A: 1f
            ),
        ];
        var parsed = ShaderRampJson.Parse(ShaderRampJson.Serialize(stops));
        Assert.Equal(expected: 3, actual: parsed.Count);
        Assert.Equal(expected: 0.5f, actual: parsed[1].Pos, tolerance: 1e-3f);
        Assert.Equal(expected: 1f, actual: parsed[1].G, tolerance: 1e-3f);
        Assert.Equal(expected: 1f, actual: parsed[2].B, tolerance: 1e-3f);
    }

    [Fact]
    public void Wgsl_ColorRamp_EmitsRampFunction()
    {
        var (doc, p) = NewMaterial();
        var ramp = Add(doc: doc, defId: ShaderNodeLibrary.ColorRamp);
        Connect(
            doc: doc,
            from: ramp,
            fromPin: "out.color",
            to: p,
            toPin: "in.base_color"
        );
        string wgsl = ShaderGraphCompiler.Compile(doc).Wgsl;
        Assert.Contains(expectedSubstring: "fn zg_ramp_0", actualString: wgsl);
        Assert.Contains(expectedSubstring: "zg_ramp_0(", actualString: wgsl);
        Assert.Equal(expected: CountChar(s: wgsl, c: '{'), actual: CountChar(s: wgsl, c: '}'));
        AssertSsaDeclaredBeforeUse(wgsl);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static int CountChar(string s, char c)
    {
        int n = 0;
        foreach (char ch in s)
        {
            if (ch == c)
                n++;
        }

        return n;
    }

    private static void AssertSsaDeclaredBeforeUse(string wgsl)
    {
        var declared = new HashSet<int>();
        foreach (string line in wgsl.Split('\n'))
        {
            int declId = DeclIndex(line);
            foreach (int use in FindRefs(line))
            {
                if (use == declId) continue; // the LHS being declared on this line
                Assert.True(
                    condition: declared.Contains(use),
                    userMessage: $"WGSL uses v{use} before it is declared:\n{line}"
                );
            }

            if (declId >= 0) declared.Add(declId);
        }
    }

    private static IEnumerable<int> FindRefs(string line)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == 'v' && char.IsDigit(line[i + 1]) &&
                (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
            {
                int j = i + 1;
                while (j < line.Length && char.IsDigit(line[j])) j++;
                yield return int.Parse(line[(i + 1)..j]);
                i = j;
            }
        }
    }

    private static int DeclIndex(string line)
    {
        string t = line.TrimStart();
        if (!t.StartsWith(value: "let v", comparisonType: StringComparison.Ordinal)) return -1;
        int colon = t.IndexOf(':');
        if (colon < 5) return -1;
        return int.TryParse(s: t[5..colon], result: out int id) ? id : -1;
    }
}
