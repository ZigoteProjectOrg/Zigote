using Zigote.Graphs.Core;

namespace Zigote.Graphs.Registry;

public sealed class PinDefinition
{
    /// <summary>Stable pin ID, e.g. "input.a", "output.result", "flow.in".</summary>
    public string Id { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public PinDirection Direction { get; init; }
    public PinRole Role { get; init; }
    public GraphTypeRef Type { get; init; }

    public bool IsRequired { get; init; }
    public bool AllowsMultipleConnections { get; init; }

    public GraphValue? DefaultValue { get; init; }
}

/// <summary>
///     High-level semantic role. Kept intentionally small and generic.
///     Domain-specific semantics live in <see cref="GraphTypeRef" />, not here.
/// </summary>
public enum PinRole
{
    Data,
    Control,
    Event,
    Resource,
    Custom,
}
