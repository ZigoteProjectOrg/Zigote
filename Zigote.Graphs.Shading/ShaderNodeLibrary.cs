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
                id: Output,
                name: "Material Output",
                category: "Output",
                inputs: [
                    In(id: "in.surface", name: "Surface", typeId: BsdfType),
                    In(id: "in.volume", name: "Volume", typeId: BsdfType),
                    In(id: "in.displacement", name: "Displacement", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: []
            ),

            Def(
                id: Principled,
                name: "Principled BSDF",
                category: "Shader",
                inputs: [
                    In(id: "in.base_color", name: "Base Color", typeId: GraphTypeRef.Color.Id),
                    In(id: "in.metallic", name: "Metallic", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.roughness", name: "Roughness", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.specular", name: "Specular", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.emission", name: "Emission Color", typeId: GraphTypeRef.Color.Id),
                    In(
                        id: "in.emission_strength",
                        name: "Emission Strength",
                        typeId: GraphTypeRef.Float.Id
                    ),
                    In(id: "in.clearcoat", name: "Coat Weight", typeId: GraphTypeRef.Float.Id),
                    In(
                        id: "in.clearcoat_roughness",
                        name: "Coat Roughness",
                        typeId: GraphTypeRef.Float.Id
                    ),
                    In(id: "in.normal", name: "Normal", typeId: GraphTypeRef.Float3.Id),
                ],
                outputs: [Out(id: "out.bsdf", name: "BSDF", typeId: BsdfType)],
                props: [
                    Prop(
                        id: "in.base_color",
                        name: "Base Color",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 0.8f,
                            y: 0.8f,
                            z: 0.8f,
                            w: 1f
                        )
                    ),
                    PropF(
                        id: "in.metallic",
                        name: "Metallic",
                        def: 0f,
                        min: 0f,
                        max: 1f
                    ),
                    PropF(
                        id: "in.roughness",
                        name: "Roughness",
                        def: 0.5f,
                        min: 0f,
                        max: 1f
                    ),
                    PropF(
                        id: "in.specular",
                        name: "Specular",
                        def: 1f,
                        min: 0f,
                        max: 2f
                    ),
                    Prop(
                        id: "in.emission",
                        name: "Emission Color",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 0f,
                            y: 0f,
                            z: 0f,
                            w: 1f
                        )
                    ),
                    PropF(
                        id: "in.emission_strength",
                        name: "Emission Strength",
                        def: 0f,
                        min: 0f,
                        max: 50f
                    ),
                    PropF(
                        id: "in.clearcoat",
                        name: "Coat Weight",
                        def: 0f,
                        min: 0f,
                        max: 1f
                    ),
                    PropF(
                        id: "in.clearcoat_roughness",
                        name: "Coat Roughness",
                        def: 0.03f,
                        min: 0f,
                        max: 1f
                    ),
                ]
            ),

            Def(
                id: TexImage,
                name: "Image Texture",
                category: "Texture",
                inputs: [],
                outputs: [
                    Out(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id),
                    Out(id: "out.alpha", name: "Alpha", typeId: GraphTypeRef.Float.Id),
                ],
                props: [
                    Prop(
                        id: "path",
                        name: "Image",
                        type: GraphTypeRef.String,
                        def: GraphValue.FromString("")
                    ),
                ]
            ),

            Def(
                id: Rgb,
                name: "RGB",
                category: "Input",
                inputs: [],
                outputs: [Out(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id)],
                props: [
                    Prop(
                        id: "color",
                        name: "Color",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 0.8f,
                            y: 0.8f,
                            z: 0.8f,
                            w: 1f
                        )
                    ),
                ]
            ),

            Def(
                id: Value,
                name: "Value",
                category: "Input",
                inputs: [],
                outputs: [Out(id: "out.value", name: "Value", typeId: GraphTypeRef.Float.Id)],
                props: [
                    PropF(
                        id: "value",
                        name: "Value",
                        def: 0.5f,
                        min: 0f,
                        max: 1f
                    ),
                ]
            ),

            Def(
                id: Math,
                name: "Math",
                category: "Converter",
                inputs: [
                    In(id: "in.a", name: "A", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.b", name: "B", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [Out(id: "out.result", name: "Result", typeId: GraphTypeRef.Float.Id)],
                props: [
                    PropF(
                        id: "in.a",
                        name: "A",
                        def: 0f,
                        min: 0f,
                        max: 1f
                    ),
                    PropF(
                        id: "in.b",
                        name: "B",
                        def: 0f,
                        min: 0f,
                        max: 1f
                    ),
                    PropEnum(id: "op", name: "Operation", labels: MathOpLabels),
                ]
            ),

            Def(
                id: MixColor,
                name: "Mix Color",
                category: "Color",
                inputs: [
                    In(id: "in.factor", name: "Factor", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.a", name: "A", typeId: GraphTypeRef.Color.Id),
                    In(id: "in.b", name: "B", typeId: GraphTypeRef.Color.Id),
                ],
                outputs: [Out(id: "out.result", name: "Result", typeId: GraphTypeRef.Color.Id)],
                props: [
                    PropF(
                        id: "in.factor",
                        name: "Factor",
                        def: 0.5f,
                        min: 0f,
                        max: 1f
                    ),
                    Prop(
                        id: "in.a",
                        name: "A",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 0f,
                            y: 0f,
                            z: 0f,
                            w: 1f
                        )
                    ),
                    Prop(
                        id: "in.b",
                        name: "B",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 1f,
                            y: 1f,
                            z: 1f,
                            w: 1f
                        )
                    ),
                ]
            ),

            Def(
                id: MulColor,
                name: "Multiply Color",
                category: "Color",
                inputs: [
                    In(id: "in.a", name: "A", typeId: GraphTypeRef.Color.Id),
                    In(id: "in.b", name: "B", typeId: GraphTypeRef.Color.Id),
                ],
                outputs: [Out(id: "out.result", name: "Result", typeId: GraphTypeRef.Color.Id)],
                props: [
                    Prop(
                        id: "in.a",
                        name: "A",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 1f,
                            y: 1f,
                            z: 1f,
                            w: 1f
                        )
                    ),
                    Prop(
                        id: "in.b",
                        name: "B",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 1f,
                            y: 1f,
                            z: 1f,
                            w: 1f
                        )
                    ),
                ]
            ),

            Def(
                id: Clamp,
                name: "Clamp",
                category: "Converter",
                inputs: [
                    In(id: "in.value", name: "Value", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.min", name: "Min", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.max", name: "Max", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [Out(id: "out.result", name: "Result", typeId: GraphTypeRef.Float.Id)],
                props: [
                    PropF(
                        id: "in.value",
                        name: "Value",
                        def: 0.5f,
                        min: 0f,
                        max: 1f
                    ),
                    PropF(
                        id: "in.min",
                        name: "Min",
                        def: 0f,
                        min: 0f,
                        max: 1f
                    ),
                    PropF(
                        id: "in.max",
                        name: "Max",
                        def: 1f,
                        min: 0f,
                        max: 1f
                    ),
                ]
            ),

            Def(
                id: NormalMap,
                name: "Normal Map",
                category: "Vector",
                inputs: [In(id: "in.color", name: "Color", typeId: GraphTypeRef.Color.Id)],
                outputs: [Out(id: "out.normal", name: "Normal", typeId: GraphTypeRef.Float3.Id)],
                props: [
                    Prop(
                        id: "path",
                        name: "Image",
                        type: GraphTypeRef.String,
                        def: GraphValue.FromString("")
                    ),
                ]
            ),

            // ── Core procedural nodes ──────────────────────────────────────────
            Def(
                id: TexCoord,
                name: "Texture Coordinate",
                category: "Input",
                inputs: [],
                outputs: [
                    Out(id: "out.generated", name: "Generated", typeId: GraphTypeRef.Float3.Id),
                    Out(id: "out.uv", name: "UV", typeId: GraphTypeRef.Float3.Id),
                    Out(id: "out.object", name: "Object", typeId: GraphTypeRef.Float3.Id),
                    Out(id: "out.normal", name: "Normal", typeId: GraphTypeRef.Float3.Id),
                    Out(id: "out.position", name: "Position", typeId: GraphTypeRef.Float3.Id),
                ]
            ),

            Def(
                id: Mapping,
                name: "Mapping",
                category: "Vector",
                inputs: [
                    In(id: "in.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.location", name: "Location", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.rotation", name: "Rotation", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.scale", name: "Scale", typeId: GraphTypeRef.Float3.Id),
                ],
                outputs: [Out(id: "out.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id)],
                props: [
                    PropEnum(id: "type", name: "Type", labels: MappingTypeLabels),
                    PropV3(
                        id: "in.location",
                        name: "Location",
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                    PropV3(
                        id: "in.rotation",
                        name: "Rotation",
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                    PropV3(
                        id: "in.scale",
                        name: "Scale",
                        x: 1f,
                        y: 1f,
                        z: 1f
                    ),
                ]
            ),

            Def(
                id: Noise,
                name: "Noise Texture",
                category: "Texture",
                inputs: [
                    In(id: "in.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.scale", name: "Scale", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.detail", name: "Detail", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.roughness", name: "Roughness", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.distortion", name: "Distortion", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [
                    Out(id: "out.fac", name: "Fac", typeId: GraphTypeRef.Float.Id),
                    Out(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id),
                ],
                props: [
                    PropEnum(
                        id: "dimensions",
                        name: "Dimensions",
                        labels: NoiseDimLabels,
                        def: 2
                    ),
                    PropF(
                        id: "in.scale",
                        name: "Scale",
                        def: 5f,
                        min: 0f,
                        max: 50f
                    ),
                    PropF(
                        id: "in.detail",
                        name: "Detail",
                        def: 2f,
                        min: 0f,
                        max: 16f
                    ),
                    PropF(
                        id: "in.roughness",
                        name: "Roughness",
                        def: 0.5f,
                        min: 0f,
                        max: 1f
                    ),
                    PropF(
                        id: "in.distortion",
                        name: "Distortion",
                        def: 0f,
                        min: 0f,
                        max: 10f
                    ),
                ]
            ),

            Def(
                id: Gradient,
                name: "Gradient Texture",
                category: "Texture",
                inputs: [In(id: "in.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id)],
                outputs: [
                    Out(id: "out.fac", name: "Fac", typeId: GraphTypeRef.Float.Id),
                    Out(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id),
                ],
                props: [PropEnum(id: "gradient_type", name: "Type", labels: GradientTypeLabels)]
            ),

            Def(
                id: Checker,
                name: "Checker Texture",
                category: "Texture",
                inputs: [
                    In(id: "in.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.color1", name: "Color1", typeId: GraphTypeRef.Color.Id),
                    In(id: "in.color2", name: "Color2", typeId: GraphTypeRef.Color.Id),
                    In(id: "in.scale", name: "Scale", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [
                    Out(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id),
                    Out(id: "out.fac", name: "Fac", typeId: GraphTypeRef.Float.Id),
                ],
                props: [
                    Prop(
                        id: "in.color1",
                        name: "Color1",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 0.8f,
                            y: 0.8f,
                            z: 0.8f,
                            w: 1f
                        )
                    ),
                    Prop(
                        id: "in.color2",
                        name: "Color2",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 0.2f,
                            y: 0.2f,
                            z: 0.2f,
                            w: 1f
                        )
                    ),
                    PropF(
                        id: "in.scale",
                        name: "Scale",
                        def: 5f,
                        min: 0f,
                        max: 50f
                    ),
                ]
            ),

            Def(
                id: Wave,
                name: "Wave Texture",
                category: "Texture",
                inputs: [
                    In(id: "in.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.scale", name: "Scale", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.distortion", name: "Distortion", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.detail", name: "Detail", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [
                    Out(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id),
                    Out(id: "out.fac", name: "Fac", typeId: GraphTypeRef.Float.Id),
                ],
                props: [
                    PropEnum(id: "wave_type", name: "Type", labels: WaveTypeLabels),
                    PropEnum(id: "wave_profile", name: "Profile", labels: WaveProfileLabels),
                    PropF(
                        id: "in.scale",
                        name: "Scale",
                        def: 5f,
                        min: 0f,
                        max: 50f
                    ),
                    PropF(
                        id: "in.distortion",
                        name: "Distortion",
                        def: 0f,
                        min: 0f,
                        max: 50f
                    ),
                    PropF(
                        id: "in.detail",
                        name: "Detail",
                        def: 2f,
                        min: 0f,
                        max: 16f
                    ),
                ]
            ),

            Def(
                id: VecMath,
                name: "Vector Math",
                category: "Converter",
                inputs: [
                    In(id: "in.a", name: "A", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.b", name: "B", typeId: GraphTypeRef.Float3.Id),
                    In(id: "in.scale", name: "Scale", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [
                    Out(id: "out.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id),
                    Out(id: "out.value", name: "Value", typeId: GraphTypeRef.Float.Id),
                ],
                props: [
                    PropEnum(id: "op", name: "Operation", labels: VecMathOpLabels), PropF(
                        id: "in.scale",
                        name: "Scale",
                        def: 1f,
                        min: -10f,
                        max: 10f
                    ),
                ]
            ),

            Def(
                id: MapRange,
                name: "Map Range",
                category: "Converter",
                inputs: [
                    In(id: "in.value", name: "Value", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.from_min", name: "From Min", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.from_max", name: "From Max", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.to_min", name: "To Min", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.to_max", name: "To Max", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [Out(id: "out.result", name: "Result", typeId: GraphTypeRef.Float.Id)],
                props: [
                    Prop(
                        id: "clamp",
                        name: "Clamp",
                        type: GraphTypeRef.Bool,
                        def: GraphValue.FromBool(true)
                    ),
                    PropF(
                        id: "in.value",
                        name: "Value",
                        def: 0f,
                        min: -10f,
                        max: 10f
                    ),
                    PropF(
                        id: "in.from_min",
                        name: "From Min",
                        def: 0f,
                        min: -10f,
                        max: 10f
                    ),
                    PropF(
                        id: "in.from_max",
                        name: "From Max",
                        def: 1f,
                        min: -10f,
                        max: 10f
                    ),
                    PropF(
                        id: "in.to_min",
                        name: "To Min",
                        def: 0f,
                        min: -10f,
                        max: 10f
                    ),
                    PropF(
                        id: "in.to_max",
                        name: "To Max",
                        def: 1f,
                        min: -10f,
                        max: 10f
                    ),
                ]
            ),

            Def(
                id: SeparateXyz,
                name: "Separate XYZ",
                category: "Converter",
                inputs: [In(id: "in.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id)],
                outputs: [
                    Out(id: "out.x", name: "X", typeId: GraphTypeRef.Float.Id),
                    Out(id: "out.y", name: "Y", typeId: GraphTypeRef.Float.Id),
                    Out(id: "out.z", name: "Z", typeId: GraphTypeRef.Float.Id),
                ]
            ),

            Def(
                id: CombineXyz,
                name: "Combine XYZ",
                category: "Converter",
                inputs: [
                    In(id: "in.x", name: "X", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.y", name: "Y", typeId: GraphTypeRef.Float.Id),
                    In(id: "in.z", name: "Z", typeId: GraphTypeRef.Float.Id),
                ],
                outputs: [Out(id: "out.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id)],
                props: [
                    PropF(
                        id: "in.x",
                        name: "X",
                        def: 0f,
                        min: -10f,
                        max: 10f
                    ),
                    PropF(
                        id: "in.y",
                        name: "Y",
                        def: 0f,
                        min: -10f,
                        max: 10f
                    ),
                    PropF(
                        id: "in.z",
                        name: "Z",
                        def: 0f,
                        min: -10f,
                        max: 10f
                    ),
                ]
            ),

            Def(
                id: ColorRamp,
                name: "Color Ramp",
                category: "Converter",
                inputs: [In(id: "in.fac", name: "Fac", typeId: GraphTypeRef.Float.Id)],
                outputs: [
                    Out(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id),
                    Out(id: "out.alpha", name: "Alpha", typeId: GraphTypeRef.Float.Id),
                ],
                props: [
                    PropEnum(id: "interpolation", name: "Interpolation", labels: RampInterpLabels),
                    PropF(
                        id: "in.fac",
                        name: "Fac",
                        def: 0.5f,
                        min: 0f,
                        max: 1f
                    ),
                    PropRamp(id: "ramp", name: "Ramp"),
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
            DefaultValue = GraphValue.FromFloat3(x: x, y: y, z: z),
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
