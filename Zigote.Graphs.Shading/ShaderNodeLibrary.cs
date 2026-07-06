using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;

namespace Zigote.Graphs.Shading;

/// <summary>
///     The shader-material node catalogue: stable node-type IDs + their <see cref="NodeDefinition" />
///     s,
///     shared by <c>ShaderMaterialDomain</c> (the editor-side <see cref="IGraphDomain" />) and the
///     compiler. Kept UI/native-free so it is reusable and headless-testable.
/// </summary>
public static class ShaderNodeLibrary
{
    public const string DomainId = "zigote.shader";
    public const string MaterialSchema = "shader.material";
    public const string BsdfType = "shader.bsdf";

    // ── Node-type IDs ──────────────────────────────────────────────────────────
    public const string Output = "shader.output";
    public const string Principled = "shader.principled";
    public const string TexImage = "shader.tex_image";
    public const string Rgb = "shader.rgb";
    public const string Value = "shader.value";
    public const string Math = "shader.math";
    public const string MixColor = "shader.mix_color";
    public const string MulColor = "shader.mul_color";
    public const string Clamp = "shader.clamp";
    public const string NormalMap = "shader.normal_map";

    // Core procedural nodes.
    public const string TexCoord = "shader.tex_coord";
    public const string Mapping = "shader.mapping";
    public const string Noise = "shader.noise";
    public const string Gradient = "shader.gradient";
    public const string Checker = "shader.checker";
    public const string Wave = "shader.wave";
    public const string VecMath = "shader.vec_math";
    public const string MapRange = "shader.map_range";
    public const string SeparateXyz = "shader.separate_xyz";
    public const string CombineXyz = "shader.combine_xyz";
    public const string ColorRamp = "shader.color_ramp";

    // Enum labels — order MUST match the corresponding op enum in ShaderGraphProgram.
    public static readonly string[] MathOpLabels = [
        "Add", "Subtract", "Multiply", "Divide", "Power", "Logarithm", "Minimum", "Maximum",
        "Square Root",
        "Absolute", "Sine", "Cosine", "Floor", "Fraction", "Modulo", "Greater Than", "Less Than",
    ];

    public static readonly string[] VecMathOpLabels = [
        "Add", "Subtract", "Multiply", "Divide", "Cross Product", "Dot Product", "Normalize",
        "Length", "Scale",
        "Distance", "Floor", "Fraction",
    ];

    public static readonly string[] MappingTypeLabels = ["Point", "Texture", "Vector", "Normal"];
    public static readonly string[] NoiseDimLabels = ["1D", "2D", "3D", "4D"];

    public static readonly string[] GradientTypeLabels =
        ["Linear", "Quadratic", "Easing", "Diagonal", "Spherical", "Quadratic Sphere", "Radial"];

    public static readonly string[] WaveTypeLabels = ["Bands", "Rings"];
    public static readonly string[] WaveProfileLabels = ["Sine", "Saw", "Triangle"];
    public static readonly string[] RampInterpLabels = ["Linear", "Constant", "Ease"];

    public static IReadOnlyList<GraphTypeDefinition> TypeDefinitions { get; } = [
        new() {
            Id = BsdfType,
            DisplayName = "BSDF",
            DomainId = DomainId,
            Category = GraphTypeCategory.Opaque,
            WireColor = 0xFF44AA66,
        },
    ];

    public static IReadOnlyList<NodeDefinition> Definitions { get; } = BuildDefinitions();

