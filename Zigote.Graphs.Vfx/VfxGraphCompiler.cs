using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Graphs.Core;
using Zigote.Vfx;

namespace Zigote.Graphs.Vfx;

/// <summary>
///     Lowers a VFX <see cref="GraphDocument" /> into a <see cref="VfxEmitterAsset" /> module stack.
///     Walks
///     from the single <c>VFX Output</c> emitter, gathering each connected block (spawn / shape /
///     initialize / update / render). Update modules are sorted by a canonical priority (forces before
///     over-life), so the result is independent of wiring order — predictable and deterministic.
/// </summary>
public static class VfxGraphCompiler
{
    private const float Deg2Rad = MathF.PI / 180f;

    public static CompiledVfxGraph Compile(GraphDocument graph)
    {
        var diags = new List<GraphDiagnostic>();
        var asset = new VfxEmitterAsset();

        var output = graph.Nodes.FirstOrDefault(n => n.DefinitionId == VfxNodeLibrary.Output);
        if (output is null)
        {
            diags.Add(Error(code: "VFX0001", message: "Graph has no VFX Output node."));
            return new CompiledVfxGraph {
                Success = false,
                Asset = asset,
                Diagnostics = diags,
            };
        }

        // ── Emitter ──────────────────────────────────────────────────────────
        asset.Capacity = Math.Max(
            val1: 1,
            val2: IntProp(node: output, id: "capacity", fallback: 1024)
        );
        asset.Looping = BoolProp(node: output, id: "looping", fallback: true);
        asset.Duration = MathF.Max(x: 0f, y: FloatProp(node: output, id: "duration", fallback: 0f));
        asset.Space = (SimulationSpace)IntProp(node: output, id: "space", fallback: 0);
        asset.Seed = unchecked((uint)IntProp(node: output, id: "seed", fallback: 12345));

        // ── Spawn ────────────────────────────────────────────────────────────
        bool hasSpawn = false;
        bool sawRate = false;
        foreach (var node in Sources(graph: graph, nodeId: output.Id, pinId: "in.spawn"))
        {
            switch (node.DefinitionId)
            {
                case VfxNodeLibrary.SpawnRate:
                    asset.SpawnRate = FloatProp(node: node, id: "rate", fallback: 24f);
                    sawRate = hasSpawn = true;
                    break;
                case VfxNodeLibrary.Burst:
                    asset.Bursts.Add(
                        new VfxBurst(
                            time: FloatProp(node: node, id: "time", fallback: 0f),
                            count: IntProp(node: node, id: "count", fallback: 30)
                        )
                    );
                    hasSpawn = true;
                    break;
            }
        }

        if (!sawRate) asset.SpawnRate = 0f; // no Spawn Rate node → bursts only

        // ── Emission shape ─────────────────────────────────────────────────────
        var shapeNode = Sources(graph: graph, nodeId: output.Id, pinId: "in.shape")
            .FirstOrDefault();
        if (shapeNode is not null)
        {
            asset.Shape = (EmissionShape)IntProp(node: shapeNode, id: "shape", fallback: 4);
            asset.ShapeRadius = FloatProp(node: shapeNode, id: "radius", fallback: 0.25f);
            asset.ConeAngleDegrees = FloatProp(node: shapeNode, id: "cone_angle", fallback: 25f);
            asset.ShapeBoxHalfExtents = Vec3Prop(
                node: shapeNode,
                id: "box",
                dx: 0.5f,
                dy: 0.5f,
                dz: 0.5f
            );
            asset.EmitDirection = Vec3Prop(
                node: shapeNode,
                id: "direction",
                dx: 0f,
                dy: 1f,
                dz: 0f
            );
        }

        // ── Initialize ─────────────────────────────────────────────────────────
        foreach (var node in Sources(graph: graph, nodeId: output.Id, pinId: "in.init"))
        {
            switch (node.DefinitionId)
            {
                case VfxNodeLibrary.InitVelocity:
                {
                    float? driven = EvalFloat(graph: graph, node: node, pin: "in.speed");
                    asset.StartSpeed = driven is { } s
                        ? FloatRange.Constant(s)
                        : new FloatRange(
                            min: FloatProp(node: node, id: "speed_min", fallback: 2f),
                            max: FloatProp(node: node, id: "speed_max", fallback: 4f)
                        );
                    break;
                }
                case VfxNodeLibrary.InitSize:
                    asset.StartSize = new FloatRange(
                        min: FloatProp(node: node, id: "size_min", fallback: 0.15f),
                        max: FloatProp(node: node, id: "size_max", fallback: 0.3f)
                    );
                    break;
                case VfxNodeLibrary.InitColor:
                    asset.StartColor = EvalColor(graph: graph, node: node, pin: "in.color") ??
                                       ColorProp(node: node, id: "color", fallback: Color.White);
                    asset.StartColorVariation = ColorProp(
                        node: node,
                        id: "variation",
                        fallback: Color.White
                    );
                    break;
                case VfxNodeLibrary.InitLifetime:
                    asset.StartLifetime =
                        new FloatRange(
                            min: FloatProp(node: node, id: "life_min", fallback: 1.5f),
                            max: FloatProp(node: node, id: "life_max", fallback: 2.5f)
                        );
                    break;
                case VfxNodeLibrary.InitRotation:
                    asset.StartRotation = new FloatRange(
                        min: FloatProp(node: node, id: "rot_min", fallback: 0f) * Deg2Rad,
                        max: FloatProp(node: node, id: "rot_max", fallback: 0f) * Deg2Rad
                    );
                    asset.StartAngularVelocity = new FloatRange(
                        min: FloatProp(node: node, id: "spin_min", fallback: 0f) * Deg2Rad,
                        max: FloatProp(node: node, id: "spin_max", fallback: 0f) * Deg2Rad
                    );
                    break;
            }
        }

        // ── Update modules (sorted by canonical priority) ────────────────────────
        var modules = new List<(int priority, VfxUpdateModule module)>();
        foreach (var node in Sources(graph: graph, nodeId: output.Id, pinId: "in.update"))
        {
            var built = BuildUpdateModule(graph: graph, node: node);
            if (built is not null) modules.Add(built.Value);
        }

        foreach (var (_, module) in modules.OrderBy(m => m.priority))
            asset.UpdateModules.Add(module);

        // ── Render ─────────────────────────────────────────────────────────────
        var renderNode = Sources(graph: graph, nodeId: output.Id, pinId: "in.render")
            .FirstOrDefault();
        if (renderNode is not null)
        {
            asset.Blend = (VfxBlendMode)IntProp(node: renderNode, id: "blend", fallback: 0);
            string? tex = StringProp(node: renderNode, id: "texture");
            asset.TexturePath = string.IsNullOrWhiteSpace(tex) ? null : tex;
            asset.SoftParticles = BoolProp(node: renderNode, id: "soft", fallback: true);
        }

        if (!hasSpawn)
        {
            diags.Add(
                Warn(
                    code: "VFX0002",
                    message: "Emitter has no Spawn Rate or Burst — nothing will spawn.",
                    node: output.Id
                )
            );
        }

        return new CompiledVfxGraph {
            Success = !diags.Any(d => d.Severity == GraphDiagnosticSeverity.Error),
            Asset = asset,
            Diagnostics = diags,
        };
    }

