namespace Zigote.Network;

/// <summary>
///     Static convenience accessor over the active <see cref="NetworkManager" />, mirroring the
///     engine's other script-facing providers (Input/Physics/Hud). EXPERIMENTAL: no engine host
///     (game runtime / editor play session) wires networking up yet — a host must assign
///     <see cref="Manager" /> itself when networking goes live and clear it on stop; until then
///     every query is a safe no-op. Lets game <c>Component</c> scripts reach the network without
///     threading a reference everywhere.
/// </summary>
public static class Net
{
    public static NetworkManager? Manager { get; set; }

    public static bool IsRunning => Manager?.IsRunning ?? false;
    public static bool IsServer => Manager?.IsServer ?? false;
    public static bool IsClient => Manager?.IsClient ?? false;

    /// <summary>
    ///     True on a client/peer that holds authority over nothing (pure client); convenience for
    ///     guarding sim code.
    /// </summary>
    public static bool HasAuthority => Manager?.IsServer ?? false;

    public static NetworkClock? Clock => Manager?.Clock;
    public static ReplicationManager? Replication => Manager?.Replication;

    public static void OnMessage<T>(Action<NetConnection, T> handler) where T : INetMessage, new()
    {
        Manager?.OnMessage(handler);
    }

    public static void SendToServer<T>(T message,
        DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new()
    {
        Manager?.SendToServer(message, delivery);
    }

    public static void SendToAll<T>(T message,
        DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new()
    {
        Manager?.SendToAll(message, delivery);
    }
}
