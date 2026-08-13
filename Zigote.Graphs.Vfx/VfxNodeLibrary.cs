using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;

namespace Zigote.Graphs.Vfx;

/// <summary>
///     The VFX node catalogue: stable node-type IDs + their <see cref="NodeDefinition" />s, shared by
///     <see cref="VfxDomain" /> and <see cref="VfxGraphCompiler" />. UI/native-free so it stays
///     headless-testable. The graph wires module nodes (spawn / shape / initialize / update / render)
///     into
///     a single <c>VFX Output</c> emitter, which the compiler lowers to a <c>VfxEmitterAsset</c>
///     module
///     stack — the same data the CPU simulator and the future GPU kernel consume.
/// </summary>
public static class VfxNodeLibrary
{
    public const string DomainId = "zigote.vfx";
    public const string EmitterSchema = "vfx.emitter";

    // ── Wire types (the emitter's block sockets) ─────────────────────────────
    public const string SpawnType = "vfx.spawn";
    public const string ShapeType = "vfx.shape";
    public const string InitType = "vfx.init";
    public const string UpdateType = "vfx.update";
    public const string RenderType = "vfx.render";

    // ── Node-type IDs ────────────────────────────────────────────────────────
    public const string Output = "vfx.output";
    public const string SpawnRate = "vfx.spawn_rate";
    public const string Burst = "vfx.burst";
    public const string Shape = "vfx.shape_emit";
    public const string InitVelocity = "vfx.init_velocity";
    public const string InitSize = "vfx.init_size";
    public const string InitColor = "vfx.init_color";
    public const string InitLifetime = "vfx.init_lifetime";
    public const string InitRotation = "vfx.init_rotation";
    public const string Gravity = "vfx.gravity";
    public const string Drag = "vfx.drag";
    public const string Turbulence = "vfx.turbulence";
    public const string Vortex = "vfx.vortex";
    public const string ColorOverLife = "vfx.color_over_life";
    public const string SizeOverLife = "vfx.size_over_life";
    public const string AlphaOverLife = "vfx.alpha_over_life";
    public const string Render = "vfx.render_settings";
    public const string FloatValue = "vfx.value_float";
    public const string ColorValue = "vfx.value_color";
    public const string VectorValue = "vfx.value_vector";

    // Enum labels — order MUST match the enums the compiler casts to.
    public static readonly string[] ShapeLabels =
        ["Point", "Sphere", "Hemisphere", "Box", "Cone", "Circle"];

    public static readonly string[] SpaceLabels = ["World", "Local"];
    public static readonly string[] BlendLabels = ["Additive", "Alpha Blend"];

    public static readonly string[] LifeProfileLabels =
        ["Constant", "Fade In", "Fade Out", "Fade In-Out", "Grow", "Shrink", "Grow-Shrink"];

    public static IReadOnlyList<GraphTypeDefinition> TypeDefinitions { get; } = [
        Type(id: SpawnType, name: "Spawn", wire: 0xFF6FCF97),
        Type(id: ShapeType, name: "Shape", wire: 0xFF56CCF2),
        Type(id: InitType, name: "Initialize", wire: 0xFFF2C94C),
        Type(id: UpdateType, name: "Update", wire: 0xFFEB5757),
        Type(id: RenderType, name: "Render", wire: 0xFFBB6BD9),
    ];

    public static IReadOnlyList<NodeDefinition> Definitions { get; } = BuildDefinitions();

