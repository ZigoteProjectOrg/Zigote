using Zigote.Core.Math3D;

namespace Zigote.Network;

/// <summary>
///     Decides which replicated objects a given connection should receive — the area-of-interest
///     filter that
///     keeps bandwidth bounded as the world grows. The default replicates everything; swap in a
///     spatial filter
///     for large worlds.
/// </summary>
public interface IInterestManagement
{
    bool IsRelevant(NetObject obj, NetConnection connection);
}

/// <summary>Replicate every object to every connection. The default — correct for small worlds.</summary>
public sealed class AlwaysRelevant : IInterestManagement
{
    public static readonly AlwaysRelevant Instance = new();

    public bool IsRelevant(NetObject obj, NetConnection connection)
    {
        return true;
    }
}

/// <summary>
///     Relevance by distance: an object is sent to a connection only while within
///     <see cref="Radius" /> of that
///     connection's view position. The game supplies each connection's view position (typically its
///     player's
///     location) via <see cref="SetViewPosition" />. Objects an owner owns are always relevant to that
///     owner.
/// </summary>
public sealed class DistanceInterestManagement(float radius) : IInterestManagement
{
    private readonly Dictionary<int, Vec3> _viewPositions = new();
    public float Radius { get; set; } = radius;

    public bool IsRelevant(NetObject obj, NetConnection connection)
    {
        if (obj.OwnerConnectionId == connection.Id) return true;
        if (!_viewPositions.TryGetValue(connection.Id, out var view))
            return true; // unknown view → don't hide
        var r2 = Radius * Radius;
        return (obj.RelevancePosition - view).LengthSq() <= r2;
    }

    public void SetViewPosition(NetConnection connection, Vec3 position)
    {
        _viewPositions[connection.Id] = position;
    }
}