using Zigote.Core.Engine;

namespace Zigote.Audioplayer;

/// <summary>
///     One band of an <see cref="Equalizer" />. Shelves take Q, the way every parametric EQ UI
///     specifies them.
/// </summary>
public readonly record struct EqualizerBand(
    AudioBandKind Kind,
    float FreqHz,
    float GainDb,
    float Q = 0.707f);

/// <summary>
///     A chain of biquads spliced between the player and the output — Timbre's "compose the signal
///     path out of nodes", except the nodes live in the engine's mixer graph rather than in a chain of
///     C# objects. Nothing here touches samples; a band is four numbers pushed at a native filter.
///     <para>
///         Filters are reconfigured in place, so dragging a slider does not click. Assign it to
///         <see cref="AudioPlayer.Equalizer" /> and every item the player loads is routed through it,
///         including the gapless successor it arms behind the scenes.
///     </para>
/// </summary>
public sealed class Equalizer : IDisposable
{
    /// <summary>The ISO 10-band centres, the default of every equalizer UI ever shipped.</summary>
    public static readonly float[] TenBandFrequencies =
        [31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    private readonly IAudioApi _audio;
    private readonly EqualizerBand[] _bands;
    private bool _enabled = true;

    /// <summary>Create a chain from explicit bands (max 16 — the engine's limit).</summary>
    public Equalizer(IAudioApi audio, params EqualizerBand[] bands)
    {
        _audio = audio;
        _bands = bands.Length <= 16 ? [.. bands] : bands[..16];
        Id = _audio.EqCreate(_bands.Length);
        for (int i = 0; i < _bands.Length; i++) Apply(i);
    }

    /// <summary>
    ///     The engine-side chain id; 0 when the device refused it. Pass to
    ///     <see cref="IAudioApi.SetEq" />.
    /// </summary>
    public uint Id { get; private set; }

    public IReadOnlyList<EqualizerBand> Bands => _bands;

    /// <summary>Bypass without losing the settings — the A/B lever.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (Id != 0) _audio.EqSetEnabled(eqId: Id, enabled: value);
        }
    }

    public void Dispose()
    {
        if (Id == 0) return;
        _audio.EqDestroy(Id);
        Id = 0;
    }

    /// <summary>A flat 10-band graphic EQ at the ISO centres, ready to have gains dialled in.</summary>
    public static Equalizer TenBand(IAudioApi audio)
    {
        return new Equalizer(
            audio: audio,
            bands: [
                .. TenBandFrequencies.Select(f => new EqualizerBand(
                        Kind: AudioBandKind.Peak,
                        FreqHz: f,
                        GainDb: 0f
                    )
                ),
            ]
        );
    }

    /// <summary>Retune one band. Out-of-range indexes are ignored, like every other id in this API.</summary>
    public void SetBand(int index, EqualizerBand band)
    {
        if ((uint)index >= (uint)_bands.Length) return;
        _bands[index] = band;
        Apply(index);
    }

    /// <summary>Set just the gain of a band — the only thing a graphic-EQ slider moves.</summary>
    public void SetGain(int index, float gainDb)
    {
        if ((uint)index >= (uint)_bands.Length) return;
        SetBand(index: index, band: _bands[index] with { GainDb = gainDb });
    }

    private void Apply(int index)
    {
        if (Id == 0) return;
        var b = _bands[index];
        _audio.EqSetBand(
            eqId: Id,
            index: index,
            kind: b.Kind,
            freqHz: b.FreqHz,
            gainDb: b.GainDb,
            q: b.Q
        );
    }
}