    private static IReadOnlyList<NodeDefinition> BuildDefinitions()
    {
        return [
            Def(
                id: Output,
                name: "VFX Output",
                category: "Output",
                inputs: [
                    InPin(
                        id: "in.spawn",
                        name: "Spawn",
                        typeId: SpawnType,
                        multi: true
                    ),
                    InPin(id: "in.shape", name: "Shape", typeId: ShapeType),
                    InPin(
                        id: "in.init",
                        name: "Initialize",
                        typeId: InitType,
                        multi: true
                    ),
                    InPin(
                        id: "in.update",
                        name: "Update",
                        typeId: UpdateType,
                        multi: true
                    ),
                    InPin(id: "in.render", name: "Render", typeId: RenderType),
                ],
                outputs: [],
                props: [
                    PropI(
                        id: "capacity",
                        name: "Capacity",
                        def: 1024,
                        min: 1,
                        max: 1_000_000
                    ),
                    PropB(id: "looping", name: "Looping", def: true),
                    PropF(
                        id: "duration",
                        name: "Duration",
                        def: 0f,
                        min: 0f,
                        max: 60f
                    ),
                    PropEnum(id: "space", name: "Simulation Space", labels: SpaceLabels),
                    PropI(
                        id: "seed",
                        name: "Seed",
                        def: 12345,
                        min: 0,
                        max: int.MaxValue
                    ),
                ]
            ),

            Def(
                id: SpawnRate,
                name: "Spawn Rate",
                category: "Spawn",
                inputs: [],
                outputs: [OutPin(id: "out.spawn", name: "Spawn", typeId: SpawnType)],
                props: [
                    PropF(
                        id: "rate",
                        name: "Rate",
                        def: 24f,
                        min: 0f,
                        max: 5000f
                    ),
                ]
            ),

            Def(
                id: Burst,
                name: "Burst",
                category: "Spawn",
                inputs: [],
                outputs: [OutPin(id: "out.spawn", name: "Spawn", typeId: SpawnType)],
                props: [
                    PropF(
                        id: "time",
                        name: "Time",
                        def: 0f,
                        min: 0f,
                        max: 60f
                    ),
                    PropI(
                        id: "count",
                        name: "Count",
                        def: 30,
                        min: 0,
                        max: 100_000
                    ),
                ]
            ),

            Def(
                id: Shape,
                name: "Emission Shape",
                category: "Shape",
                inputs: [],
                outputs: [OutPin(id: "out.shape", name: "Shape", typeId: ShapeType)],
                props: [
                    PropEnum(
                        id: "shape",
                        name: "Shape",
                        labels: ShapeLabels,
                        def: 4
                    ),
                    PropF(
                        id: "radius",
                        name: "Radius",
                        def: 0.25f,
                        min: 0f,
                        max: 20f
                    ),
                    PropF(
                        id: "cone_angle",
                        name: "Cone Angle",
                        def: 25f,
                        min: 0f,
                        max: 90f
                    ),
                    PropV3(
                        id: "box",
                        name: "Box Half-Extents",
                        x: 0.5f,
                        y: 0.5f,
                        z: 0.5f
                    ),
                    PropV3(
                        id: "direction",
                        name: "Direction",
                        x: 0f,
                        y: 1f,
                        z: 0f
                    ),
                ]
            ),

            Def(
                id: InitVelocity,
                name: "Initial Velocity",
                category: "Initialize",
                inputs: [InPin(id: "in.speed", name: "Speed", typeId: GraphTypeRef.Float.Id)],
                outputs: [OutPin(id: "out.init", name: "Init", typeId: InitType)],
                props: [
                    PropF(
                        id: "speed_min",
                        name: "Speed Min",
                        def: 2f,
                        min: 0f,
                        max: 100f
                    ),
                    PropF(
                        id: "speed_max",
                        name: "Speed Max",
                        def: 4f,
                        min: 0f,
                        max: 100f
                    ),
                ]
            ),

            Def(
                id: InitSize,
                name: "Initial Size",
                category: "Initialize",
                inputs: [],
                outputs: [OutPin(id: "out.init", name: "Init", typeId: InitType)],
                props: [
                    PropF(
                        id: "size_min",
                        name: "Size Min",
                        def: 0.15f,
                        min: 0f,
                        max: 20f
                    ),
                    PropF(
                        id: "size_max",
                        name: "Size Max",
                        def: 0.3f,
                        min: 0f,
                        max: 20f
                    ),
                ]
            ),

            Def(
                id: InitColor,
                name: "Initial Color",
                category: "Initialize",
                inputs: [InPin(id: "in.color", name: "Color", typeId: GraphTypeRef.Color.Id)],
                outputs: [OutPin(id: "out.init", name: "Init", typeId: InitType)],
                props: [
                    Prop(
                        id: "color",
                        name: "Color",
                        type: GraphTypeRef.Color,
                        def: GraphValue.FromFloat4(
                            x: 1f,
                            y: 1f,
                            z: 1f,
                            w: 1f
                        )
                    ),
                    Prop(
                        id: "variation",
                        name: "Variation",
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
                id: InitLifetime,
                name: "Initial Lifetime",
                category: "Initialize",
                inputs: [],
                outputs: [OutPin(id: "out.init", name: "Init", typeId: InitType)],
                props: [
                    PropF(
                        id: "life_min",
                        name: "Lifetime Min",
                        def: 1.5f,
                        min: 0.01f,
                        max: 60f
                    ),
                    PropF(
                        id: "life_max",
                        name: "Lifetime Max",
                        def: 2.5f,
                        min: 0.01f,
                        max: 60f
                    ),
                ]
            ),

            Def(
                id: InitRotation,
                name: "Initial Rotation",
                category: "Initialize",
                inputs: [],
                outputs: [OutPin(id: "out.init", name: "Init", typeId: InitType)],
                props: [
                    PropF(
                        id: "rot_min",
                        name: "Rotation Min",
                        def: 0f,
                        min: -360f,
                        max: 360f
                    ),
                    PropF(
                        id: "rot_max",
                        name: "Rotation Max",
                        def: 0f,
                        min: -360f,
                        max: 360f
                    ),
                    PropF(
                        id: "spin_min",
                        name: "Spin Min",
                        def: 0f,
                        min: -720f,
                        max: 720f
                    ),
                    PropF(
                        id: "spin_max",
                        name: "Spin Max",
                        def: 0f,
                        min: -720f,
                        max: 720f
                    ),
                ]
            ),

            Def(
                id: Gravity,
                name: "Gravity",
                category: "Update · Force",
                inputs: [InPin(id: "in.gravity", name: "Gravity", typeId: GraphTypeRef.Float3.Id)],
                outputs: [OutPin(id: "out.update", name: "Update", typeId: UpdateType)],
                props: [
                    PropV3(
                        id: "gravity",
                        name: "Gravity",
                        x: 0f,
                        y: -9.8f,
                        z: 0f
                    ),
                ]
            ),

            Def(
                id: Drag,
                name: "Drag",
                category: "Update · Force",
                inputs: [],
                outputs: [OutPin(id: "out.update", name: "Update", typeId: UpdateType)],
                props: [
                    PropF(
                        id: "drag",
                        name: "Drag",
                        def: 0.5f,
                        min: 0f,
                        max: 20f
                    ),
                ]
            ),

            Def(
                id: Turbulence,
                name: "Turbulence",
                category: "Update · Force",
                inputs: [],
                outputs: [OutPin(id: "out.update", name: "Update", typeId: UpdateType)],
                props: [
                    PropF(
                        id: "strength",
                        name: "Strength",
                        def: 1f,
                        min: 0f,
                        max: 50f
                    ),
                    PropF(
                        id: "frequency",
                        name: "Frequency",
                        def: 1f,
                        min: 0f,
                        max: 10f
                    ),
                ]
            ),

            Def(
                id: Vortex,
                name: "Vortex",
                category: "Update · Force",
                inputs: [],
                outputs: [OutPin(id: "out.update", name: "Update", typeId: UpdateType)],
                props: [
                    PropV3(
                        id: "axis",
                        name: "Axis",
                        x: 0f,
                        y: 1f,
                        z: 0f
                    ),
                    PropF(
                        id: "strength",
                        name: "Strength",
                        def: 2f,
                        min: -50f,
                        max: 50f
                    ),
                ]
            ),

            Def(
                id: ColorOverLife,
                name: "Color over Life",
                category: "Update",
                inputs: [],
                outputs: [OutPin(id: "out.update", name: "Update", typeId: UpdateType)],
                props: [PropRamp(id: "ramp", name: "Ramp")]
            ),

            Def(
                id: SizeOverLife,
                name: "Size over Life",
                category: "Update",
                inputs: [],
                outputs: [OutPin(id: "out.update", name: "Update", typeId: UpdateType)],
                props: [
                    PropEnum(
                        id: "profile",
                        name: "Profile",
                        labels: LifeProfileLabels,
                        def: 5
                    ),
                    PropF(
                        id: "scale",
                        name: "Scale",
                        def: 1f,
                        min: 0f,
                        max: 10f
                    ),
                ]
            ),

            Def(
                id: AlphaOverLife,
                name: "Alpha over Life",
                category: "Update",
                inputs: [],
                outputs: [OutPin(id: "out.update", name: "Update", typeId: UpdateType)],
                props: [
                    PropEnum(
                        id: "profile",
                        name: "Profile",
                        labels: LifeProfileLabels,
                        def: 2
                    ),
                    PropF(
                        id: "scale",
                        name: "Scale",
                        def: 1f,
                        min: 0f,
                        max: 1f
                    ),
                ]
            ),

            Def(
                id: Render,
                name: "Render Settings",
                category: "Render",
                inputs: [],
                outputs: [OutPin(id: "out.render", name: "Render", typeId: RenderType)],
                props: [
                    PropEnum(id: "blend", name: "Blend", labels: BlendLabels),
                    Prop(
                        id: "texture",
                        name: "Texture",
                        type: GraphTypeRef.String,
                        def: GraphValue.FromString("")
                    ),
                    PropB(id: "soft", name: "Soft Particles", def: true),
                ]
            ),

            Def(
                id: FloatValue,
                name: "Float",
                category: "Input",
                inputs: [],
                outputs: [OutPin(id: "out.value", name: "Value", typeId: GraphTypeRef.Float.Id)],
                props: [
                    PropF(
                        id: "value",
                        name: "Value",
                        def: 1f,
                        min: -100f,
                        max: 100f
                    ),
                ]
            ),

            Def(
                id: ColorValue,
                name: "Color",
                category: "Input",
                inputs: [],
                outputs: [OutPin(id: "out.color", name: "Color", typeId: GraphTypeRef.Color.Id)],
                props: [
                    Prop(
                        id: "color",
                        name: "Color",
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
                id: VectorValue,
                name: "Vector",
                category: "Input",
                inputs: [],
                outputs: [OutPin(id: "out.vector", name: "Vector", typeId: GraphTypeRef.Float3.Id)],
                props: [
                    PropV3(
                        id: "vector",
                        name: "Vector",
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                ]
            ),
        ];
    }

    /// <summary>The input-pin type for a node definition + pin id, or null. Used for edge type-checking.</summary>
    public static GraphTypeRef? PinType(string definitionId, string pinId, PinDirection direction)
    {
        foreach (var def in Definitions)
        {
            if (def.Id != definitionId) continue;
            var pins = direction == PinDirection.Input ? def.Inputs : def.Outputs;
            foreach (var p in pins)
            {
                if (p.Id == pinId)
                    return p.Type;
            }
        }

        return null;
    }

    public static bool AllowsMultiple(string definitionId, string pinId)
    {
        foreach (var def in Definitions)
        {
            if (def.Id != definitionId) continue;
            foreach (var p in def.Inputs)
            {
                if (p.Id == pinId)
                    return p.AllowsMultipleConnections;
            }
        }

        return false;
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static GraphTypeDefinition Type(string id, string name, uint wire)
    {
        return new GraphTypeDefinition {
            Id = id,
            DisplayName = name,
            DomainId = DomainId,
            Category = GraphTypeCategory.Custom,
            WireColor = wire,
        };
    }

    private static NodeDefinition Def(string id, string name, string category,
        IReadOnlyList<PinDefinition> inputs, IReadOnlyList<PinDefinition> outputs,
        IReadOnlyList<PropertyDefinition>? props = null)
    {
        return new NodeDefinition {
            Id = id,
            DomainId = DomainId,
            SchemaId = EmitterSchema,
            DisplayName = name,
            Category = category,
            Inputs = inputs,
            Outputs = outputs,
            Properties = props ?? [],
        };
    }

    private static PinDefinition InPin(string id, string name, string typeId, bool multi = false)
    {
        return new PinDefinition {
            Id = id,
            DisplayName = name,
            Direction = PinDirection.Input,
            Role = PinRole.Data,
            Type = new GraphTypeRef(typeId),
            AllowsMultipleConnections = multi,
        };
    }

    private static PinDefinition OutPin(string id, string name, string typeId)
    {
        return new PinDefinition {
            Id = id,
            DisplayName = name,
            Direction = PinDirection.Output,
            Role = PinRole.Data,
            Type = new GraphTypeRef(typeId),
        };
    }

    private static PropertyDefinition Prop(string id, string name, GraphTypeRef type,
        GraphValue def)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = type,
            DefaultValue = def,
        };
    }

    private static PropertyDefinition PropF(string id, string name, float def, float min, float max)
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

    private static PropertyDefinition PropI(string id, string name, int def, int min, int max)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Int,
            DefaultValue = GraphValue.FromInt(def),
            Min = min,
            Max = max,
        };
    }

    private static PropertyDefinition PropB(string id, string name, bool def)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Bool,
            DefaultValue = GraphValue.FromBool(def),
        };
    }

    private static PropertyDefinition PropEnum(string id, string name, string[] labels, int def = 0)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Int,
            DefaultValue = GraphValue.FromInt(def),
            EnumLabels = labels,
        };
    }

    private static PropertyDefinition PropV3(string id, string name, float x, float y, float z)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Float3,
            DefaultValue = GraphValue.FromFloat3(x: x, y: y, z: z),
        };
    }

    private static PropertyDefinition PropRamp(string id, string name)
    {
        return new PropertyDefinition {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.String,
            DefaultValue = GraphValue.FromString(VfxRampJson.Default),
            Editor = "gradient",
        };
    }
}
