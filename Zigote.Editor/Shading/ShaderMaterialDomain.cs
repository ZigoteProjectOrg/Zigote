using Zigote.Core.Math3D;
using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;
using Zigote.Graphs.Shading;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Shading;

/// <summary>
///     Blender-style node shading domain (Task 2). Registers shader node definitions with the
///     <see cref="GraphDomainRegistry" /> and compiles a Principled-BSDF graph down to the engine's
///     existing PBR material parameters (<see cref="CompiledMaterial" />), which map 1:1 onto a
///     <see cref="SceneNode" />'s material.
///     Foundation scope: constant inputs (RGB / Value / node properties), single Image-Texture →
///     Base Color and Normal Map → Normal, and one level of Math / Mix Color evaluation. Full
///     arbitrary-graph → WGSL codegen is the next phase (see ROADMAP.md → Node-graph shading).
/// </summary>
public sealed class ShaderMaterialDomain : IGraphDomain
{
    public const string DomainIdConst = ShaderNodeLibrary.DomainId;
    public const string MaterialSchema = ShaderNodeLibrary.MaterialSchema;
    public const string BsdfType = ShaderNodeLibrary.BsdfType;

    // Node definition IDs (the catalogue lives in ShaderNodeLibrary; these forward for call-site brevity).
    public const string OutputId = ShaderNodeLibrary.Output;
    public const string PrincipledId = ShaderNodeLibrary.Principled;
    public const string TexImageId = ShaderNodeLibrary.TexImage;
    public const string RgbId = ShaderNodeLibrary.Rgb;
    public const string ValueId = ShaderNodeLibrary.Value;
    public const string MathId = ShaderNodeLibrary.Math;
    public const string MixColorId = ShaderNodeLibrary.MixColor;
    public const string MulColorId = ShaderNodeLibrary.MulColor;
    public const string ClampId = ShaderNodeLibrary.Clamp;
    public const string NormalMapId = ShaderNodeLibrary.NormalMap;

    public string Id => DomainIdConst;
    public string DisplayName => "Shader";
    public IReadOnlyList<string> SupportedSchemas => [MaterialSchema];

    public IReadOnlyList<GraphTypeDefinition> GetTypeDefinitions()
    {
        return ShaderNodeLibrary.TypeDefinitions;
    }

    public IReadOnlyList<NodeDefinition> GetNodeDefinitions()
    {
        return ShaderNodeLibrary.Definitions;
    }

    public bool CanCreateEdge(GraphDocument graph, GraphPinEndpoint from, GraphPinEndpoint to,
        out string? reason)
    {
        reason = null;
        if (from.NodeId == to.NodeId)
        {
            reason = "Cannot connect a node to itself.";
            return false;
        }

        return
            true; // foundation: type-checking handled at compile; the editor enforces in/out direction
    }

    public GraphValidationResult Validate(GraphDocument graph)
    {
        var diags = new List<GraphDiagnostic>();
        var outputs = graph.Nodes.Count(n => n.DefinitionId == OutputId);
        if (outputs == 0)
            diags.Add(
                new GraphDiagnostic {
                    Severity = GraphDiagnosticSeverity.Error,
                    Code = "SG0001",
                    Message = "Graph has no Material Output node.",
                    DomainId = DomainIdConst,
                }
            );
        else if (outputs > 1)
            diags.Add(
                new GraphDiagnostic {
                    Severity = GraphDiagnosticSeverity.Warning,
                    Code = "SG0002",
                    Message = "Multiple Material Output nodes; the first is used.",
                    DomainId = DomainIdConst,
                }
            );
        return new GraphValidationResult {
            IsValid = !diags.Any(d => d.Severity == GraphDiagnosticSeverity.Error),
            Diagnostics = diags,
        };
    }