    private static (int priority, VfxUpdateModule module)? BuildUpdateModule(GraphDocument graph,
        GraphNode node)
    {
        switch (node.DefinitionId)
        {
            case VfxNodeLibrary.Gravity:
            {
                var g = EvalVec3(graph: graph, node: node, pin: "in.gravity") ?? Vec3Prop(
                    node: node,
                    id: "gravity",
                    dx: 0f,
                    dy: -9.8f,
                    dz: 0f
                );
                return (0, new GravityModule(g));
            }
            case VfxNodeLibrary.Drag:
                return (1, new DragModule(FloatProp(node: node, id: "drag", fallback: 0.5f)));
            case VfxNodeLibrary.Turbulence:
                return (2,
                    new TurbulenceModule(
                        strength: FloatProp(node: node, id: "strength", fallback: 1f),
                        frequency: FloatProp(node: node, id: "frequency", fallback: 1f)
                    ));
            case VfxNodeLibrary.Vortex:
                return (3, new VortexModule(
                    axis: Vec3Prop(
                        node: node,
                        id: "axis",
                        dx: 0f,
                        dy: 1f,
                        dz: 0f
                    ),
                    strength: FloatProp(node: node, id: "strength", fallback: 2f)
                ));
            case VfxNodeLibrary.ColorOverLife:
                return (10,
                    new ColorOverLifeModule(VfxRampJson.Parse(StringProp(node: node, id: "ramp"))));
            case VfxNodeLibrary.SizeOverLife:
                return (11,
                    new SizeOverLifeModule(
                        LifeCurve(
                            profile: (LifeProfile)IntProp(node: node, id: "profile", fallback: 5),
                            scale: FloatProp(node: node, id: "scale", fallback: 1f)
                        )
                    ));
            case VfxNodeLibrary.AlphaOverLife:
                return (12,
                    new AlphaOverLifeModule(
                        LifeCurve(
                            profile: (LifeProfile)IntProp(node: node, id: "profile", fallback: 2),
                            scale: FloatProp(node: node, id: "scale", fallback: 1f)
                        )
                    ));
            default:
                return null;
        }
    }

