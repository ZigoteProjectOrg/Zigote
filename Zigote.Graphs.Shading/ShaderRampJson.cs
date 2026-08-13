using System.Globalization;

namespace Zigote.Graphs.Shading;

/// <summary>
///     Serialises a Color-Ramp's stops to/from a node-property string. The format is a flat,
///     culture-invariant, comma-separated list in groups of five — <c>pos, r, g, b, a</c> per stop —
///     so it
///     round-trips through the graph's <c>ReferenceHandler.Preserve</c> persistence as an opaque
///     scalar
///     (no custom converter, no dictionary-on-ctor-type pitfall). The editor's gradient widget
///     reads/writes
///     the same format.
/// </summary>
public static class ShaderRampJson
{
    public static readonly IReadOnlyList<ShaderRampStop> Default = [
        new(
            Pos: 0f,
            R: 0f,
            G: 0f,
            B: 0f,
            A: 1f
        ),
        new(
            Pos: 1f,
            R: 1f,
            G: 1f,
            B: 1f,
            A: 1f
        ),
    ];

    public static IReadOnlyList<ShaderRampStop> Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Default;
        string[] parts = s.Split(
            separator: ',',
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        var stops = new List<ShaderRampStop>();
        for (int i = 0; i + 4 < parts.Length; i += 5)
        {
            stops.Add(
                new ShaderRampStop(
                    Pos: F(parts[i]),
                    R: F(parts[i + 1]),
                    G: F(parts[i + 2]),
                    B: F(parts[i + 3]),
                    A: F(parts[i + 4])
                )
            );
        }

        return stops.Count > 0 ? stops : Default;
    }

    public static string Serialize(IReadOnlyList<ShaderRampStop> stops)
    {
        var fields = new List<string>(stops.Count * 5);
        foreach (var s in stops)
        {
            fields.Add(C(s.Pos));
            fields.Add(C(s.R));
            fields.Add(C(s.G));
            fields.Add(C(s.B));
            fields.Add(C(s.A));
        }

        return string.Join(separator: ',', values: fields);
    }

    private static float F(string x)
    {
        return float.TryParse(
            s: x,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out float v
        )
            ? v
            : 0f;
    }

    private static string C(float v) => v.ToString(
        format: "0.####",
        provider: CultureInfo.InvariantCulture
    );
}
