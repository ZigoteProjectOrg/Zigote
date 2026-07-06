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
            diags.Add(Error("VFX0001", "Graph has no VFX Output node."));
            return new CompiledVfxGraph {
                Success = false,
                Asset = asset,
                Diagnostics = diags,
            };
        }

        // ── Emitter ──────────────────────────────────────────────────────────
        asset.Capacity = Math.Max(1, IntProp(output, "capacity", 1024));
        asset.Looping = BoolProp(output, "looping", true);
        asset.Duration = MathF.Max(0f, FloatProp(output, "duration", 0f));
        asset.Space = (SimulationSpace)IntProp(output, "space", 0);
        asset.Seed = unchecked((uint)IntProp(output, "seed", 12345));

        // ── Spawn ────────────────────────────────────────────────────────────
        var hasSpawn = false;
        var sawRate = false;
        foreach (var node in Sources(graph, output.Id, "in.spawn"))
            switch (node.DefinitionId)
            {
                case VfxNodeLibrary.SpawnRate:
                    asset.SpawnRate = FloatProp(node, "rate", 24f);
                    sawRate = hasSpawn = true;
                    break;
                case VfxNodeLibrary.Burst:
                    asset.Bursts.Add(
                        new VfxBurst(FloatProp(node, "time", 0f), IntProp(node, "count", 30))
                    );
                    hasSpawn = true;
                    break;
            }

        if (!sawRate) asset.SpawnRate = 0f; // no Spawn Rate node → bursts only

        // ── Emission shape ─────────────────────────────────────────────────────
        var shapeNode = Sources(graph, output.Id, "in.shape").FirstOrDefault();
        if (shapeNode is not null)
        {
            asset.Shape = (EmissionShape)IntProp(shapeNode, "shape", 4);
            asset.ShapeRadius = FloatProp(shapeNode, "radius", 0.25f);
            asset.ConeAngleDegrees = FloatProp(shapeNode, "cone_angle", 25f);
            asset.ShapeBoxHalfExtents = Vec3Prop(
                shapeNode,
                "box",
                0.5f,
                0.5f,
                0.5f
            );
            asset.EmitDirection = Vec3Prop(
                shapeNode,
                "direction",
                0f,
                1f,
                0f
            );
        }

        // ── Initialize ─────────────────────────────────────────────────────────
        foreach (var node in Sources(graph, output.Id, "in.init"))
            switch (node.DefinitionId)
            {
                case VfxNodeLibrary.InitVelocity:
                {
                    var driven = EvalFloat(graph, node, "in.speed");
                    asset.StartSpeed = driven is { } s
                        ? FloatRange.Constant(s)
                        : new FloatRange(
                            FloatProp(node, "speed_min", 2f),
                            FloatProp(node, "speed_max", 4f)
                        );
                    break;
                }
                case VfxNodeLibrary.InitSize:
                    asset.StartSize = new FloatRange(
                        FloatProp(node, "size_min", 0.15f),
                        FloatProp(node, "size_max", 0.3f)
                    );
                    break;
                case VfxNodeLibrary.InitColor:
                    asset.StartColor = EvalColor(graph, node, "in.color") ??
                                       ColorProp(node, "color", Color.White);
                    asset.StartColorVariation = ColorProp(node, "variation", Color.White);
                    break;
                case VfxNodeLibrary.InitLifetime:
                    asset.StartLifetime =
                        new FloatRange(
                            FloatProp(node, "life_min", 1.5f),
                            FloatProp(node, "life_max", 2.5f)
                        );
                    break;
                case VfxNodeLibrary.InitRotation:
                    asset.StartRotation = new FloatRange(
                        FloatProp(node, "rot_min", 0f) * Deg2Rad,
                        FloatProp(node, "rot_max", 0f) * Deg2Rad
                    );
                    asset.StartAngularVelocity = new FloatRange(
                        FloatProp(node, "spin_min", 0f) * Deg2Rad,
                        FloatProp(node, "spin_max", 0f) * Deg2Rad
                    );
                    break;
            }

        // ── Update modules (sorted by canonical priority) ────────────────────────
        var modules = new List<(int priority, VfxUpdateModule module)>();
        foreach (var node in Sources(graph, output.Id, "in.update"))
        {
            var built = BuildUpdateModule(graph, node);
            if (built is not null) modules.Add(built.Value);
        }

        foreach (var (_, module) in modules.OrderBy(m => m.priority))
            asset.UpdateModules.Add(module);

        // ── Render ─────────────────────────────────────────────────────────────
        var renderNode = Sources(graph, output.Id, "in.render").FirstOrDefault();
        if (renderNode is not null)
        {
            asset.Blend = (VfxBlendMode)IntProp(renderNode, "blend", 0);
            var tex = StringProp(renderNode, "texture");
            asset.TexturePath = string.IsNullOrWhiteSpace(tex) ? null : tex;
            asset.SoftParticles = BoolProp(renderNode, "soft", true);
        }

        if (!hasSpawn)
            diags.Add(
                Warn(
                    "VFX0002",
                    "Emitter has no Spawn Rate or Burst — nothing will spawn.",
                    output.Id
                )
            );

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
                var g = EvalVec3(graph, node, "in.gravity") ?? Vec3Prop(
                    node,
                    "gravity",
                    0f,
                    -9.8f,
                    0f
                );
                return (0, new GravityModule(g));
            }
            case VfxNodeLibrary.Drag:
                return (1, new DragModule(FloatProp(node, "drag", 0.5f)));
            case VfxNodeLibrary.Turbulence:
                return (2,
                    new TurbulenceModule(
                        FloatProp(node, "strength", 1f),
                        FloatProp(node, "frequency", 1f)
                    ));
            case VfxNodeLibrary.Vortex:
                return (3, new VortexModule(
                    Vec3Prop(
                        node,
                        "axis",
                        0f,
                        1f,
                        0f
                    ),
                    FloatProp(node, "strength", 2f)
                ));
            case VfxNodeLibrary.ColorOverLife:
                return (10, new ColorOverLifeModule(VfxRampJson.Parse(StringProp(node, "ramp"))));
            case VfxNodeLibrary.SizeOverLife:
                return (11,
                    new SizeOverLifeModule(
                        LifeCurve(
                            (LifeProfile)IntProp(node, "profile", 5),
                            FloatProp(node, "scale", 1f)
                        )
                    ));
            case VfxNodeLibrary.AlphaOverLife:
                return (12,
                    new AlphaOverLifeModule(
                        LifeCurve(
                            (LifeProfile)IntProp(node, "profile", 2),
                            FloatProp(node, "scale", 1f)
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
            LifeProfile.FadeIn or LifeProfile.Grow => FloatCurve.Linear(0f, scale),
            LifeProfile.FadeOut or LifeProfile.Shrink => FloatCurve.Linear(scale, 0f),
            _ => new FloatCurve(
                [new CurveKey(0f, 0f), new CurveKey(0.5f, scale), new CurveKey(1f, 0f)]
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
            if (e.To.NodeId == nodeId && e.To.PinId == pinId)
                return graph.FindNode(e.From.NodeId);
        return null;
    }

    private static float? EvalFloat(GraphDocument graph, GraphNode node, string pin)
    {
        var src = Source(graph, node.Id, pin);
        return src?.DefinitionId == VfxNodeLibrary.FloatValue ? FloatProp(src, "value", 0f) : null;
    }

    private static Color? EvalColor(GraphDocument graph, GraphNode node, string pin)
    {
        var src = Source(graph, node.Id, pin);
        return src?.DefinitionId == VfxNodeLibrary.ColorValue
            ? ColorProp(src, "color", Color.White)
            : null;
    }

    private static Vec3? EvalVec3(GraphDocument graph, GraphNode node, string pin)
    {
        var src = Source(graph, node.Id, pin);
        return src?.DefinitionId == VfxNodeLibrary.VectorValue
            ? Vec3Prop(
                src,
                "vector",
                0f,
                0f,
                0f
            )
            : null;
    }

    // ── Property readers ───────────────────────────────────────────────────────

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

    private static bool BoolProp(GraphNode node, string id, bool fallback)
    {
        return node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Bool
            ? v.AsBool()
            : fallback;
    }

    private static Color ColorProp(GraphNode node, string id, Color fallback)
    {
        if (node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Float4)
        {
            var c = v.AsFloat4();
            return new Color(
                c[0],
                c[1],
                c[2],
                c[3]
            );
        }

        return fallback;
    }

    private static Vec3 Vec3Prop(GraphNode node, string id, float dx, float dy, float dz)
    {
        if (node.Properties.TryGetValue(id, out var v) && v.Kind == GraphValueKind.Float3)
        {
            var a = v.AsFloat3();
            return new Vec3(a[0], a[1], a[2]);
        }

        return new Vec3(dx, dy, dz);
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