    public GraphCompileResult Compile(GraphDocument graph, GraphCompileContext context)
    {
        var compiled = ShaderGraphCompiler.Compile(graph);
        // The artifact is the full CompiledShaderGraph (WGSL + program + textures + constants). The editor
        // drives the live preview + WGSL view from it and maps its constant-fold onto the scene node.
        return new GraphCompileResult {
            Success = compiled.Success,
            Diagnostics = compiled.Diagnostics,
            CompiledArtifact = compiled.Success ? compiled : null,
        };
    }

    /// <summary>Compile and map the constant approximation onto the engine's fixed PBR material.</summary>
    public CompiledMaterial? CompileMaterial(GraphDocument graph,
        out IReadOnlyList<GraphDiagnostic> diagnostics)
    {
        var compiled = ShaderGraphCompiler.Compile(graph);
        diagnostics = compiled.Diagnostics;
        return compiled.Success ? ToMaterial(compiled) : null;
    }

    /// <summary>
    ///     Map a compiled graph's constant approximation + texture refs onto a
    ///     <see cref="CompiledMaterial" />.
    /// </summary>
    public static CompiledMaterial ToMaterial(CompiledShaderGraph compiled)
    {
        var c = compiled.Constants;
        return new CompiledMaterial {
            BaseColor = [c.BaseR, c.BaseG, c.BaseB, c.BaseA],
            Metallic = c.Metallic,
            Roughness = c.Roughness,
            Specular = c.Specular,
            Clearcoat = c.Clearcoat,
            ClearcoatRoughness = c.ClearcoatRoughness,
            Emissive = [c.EmissiveR, c.EmissiveG, c.EmissiveB],
            BaseColorTexturePath = compiled.TexturePath(TextureSlot.BaseColor),
            NormalTexturePath = compiled.TexturePath(TextureSlot.Normal),
        };
    }

