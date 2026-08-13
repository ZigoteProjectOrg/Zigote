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
                new ColorStop(position: 0f, color: Color.White),
                new ColorStop(position: 1f, color: Color.White.WithAlpha(0f)),
            ]
        )
    );

    public static string Serialize(ColorRamp ramp)
    {
        return string.Join(
            separator: ';',
            values: ramp.Stops.Select(s =>
                string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler:
                    $"{s.Position:0.####},{s.Color.R:0.####},{s.Color.G:0.####},{s.Color.B:0.####},{s.Color.A:0.####}"
                )
            )
        );
    }

    public static ColorRamp Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fallback();

        var stops = new List<ColorStop>();
        foreach (string part in text.Split(
                     separator: ';',
                     options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                 ))
        {
            string[] f = part.Split(',');
            if (f.Length < 5) continue;
            if (!TryF(s: f[0], v: out float pos) || !TryF(s: f[1], v: out float r) ||
                !TryF(s: f[2], v: out float g) ||
                !TryF(s: f[3], v: out float b) || !TryF(s: f[4], v: out float a))
                continue;
            stops.Add(
                new ColorStop(
                    position: pos,
                    color: new Color(
                        r: r,
                        g: g,
                        b: b,
                        a: a
                    )
                )
            );
        }

        return stops.Count == 0 ? Fallback() : new ColorRamp(stops);
    }

    private static ColorRamp Fallback()
    {
        return new ColorRamp(
            [
                new ColorStop(position: 0f, color: Color.White),
                new ColorStop(position: 1f, color: Color.White.WithAlpha(0f)),
            ]
        );
    }

    private static bool TryF(string s, out float v)
    {
        return float.TryParse(
            s: s,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out v
        );
    }
}
