using Zigote.Graphs.Core;

namespace Zigote.Graphs.Shading;

/// <summary>
///     Lowers a shader-material <see cref="GraphDocument" /> into a backend-agnostic
///     <see cref="ShaderGraphProgram" /> (typed SSA). Walks from Material Output → Principled BSDF and
///     recursively lowers each surface input's upstream DAG, memoising shared sub-graphs by
///     <c>(node, output-pin)</c>. Producers are always emitted before consumers, so the instruction
///     list
///     is in topological order (evaluable / emittable front-to-back).
/// </summary>
public sealed class ShaderGraphLowerer
{
    private readonly HashSet<(Guid, string)> _active = [];
    private readonly List<GraphDiagnostic> _diags = [];
    private readonly GraphDocument _graph;
    private readonly List<ShaderInstr> _instrs = [];
    private readonly Dictionary<(Guid, string), ShaderValueRef> _memo = [];
    private readonly List<ShaderColorRamp> _ramps = [];
    private readonly List<ShaderTextureRef> _textures = [];

    private ShaderGraphLowerer(GraphDocument graph)
    {
        _graph = graph;
    }

    public static ShaderGraphProgram Lower(GraphDocument graph,
        out IReadOnlyList<GraphDiagnostic> diagnostics)
    {
        return new ShaderGraphLowerer(graph).Run(out diagnostics);
    }

    private ShaderGraphProgram Run(out IReadOnlyList<GraphDiagnostic> diagnostics)
    {
        diagnostics = _diags;

        var output = _graph.Nodes.FirstOrDefault(n => n.DefinitionId == ShaderNodeLibrary.Output);
        if (output is null)
        {
            _diags.Add(Error("SG0001", "Graph has no Material Output node."));
            return Build(
                -1,
                -1,
                -1,
                -1,
                -1,
                -1,
                -1,
                -1,
                -1
            );
        }

        var surfSrc = Source(output.Id, "in.surface");
        var bsdf = surfSrc.HasValue ? _graph.FindNode(surfSrc.Value.node) : null;
        if (bsdf is null || bsdf.DefinitionId != ShaderNodeLibrary.Principled)
        {
            _diags.Add(
                Warn(
                    "SG0003",
                    "Material Output's Surface is not a Principled BSDF; using defaults."
                )
            );
            return Build(
                -1,
                -1,
                -1,
                -1,
                -1,
                -1,
                -1,
                -1,
                -1
            );
        }

        var baseColor = PrincipledColor(
            bsdf,
            "in.base_color",
            TextureSlot.BaseColor,
            [0.8f, 0.8f, 0.8f, 1f]
        ).Id;
        var metallic = ScalarInput(bsdf, "in.metallic", 0f).Id;
        var roughness = ScalarInput(bsdf, "in.roughness", 0.5f).Id;
        var specular = ScalarInput(bsdf, "in.specular", 1f).Id;
        var emission = ColorInput(bsdf, "in.emission", [0f, 0f, 0f, 1f]).Id;
        var emissionStrength = ScalarInput(bsdf, "in.emission_strength", 0f).Id;
        var clearcoat = ScalarInput(bsdf, "in.clearcoat", 0f).Id;
        var clearcoatRoughness = ScalarInput(bsdf, "in.clearcoat_roughness", 0.03f).Id;
        var normal = NormalInput(bsdf, "in.normal");

        return Build(
            baseColor,
            metallic,
            roughness,
            specular,
            emission,
            emissionStrength,
            clearcoat,
            clearcoatRoughness,
            normal
        );
    }

    private ShaderGraphProgram Build(int baseColor, int metallic, int roughness, int specular,
        int emission,
        int emissionStrength, int clearcoat, int clearcoatRoughness, int normal)
    {
        return new ShaderGraphProgram {
            Instructions = _instrs,
            Ramps = _ramps,
            Textures = _textures,
            BaseColor = baseColor,
            Metallic = metallic,
            Roughness = roughness,
            Specular = specular,
            Emission = emission,
            EmissionStrength = emissionStrength,
            Clearcoat = clearcoat,
            ClearcoatRoughness = clearcoatRoughness,
            Normal = normal,
        };
    }

    // ── Recursive output lowering ───────────────────────────────────────────────

