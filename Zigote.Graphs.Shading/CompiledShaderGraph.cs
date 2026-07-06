using Zigote.Graphs.Core;

namespace Zigote.Graphs.Shading;

/// <summary>
///     The full result of compiling a shader-material graph: the generated WGSL (viewable now, the
///     seam
///     for the future native per-material pipeline), the backend-agnostic
///     <see cref="ShaderGraphProgram" />
///     (drives the live CPU preview), the external textures referenced, the constant approximation
///     that
///     maps onto the engine's fixed PBR material, and any diagnostics.
/// </summary>
public sealed class CompiledShaderGraph
{
    public bool Success { get; init; }
    public string Wgsl { get; init; } = "";
    public ShaderGraphProgram Program { get; init; } = ShaderGraphProgram.Empty;
    public IReadOnlyList<ShaderTextureRef> Textures { get; init; } = [];
    public SurfaceConstants Constants { get; init; } = SurfaceConstants.Default;
    public IReadOnlyList<GraphDiagnostic> Diagnostics { get; init; } = [];

    public string? TexturePath(TextureSlot slot)
    {
        foreach (var t in Textures)
            if (t.Slot == slot)
                return t.Path;
        return null;
    }

    /// <summary>
    ///     A trivial compiled graph that shades a fixed PBR material (no procedural nodes) — lets the
    ///     preview widget render a plain material via the same per-pixel path as a real graph.
    /// </summary>
    public static CompiledShaderGraph Constant(SurfaceConstants c,
        IReadOnlyList<ShaderTextureRef>? textures = null)
    {
        var instr = new List<ShaderInstr>();

        int F(float v)
        {
            var id = instr.Count;
            instr.Add(
                new ShaderInstr(
                    id,
                    ShaderOp.ConstFloat,
                    ShaderValueType.Float,
                    [],
                    v
                )
            );
            return id;
        }

        int V4(float r, float g, float b, float a)
        {
            var id = instr.Count;
            instr.Add(
                new ShaderInstr(
                    id,
                    ShaderOp.ConstVec4,
                    ShaderValueType.Vec4,
                    [],
                    r,
                    g,
                    b,
                    a
                )
            );
            return id;
        }

        var program = new ShaderGraphProgram {
            Instructions = instr,
            BaseColor = V4(
                c.BaseR,
                c.BaseG,
                c.BaseB,
                c.BaseA
            ),
            Metallic = F(c.Metallic),
            Roughness = F(c.Roughness),
            Specular = F(c.Specular),
            Emission = V4(
                c.EmissiveR,
                c.EmissiveG,
                c.EmissiveB,
                1f
            ),
            EmissionStrength = F(1f),
            Clearcoat = F(c.Clearcoat),
            ClearcoatRoughness = F(c.ClearcoatRoughness),
        };

        return new CompiledShaderGraph {
            Success = true,
            Program = program,
            Constants = c,
            Textures = textures ?? [],
        };
    }
}