namespace Zigote.Network;

/// <summary>
///     The first byte of every session-layer payload, multiplexing the subsystems that share a single
///     transport connection. Each <see cref="NetConnection" /> dispatches an incoming payload to the
///     handler
///     registered for its leading channel byte. Internal — game code works through
///     <see cref="NetworkManager" />.
/// </summary>
internal enum NetChannel : byte
{
    /// <summary>A typed game message (see <see cref="MessageRouter" />).</summary>
    Message = 1,

    /// <summary>An RPC request awaiting a response.</summary>
    RpcRequest = 2,

    /// <summary>An RPC response correlated to a prior request.</summary>
    RpcResponse = 3,

    /// <summary>Reliable replication events — object spawns and despawns.</summary>
    ReplicationEvents = 4,

    /// <summary>Unreliable replication state — per-tick object field updates.</summary>
    ReplicationState = 5,

    /// <summary>Clock-sync ping/pong for server-time estimation.</summary>
    TimeSync = 6,
}