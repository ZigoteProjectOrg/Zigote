namespace Zigote.Graphs.Core;

/// <summary>
///     A named, typed value exposed by a graph (e.g., shader uniforms, animation blend parameters).
/// </summary>
public sealed class GraphParameter
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";
    public GraphTypeRef Type { get; set; }
    public GraphValue DefaultValue { get; set; } = GraphValue.Null();
    public GraphParameterFlags Flags { get; set; }
}

[Flags]
public enum GraphParameterFlags
{
    None = 0,
    ExposedToInspector = 1 << 0,
    RuntimeMutable = 1 << 1,
    EditorOnly = 1 << 2,
    Serializable = 1 << 3,
}