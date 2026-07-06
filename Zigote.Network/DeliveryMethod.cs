namespace Zigote.Network;

/// <summary>
///     How a packet is delivered across the wire. The transport honours these as best its medium
///     allows
///     (a reliable transport such as QUIC/WebSocket treats every method as at-least reliable-ordered;
///     the
///     UDP transport implements all five natively via its own ack/sequence layer).
/// </summary>
public enum DeliveryMethod : byte
{
    /// <summary>
    ///     Fire-and-forget. May be dropped, duplicated or reordered. Lowest latency — movement
    ///     deltas, voice.
    /// </summary>
    Unreliable = 0,

    /// <summary>
    ///     Unreliable, but stale packets (older than the newest received) are discarded. No
    ///     retransmit.
    /// </summary>
    UnreliableSequenced = 1,

    /// <summary>Guaranteed delivery, any order. Retransmitted until acked. Good for unordered events.</summary>
    ReliableUnordered = 2,

    /// <summary>
    ///     Guaranteed delivery, delivered to the application strictly in send order. The default for
    ///     game logic.
    /// </summary>
    ReliableOrdered = 3,

    /// <summary>
    ///     Guaranteed delivery of the newest only — superseded reliable packets are dropped on the
    ///     sender.
    /// </summary>
    ReliableSequenced = 4,
}

/// <summary>Convenience predicates over <see cref="DeliveryMethod" />.</summary>
public static class DeliveryMethodExtensions
{
    public static bool IsReliable(this DeliveryMethod m)
    {
        return m is DeliveryMethod.ReliableUnordered or DeliveryMethod.ReliableOrdered
            or DeliveryMethod.ReliableSequenced;
    }

    public static bool IsOrdered(this DeliveryMethod m)
    {
        return m is DeliveryMethod.ReliableOrdered;
    }

    public static bool IsSequenced(this DeliveryMethod m)
    {
        return m is DeliveryMethod.UnreliableSequenced or DeliveryMethod.ReliableSequenced;
    }
}