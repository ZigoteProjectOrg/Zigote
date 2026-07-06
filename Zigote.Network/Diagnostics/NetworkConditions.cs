namespace Zigote.Network;

/// <summary>
///     Artificial network impairment for testing: added latency, jitter, packet loss, duplication and
///     reordering. A <c>SimulatedTransport</c> applies these to outgoing/incoming packets so a game
///     can be
///     developed and tested against bad-network conditions on localhost (or fully in-process via
///     loopback).
///     Deterministic when seeded, so impairment tests are reproducible.
/// </summary>
public sealed class NetworkConditions
{
    /// <summary>One-way base latency added to every packet (seconds).</summary>
    public float LatencySeconds { get; set; }

    /// <summary>Random extra latency in [0, JitterSeconds] added per packet.</summary>
    public float JitterSeconds { get; set; }

    /// <summary>Probability in [0, 1] that an outgoing packet is dropped entirely.</summary>
    public float PacketLoss { get; set; }

    /// <summary>Probability in [0, 1] that a packet is duplicated.</summary>
    public float DuplicationChance { get; set; }

    /// <summary>Probability in [0, 1] that a packet is delivered out of order (extra random delay).</summary>
    public float ReorderChance { get; set; }

    /// <summary>RNG seed for reproducible impairment. 0 = time-independent fixed default.</summary>
    public int Seed { get; set; }

    public static NetworkConditions None => new();

    /// <summary>~75 ms RTT, mild jitter, 1% loss — a decent broadband connection.</summary>
    public static NetworkConditions Good => new() {
        LatencySeconds = 0.0375f,
        JitterSeconds = 0.005f,
        PacketLoss = 0.01f,
    };

    /// <summary>~150 ms RTT, noticeable jitter, 5% loss, occasional reorder — a poor mobile connection.</summary>
    public static NetworkConditions Poor => new() {
        LatencySeconds = 0.075f,
        JitterSeconds = 0.03f,
        PacketLoss = 0.05f,
        DuplicationChance = 0.01f,
        ReorderChance = 0.05f,
    };

    public NetworkConditions Clone()
    {
        return (NetworkConditions)MemberwiseClone();
    }
}