    private ShaderValueRef LowerOutput(Guid nodeId, string pinId)
    {
        var key = (nodeId, pinId);
        if (_memo.TryGetValue(key, out var cached)) return cached;
        if (!_active.Add(key))
        {
            _diags.Add(
                Warn(
                    "SG0010",
                    "Cycle detected in the shader graph; using a default value.",
                    nodeId,
                    pinId
                )
            );
            return ConstF(0f); // not memoised — the cycle resolves once the outer call completes
        }

        var node = _graph.FindNode(nodeId);
        var result = node is null ? ConstF(0f) : LowerNodeOutput(node, pinId);
        _active.Remove(key);
        _memo[key] = result;
        return result;
    }

    private ShaderValueRef LowerNodeOutput(GraphNode node, string pinId)
    {
        switch (node.DefinitionId)
        {
            case ShaderNodeLibrary.Rgb:
            {
                var c = ColorProp(node, "color", [0.8f, 0.8f, 0.8f, 1f]);
                return ConstV4(
                    c[0],
                    c[1],
                    c[2],
                    c[3]
                );
            }
            case ShaderNodeLibrary.Value:
                return ConstF(FloatProp(node, "value", 0.5f));
            case ShaderNodeLibrary.Math:
            {
                var a = ScalarInput(node, "in.a", 0f);
                var b = ScalarInput(node, "in.b", 0f);
                var op = IntProp(node, "op", 0);
                return Float(ShaderOp.Math, [a.Id, b.Id], op);
            }
            case ShaderNodeLibrary.Clamp:
            {
                var v = ScalarInput(node, "in.value", 0.5f);
                var lo = ScalarInput(node, "in.min", 0f);
                var hi = ScalarInput(node, "in.max", 1f);
                return Float(ShaderOp.Clamp, [v.Id, lo.Id, hi.Id]);
            }
            case ShaderNodeLibrary.MixColor:
            {
                var fac = ScalarInput(node, "in.factor", 0.5f);
                var a = ColorInput(node, "in.a", [0f, 0f, 0f, 1f]);
                var b = ColorInput(node, "in.b", [1f, 1f, 1f, 1f]);
                return Color(ShaderOp.MixColor, [fac.Id, a.Id, b.Id]);
            }
            case ShaderNodeLibrary.MulColor:
            {
                var a = ColorInput(node, "in.a", [1f, 1f, 1f, 1f]);
                var b = ColorInput(node, "in.b", [1f, 1f, 1f, 1f]);
                var one = ConstF(1f);
                return Color(ShaderOp.MixColor, [one.Id, a.Id, b.Id], (int)MixMode.Multiply);
            }
            case ShaderNodeLibrary.TexImage:
                // Texture sampling is deferred to the native stage; the base/normal slots are recorded at
                // the Principled level. Here the colour reads white, alpha 1.
                return pinId == "out.alpha"
                    ? ConstF(1f)
                    : ConstV4(
                        1f,
                        1f,
                        1f,
                        1f
                    );
            case ShaderNodeLibrary.NormalMap:
                return Input(ShaderOp.InputNormal, ShaderValueType.Vec3);

            case ShaderNodeLibrary.TexCoord:
                return pinId switch {
                    "out.uv" => Input(ShaderOp.InputUv, ShaderValueType.Vec3),
                    "out.object" => Input(ShaderOp.InputObject, ShaderValueType.Vec3),
                    "out.normal" => Input(ShaderOp.InputNormal, ShaderValueType.Vec3),
                    "out.position" => Input(ShaderOp.InputPosition, ShaderValueType.Vec3),
                    _ => Input(ShaderOp.InputGenerated, ShaderValueType.Vec3),
                };

            case ShaderNodeLibrary.Mapping:
            {
                var vec = VectorInput(node, "in.vector");
                var loc = Vec3PropInput(
                    node,
                    "in.location",
                    0f,
                    0f,
                    0f
                );
                var rot = Vec3PropInput(
                    node,
                    "in.rotation",
                    0f,
                    0f,
                    0f
                );
                var scl = Vec3PropInput(
                    node,
                    "in.scale",
                    1f,
                    1f,
                    1f
                );
                return Vec3Op(
                    ShaderOp.Mapping,
                    [vec.Id, loc.Id, rot.Id, scl.Id],
                    IntProp(node, "type", 0)
                );
            }

            case ShaderNodeLibrary.Noise:
            {
                var vec = VectorInput(node, "in.vector");
                var scale = ScalarInput(node, "in.scale", 5f);
                var detail = ScalarInput(node, "in.detail", 2f);
                var rough = ScalarInput(node, "in.roughness", 0.5f);
                var dist = ScalarInput(node, "in.distortion", 0f);
                int[] args = [vec.Id, scale.Id, detail.Id, rough.Id, dist.Id];
                var dims = IntProp(node, "dimensions", 2);
                return pinId == "out.color"
                    ? Color(ShaderOp.NoiseColor, args, dims)
                    : Float(ShaderOp.NoiseFac, args, dims);
            }

            case ShaderNodeLibrary.Gradient:
            {
                var vec = VectorInput(node, "in.vector");
                var type = IntProp(node, "gradient_type", 0);
                return pinId == "out.color"
                    ? Color(ShaderOp.GradientColor, [vec.Id], type)
                    : Float(ShaderOp.GradientFac, [vec.Id], type);
            }

            case ShaderNodeLibrary.Checker:
            {
                var vec = VectorInput(node, "in.vector");
                var c1 = ColorInput(node, "in.color1", [0.8f, 0.8f, 0.8f, 1f]);
                var c2 = ColorInput(node, "in.color2", [0.2f, 0.2f, 0.2f, 1f]);
                var scale = ScalarInput(node, "in.scale", 5f);
                int[] args = [vec.Id, c1.Id, c2.Id, scale.Id];
                return pinId == "out.fac"
                    ? Float(ShaderOp.CheckerFac, args)
                    : Color(ShaderOp.CheckerColor, args);
            }

            case ShaderNodeLibrary.Wave:
            {
                var vec = VectorInput(node, "in.vector");
                var scale = ScalarInput(node, "in.scale", 5f);
                var dist = ScalarInput(node, "in.distortion", 0f);
                var detail = ScalarInput(node, "in.detail", 2f);
                int[] args = [vec.Id, scale.Id, dist.Id, detail.Id];
                var type = IntProp(node, "wave_type", 0);
                var profile = IntProp(node, "wave_profile", 0);
                return pinId == "out.color"
                    ? Color(
                        ShaderOp.WaveColor,
                        args,
                        type,
                        profile
                    )
                    : Float(
                        ShaderOp.WaveFac,
                        args,
                        type,
                        profile
                    );
            }

            case ShaderNodeLibrary.VecMath:
            {
                var a = Vec3PropInput(
                    node,
                    "in.a",
                    0f,
                    0f,
                    0f
                );
                var b = Vec3PropInput(
                    node,
                    "in.b",
                    0f,
                    0f,
                    0f
                );
                var scale = ScalarInput(node, "in.scale", 1f);
                int[] args = [a.Id, b.Id, scale.Id];
                var op = IntProp(node, "op", 0);
                return pinId == "out.value"
                    ? Float(ShaderOp.VecMathScalar, args, op)
                    : Vec3Op(ShaderOp.VecMath, args, op);
            }

            case ShaderNodeLibrary.MapRange:
            {
                var value = ScalarInput(node, "in.value", 0f);
                var fmin = ScalarInput(node, "in.from_min", 0f);
                var fmax = ScalarInput(node, "in.from_max", 1f);
                var tmin = ScalarInput(node, "in.to_min", 0f);
                var tmax = ScalarInput(node, "in.to_max", 1f);
                var clamp = BoolProp(node, "clamp", true) ? 1f : 0f;
                return Float(
                    ShaderOp.MapRange,
                    [value.Id, fmin.Id, fmax.Id, tmin.Id, tmax.Id],
                    clamp
                );
            }

            case ShaderNodeLibrary.SeparateXyz:
            {
                var vec = Vec3PropInput(
                    node,
                    "in.vector",
                    0f,
                    0f,
                    0f
                );
                var op = pinId switch {
                    "out.y" => ShaderOp.SeparateY,
                    "out.z" => ShaderOp.SeparateZ,
                    _ => ShaderOp.SeparateX,
                };
                return Float(op, [vec.Id]);
            }

            case ShaderNodeLibrary.CombineXyz:
            {
                var x = ScalarInput(node, "in.x", 0f);
                var y = ScalarInput(node, "in.y", 0f);
                var z = ScalarInput(node, "in.z", 0f);
                return Vec3Op(ShaderOp.Combine, [x.Id, y.Id, z.Id]);
            }

            case ShaderNodeLibrary.ColorRamp:
            {
                var fac = ScalarInput(node, "in.fac", 0.5f);
                var ramp = new ShaderColorRamp(
                    ShaderRampJson.Parse(StringProp(node, "ramp")),
                    (RampInterpolation)IntProp(node, "interpolation", 0)
                );
                var aux = _ramps.Count;
                _ramps.Add(ramp);
                var op = pinId == "out.alpha" ? ShaderOp.ColorRampAlpha : ShaderOp.ColorRampColor;
                var type = pinId == "out.alpha" ? ShaderValueType.Float : ShaderValueType.Vec4;
                return new ShaderValueRef(
                    Emit(
                        op,
                        type,
                        [fac.Id],
                        aux: aux
                    ),
                    type
                );
            }

            default:
                return ConstF(0f);
        }
    }

