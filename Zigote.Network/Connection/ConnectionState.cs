namespace Zigote.Network;

/// <summary>Lifecycle of a single logical connection (handshake state machine).</summary>
public enum ConnectionState : byte
{
    /// <summary>Freshly created, nothing sent yet.</summary>
    Disconnected = 0,

    /// <summary>Client: connect request sent, awaiting the server's accept. Server: approval pending.</summary>
    Connecting = 1,

    /// <summary>Handshake complete — data may flow.</summary>
    Connected = 2,

    /// <summary>A graceful close is in flight (disconnect sent, awaiting confirmation/flush).</summary>
    Disconnecting = 3,
}