namespace Zigote.Network;

/// <summary>
///     A client input sample for one tick, carrying a monotonic <see cref="Sequence" /> so the server can
///     acknowledge how far it has processed and the client can replay un-acked inputs during reconciliation.
///     Implement <see cref="INetSerializable" /> to (de)serialize the actual input fields (move axes, buttons…).
/// </summary>
public interface IInputCommand : INetSerializable
{
    uint Sequence { get; set; }
}