    // ── Principled input resolution ─────────────────────────────────────────────

    private ShaderValueRef PrincipledColor(GraphNode bsdf, string pin, TextureSlot slot,
        float[] fallback)
    {
        var src = Source(bsdf.Id, pin);
        if (src.HasValue)
        {
            var sn = _graph.FindNode(src.Value.node);
            if (sn is { DefinitionId: ShaderNodeLibrary.TexImage })
            {
                var path = StringProp(sn, "path");
                if (!string.IsNullOrEmpty(path)) _textures.Add(new ShaderTextureRef(path!, slot));
                return ConstV4(
                    1f,
                    1f,
                    1f,
                    1f
                );
            }

            return Coerce(LowerOutput(src.Value.node, src.Value.pin), ShaderValueType.Vec4);
        }

        var c = ColorProp(bsdf, pin, fallback);
        return ConstV4(
            c[0],
            c[1],
            c[2],
            c[3]
        );
    }

    private int NormalInput(GraphNode bsdf, string pin)
    {
        var src = Source(bsdf.Id, pin);
        if (!src.HasValue) return -1;
        var sn = _graph.FindNode(src.Value.node);
        if (sn is { DefinitionId: ShaderNodeLibrary.NormalMap })
        {
            var path = StringProp(sn, "path");
            if (!string.IsNullOrEmpty(path))
                _textures.Add(new ShaderTextureRef(path!, TextureSlot.Normal));
            // The map modulates the surface natively; the preview/codegen falls back to the shading normal.
            return Input(ShaderOp.InputNormal, ShaderValueType.Vec3).Id;
        }

        return Coerce(LowerOutput(src.Value.node, src.Value.pin), ShaderValueType.Vec3).Id;
    }

