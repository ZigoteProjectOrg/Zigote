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

    private ShaderGraphLowerer(GraphDocument graph) => _graph = graph;

    public static ShaderGraphProgram Lower(GraphDocument graph,
        out IReadOnlyList<GraphDiagnostic> diagnostics) =>
        new ShaderGraphLowerer(graph).Run(out diagnostics);

    private ShaderGraphProgram Run(out IReadOnlyList<GraphDiagnostic> diagnostics)
    {
        diagnostics = _diags;

        var output = _graph.Nodes.FirstOrDefault(n => n.DefinitionId == ShaderNodeLibrary.Output);
        if (output is null)
        {
            _diags.Add(Error(code: "SG0001", message: "Graph has no Material Output node."));
            return Build(
                baseColor: -1,
                metallic: -1,
                roughness: -1,
                specular: -1,
                emission: -1,
                emissionStrength: -1,
                clearcoat: -1,
                clearcoatRoughness: -1,
                normal: -1
            );
        }

        var surfSrc = Source(nodeId: output.Id, pinId: "in.surface");
        var bsdf = surfSrc.HasValue ? _graph.FindNode(surfSrc.Value.node) : null;
        if (bsdf is null || bsdf.DefinitionId != ShaderNodeLibrary.Principled)
        {
            _diags.Add(
                Warn(
                    code: "SG0003",
                    message: "Material Output's Surface is not a Principled BSDF; using defaults."
                )
            );
            return Build(
                baseColor: -1,
                metallic: -1,
                roughness: -1,
                specular: -1,
                emission: -1,
                emissionStrength: -1,
                clearcoat: -1,
                clearcoatRoughness: -1,
                normal: -1
            );
        }

        int baseColor = PrincipledColor(
            bsdf: bsdf,
            pin: "in.base_color",
            slot: TextureSlot.BaseColor,
            fallback: [0.8f, 0.8f, 0.8f, 1f]
        ).Id;
        int metallic = ScalarInput(node: bsdf, pin: "in.metallic", fallback: 0f).Id;
        int roughness = ScalarInput(node: bsdf, pin: "in.roughness", fallback: 0.5f).Id;
        int specular = ScalarInput(node: bsdf, pin: "in.specular", fallback: 1f).Id;
        int emission = ColorInput(node: bsdf, pin: "in.emission", fallback: [0f, 0f, 0f, 1f]).Id;
        int emissionStrength = ScalarInput(
            node: bsdf,
            pin: "in.emission_strength",
            fallback: 0f
        ).Id;
        int clearcoat = ScalarInput(node: bsdf, pin: "in.clearcoat", fallback: 0f).Id;
        int clearcoatRoughness = ScalarInput(
            node: bsdf,
            pin: "in.clearcoat_roughness",
            fallback: 0.03f
        ).Id;
        int normal = NormalInput(bsdf: bsdf, pin: "in.normal");

        return Build(
            baseColor: baseColor,
            metallic: metallic,
            roughness: roughness,
            specular: specular,
            emission: emission,
            emissionStrength: emissionStrength,
            clearcoat: clearcoat,
            clearcoatRoughness: clearcoatRoughness,
            normal: normal
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
        if (_memo.TryGetValue(key: key, value: out var cached)) return cached;
        if (!_active.Add(key))
        {
            _diags.Add(
                Warn(
                    code: "SG0010",
                    message: "Cycle detected in the shader graph; using a default value.",
                    node: nodeId,
                    pin: pinId
                )
            );
            return ConstF(0f); // not memoised — the cycle resolves once the outer call completes
        }

        var node = _graph.FindNode(nodeId);
        var result = node is null ? ConstF(0f) : LowerNodeOutput(node: node, pinId: pinId);
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
                float[] c = ColorProp(node: node, id: "color", fallback: [0.8f, 0.8f, 0.8f, 1f]);
                return ConstV4(
                    r: c[0],
                    g: c[1],
                    b: c[2],
                    a: c[3]
                );
            }
            case ShaderNodeLibrary.Value:
                return ConstF(FloatProp(node: node, id: "value", fallback: 0.5f));
            case ShaderNodeLibrary.Math:
            {
                var a = ScalarInput(node: node, pin: "in.a", fallback: 0f);
                var b = ScalarInput(node: node, pin: "in.b", fallback: 0f);
                int op = IntProp(node: node, id: "op", fallback: 0);
                return Float(op: ShaderOp.Math, args: [a.Id, b.Id], p0: op);
            }
            case ShaderNodeLibrary.Clamp:
            {
                var v = ScalarInput(node: node, pin: "in.value", fallback: 0.5f);
                var lo = ScalarInput(node: node, pin: "in.min", fallback: 0f);
                var hi = ScalarInput(node: node, pin: "in.max", fallback: 1f);
                return Float(op: ShaderOp.Clamp, args: [v.Id, lo.Id, hi.Id]);
            }
            case ShaderNodeLibrary.MixColor:
            {
                var fac = ScalarInput(node: node, pin: "in.factor", fallback: 0.5f);
                var a = ColorInput(node: node, pin: "in.a", fallback: [0f, 0f, 0f, 1f]);
                var b = ColorInput(node: node, pin: "in.b", fallback: [1f, 1f, 1f, 1f]);
                return Color(op: ShaderOp.MixColor, args: [fac.Id, a.Id, b.Id]);
            }
            case ShaderNodeLibrary.MulColor:
            {
                var a = ColorInput(node: node, pin: "in.a", fallback: [1f, 1f, 1f, 1f]);
                var b = ColorInput(node: node, pin: "in.b", fallback: [1f, 1f, 1f, 1f]);
                var one = ConstF(1f);
                return Color(
                    op: ShaderOp.MixColor,
                    args: [one.Id, a.Id, b.Id],
                    p0: (int)MixMode.Multiply
                );
            }
            case ShaderNodeLibrary.TexImage:
                // Texture sampling is deferred to the native stage; the base/normal slots are recorded at
                // the Principled level. Here the colour reads white, alpha 1.
                return pinId == "out.alpha"
                    ? ConstF(1f)
                    : ConstV4(
                        r: 1f,
                        g: 1f,
                        b: 1f,
                        a: 1f
                    );
            case ShaderNodeLibrary.NormalMap:
                return Input(op: ShaderOp.InputNormal, t: ShaderValueType.Vec3);

            case ShaderNodeLibrary.TexCoord:
                return pinId switch {
                    "out.uv" => Input(op: ShaderOp.InputUv, t: ShaderValueType.Vec3),
                    "out.object" => Input(op: ShaderOp.InputObject, t: ShaderValueType.Vec3),
                    "out.normal" => Input(op: ShaderOp.InputNormal, t: ShaderValueType.Vec3),
                    "out.position" => Input(op: ShaderOp.InputPosition, t: ShaderValueType.Vec3),
                    _ => Input(op: ShaderOp.InputGenerated, t: ShaderValueType.Vec3),
                };

            case ShaderNodeLibrary.Mapping:
            {
                var vec = VectorInput(node: node, pin: "in.vector");
                var loc = Vec3PropInput(
                    node: node,
                    pin: "in.location",
                    dx: 0f,
                    dy: 0f,
                    dz: 0f
                );
                var rot = Vec3PropInput(
                    node: node,
                    pin: "in.rotation",
                    dx: 0f,
                    dy: 0f,
                    dz: 0f
                );
                var scl = Vec3PropInput(
                    node: node,
                    pin: "in.scale",
                    dx: 1f,
                    dy: 1f,
                    dz: 1f
                );
                return Vec3Op(
                    op: ShaderOp.Mapping,
                    args: [vec.Id, loc.Id, rot.Id, scl.Id],
                    p0: IntProp(node: node, id: "type", fallback: 0)
                );
            }

            case ShaderNodeLibrary.Noise:
            {
                var vec = VectorInput(node: node, pin: "in.vector");
                var scale = ScalarInput(node: node, pin: "in.scale", fallback: 5f);
                var detail = ScalarInput(node: node, pin: "in.detail", fallback: 2f);
                var rough = ScalarInput(node: node, pin: "in.roughness", fallback: 0.5f);
                var dist = ScalarInput(node: node, pin: "in.distortion", fallback: 0f);
                int[] args = [vec.Id, scale.Id, detail.Id, rough.Id, dist.Id];
                int dims = IntProp(node: node, id: "dimensions", fallback: 2);
                return pinId == "out.color"
                    ? Color(op: ShaderOp.NoiseColor, args: args, p0: dims)
                    : Float(op: ShaderOp.NoiseFac, args: args, p0: dims);
            }

            case ShaderNodeLibrary.Gradient:
            {
                var vec = VectorInput(node: node, pin: "in.vector");
                int type = IntProp(node: node, id: "gradient_type", fallback: 0);
                return pinId == "out.color"
                    ? Color(op: ShaderOp.GradientColor, args: [vec.Id], p0: type)
                    : Float(op: ShaderOp.GradientFac, args: [vec.Id], p0: type);
            }

            case ShaderNodeLibrary.Checker:
            {
                var vec = VectorInput(node: node, pin: "in.vector");
                var c1 = ColorInput(node: node, pin: "in.color1", fallback: [0.8f, 0.8f, 0.8f, 1f]);
                var c2 = ColorInput(node: node, pin: "in.color2", fallback: [0.2f, 0.2f, 0.2f, 1f]);
                var scale = ScalarInput(node: node, pin: "in.scale", fallback: 5f);
                int[] args = [vec.Id, c1.Id, c2.Id, scale.Id];
                return pinId == "out.fac"
                    ? Float(op: ShaderOp.CheckerFac, args: args)
                    : Color(op: ShaderOp.CheckerColor, args: args);
            }

            case ShaderNodeLibrary.Wave:
            {
                var vec = VectorInput(node: node, pin: "in.vector");
                var scale = ScalarInput(node: node, pin: "in.scale", fallback: 5f);
                var dist = ScalarInput(node: node, pin: "in.distortion", fallback: 0f);
                var detail = ScalarInput(node: node, pin: "in.detail", fallback: 2f);
                int[] args = [vec.Id, scale.Id, dist.Id, detail.Id];
                int type = IntProp(node: node, id: "wave_type", fallback: 0);
                int profile = IntProp(node: node, id: "wave_profile", fallback: 0);
                return pinId == "out.color"
                    ? Color(
                        op: ShaderOp.WaveColor,
                        args: args,
                        p0: type,
                        p1: profile
                    )
                    : Float(
                        op: ShaderOp.WaveFac,
                        args: args,
                        p0: type,
                        p1: profile
                    );
            }

            case ShaderNodeLibrary.VecMath:
            {
                var a = Vec3PropInput(
                    node: node,
                    pin: "in.a",
                    dx: 0f,
                    dy: 0f,
                    dz: 0f
                );
                var b = Vec3PropInput(
                    node: node,
                    pin: "in.b",
                    dx: 0f,
                    dy: 0f,
                    dz: 0f
                );
                var scale = ScalarInput(node: node, pin: "in.scale", fallback: 1f);
                int[] args = [a.Id, b.Id, scale.Id];
                int op = IntProp(node: node, id: "op", fallback: 0);
                return pinId == "out.value"
                    ? Float(op: ShaderOp.VecMathScalar, args: args, p0: op)
                    : Vec3Op(op: ShaderOp.VecMath, args: args, p0: op);
            }

            case ShaderNodeLibrary.MapRange:
            {
                var value = ScalarInput(node: node, pin: "in.value", fallback: 0f);
                var fmin = ScalarInput(node: node, pin: "in.from_min", fallback: 0f);
                var fmax = ScalarInput(node: node, pin: "in.from_max", fallback: 1f);
                var tmin = ScalarInput(node: node, pin: "in.to_min", fallback: 0f);
                var tmax = ScalarInput(node: node, pin: "in.to_max", fallback: 1f);
                float clamp = BoolProp(node: node, id: "clamp", fallback: true) ? 1f : 0f;
                return Float(
                    op: ShaderOp.MapRange,
                    args: [value.Id, fmin.Id, fmax.Id, tmin.Id, tmax.Id],
                    p0: clamp
                );
            }

            case ShaderNodeLibrary.SeparateXyz:
            {
                var vec = Vec3PropInput(
                    node: node,
                    pin: "in.vector",
                    dx: 0f,
                    dy: 0f,
                    dz: 0f
                );
                var op = pinId switch {
                    "out.y" => ShaderOp.SeparateY,
                    "out.z" => ShaderOp.SeparateZ,
                    _ => ShaderOp.SeparateX,
                };
                return Float(op: op, args: [vec.Id]);
            }

            case ShaderNodeLibrary.CombineXyz:
            {
                var x = ScalarInput(node: node, pin: "in.x", fallback: 0f);
                var y = ScalarInput(node: node, pin: "in.y", fallback: 0f);
                var z = ScalarInput(node: node, pin: "in.z", fallback: 0f);
                return Vec3Op(op: ShaderOp.Combine, args: [x.Id, y.Id, z.Id]);
            }

            case ShaderNodeLibrary.ColorRamp:
            {
                var fac = ScalarInput(node: node, pin: "in.fac", fallback: 0.5f);
                var ramp = new ShaderColorRamp(
                    stops: ShaderRampJson.Parse(StringProp(node: node, id: "ramp")),
                    interp: (RampInterpolation)IntProp(node: node, id: "interpolation", fallback: 0)
                );
                int aux = _ramps.Count;
                _ramps.Add(ramp);
                var op = pinId == "out.alpha" ? ShaderOp.ColorRampAlpha : ShaderOp.ColorRampColor;
                var type = pinId == "out.alpha" ? ShaderValueType.Float : ShaderValueType.Vec4;
                return new ShaderValueRef(
                    Id: Emit(
                        op: op,
                        type: type,
                        args: [fac.Id],
                        aux: aux
                    ),
                    Type: type
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
        var src = Source(nodeId: bsdf.Id, pinId: pin);
        if (src.HasValue)
        {
            var sn = _graph.FindNode(src.Value.node);
            if (sn is { DefinitionId: ShaderNodeLibrary.TexImage })
            {
                string? path = StringProp(node: sn, id: "path");
                if (!string.IsNullOrEmpty(path))
                    _textures.Add(new ShaderTextureRef(Path: path!, Slot: slot));
                return ConstV4(
                    r: 1f,
                    g: 1f,
                    b: 1f,
                    a: 1f
                );
            }

            return Coerce(
                v: LowerOutput(nodeId: src.Value.node, pinId: src.Value.pin),
                target: ShaderValueType.Vec4
            );
        }

        float[] c = ColorProp(node: bsdf, id: pin, fallback: fallback);
        return ConstV4(
            r: c[0],
            g: c[1],
            b: c[2],
            a: c[3]
        );
    }

    private int NormalInput(GraphNode bsdf, string pin)
    {
        var src = Source(nodeId: bsdf.Id, pinId: pin);
        if (!src.HasValue) return -1;
        var sn = _graph.FindNode(src.Value.node);
        if (sn is { DefinitionId: ShaderNodeLibrary.NormalMap })
        {
            string? path = StringProp(node: sn, id: "path");
            if (!string.IsNullOrEmpty(path))
                _textures.Add(new ShaderTextureRef(Path: path!, Slot: TextureSlot.Normal));
            // The map modulates the surface natively; the preview/codegen falls back to the shading normal.
            return Input(op: ShaderOp.InputNormal, t: ShaderValueType.Vec3).Id;
        }

        return Coerce(
            v: LowerOutput(nodeId: src.Value.node, pinId: src.Value.pin),
            target: ShaderValueType.Vec3
        ).Id;
    }

    private ShaderValueRef ScalarInput(GraphNode node, string pin, float fallback)
    {
        var src = Source(nodeId: node.Id, pinId: pin);
        if (src.HasValue)
        {
            return Coerce(
                v: LowerOutput(nodeId: src.Value.node, pinId: src.Value.pin),
                target: ShaderValueType.Float
            );
        }

        return ConstF(FloatProp(node: node, id: pin, fallback: fallback));
    }

    private ShaderValueRef ColorInput(GraphNode node, string pin, float[] fallback)
    {
        var src = Source(nodeId: node.Id, pinId: pin);
        if (src.HasValue)
        {
            return Coerce(
                v: LowerOutput(nodeId: src.Value.node, pinId: src.Value.pin),
                target: ShaderValueType.Vec4
            );
        }

        float[] c = ColorProp(node: node, id: pin, fallback: fallback);
        return ConstV4(
            r: c[0],
            g: c[1],
            b: c[2],
            a: c[3]
        );
    }

    // ── Emit helpers ────────────────────────────────────────────────────────────

    private int Emit(ShaderOp op, ShaderValueType type, int[] args, float p0 = 0f, float p1 = 0f,
        float p2 = 0f,
        float p3 = 0f, int aux = -1)
    {
        int id = _instrs.Count;
        _instrs.Add(
            new ShaderInstr(
                Result: id,
                Op: op,
                Type: type,
                Args: args,
                P0: p0,
                P1: p1,
                P2: p2,
                P3: p3,
                Aux: aux
            )
        );
        return id;
    }

    private ShaderValueRef ConstF(float v)
    {
        return new ShaderValueRef(
            Id: Emit(
                op: ShaderOp.ConstFloat,
                type: ShaderValueType.Float,
                args: [],
                p0: v
            ),
            Type: ShaderValueType.Float
        );
    }

    private ShaderValueRef ConstV3(float x, float y, float z)
    {
        return new ShaderValueRef(
            Id: Emit(
                op: ShaderOp.ConstVec3,
                type: ShaderValueType.Vec3,
                args: [],
                p0: x,
                p1: y,
                p2: z
            ),
            Type: ShaderValueType.Vec3
        );
    }

    private ShaderValueRef ConstV4(float r, float g, float b, float a)
    {
        return new ShaderValueRef(
            Id: Emit(
                op: ShaderOp.ConstVec4,
                type: ShaderValueType.Vec4,
                args: [],
                p0: r,
                p1: g,
                p2: b,
                p3: a
            ),
            Type: ShaderValueType.Vec4
        );
    }

    private ShaderValueRef Input(ShaderOp op, ShaderValueType t) => new(
        Id: Emit(op: op, type: t, args: []),
        Type: t
    );

    private ShaderValueRef Float(ShaderOp op, int[] args, float p0 = 0f, float p1 = 0f)
    {
        return new ShaderValueRef(
            Id: Emit(
                op: op,
                type: ShaderValueType.Float,
                args: args,
                p0: p0,
                p1: p1
            ),
            Type: ShaderValueType.Float
        );
    }

    private ShaderValueRef Vec3Op(ShaderOp op, int[] args, float p0 = 0f, float p1 = 0f)
    {
        return new ShaderValueRef(
            Id: Emit(
                op: op,
                type: ShaderValueType.Vec3,
                args: args,
                p0: p0,
                p1: p1
            ),
            Type: ShaderValueType.Vec3
        );
    }

    private ShaderValueRef Color(ShaderOp op, int[] args, float p0 = 0f, float p1 = 0f)
    {
        return new ShaderValueRef(
            Id: Emit(
                op: op,
                type: ShaderValueType.Vec4,
                args: args,
                p0: p0,
                p1: p1
            ),
            Type: ShaderValueType.Vec4
        );
    }

    /// <summary>
    ///     A texture node's main Vector input: connected → Vec3; unconnected → Generated coords
    ///     (Blender's default for an unlinked texture Vector socket).
    /// </summary>
    private ShaderValueRef VectorInput(GraphNode node, string pin)
    {
        var src = Source(nodeId: node.Id, pinId: pin);
        if (src.HasValue)
        {
            return Coerce(
                v: LowerOutput(nodeId: src.Value.node, pinId: src.Value.pin),
                target: ShaderValueType.Vec3
            );
        }

        return Input(op: ShaderOp.InputGenerated, t: ShaderValueType.Vec3);
    }

    /// <summary>A Vector input backed by a Float3 property default (location/scale/operands).</summary>
    private ShaderValueRef Vec3PropInput(GraphNode node, string pin, float dx, float dy, float dz)
    {
        var src = Source(nodeId: node.Id, pinId: pin);
        if (src.HasValue)
        {
            return Coerce(
                v: LowerOutput(nodeId: src.Value.node, pinId: src.Value.pin),
                target: ShaderValueType.Vec3
            );
        }

        float[] v = Vec3Prop(
            node: node,
            id: pin,
            dx: dx,
            dy: dy,
            dz: dz
        );
        return ConstV3(x: v[0], y: v[1], z: v[2]);
    }

    private ShaderValueRef Coerce(ShaderValueRef v, ShaderValueType target)
    {
        if (v.Type == target) return v;
        return new ShaderValueRef(
            Id: Emit(op: ShaderOp.Coerce, type: target, args: [v.Id]),
            Type: target
        );
    }

    // ── Graph + property readers ────────────────────────────────────────────────

    private (Guid node, string pin)? Source(Guid nodeId, string pinId)
    {
        foreach (var e in _graph.EdgesAtPin(nodeId: nodeId, pinId: pinId))
        {
            if (e.To.NodeId == nodeId && e.To.PinId == pinId)
                return (e.From.NodeId, e.From.PinId);
        }

        return null;
    }

    private static float FloatProp(GraphNode node, string id, float fallback)
    {
        return node.Properties.TryGetValue(key: id, value: out var v) &&
               v.Kind == GraphValueKind.Float
            ? v.AsFloat()
            : fallback;
    }

    private static int IntProp(GraphNode node, string id, int fallback)
    {
        return node.Properties.TryGetValue(key: id, value: out var v) &&
               v.Kind == GraphValueKind.Int
            ? v.AsInt()
            : fallback;
    }

    private static float[] ColorProp(GraphNode node, string id, float[] fallback)
    {
        return node.Properties.TryGetValue(key: id, value: out var v) &&
               v.Kind == GraphValueKind.Float4
            ? v.AsFloat4()
            : fallback;
    }

    private static float[] Vec3Prop(GraphNode node, string id, float dx, float dy, float dz)
    {
        return node.Properties.TryGetValue(key: id, value: out var v) &&
               v.Kind == GraphValueKind.Float3
            ? v.AsFloat3()
            : [dx, dy, dz];
    }

    private static bool BoolProp(GraphNode node, string id, bool fallback)
    {
        return node.Properties.TryGetValue(key: id, value: out var v) &&
               v.Kind == GraphValueKind.Bool
            ? v.AsBool()
            : fallback;
    }

    private static string? StringProp(GraphNode node, string id)
    {
        return node.Properties.TryGetValue(key: id, value: out var v) &&
               v.Kind == GraphValueKind.String
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