    private static IReadOnlyList<NodeDefinition> BuildDefinitions()
    {
        return [
            Def(
                Output,
                "Material Output",
                "Output",
                [
                    In("in.surface", "Surface", BsdfType), In("in.volume", "Volume", BsdfType),
                    In("in.displacement", "Displacement", GraphTypeRef.Float.Id),
                ],
                []
            ),

            Def(
                Principled,
                "Principled BSDF",
                "Shader",
                [
                    In("in.base_color", "Base Color", GraphTypeRef.Color.Id),
                    In("in.metallic", "Metallic", GraphTypeRef.Float.Id),
                    In("in.roughness", "Roughness", GraphTypeRef.Float.Id),
                    In("in.specular", "Specular", GraphTypeRef.Float.Id),
                    In("in.emission", "Emission Color", GraphTypeRef.Color.Id),
                    In("in.emission_strength", "Emission Strength", GraphTypeRef.Float.Id),
                    In("in.clearcoat", "Coat Weight", GraphTypeRef.Float.Id),
                    In("in.clearcoat_roughness", "Coat Roughness", GraphTypeRef.Float.Id),
                    In("in.normal", "Normal", GraphTypeRef.Float3.Id),
                ],
                [Out("out.bsdf", "BSDF", BsdfType)],
                [
                    Prop(
                        "in.base_color",
                        "Base Color",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            0.8f,
                            0.8f,
                            0.8f,
                            1f
                        )
                    ),
                    PropF(
                        "in.metallic",
                        "Metallic",
                        0f,
                        0f,
                        1f
                    ),
                    PropF(
                        "in.roughness",
                        "Roughness",
                        0.5f,
                        0f,
                        1f
                    ),
                    PropF(
                        "in.specular",
                        "Specular",
                        1f,
                        0f,
                        2f
                    ),
                    Prop(
                        "in.emission",
                        "Emission Color",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            0f,
                            0f,
                            0f,
                            1f
                        )
                    ),
                    PropF(
                        "in.emission_strength",
                        "Emission Strength",
                        0f,
                        0f,
                        50f
                    ),
                    PropF(
                        "in.clearcoat",
                        "Coat Weight",
                        0f,
                        0f,
                        1f
                    ),
                    PropF(
                        "in.clearcoat_roughness",
                        "Coat Roughness",
                        0.03f,
                        0f,
                        1f
                    ),
                ]
            ),

            Def(
                TexImage,
                "Image Texture",
                "Texture",
                [],
                [
                    Out("out.color", "Color", GraphTypeRef.Color.Id),
                    Out("out.alpha", "Alpha", GraphTypeRef.Float.Id),
                ],
                [
                    Prop(
                        "path",
                        "Image",
                        GraphTypeRef.String,
                        GraphValue.FromString("")
                    ),
                ]
            ),

