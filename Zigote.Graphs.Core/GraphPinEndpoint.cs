namespace Zigote.Graphs.Core;

/// <summary>One end of an edge — identifies a specific pin on a specific node.</summary>
public readonly record struct GraphPinEndpoint(Guid NodeId, string PinId)
{
    public override string ToString() => $"{NodeId:N}#{PinId}";
}
