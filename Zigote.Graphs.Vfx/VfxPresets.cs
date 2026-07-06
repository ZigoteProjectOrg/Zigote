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

    public static GraphDocument CreateDefault(string name = "VFX")
    {
        return Sparks(name);
    }

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
        var b = new Builder(name, o => { o.Properties["capacity"] = GraphValue.FromInt(512); });
        b.Spawn(VfxNodeLibrary.SpawnRate, n => n.Properties["rate"] = GraphValue.FromFloat(80f));
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Cone);
                n.Properties["radius"] = GraphValue.FromFloat(0.05f);
                n.Properties["cone_angle"] = GraphValue.FromFloat(25f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitVelocity,
            n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(3f);
                n.Properties["speed_max"] = GraphValue.FromFloat(6f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitSize,
            n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.03f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.08f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitLifetime,
            n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(0.6f);
                n.Properties["life_max"] = GraphValue.FromFloat(1.2f);
            }
        );
        b.Update(
            VfxNodeLibrary.Gravity,
            n => n.Properties["gravity"] = GraphValue.FromFloat3(0f, -6f, 0f)
        );
        b.Update(VfxNodeLibrary.Drag, n => n.Properties["drag"] = GraphValue.FromFloat(1f));
        b.Update(
            VfxNodeLibrary.ColorOverLife,
            n => SetRamp(
                n,
                new ColorStop(0f, Color.White),
                new ColorStop(0.4f, new Color(1f, 0.6f, 0.15f)),
                new ColorStop(
                    1f,
                    new Color(
                        0.6f,
                        0.1f,
                        0f,
                        0f
                    )
                )
            )
        );
        b.Update(
            VfxNodeLibrary.AlphaOverLife,
            n =>
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
        var b = new Builder(name, o => o.Properties["capacity"] = GraphValue.FromInt(1024));
        b.Spawn(VfxNodeLibrary.SpawnRate, n => n.Properties["rate"] = GraphValue.FromFloat(60f));
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Cone);
                n.Properties["radius"] = GraphValue.FromFloat(0.2f);
                n.Properties["cone_angle"] = GraphValue.FromFloat(12f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitVelocity,
            n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(1f);
                n.Properties["speed_max"] = GraphValue.FromFloat(2f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitSize,
            n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.4f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.7f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitLifetime,
            n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(1f);
                n.Properties["life_max"] = GraphValue.FromFloat(1.8f);
            }
        );
        b.Update(
            VfxNodeLibrary.Turbulence,
            n =>
            {
                n.Properties["strength"] = GraphValue.FromFloat(1.5f);
                n.Properties["frequency"] = GraphValue.FromFloat(2f);
            }
        );
        b.Update(
            VfxNodeLibrary.ColorOverLife,
            n => SetRamp(
                n,
                new ColorStop(0f, new Color(1f, 0.9f, 0.3f)),
                new ColorStop(0.5f, new Color(1f, 0.35f, 0.05f)),
                new ColorStop(
                    1f,
                    new Color(
                        0.2f,
                        0.02f,
                        0f,
                        0f
                    )
                )
            )
        );
        b.Update(
            VfxNodeLibrary.SizeOverLife,
            n =>
            {
                n.Properties["profile"] = GraphValue.FromInt(6); // Grow-Shrink
                n.Properties["scale"] = GraphValue.FromFloat(1f);
            }
        );
        b.Update(
            VfxNodeLibrary.AlphaOverLife,
            n => n.Properties["profile"] = GraphValue.FromInt(2)
        );
        b.Render(n => n.Properties["blend"] = GraphValue.FromInt((int)VfxBlendMode.Additive));
        return b.Doc;
    }

    private static GraphDocument Smoke(string name)
    {
        var b = new Builder(name, o => o.Properties["capacity"] = GraphValue.FromInt(512));
        b.Spawn(VfxNodeLibrary.SpawnRate, n => n.Properties["rate"] = GraphValue.FromFloat(18f));
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Cone);
                n.Properties["radius"] = GraphValue.FromFloat(0.3f);
                n.Properties["cone_angle"] = GraphValue.FromFloat(20f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitVelocity,
            n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(0.5f);
                n.Properties["speed_max"] = GraphValue.FromFloat(1f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitSize,
            n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.5f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.8f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitLifetime,
            n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(3f);
                n.Properties["life_max"] = GraphValue.FromFloat(5f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitColor,
            n =>
                n.Properties["color"] = GraphValue.FromFloat4(
                    0.5f,
                    0.5f,
                    0.52f,
                    1f
                )
        );
        b.Update(
            VfxNodeLibrary.Gravity,
            n => n.Properties["gravity"] = GraphValue.FromFloat3(0f, 0.4f, 0f)
        );
        b.Update(VfxNodeLibrary.Drag, n => n.Properties["drag"] = GraphValue.FromFloat(0.5f));
        b.Update(
            VfxNodeLibrary.Turbulence,
            n =>
            {
                n.Properties["strength"] = GraphValue.FromFloat(0.4f);
                n.Properties["frequency"] = GraphValue.FromFloat(0.6f);
            }
        );
        b.Update(
            VfxNodeLibrary.SizeOverLife,
            n =>
            {
                n.Properties["profile"] = GraphValue.FromInt(4); // Grow
                n.Properties["scale"] = GraphValue.FromFloat(2.2f);
            }
        );
        b.Update(
            VfxNodeLibrary.AlphaOverLife,
            n =>
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
        var b = new Builder(name, o => o.Properties["capacity"] = GraphValue.FromInt(768));
        b.Spawn(VfxNodeLibrary.SpawnRate, n => n.Properties["rate"] = GraphValue.FromFloat(50f));
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Sphere);
                n.Properties["radius"] = GraphValue.FromFloat(0.4f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitVelocity,
            n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(0.2f);
                n.Properties["speed_max"] = GraphValue.FromFloat(0.6f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitSize,
            n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.06f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.12f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitLifetime,
            n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(1.5f);
                n.Properties["life_max"] = GraphValue.FromFloat(2.5f);
            }
        );
        b.Update(
            VfxNodeLibrary.Vortex,
            n =>
            {
                n.Properties["axis"] = GraphValue.FromFloat3(0f, 1f, 0f);
                n.Properties["strength"] = GraphValue.FromFloat(3f);
            }
        );
        b.Update(VfxNodeLibrary.Drag, n => n.Properties["drag"] = GraphValue.FromFloat(0.3f));
        b.Update(
            VfxNodeLibrary.ColorOverLife,
            n => SetRamp(
                n,
                new ColorStop(0f, new Color(0.3f, 0.9f, 1f)),
                new ColorStop(0.5f, new Color(0.8f, 0.4f, 1f)),
                new ColorStop(
                    1f,
                    new Color(
                        0.4f,
                        0.1f,
                        0.8f,
                        0f
                    )
                )
            )
        );
        b.Update(
            VfxNodeLibrary.AlphaOverLife,
            n => n.Properties["profile"] = GraphValue.FromInt(2)
        );
        b.Render(n => n.Properties["blend"] = GraphValue.FromInt((int)VfxBlendMode.Additive));
        return b.Doc;
    }

    private static GraphDocument Rain(string name)
    {
        var b = new Builder(name, o => o.Properties["capacity"] = GraphValue.FromInt(2048));
        b.Spawn(VfxNodeLibrary.SpawnRate, n => n.Properties["rate"] = GraphValue.FromFloat(200f));
        b.Shape(n =>
            {
                n.Properties["shape"] = GraphValue.FromInt((int)EmissionShape.Box);
                n.Properties["box"] = GraphValue.FromFloat3(5f, 0.1f, 5f);
                n.Properties["direction"] = GraphValue.FromFloat3(0f, -1f, 0f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitVelocity,
            n =>
            {
                n.Properties["speed_min"] = GraphValue.FromFloat(8f);
                n.Properties["speed_max"] = GraphValue.FromFloat(10f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitSize,
            n =>
            {
                n.Properties["size_min"] = GraphValue.FromFloat(0.02f);
                n.Properties["size_max"] = GraphValue.FromFloat(0.04f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitLifetime,
            n =>
            {
                n.Properties["life_min"] = GraphValue.FromFloat(1f);
                n.Properties["life_max"] = GraphValue.FromFloat(1.5f);
            }
        );
        b.Init(
            VfxNodeLibrary.InitColor,
            n =>
                n.Properties["color"] = GraphValue.FromFloat4(
                    0.6f,
                    0.7f,
                    0.9f,
                    0.7f
                )
        );
        b.Update(
            VfxNodeLibrary.Gravity,
            n => n.Properties["gravity"] = GraphValue.FromFloat3(0f, -9.8f, 0f)
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
                VfxNodeLibrary.Output,
                820f,
                200f,
                configureOutput
            );
        }

        public GraphDocument Doc { get; }

        public void Spawn(string defId, Action<GraphNode> cfg)
        {
            Block(
                60f,
                ref _inY,
                defId,
                "out.spawn",
                "in.spawn",
                cfg
            );
        }

        public void Shape(Action<GraphNode> cfg)
        {
            Block(
                60f,
                ref _inY,
                VfxNodeLibrary.Shape,
                "out.shape",
                "in.shape",
                cfg
            );
        }

        public void Init(string defId, Action<GraphNode> cfg)
        {
            Block(
                60f,
                ref _inY,
                defId,
                "out.init",
                "in.init",
                cfg
            );
        }

        public void Update(string defId, Action<GraphNode> cfg)
        {
            Block(
                440f,
                ref _upY,
                defId,
                "out.update",
                "in.update",
                cfg
            );
        }

        public void Render(Action<GraphNode> cfg)
        {
            Block(
                440f,
                ref _upY,
                VfxNodeLibrary.Render,
                "out.render",
                "in.render",
                cfg
            );
        }

        private void Block(float x, ref float y, string defId, string fromPin, string toPin,
            Action<GraphNode> cfg)
        {
            var node = Add(
                defId,
                x,
                y,
                cfg
            );
            y += 96f;
            Doc.Edges.Add(
                new GraphEdge {
                    From = new GraphPinEndpoint(node.Id, fromPin),
                    To = new GraphPinEndpoint(_output.Id, toPin),
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