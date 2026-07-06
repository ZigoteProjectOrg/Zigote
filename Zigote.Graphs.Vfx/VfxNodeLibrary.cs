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
        Type(SpawnType, "Spawn", 0xFF6FCF97),
        Type(ShapeType, "Shape", 0xFF56CCF2),
        Type(InitType, "Initialize", 0xFFF2C94C),
        Type(UpdateType, "Update", 0xFFEB5757),
        Type(RenderType, "Render", 0xFFBB6BD9),
    ];

    public static IReadOnlyList<NodeDefinition> Definitions { get; } = BuildDefinitions();

    private static IReadOnlyList<NodeDefinition> BuildDefinitions()
    {
        return [
            Def(
                Output,
                "VFX Output",
                "Output",
                [
                    InPin(
                        "in.spawn",
                        "Spawn",
                        SpawnType,
                        true
                    ),
                    InPin("in.shape", "Shape", ShapeType),
                    InPin(
                        "in.init",
                        "Initialize",
                        InitType,
                        true
                    ),
                    InPin(
                        "in.update",
                        "Update",
                        UpdateType,
                        true
                    ),
                    InPin("in.render", "Render", RenderType),
                ],
                [],
                [
                    PropI(
                        "capacity",
                        "Capacity",
                        1024,
                        1,
                        1_000_000
                    ),
                    PropB("looping", "Looping", true),
                    PropF(
                        "duration",
                        "Duration",
                        0f,
                        0f,
                        60f
                    ),
                    PropEnum("space", "Simulation Space", SpaceLabels),
                    PropI(
                        "seed",
                        "Seed",
                        12345,
                        0,
                        int.MaxValue
                    ),
                ]
            ),

            Def(
                SpawnRate,
                "Spawn Rate",
                "Spawn",
                [],
                [OutPin("out.spawn", "Spawn", SpawnType)],
                [
                    PropF(
                        "rate",
                        "Rate",
                        24f,
                        0f,
                        5000f
                    ),
                ]
            ),

            Def(
                Burst,
                "Burst",
                "Spawn",
                [],
                [OutPin("out.spawn", "Spawn", SpawnType)],
                [
                    PropF(
                        "time",
                        "Time",
                        0f,
                        0f,
                        60f
                    ),
                    PropI(
                        "count",
                        "Count",
                        30,
                        0,
                        100_000
                    ),
                ]
            ),

            Def(
                Shape,
                "Emission Shape",
                "Shape",
                [],
                [OutPin("out.shape", "Shape", ShapeType)],
                [
                    PropEnum(
                        "shape",
                        "Shape",
                        ShapeLabels,
                        4
                    ),
                    PropF(
                        "radius",
                        "Radius",
                        0.25f,
                        0f,
                        20f
                    ),
                    PropF(
                        "cone_angle",
                        "Cone Angle",
                        25f,
                        0f,
                        90f
                    ),
                    PropV3(
                        "box",
                        "Box Half-Extents",
                        0.5f,
                        0.5f,
                        0.5f
                    ),
                    PropV3(
                        "direction",
                        "Direction",
                        0f,
                        1f,
                        0f
                    ),
                ]
            ),

            Def(
                InitVelocity,
                "Initial Velocity",
                "Initialize",
                [InPin("in.speed", "Speed", GraphTypeRef.Float.Id)],
                [OutPin("out.init", "Init", InitType)],
                [
                    PropF(
                        "speed_min",
                        "Speed Min",
                        2f,
                        0f,
                        100f
                    ),
                    PropF(
                        "speed_max",
                        "Speed Max",
                        4f,
                        0f,
                        100f
                    ),
                ]
            ),

            Def(
                InitSize,
                "Initial Size",
                "Initialize",
                [],
                [OutPin("out.init", "Init", InitType)],
                [
                    PropF(
                        "size_min",
                        "Size Min",
                        0.15f,
                        0f,
                        20f
                    ),
                    PropF(
                        "size_max",
                        "Size Max",
                        0.3f,
                        0f,
                        20f
                    ),
                ]
            ),

            Def(
                InitColor,
                "Initial Color",
                "Initialize",
                [InPin("in.color", "Color", GraphTypeRef.Color.Id)],
                [OutPin("out.init", "Init", InitType)],
                [
                    Prop(
                        "color",
                        "Color",
                        GraphTypeRef.Color,
                        GraphValue.FromFloat4(
                            1f,
                            1f,
                            1f,
                            1f
                        )
                    ),
                    Prop(
                        "variation",
                        "Variation",
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
                InitLifetime,
                "Initial Lifetime",
                "Initialize",
                [],
                [OutPin("out.init", "Init", InitType)],
                [
                    PropF(
                        "life_min",
                        "Lifetime Min",
                        1.5f,
                        0.01f,
                        60f
                    ),
                    PropF(
                        "life_max",
                        "Lifetime Max",
                        2.5f,
                        0.01f,
                        60f
                    ),
                ]
            ),

            Def(
                InitRotation,
                "Initial Rotation",
                "Initialize",
                [],
                [OutPin("out.init", "Init", InitType)],
                [
                    PropF(
                        "rot_min",
                        "Rotation Min",
                        0f,
                        -360f,
                        360f
                    ),
                    PropF(
                        "rot_max",
                        "Rotation Max",
                        0f,
                        -360f,
                        360f
                    ),
                    PropF(
                        "spin_min",
                        "Spin Min",
                        0f,
                        -720f,
                        720f
                    ),
                    PropF(
                        "spin_max",
                        "Spin Max",
                        0f,
                        -720f,
                        720f
                    ),
                ]
            ),

            Def(
                Gravity,
                "Gravity",
                "Update · Force",
                [InPin("in.gravity", "Gravity", GraphTypeRef.Float3.Id)],
                [OutPin("out.update", "Update", UpdateType)],
                [
                    PropV3(
                        "gravity",
                        "Gravity",
                        0f,
                        -9.8f,
                        0f
                    ),
                ]
            ),

            Def(
                Drag,
                "Drag",
                "Update · Force",
                [],
                [OutPin("out.update", "Update", UpdateType)],
                [
                    PropF(
                        "drag",
                        "Drag",
                        0.5f,
                        0f,
                        20f
                    ),
                ]
            ),

            Def(
                Turbulence,
                "Turbulence",
                "Update · Force",
                [],
                [OutPin("out.update", "Update", UpdateType)],
                [
                    PropF(
                        "strength",
                        "Strength",
                        1f,
                        0f,
                        50f
                    ),
                    PropF(
                        "frequency",
                        "Frequency",
                        1f,
                        0f,
                        10f
                    ),
                ]
            ),

            Def(
                Vortex,
                "Vortex",
                "Update · Force",
                [],
                [OutPin("out.update", "Update", UpdateType)],
                [
                    PropV3(
                        "axis",
                        "Axis",
                        0f,
                        1f,
                        0f
                    ),
                    PropF(
                        "strength",
                        "Strength",
                        2f,
                        -50f,
                        50f
                    ),
                ]
            ),

            Def(
                ColorOverLife,
                "Color over Life",
                "Update",
                [],
                [OutPin("out.update", "Update", UpdateType)],
                [PropRamp("ramp", "Ramp")]
            ),

            Def(
                SizeOverLife,
                "Size over Life",
                "Update",
                [],
                [OutPin("out.update", "Update", UpdateType)],
                [
                    PropEnum(
                        "profile",
                        "Profile",
                        LifeProfileLabels,
                        5
                    ),
                    PropF(
                        "scale",
                        "Scale",
                        1f,
                        0f,
                        10f
                    ),
                ]
            ),

            Def(
                AlphaOverLife,
                "Alpha over Life",
                "Update",
                [],
                [OutPin("out.update", "Update", UpdateType)],
                [
                    PropEnum(
                        "profile",
                        "Profile",
                        LifeProfileLabels,
                        2
                    ),
                    PropF(
                        "scale",
                        "Scale",
                        1f,
                        0f,
                        1f
                    ),
                ]
            ),

            Def(
                Render,
                "Render Settings",
                "Render",
                [],
                [OutPin("out.render", "Render", RenderType)],
                [
                    PropEnum("blend", "Blend", BlendLabels),
                    Prop(
                        "texture",
                        "Texture",
                        GraphTypeRef.String,
                        GraphValue.FromString("")
                    ),
                    PropB("soft", "Soft Particles", true),
                ]
            ),

            Def(
                FloatValue,
                "Float",
                "Input",
                [],
                [OutPin("out.value", "Value", GraphTypeRef.Float.Id)],
                [
                    PropF(
                        "value",
                        "Value",
                        1f,
                        -100f,
                        100f
                    ),
                ]
            ),

            Def(
                ColorValue,
                "Color",
                "Input",
                [],
                [OutPin("out.color", "Color", GraphTypeRef.Color.Id)],
                [
                    Prop(
                        "color",
                        "Color",
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
                VectorValue,
                "Vector",
                "Input",
                [],
                [OutPin("out.vector", "Vector", GraphTypeRef.Float3.Id)],
                [
                    PropV3(
                        "vector",
                        "Vector",
                        0f,
                        0f,
                        0f
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
                if (p.Id == pinId)
                    return p.Type;
        }

        return null;
    }

    public static bool AllowsMultiple(string definitionId, string pinId)
    {
        foreach (var def in Definitions)
        {
            if (def.Id != definitionId) continue;
            foreach (var p in def.Inputs)
                if (p.Id == pinId)
                    return p.AllowsMultipleConnections;
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
            DefaultValue = GraphValue.FromFloat3(x, y, z),
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