    private ShaderValueRef ScalarInput(GraphNode node, string pin, float fallback)
    {
        var src = Source(node.Id, pin);
        if (src.HasValue)
            return Coerce(LowerOutput(src.Value.node, src.Value.pin), ShaderValueType.Float);
        return ConstF(FloatProp(node, pin, fallback));
    }

    private ShaderValueRef ColorInput(GraphNode node, string pin, float[] fallback)
    {
        var src = Source(node.Id, pin);
        if (src.HasValue)
            return Coerce(LowerOutput(src.Value.node, src.Value.pin), ShaderValueType.Vec4);
        var c = ColorProp(node, pin, fallback);
        return ConstV4(
            c[0],
            c[1],
            c[2],
            c[3]
        );
    }

    // ── Emit helpers ────────────────────────────────────────────────────────────

    private int Emit(ShaderOp op, ShaderValueType type, int[] args, float p0 = 0f, float p1 = 0f,
        float p2 = 0f,
        float p3 = 0f, int aux = -1)
    {
        var id = _instrs.Count;
        _instrs.Add(
            new ShaderInstr(
                id,
                op,
                type,
                args,
                p0,
                p1,
                p2,
                p3,
                aux
            )
        );
        return id;
    }

    private ShaderValueRef ConstF(float v)
    {
        return new ShaderValueRef(
            Emit(
                ShaderOp.ConstFloat,
                ShaderValueType.Float,
                [],
                v
            ),
            ShaderValueType.Float
        );
    }

    private ShaderValueRef ConstV3(float x, float y, float z)
    {
        return new ShaderValueRef(
            Emit(
                ShaderOp.ConstVec3,
                ShaderValueType.Vec3,
                [],
                x,
                y,
                z
            ),
            ShaderValueType.Vec3
        );
    }

    private ShaderValueRef ConstV4(float r, float g, float b, float a)
    {
        return new ShaderValueRef(
            Emit(
                ShaderOp.ConstVec4,
                ShaderValueType.Vec4,
                [],
                r,
                g,
                b,
                a
            ),
            ShaderValueType.Vec4
        );
    }