    /// <summary>A registry with the shader domain registered — for hosting the graph editor.</summary>
    public static GraphDomainRegistry CreateRegistry()
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(new ShaderMaterialDomain());
        return registry;
    }

    /// <summary>
    ///     Build an editable shader graph seeded from a scene node's current material values,
    ///     so opening the node editor reflects the live material and editing it round-trips back.
    /// </summary>
    public static GraphDocument CreateGraphFromNode(SceneNode node)
    {
        var doc = CreateDefaultMaterialGraph($"{node.Name} material");
        var p = doc.Nodes.First(n => n.DefinitionId == PrincipledId);
        p.Properties["in.base_color"] = GraphValue.FromFloat4(
            node.MeshColor.X,
            node.MeshColor.Y,
            node.MeshColor.Z,
            1f
        );
        p.Properties["in.metallic"] = GraphValue.FromFloat(node.MeshMetallic);
        p.Properties["in.roughness"] = GraphValue.FromFloat(node.MeshRoughness);
        p.Properties["in.specular"] = GraphValue.FromFloat(node.MeshSpecular);
        p.Properties["in.clearcoat"] = GraphValue.FromFloat(node.MeshClearcoat);
        p.Properties["in.clearcoat_roughness"] = GraphValue.FromFloat(node.MeshClearcoatRoughness);
        p.Properties["in.emission"] =
            GraphValue.FromFloat4(
                node.MeshEmissive.X,
                node.MeshEmissive.Y,
                node.MeshEmissive.Z,
                1f
            );
        p.Properties["in.emission_strength"] = GraphValue.FromFloat(1f);
        return doc;
    }

    /// <summary>A ready-to-edit default graph: Principled BSDF → Material Output.</summary>
    public static GraphDocument CreateDefaultMaterialGraph(string name)
    {
        var doc = new GraphDocument {
            Name = name,
            DomainId = DomainIdConst,
            SchemaId = MaterialSchema,
        };
        var principled = AddNode(
            doc,
            PrincipledId,
            -300f,
            -150f,
            210f
        );
        var output = AddNode(
            doc,
            OutputId,
            100f,
            -70f,
            180f
        );
        doc.Edges.Add(
            new GraphEdge {
                From = new GraphPinEndpoint(principled.Id, "out.bsdf"),
                To = new GraphPinEndpoint(output.Id, "in.surface"),
            }
        );
        return doc;
    }

    /// <summary>
    ///     A starter graph for a named preset (pbr / unlit / glass / car_paint). Each fully seeds node
    ///     layouts (a node with no layout is invisible/unhittable on the canvas — the seed-layout fix).
    /// </summary>
    public static GraphDocument CreatePreset(string presetId, string name)
    {
        var doc = new GraphDocument {
            Name = name,
            DomainId = DomainIdConst,
            SchemaId = MaterialSchema,
        };
        var p = AddNode(
            doc,
            PrincipledId,
            -300f,
            -150f,
            210f
        );
        var output = AddNode(
            doc,
            OutputId,
            100f,
            -70f,
            180f
        );
        doc.Edges.Add(
            new GraphEdge {
                From = new GraphPinEndpoint(p.Id, "out.bsdf"),
                To = new GraphPinEndpoint(output.Id, "in.surface"),
            }
        );

        switch (presetId)
        {
            case "unlit":
                p.Properties["in.metallic"] = GraphValue.FromFloat(0f);
                p.Properties["in.roughness"] = GraphValue.FromFloat(1f);
                p.Properties["in.specular"] = GraphValue.FromFloat(0f);
                break;
            case "glass":
                p.Properties["in.base_color"] = GraphValue.FromFloat4(
                    0.85f,
                    0.92f,
                    0.95f,
                    1f
                );
                p.Properties["in.roughness"] = GraphValue.FromFloat(0.03f);
                p.Properties["in.clearcoat"] = GraphValue.FromFloat(1f);
                p.Properties["in.specular"] = GraphValue.FromFloat(1.5f);
                break;
            case "car_paint":
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
                break;
        }

        return doc;
    }

    /// <summary>Add a node and seed its editor layout (X/Y/Width). Height auto-fits the node's content.</summary>
    private static GraphNode AddNode(GraphDocument doc, string defId, float x, float y,
        float width = 190f)
    {
        var n = new GraphNode { DefinitionId = defId };
        doc.Nodes.Add(n);
        doc.EditorData.NodeLayouts[n.Id] = new NodeLayoutData {
            X = x,
            Y = y,
            Width = width,
            Height = 90f,
        };
        return n;
    }

    /// <summary>Push a compiled material onto a scene node (graph → material → renderer).</summary>
    public static void ApplyTo(CompiledMaterial mat, SceneNode node)
    {
        node.MeshColor = new Vec3(mat.BaseColor[0], mat.BaseColor[1], mat.BaseColor[2]);
        node.MeshMetallic = mat.Metallic;
        node.MeshRoughness = mat.Roughness;
        node.MeshClearcoat = mat.Clearcoat;
        node.MeshClearcoatRoughness = mat.ClearcoatRoughness;
        node.MeshSpecular = mat.Specular;
        node.MeshEmissive = new Vec3(mat.Emissive[0], mat.Emissive[1], mat.Emissive[2]);
        if (mat.BaseColorTexturePath is { Length: > 0 })
            node.TexturePath = mat.BaseColorTexturePath;
        // Round-trip the normal map (previously dropped, so a graph-set normal was lost on apply).
        if (mat.NormalTexturePath is { Length: > 0 })
            node.NormalTexturePath = mat.NormalTexturePath;
    }
}

/// <summary>Compiled output of a shader material graph — maps 1:1 onto the engine PBR material.</summary>
public sealed class CompiledMaterial
{
    public float[] BaseColor { get; set; } = [0.8f, 0.8f, 0.8f, 1f];
    public float Metallic { get; set; }
    public float Roughness { get; set; } = 0.5f;
    public float Specular { get; set; } = 1f;
    public float Clearcoat { get; set; }
    public float ClearcoatRoughness { get; set; } = 0.03f;
    public float[] Emissive { get; set; } = [0f, 0f, 0f];
    public string? BaseColorTexturePath { get; set; }
    public string? NormalTexturePath { get; set; }
}