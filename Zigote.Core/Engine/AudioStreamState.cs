namespace Zigote.Core.Engine;

/// <summary>
///     Where a push stream is in its life. Everything a UI needs to say why nothing is coming out
///     of the speakers yet — or why nothing ever will.
///     <para>Values must match <c>State</c> in the engine's <c>src/ffi/netstream.zig</c>.</para>
/// </summary>
public enum AudioStreamState : uint
{
    /// <summary>Too few bytes have arrived to identify the container. Silence, not failure.</summary>
    Connecting = 0,

    /// <summary>Decoding. Whether it is <i>audible</i> yet is a question for the buffer level.</summary>
    Playing = 1,

    /// <summary>
    ///     Nothing here can decode this stream. The engine reads MP3, FLAC, WAV and Vorbis, so in
    ///     practice this is an AAC station.
    /// </summary>
    Unsupported = 2,

    /// <summary>The source ended and the buffer drained; the sound now reports end-of-stream.</summary>
    Ended = 3,
}