    private ShaderValueRef Input(ShaderOp op, ShaderValueType t)
    {
        return new ShaderValueRef(Emit(op, t, []), t);
    }

    private ShaderValueRef Float(ShaderOp op, int[] args, float p0 = 0f, float p1 = 0f)
    {
        return new ShaderValueRef(
            Emit(
                op,
                ShaderValueType.Float,
                args,
                p0,
                p1
            ),
            ShaderValueType.Float
        );
    }

    private ShaderValueRef Vec3Op(ShaderOp op, int[] args, float p0 = 0f, float p1 = 0f)
    {
        return new ShaderValueRef(
            Emit(
                op,
                ShaderValueType.Vec3,
                args,
                p0,
                p1
            ),
            ShaderValueType.Vec3
        );
    }

    private ShaderValueRef Color(ShaderOp op, int[] args, float p0 = 0f, float p1 = 0f)
    {
        return new ShaderValueRef(
            Emit(
                op,
                ShaderValueType.Vec4,
                args,
                p0,
                p1
            ),
            ShaderValueType.Vec4
        );
    }

    /// <summary>
    ///     A texture node's main Vector input: connected → Vec3; unconnected → Generated coords
    ///     (Blender's default for an unlinked texture Vector socket).
    /// </summary>
    private ShaderValueRef VectorInput(GraphNode node, string pin)
    {
        var src = Source(node.Id, pin);
        if (src.HasValue)
            return Coerce(LowerOutput(src.Value.node, src.Value.pin), ShaderValueType.Vec3);
        return Input(ShaderOp.InputGenerated, ShaderValueType.Vec3);
    }

    /// <summary>A Vector input backed by a Float3 property default (location/scale/operands).</summary>
    private ShaderValueRef Vec3PropInput(GraphNode node, string pin, float dx, float dy, float dz)
    {
        var src = Source(node.Id, pin);
        if (src.HasValue)
            return Coerce(LowerOutput(src.Value.node, src.Value.pin), ShaderValueType.Vec3);
        var v = Vec3Prop(
            node,
            pin,
            dx,
            dy,
            dz
        );
        return ConstV3(v[0], v[1], v[2]);
    }

    private ShaderValueRef Coerce(ShaderValueRef v, ShaderValueType target)
    {
        if (v.Type == target) return v;
        return new ShaderValueRef(Emit(ShaderOp.Coerce, target, [v.Id]), target);
    }

    // ── Graph + property readers ────────────────────────────────────────────────

    private (Guid node, string pin)? Source(Guid nodeId, string pinId)
    {
        foreach (var e in _graph.EdgesAtPin(nodeId, pinId))
            if (e.To.NodeId == nodeId && e.To.PinId == pinId)
                return (e.From.NodeId, e.From.PinId);
        return null;
    }

    private static float FloatProp(GraphNode node, string id, float fallback)
    {
        return node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Float
            ? v.AsFloat()
            : fallback;
    }

    private static int IntProp(GraphNode node, string id, int fallback)
    {
        return node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Int
            ? v.AsInt()
            : fallback;
    }

    private static float[] ColorProp(GraphNode node, string id, float[] fallback)
    {
        return node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Float4
            ? v.AsFloat4()
            : fallback;
    }

    private static float[] Vec3Prop(GraphNode node, string id, float dx, float dy, float dz)
    {
        return node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Float3
            ? v.AsFloat3()
            : [dx, dy, dz];
    }

    private static bool BoolProp(GraphNode node, string id, bool fallback)
    {
        return node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Bool
            ? v.AsBool()
            : fallback;
    }

    private static string? StringProp(GraphNode node, string id)
    {
        return node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.String
            ? v.AsString()
            : null;
    }

    private static GraphDiagnostic Error(string code, string message)
    {
        return new GraphDiagnostic {
            Severity = GraphDiagnosticSeverity.Error,
            Code = code,
            Message = message,
            DomainId = ShaderNodeLibrary.DomainId,
        };
    }

    private static GraphDiagnostic Warn(string code, string message, Guid? node = null,
        string? pin = null)
    {
        return new GraphDiagnostic {
            Severity = GraphDiagnosticSeverity.Warning,
            Code = code,
            Message = message,
            DomainId = ShaderNodeLibrary.DomainId,
            NodeId = node,
            PinId = pin,
        };
    }
}
