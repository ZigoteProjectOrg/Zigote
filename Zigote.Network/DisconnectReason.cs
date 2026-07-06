namespace Zigote.Network;

/// <summary>Why a connection ended. Delivered with the disconnect event so games can react/reconnect.</summary>
public enum DisconnectReason : byte
{
    /// <summary>No disconnect has occurred (default).</summary>
    None = 0,

    /// <summary>The local side called Disconnect/Stop.</summary>
    LocalClose = 1,

    /// <summary>The remote side closed the connection gracefully.</summary>
    RemoteClose = 2,

    /// <summary>No traffic within the configured timeout — the peer is assumed gone.</summary>
    Timeout = 3,

    /// <summary>The server rejected the connection during approval/handshake.</summary>
    ConnectionRejected = 4,

    /// <summary>The server is full (max connections reached).</summary>
    ServerFull = 5,

    /// <summary>Protocol or version mismatch detected during the handshake.</summary>
    ProtocolMismatch = 6,

    /// <summary>An underlying socket/transport error tore the connection down.</summary>
    TransportError = 7,

    /// <summary>The connection attempt could not reach the host (no response to connect requests).</summary>
    ConnectFailed = 8,
}