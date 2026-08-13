namespace Zigote.Graphs.Registry;

public sealed class GraphTypeDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string DomainId { get; init; } = "core";
    public GraphTypeCategory Category { get; init; }

    /// <summary>ARGB hex color used to tint pin/wire visuals for this type.</summary>
    public uint WireColor { get; init; } = 0xFFAAAAAA;
}

public enum GraphTypeCategory
{
    Scalar,
    Vector,
    Matrix,
    String,
    Resource,
    Flow,
    Event,
    Opaque,
    Custom,
}

/// <summary>Built-in core type definitions, available to every domain.</summary>
public static class CoreTypeDefinitions
{
    public static IReadOnlyList<GraphTypeDefinition> All { get; } = [
        new() {
            Id = "core.bool",
            DisplayName = "Bool",
            Category = GraphTypeCategory.Scalar,
            WireColor = 0xFF5555FF,
        },
        new() {
            Id = "core.int",
            DisplayName = "Int",
            Category = GraphTypeCategory.Scalar,
            WireColor = 0xFF00BBAA,
        },
        new() {
            Id = "core.float",
            DisplayName = "Float",
            Category = GraphTypeCategory.Scalar,
            WireColor = 0xFFAADD55,
        },
        new() {
            Id = "core.float2",
            DisplayName = "Float2",
            Category = GraphTypeCategory.Vector,
            WireColor = 0xFF66CC88,
        },
        new() {
            Id = "core.float3",
            DisplayName = "Float3",
            Category = GraphTypeCategory.Vector,
            WireColor = 0xFF88AACC,
        },
        new() {
            Id = "core.float4",
            DisplayName = "Float4",
            Category = GraphTypeCategory.Vector,
            WireColor = 0xFFCC88AA,
        },
        new() {
            Id = "core.string",
            DisplayName = "String",
            Category = GraphTypeCategory.String,
            WireColor = 0xFFFFCC55,
        },
        new() {
            Id = "core.color",
            DisplayName = "Color",
            Category = GraphTypeCategory.Vector,
            WireColor = 0xFFFF8844,
        },
        new() {
            Id = "core.any",
            DisplayName = "Any",
            Category = GraphTypeCategory.Opaque,
            WireColor = 0xFF888888,
        },
    ];
}