    private static FloatCurve LifeCurve(LifeProfile profile, float scale)
    {
        return profile switch {
            LifeProfile.Constant => FloatCurve.Constant(scale),
            LifeProfile.FadeIn or LifeProfile.Grow => FloatCurve.Linear(from: 0f, to: scale),
            LifeProfile.FadeOut or LifeProfile.Shrink => FloatCurve.Linear(from: scale, to: 0f),
            _ => new FloatCurve(
                [
                    new CurveKey(position: 0f, value: 0f),
                    new CurveKey(position: 0.5f, value: scale),
                    new CurveKey(position: 1f, value: 0f),
                ]
            ),
        };
    }

    // ── Graph readers ────────────────────────────────────────────────────────

    private static IEnumerable<GraphNode> Sources(GraphDocument graph, Guid nodeId, string pinId)
    {
        foreach (var e in graph.Edges)
        {
            if (e.To.NodeId != nodeId || e.To.PinId != pinId) continue;
            var n = graph.FindNode(e.From.NodeId);
            if (n is not null) yield return n;
        }
    }

    private static GraphNode? Source(GraphDocument graph, Guid nodeId, string pinId)
    {
        foreach (var e in graph.Edges)
        {
            if (e.To.NodeId == nodeId && e.To.PinId == pinId)
                return graph.FindNode(e.From.NodeId);
        }

        return null;
    }

    private static float? EvalFloat(GraphDocument graph, GraphNode node, string pin)
    {
        var src = Source(graph: graph, nodeId: node.Id, pinId: pin);
        return src?.DefinitionId == VfxNodeLibrary.FloatValue
            ? FloatProp(node: src, id: "value", fallback: 0f)
            : null;
    }

    private static Color? EvalColor(GraphDocument graph, GraphNode node, string pin)
    {
        var src = Source(graph: graph, nodeId: node.Id, pinId: pin);
        return src?.DefinitionId == VfxNodeLibrary.ColorValue
            ? ColorProp(node: src, id: "color", fallback: Color.White)
            : null;
    }

    private static Vec3? EvalVec3(GraphDocument graph, GraphNode node, string pin)
    {
        var src = Source(graph: graph, nodeId: node.Id, pinId: pin);
        return src?.DefinitionId == VfxNodeLibrary.VectorValue
            ? Vec3Prop(
                node: src,
                id: "vector",
                dx: 0f,
                dy: 0f,
                dz: 0f
            )
            : null;
    }

    // ── Property readers ───────────────────────────────────────────────────────

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

    private static bool BoolProp(GraphNode node, string id, bool fallback)
    {
        return node.Properties.TryGetValue(key: id, value: out var v) &&
               v.Kind == GraphValueKind.Bool
            ? v.AsBool()
            : fallback;
    }

    private static Color ColorProp(GraphNode node, string id, Color fallback)
    {
        if (node.Properties.TryGetValue(key: id, value: out var v) &&
            v.Kind == GraphValueKind.Float4)
        {
            float[] c = v.AsFloat4();
            return new Color(
                r: c[0],
                g: c[1],
                b: c[2],
                a: c[3]
            );
        }

        return fallback;
    }

    private static Vec3 Vec3Prop(GraphNode node, string id, float dx, float dy, float dz)
    {
        if (node.Properties.TryGetValue(key: id, value: out var v) &&
            v.Kind == GraphValueKind.Float3)
        {
            float[] a = v.AsFloat3();
            return new Vec3(x: a[0], y: a[1], z: a[2]);
        }

        return new Vec3(x: dx, y: dy, z: dz);
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
            DomainId = VfxNodeLibrary.DomainId,
        };
    }

    private static GraphDiagnostic Warn(string code, string message, Guid? node = null)
    {
        return new GraphDiagnostic {
            Severity = GraphDiagnosticSeverity.Warning,
            Code = code,
            Message = message,
            DomainId = VfxNodeLibrary.DomainId,
            NodeId = node,
        };
    }

    private enum LifeProfile
    {
        Constant,
        FadeIn,
        FadeOut,
        FadeInOut,
        Grow,
        Shrink,
        GrowShrink,
    }
}
