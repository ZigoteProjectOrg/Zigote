namespace Zigote.Network;

/// <summary>
///     Wire opcodes for the reliable replication-events stream (
///     <see cref="NetChannel.ReplicationEvents" />).
///     State updates ride the separate unreliable <see cref="NetChannel.ReplicationState" /> channel
///     and need
///     no opcode. Spawns carry full initial state; despawns carry only the id.
/// </summary>
internal enum ReplicationOp : byte
{
    Spawn = 0,
    Despawn = 1,
}