            Def(
                Rgb,
                "RGB",
                "Input",
                [],
                [Out("out.color", "Color", GraphTypeRef.Color.Id)],
                [
                    Prop(
                        "color",
                        "Color",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            0.8f,
                            0.8f,
                            0.8f,
                            1f
                        )
                    ),
                ]
            ),

            Def(
                Value,
                "Value",
                "Input",
                [],
                [Out("out.value", "Value", GraphTypeRef.Float.Id)],
                [
                    PropF(
                        "value",
                        "Value",
                        0.5f,
                        0f,
                        1f
                    ),
                ]
            ),

            Def(
                Math,
                "Math",
                "Converter",
                [In("in.a", "A", GraphTypeRef.Float.Id), In("in.b", "B", GraphTypeRef.Float.Id)],
                [Out("out.result", "Result", GraphTypeRef.Float.Id)],
                [
                    PropF(
                        "in.a",
                        "A",
                        0f,
                        0f,
                        1f
                    ),
                    PropF(
                        "in.b",
                        "B",
                        0f,
                        0f,
                        1f
                    ),
                    PropEnum("op", "Operation", MathOpLabels),
                ]
            ),

            Def(
                MixColor,
                "Mix Color",
                "Color",
                [
                    In("in.factor", "Factor", GraphTypeRef.Float.Id),
                    In("in.a", "A", GraphTypeRef.Color.Id),
                    In("in.b", "B", GraphTypeRef.Color.Id),
                ],
                [Out("out.result", "Result", GraphTypeRef.Color.Id)],
                [
                    PropF(
                        "in.factor",
                        "Factor",
                        0.5f,
                        0f,
                        1f
                    ),
                    Prop(
                        "in.a",
                        "A",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            0f,
                            0f,
                            0f,
                            1f
                        )
                    ),
                    Prop(
                        "in.b",
                        "B",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            1f,
                            1f,
                            1f,
                            1f
                        )
                    ),
                ]
            ),

            Def(
                MulColor,
                "Multiply Color",
                "Color",
                [In("in.a", "A", GraphTypeRef.Color.Id), In("in.b", "B", GraphTypeRef.Color.Id)],
                [Out("out.result", "Result", GraphTypeRef.Color.Id)],
                [
                    Prop(
                        "in.a",
                        "A",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            1f,
                            1f,
                            1f,
                            1f
                        )
                    ),
                    Prop(
                        "in.b",
                        "B",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            1f,
                            1f,
                            1f,
                            1f
                        )
                    ),
                ]
            ),

            Def(
                Clamp,
                "Clamp",
                "Converter",
                [
                    In("in.value", "Value", GraphTypeRef.Float.Id),
                    In("in.min", "Min", GraphTypeRef.Float.Id),
                    In("in.max", "Max", GraphTypeRef.Float.Id),
                ],
                [Out("out.result", "Result", GraphTypeRef.Float.Id)],
                [
                    PropF(
                        "in.value",
                        "Value",
                        0.5f,
                        0f,
                        1f
                    ),
                    PropF(
                        "in.min",
                        "Min",
                        0f,
                        0f,
                        1f
                    ),
                    PropF(
                        "in.max",
                        "Max",
                        1f,
                        0f,
                        1f
                    ),
                ]
            ),

            Def(
                NormalMap,
                "Normal Map",
                "Vector",
                [In("in.color", "Color", GraphTypeRef.Color.Id)],
                [Out("out.normal", "Normal", GraphTypeRef.Float3.Id)],
                [
                    Prop(
                        "path",
                        "Image",
                        GraphTypeRef.String,
                        GraphValue.FromString("")
                    ),
                ]
            ),

            // ── Core procedural nodes ──────────────────────────────────────────
            Def(
                TexCoord,
                "Texture Coordinate",
                "Input",
                [],
                [
                    Out("out.generated", "Generated", GraphTypeRef.Float3.Id),
                    Out("out.uv", "UV", GraphTypeRef.Float3.Id),
                    Out("out.object", "Object", GraphTypeRef.Float3.Id),
                    Out("out.normal", "Normal", GraphTypeRef.Float3.Id),
                    Out("out.position", "Position", GraphTypeRef.Float3.Id),
                ]
            ),

            Def(
                Mapping,
                "Mapping",
                "Vector",
                [
                    In("in.vector", "Vector", GraphTypeRef.Float3.Id),
                    In("in.location", "Location", GraphTypeRef.Float3.Id),
                    In("in.rotation", "Rotation", GraphTypeRef.Float3.Id),
                    In("in.scale", "Scale", GraphTypeRef.Float3.Id),
                ],
                [Out("out.vector", "Vector", GraphTypeRef.Float3.Id)],
                [
                    PropEnum("type", "Type", MappingTypeLabels),
                    PropV3(
                        "in.location",
                        "Location",
                        0f,
                        0f,
                        0f
                    ),
                    PropV3(
                        "in.rotation",
                        "Rotation",
                        0f,
                        0f,
                        0f
                    ),
                    PropV3(
                        "in.scale",
                        "Scale",
                        1f,
                        1f,
                        1f
                    ),
                ]
            ),

            Def(
                Noise,
                "Noise Texture",
                "Texture",
                [
                    In("in.vector", "Vector", GraphTypeRef.Float3.Id),
                    In("in.scale", "Scale", GraphTypeRef.Float.Id),
                    In("in.detail", "Detail", GraphTypeRef.Float.Id),
                    In("in.roughness", "Roughness", GraphTypeRef.Float.Id),
                    In("in.distortion", "Distortion", GraphTypeRef.Float.Id),
                ],
                [
                    Out("out.fac", "Fac", GraphTypeRef.Float.Id),
                    Out("out.color", "Color", GraphTypeRef.Color.Id),
                ],
                [
                    PropEnum(
                        "dimensions",
                        "Dimensions",
                        NoiseDimLabels,
                        2
                    ),
                    PropF(
                        "in.scale",
                        "Scale",
                        5f,
                        0f,
                        50f
                    ),
                    PropF(
                        "in.detail",
                        "Detail",
                        2f,
                        0f,
                        16f
                    ),
                    PropF(
                        "in.roughness",
                        "Roughness",
                        0.5f,
                        0f,
                        1f
                    ),
                    PropF(
                        "in.distortion",
                        "Distortion",
                        0f,
                        0f,
                        10f
                    ),
                ]
            ),

            Def(
                Gradient,
                "Gradient Texture",
                "Texture",
                [In("in.vector", "Vector", GraphTypeRef.Float3.Id)],
                [
                    Out("out.fac", "Fac", GraphTypeRef.Float.Id),
                    Out("out.color", "Color", GraphTypeRef.Color.Id),
                ],
                [PropEnum("gradient_type", "Type", GradientTypeLabels)]
            ),

            Def(
                Checker,
                "Checker Texture",
                "Texture",
                [
                    In("in.vector", "Vector", GraphTypeRef.Float3.Id),
                    In("in.color1", "Color1", GraphTypeRef.Color.Id),
                    In("in.color2", "Color2", GraphTypeRef.Color.Id),
                    In("in.scale", "Scale", GraphTypeRef.Float.Id),
                ],
                [
                    Out("out.color", "Color", GraphTypeRef.Color.Id),
                    Out("out.fac", "Fac", GraphTypeRef.Float.Id),
                ],
                [
                    Prop(
                        "in.color1",
                        "Color1",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            0.8f,
                            0.8f,
                            0.8f,
                            1f
                        )
                    ),
                    Prop(
                        "in.color2",
                        "Color2",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            0.2f,
                            0.2f,
                            0.2f,
                            1f
                        )
                    ),
                    PropF(
                        "in.scale",
                        "Scale",
                        5f,
                        0f,
                        50f
                    ),
                ]
            ),

            Def(
                Wave,
                "Wave Texture",
                "Texture",
                [
                    In("in.vector", "Vector", GraphTypeRef.Float3.Id),
                    In("in.scale", "Scale", GraphTypeRef.Float.Id),
                    In("in.distortion", "Distortion", GraphTypeRef.Float.Id),
                    In("in.detail", "Detail", GraphTypeRef.Float.Id),
                ],
                [
                    Out("out.color", "Color", GraphTypeRef.Color.Id),
                    Out("out.fac", "Fac", GraphTypeRef.Float.Id),
                ],
                [
                    PropEnum("wave_type", "Type", WaveTypeLabels),
                    PropEnum("wave_profile", "Profile", WaveProfileLabels),
                    PropF(
                        "in.scale",
                        "Scale",
                        5f,
                        0f,
                        50f
                    ),
                    PropF(
                        "in.distortion",
                        "Distortion",
                        0f,
                        0f,
                        50f
                    ),
                    PropF(
                        "in.detail",
                        "Detail",
                        2f,
                        0f,
                        16f
                    ),
                ]
            ),

            Def(
                VecMath,
                "Vector Math",
                "Converter",
                [
                    In("in.a", "A", GraphTypeRef.Float3.Id),
                    In("in.b", "B", GraphTypeRef.Float3.Id),
                    In("in.scale", "Scale", GraphTypeRef.Float.Id),
                ],
                [
                    Out("out.vector", "Vector", GraphTypeRef.Float3.Id),
                    Out("out.value", "Value", GraphTypeRef.Float.Id),
                ],
                [
                    PropEnum("op", "Operation", VecMathOpLabels), PropF(
                        "in.scale",
                        "Scale",
                        1f,
                        -10f,
                        10f
                    ),
                ]
            ),

            Def(
                MapRange,
                "Map Range",
                "Converter",
                [
                    In("in.value", "Value", GraphTypeRef.Float.Id),
                    In("in.from_min", "From Min", GraphTypeRef.Float.Id),
                    In("in.from_max", "From Max", GraphTypeRef.Float.Id),
                    In("in.to_min", "To Min", GraphTypeRef.Float.Id),
                    In("in.to_max", "To Max", GraphTypeRef.Float.Id),
                ],
                [Out("out.result", "Result", GraphTypeRef.Float.Id)],
                [
                    Prop(
                        "clamp",
                        "Clamp",
                        GraphTypeRef.Bool,
                        GraphValue.FromBool(true)
                    ),
                    PropF(
                        "in.value",
                        "Value",
                        0f,
                        -10f,
                        10f
                    ),
                    PropF(
                        "in.from_min",
                        "From Min",
                        0f,
                        -10f,
                        10f
                    ),
                    PropF(
                        "in.from_max",
                        "From Max",
                        1f,
                        -10f,
                        10f
                    ),
                    PropF(
                        "in.to_min",
                        "To Min",
                        0f,
                        -10f,
                        10f
                    ),
                    PropF(
                        "in.to_max",
                        "To Max",
                        1f,
                        -10f,
                        10f
                    ),
                ]
            ),

            Def(
                SeparateXyz,
                "Separate XYZ",
                "Converter",
                [In("in.vector", "Vector", GraphTypeRef.Float3.Id)],
                [
                    Out("out.x", "X", GraphTypeRef.Float.Id),
                    Out("out.y", "Y", GraphTypeRef.Float.Id),
                    Out("out.z", "Z", GraphTypeRef.Float.Id),
                ]
            ),

            Def(
                CombineXyz,
                "Combine XYZ",
                "Converter",
                [
                    In("in.x", "X", GraphTypeRef.Float.Id), In("in.y", "Y", GraphTypeRef.Float.Id),
                    In("in.z", "Z", GraphTypeRef.Float.Id),
                ],
                [Out("out.vector", "Vector", GraphTypeRef.Float3.Id)],
                [
                    PropF(
                        "in.x",
                        "X",
                        0f,
                        -10f,
                        10f
                    ),
                    PropF(
                        "in.y",
                        "Y",
                        0f,
                        -10f,
                        10f
                    ),
                    PropF(
                        "in.z",
                        "Z",
                        0f,
                        -10f,
                        10f
                    ),
                ]
            ),

            Def(
                ColorRamp,
                "Color Ramp",
                "Converter",
                [In("in.fac", "Fac", GraphTypeRef.Float.Id)],
                [
                    Out("out.color", "Color", GraphTypeRef.Color.Id),
                    Out("out.alpha", "Alpha", GraphTypeRef.Float.Id),
                ],
                [
                    PropEnum("interpolation", "Interpolation", RampInterpLabels),
                    PropF(
                        "in.fac",
                        "Fac",
                        0.5f,
                        0f,
                        1f
                    ),
                    PropRamp("ramp", "Ramp"),
                ]
            ),
        ];
    }

    // ── Node-definition builders ───────────────────────────────────────────────

    internal static NodeDefinition Def(string id, string name, string category,
        IReadOnlyList<PinDefinition> inputs, IReadOnlyList<PinDefinition> outputs,
        IReadOnlyList<PropertyDefinition>? props = null)
    {
        return new NodeDefinition {
            Id = id,
            DomainId = DomainId,
            SchemaId = MaterialSchema,
            DisplayName = name,
            Category = category,
            Inputs = inputs,
            Outputs = outputs,
            Properties = props ?? [],
        };
    }

    internal static PinDefinition In(string id, string name, string typeId)
    {
        return new PinDefinition {
            Id = id,
            DisplayName = name,
            Direction = PinDirection.Input,
            Role = PinRole.Data,
            Type = new GraphTypeRef(typeId),
        };
    }

    internal static PinDefinition Out(string id, string name, string typeId)
    {
        return new PinDefinition {
            Id = id,
            DisplayName = name,
            Direction = PinDirection.Output,
            Role = PinRole.Data,
            Type = new GraphTypeRef(typeId),
        };
    }

    internal static PropertyDefinition Prop(string id, string name, GraphTypeRef type,
        GraphValue def)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = type,
            DefaultValue = def,
        };
    }

    internal static PropertyDefinition PropF(string id, string name, float def, float min,
        float max)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Float,
            DefaultValue = GraphValue.FromFloat(def),
            Min = min,
            Max = max,
        };
    }

    internal static PropertyDefinition PropEnum(string id, string name, string[] labels,
        int def = 0)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Int,
            DefaultValue = GraphValue.FromInt(def),
            EnumLabels = labels,
        };
    }

    internal static PropertyDefinition PropV3(string id, string name, float x, float y, float z)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Float3,
            DefaultValue = GraphValue.FromFloat3(x, y, z),
        };
    }

    internal static PropertyDefinition PropRamp(string id, string name)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.String,
            DefaultValue = GraphValue.FromString(ShaderRampJson.Serialize(ShaderRampJson.Default)),
            Editor = "gradient",
        };
    }
}