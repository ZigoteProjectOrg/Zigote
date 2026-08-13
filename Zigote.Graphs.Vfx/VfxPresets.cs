using Zigote.Core;
using Zigote.Graphs.Core;
using Zigote.Vfx;

namespace Zigote.Graphs.Vfx;

/// <summary>
///     Ready-made VFX emitter graphs. The editor seeds a new VFX node from
///     <see cref="CreateDefault" /> and
///     offers the named presets in the search/"Edit as Nodes…" flow. Each builds a fully-wired
///     <see cref="GraphDocument" /> (Output + spawn/shape/init/update/render blocks) with editor
///     layout.
/// </summary>
public static class VfxPresets
{
    public static IReadOnlyList<string> Names { get; } =
        ["Sparks", "Fire", "Smoke", "Magic", "Rain"];

    public static GraphDocument CreateDefault(string name = "VFX") => Sparks(name);

    public static GraphDocument Create(string preset, string name)
    {
        return preset switch {
            "Fire" => Fire(name),
            "Smoke" => Smoke(name),
            "Magic" => Magic(name),
            "Rain" => Rain(name),
            _ => Sparks(name),
        };
    }

    private static GraphDocument Sparks(string name)
    {
        var b = new Builder(
            name: name,
            configureOutput: o => { o.Properties["capacity"] = GraphValue.FromInt(512); }
        );
        b.Spawn(
            defId: VfxNodeLibrary.SpawnRate,
            cfg: n => n.Properties["rate"] = GraphValue.FromFloat(80f)
        );
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Cone);
                n.Properties["radius"] = GraphValue.FromFloat(0.05f);
                n.Properties["cone_angle"] = GraphValue.FromFloat(25f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitVelocity,
            cfg: n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(3f);
                n.Properties["speed_max"] = GraphValue.FromFloat(6f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitSize,
            cfg: n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.03f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.08f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitLifetime,
            cfg: n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(0.6f);
                n.Properties["life_max"] = GraphValue.FromFloat(1.2f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.Gravity,
            cfg: n => n.Properties["gravity"] = GraphValue.FromFloat3(x: 0f, y: -6f, z: 0f)
        );
        b.Update(
            defId: VfxNodeLibrary.Drag,
            cfg: n => n.Properties["drag"] = GraphValue.FromFloat(1f)
        );
        b.Update(
            defId: VfxNodeLibrary.ColorOverLife,
            cfg: n => SetRamp(
                node: n,
                new ColorStop(position: 0f, color: Color.White),
                new ColorStop(position: 0.4f, color: new Color(r: 1f, g: 0.6f, b: 0.15f)),
                new ColorStop(
                    position: 1f,
                    color: new Color(
                        r: 0.6f,
                        g: 0.1f,
                        b: 0f,
                        a: 0f
                    )
                )
            )
        );
        b.Update(
            defId: VfxNodeLibrary.AlphaOverLife,
            cfg: n =>
            {
                n.Properties["profile"] = GraphValue.FromInt(2); // Fade Out
                n.Properties["scale"] = GraphValue.FromFloat(1f);
            }
        );
        b.Render(n => n.Properties["blend"] = GraphValue.FromInt((int)VfxBlendMode.Additive));
        return b.Doc;
    }

    private static GraphDocument Fire(string name)
    {
        var b = new Builder(
            name: name,
            configureOutput: o => o.Properties["capacity"] = GraphValue.FromInt(1024)
        );
        b.Spawn(
            defId: VfxNodeLibrary.SpawnRate,
            cfg: n => n.Properties["rate"] = GraphValue.FromFloat(60f)
        );
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Cone);
                n.Properties["radius"] = GraphValue.FromFloat(0.2f);
                n.Properties["cone_angle"] = GraphValue.FromFloat(12f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitVelocity,
            cfg: n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(1f);
                n.Properties["speed_max"] = GraphValue.FromFloat(2f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitSize,
            cfg: n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.4f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.7f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitLifetime,
            cfg: n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(1f);
                n.Properties["life_max"] = GraphValue.FromFloat(1.8f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.Turbulence,
            cfg: n =>
            {
                n.Properties["strength"] = GraphValue.FromFloat(1.5f);
                n.Properties["frequency"] = GraphValue.FromFloat(2f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.ColorOverLife,
            cfg: n => SetRamp(
                node: n,
                new ColorStop(position: 0f, color: new Color(r: 1f, g: 0.9f, b: 0.3f)),
                new ColorStop(position: 0.5f, color: new Color(r: 1f, g: 0.35f, b: 0.05f)),
                new ColorStop(
                    position: 1f,
                    color: new Color(
                        r: 0.2f,
                        g: 0.02f,
                        b: 0f,
                        a: 0f
                    )
                )
            )
        );
        b.Update(
            defId: VfxNodeLibrary.SizeOverLife,
            cfg: n =>
            {
                n.Properties["profile"] = GraphValue.FromInt(6); // Grow-Shrink
                n.Properties["scale"] = GraphValue.FromFloat(1f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.AlphaOverLife,
            cfg: n => n.Properties["profile"] = GraphValue.FromInt(2)
        );
        b.Render(n => n.Properties["blend"] = GraphValue.FromInt((int)VfxBlendMode.Additive));
        return b.Doc;
    }

    private static GraphDocument Smoke(string name)
    {
        var b = new Builder(
            name: name,
            configureOutput: o => o.Properties["capacity"] = GraphValue.FromInt(512)
        );
        b.Spawn(
            defId: VfxNodeLibrary.SpawnRate,
            cfg: n => n.Properties["rate"] = GraphValue.FromFloat(18f)
        );
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Cone);
                n.Properties["radius"] = GraphValue.FromFloat(0.3f);
                n.Properties["cone_angle"] = GraphValue.FromFloat(20f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitVelocity,
            cfg: n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(0.5f);
                n.Properties["speed_max"] = GraphValue.FromFloat(1f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitSize,
            cfg: n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.5f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.8f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitLifetime,
            cfg: n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(3f);
                n.Properties["life_max"] = GraphValue.FromFloat(5f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitColor,
            cfg: n =>
                n.Properties["color"] = GraphValue.FromFloat4(
                    x: 0.5f,
                    y: 0.5f,
                    z: 0.52f,
                    w: 1f
                )
        );
        b.Update(
            defId: VfxNodeLibrary.Gravity,
            cfg: n => n.Properties["gravity"] = GraphValue.FromFloat3(x: 0f, y: 0.4f, z: 0f)
        );
        b.Update(
            defId: VfxNodeLibrary.Drag,
            cfg: n => n.Properties["drag"] = GraphValue.FromFloat(0.5f)
        );
        b.Update(
            defId: VfxNodeLibrary.Turbulence,
            cfg: n =>
            {
                n.Properties["strength"] = GraphValue.FromFloat(0.4f);
                n.Properties["frequency"] = GraphValue.FromFloat(0.6f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.SizeOverLife,
            cfg: n =>
            {
                n.Properties["profile"] = GraphValue.FromInt(4); // Grow
                n.Properties["scale"] = GraphValue.FromFloat(2.2f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.AlphaOverLife,
            cfg: n =>
            {
                n.Properties["profile"] = GraphValue.FromInt(3); // Fade In-Out
                n.Properties["scale"] = GraphValue.FromFloat(0.5f);
            }
        );
        b.Render(n =>
            {
                n.Properties["blend"] = GraphValue.FromInt((int)VfxBlendMode.AlphaBlend);
                n.Properties["soft"] = GraphValue.FromBool(true);
            }
        );
        return b.Doc;
    }

    private static GraphDocument Magic(string name)
    {
        var b = new Builder(
            name: name,
            configureOutput: o => o.Properties["capacity"] = GraphValue.FromInt(768)
        );
        b.Spawn(
            defId: VfxNodeLibrary.SpawnRate,
            cfg: n => n.Properties["rate"] = GraphValue.FromFloat(50f)
        );
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Sphere);
                n.Properties["radius"] = GraphValue.FromFloat(0.4f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitVelocity,
            cfg: n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(0.2f);
                n.Properties["speed_max"] = GraphValue.FromFloat(0.6f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitSize,
            cfg: n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.06f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.12f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitLifetime,
            cfg: n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(1.5f);
                n.Properties["life_max"] = GraphValue.FromFloat(2.5f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.Vortex,
            cfg: n =>
            {
                n.Properties["axis"] = GraphValue.FromFloat3(x: 0f, y: 1f, z: 0f);
                n.Properties["strength"] = GraphValue.FromFloat(3f);
            }
        );
        b.Update(
            defId: VfxNodeLibrary.Drag,
            cfg: n => n.Properties["drag"] = GraphValue.FromFloat(0.3f)
        );
        b.Update(
            defId: VfxNodeLibrary.ColorOverLife,
            cfg: n => SetRamp(
                node: n,
                new ColorStop(position: 0f, color: new Color(r: 0.3f, g: 0.9f, b: 1f)),
                new ColorStop(position: 0.5f, color: new Color(r: 0.8f, g: 0.4f, b: 1f)),
                new ColorStop(
                    position: 1f,
                    color: new Color(
                        r: 0.4f,
                        g: 0.1f,
                        b: 0.8f,
                        a: 0f
                    )
                )
            )
        );
        b.Update(
            defId: VfxNodeLibrary.AlphaOverLife,
            cfg: n => n.Properties["profile"] = GraphValue.FromInt(2)
        );
        b.Render(n => n.Properties["blend"] = GraphValue.FromInt((int)VfxBlendMode.Additive));
        return b.Doc;
    }

    private static GraphDocument Rain(string name)
    {
        var b = new Builder(
            name: name,
            configureOutput: o => o.Properties["capacity"] = GraphValue.FromInt(2048)
        );
        b.Spawn(
            defId: VfxNodeLibrary.SpawnRate,
            cfg: n => n.Properties["rate"] = GraphValue.FromFloat(200f)
        );
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Box);
                n.Properties["box"] = GraphValue.FromFloat3(x: 5f, y: 0.1f, z: 5f);
                n.Properties["direction"] = GraphValue.FromFloat3(x: 0f, y: -1f, z: 0f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitVelocity,
            cfg: n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(8f);
                n.Properties["speed_max"] = GraphValue.FromFloat(10f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitSize,
            cfg: n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.02f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.04f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitLifetime,
            cfg: n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(1f);
                n.Properties["life_max"] = GraphValue.FromFloat(1.5f);
            }
        );
        b.Init(
            defId: VfxNodeLibrary.InitColor,
            cfg: n =>
                n.Properties["color"] = GraphValue.FromFloat4(
                    x: 0.6f,
                    y: 0.7f,
                    z: 0.9f,
                    w: 0.7f
                )
        );
        b.Update(
            defId: VfxNodeLibrary.Gravity,
            cfg: n => n.Properties["gravity"] = GraphValue.FromFloat3(x: 0f, y: -9.8f, z: 0f)
        );
        b.Render(n => n.Properties["blend"] = GraphValue.FromInt((int)VfxBlendMode.AlphaBlend));
        return b.Doc;
    }

    private static void SetRamp(GraphNode node, params ColorStop[] stops)
    {
        node.Properties["ramp"] =
            GraphValue.FromString(VfxRampJson.Serialize(new ColorRamp(stops)));
    }

    /// <summary>Builds a wired emitter graph, laying blocks into category columns feeding the Output node.</summary>
    private sealed class Builder
    {
        private readonly GraphNode _output;
        private float _inY = 40f;
        private float _upY = 40f;

        public Builder(string name, Action<GraphNode> configureOutput)
        {
            Doc = new GraphDocument {
                Name = name,
                DomainId = VfxNodeLibrary.DomainId,
                SchemaId = VfxNodeLibrary.EmitterSchema,
            };
            _output = Add(
                defId: VfxNodeLibrary.Output,
                x: 820f,
                y: 200f,
                cfg: configureOutput
            );
        }

        public GraphDocument Doc { get; }

        public void Spawn(string defId, Action<GraphNode> cfg)
        {
            Block(
                x: 60f,
                y: ref _inY,
                defId: defId,
                fromPin: "out.spawn",
                toPin: "in.spawn",
                cfg: cfg
            );
        }

        public void Shape(Action<GraphNode> cfg)
        {
            Block(
                x: 60f,
                y: ref _inY,
                defId: VfxNodeLibrary.Shape,
                fromPin: "out.shape",
                toPin: "in.shape",
                cfg: cfg
            );
        }

        public void Init(string defId, Action<GraphNode> cfg)
        {
            Block(
                x: 60f,
                y: ref _inY,
                defId: defId,
                fromPin: "out.init",
                toPin: "in.init",
                cfg: cfg
            );
        }

        public void Update(string defId, Action<GraphNode> cfg)
        {
            Block(
                x: 440f,
                y: ref _upY,
                defId: defId,
                fromPin: "out.update",
                toPin: "in.update",
                cfg: cfg
            );
        }

        public void Render(Action<GraphNode> cfg)
        {
            Block(
                x: 440f,
                y: ref _upY,
                defId: VfxNodeLibrary.Render,
                fromPin: "out.render",
                toPin: "in.render",
                cfg: cfg
            );
        }

        private void Block(float x, ref float y, string defId, string fromPin, string toPin,
            Action<GraphNode> cfg)
        {
            var node = Add(
                defId: defId,
                x: x,
                y: y,
                cfg: cfg
            );
            y += 96f;
            Doc.Edges.Add(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: node.Id, PinId: fromPin),
                    To = new GraphPinEndpoint(NodeId: _output.Id, PinId: toPin),
                }
            );
        }

        private GraphNode Add(string defId, float x, float y, Action<GraphNode> cfg)
        {
            var node = new GraphNode { DefinitionId = defId };
            cfg(node);
            Doc.Nodes.Add(node);
            Doc.EditorData.NodeLayouts[node.Id] = new NodeLayoutData {
                X = x,
                Y = y,
                Width = 170f,
                Height = 80f,
            };
            return node;
        }
    }
}
