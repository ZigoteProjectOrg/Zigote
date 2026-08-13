using System.Globalization;
using Zigote.Core;
using Zigote.Vfx;

namespace Zigote.Graphs.Vfx;

/// <summary>
///     Compact, ReferenceHandler.Preserve-safe serialization of a <see cref="ColorRamp" /> as a flat
///     string
///     property — stops as <c>pos,r,g,b,a</c> separated by <c>;</c>. The editor's
///     <c>GradientEditor</c>
///     (via the <c>"gradient"</c> property-editor hint) round-trips through this same format,
///     mirroring how
///     the shader domain stores its Color Ramp.
/// </summary>
public static class VfxRampJson
{
    public static string Default { get; } = Serialize(
        new ColorRamp(
            [
                new ColorStop(0f, Color.White),
                new ColorStop(1f, Color.White.WithAlpha(0f)),
            ]
        )
    );

    public static string Serialize(ColorRamp ramp)
    {
        return string.Join(
            ';',
            ramp.Stops.Select(s =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{s.Position:0.####},{s.Color.R:0.####},{s.Color.G:0.####},{s.Color.B:0.####},{s.Color.A:0.####}"
                )
            )
        );
    }

    public static ColorRamp Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fallback();

        var stops = new List<ColorStop>();
        foreach (var part in text.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                 ))
        {
            var f = part.Split(',');
            if (f.Length < 5) continue;
            if (!TryF(f[0], out var pos) || !TryF(f[1], out var r) || !TryF(f[2], out var g) ||
                !TryF(f[3], out var b) || !TryF(f[4], out var a))
                continue;
            stops.Add(
                new ColorStop(
                    pos,
                    new Color(
                        r,
                        g,
                        b,
                        a
                    )
                )
            );
        }

        return stops.Count == 0 ? Fallback() : new ColorRamp(stops);
    }

    private static ColorRamp Fallback()
    {
        return new ColorRamp(
            [new ColorStop(0f, Color.White), new ColorStop(1f, Color.White.WithAlpha(0f))]
        );
    }

    private static bool TryF(string s, out float v)
    {
        return float.TryParse(
            s,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out v
        );
    }
}
