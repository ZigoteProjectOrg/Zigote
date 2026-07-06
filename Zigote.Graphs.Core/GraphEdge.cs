namespace Zigote.Graphs.Core;

/// <summary>A directed connection from one node pin to another.</summary>
public sealed class GraphEdge
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Output pin the edge originates from.</summary>
    public GraphPinEndpoint From { get; init; }

    /// <summary>Input pin the edge terminates at.</summary>
    public GraphPinEndpoint To { get; init; }
}