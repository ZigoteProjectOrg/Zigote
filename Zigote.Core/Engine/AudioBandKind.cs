namespace Zigote.Core.Engine;

/// <summary>
///     The filter shape of one equalizer band. These are the three shapes a parametric EQ needs, and
///     the three the AutoEq database emits in its ParametricEQ profiles (<c>PK</c>, <c>LSC</c>,
///     <c>HSC</c>) — so a downloaded profile maps onto a chain one filter per band.
///     <para>Values must match <c>BandKind</c> in the engine's <c>src/ffi/audio.zig</c>.</para>
/// </summary>
public enum AudioBandKind : byte
{
    /// <summary>Peaking (bell) filter — boosts or cuts around its centre frequency.</summary>
    Peak = 0,

    /// <summary>Low shelf — shifts everything below its corner frequency.</summary>
    LowShelf = 1,

    /// <summary>High shelf — shifts everything above its corner frequency.</summary>
    HighShelf = 2,
}
