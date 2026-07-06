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
            0f,
            0f,
            0f,
            0f,
            1f
        ),
        new(
            1f,
            1f,
            1f,
            1f,
            1f
        ),
    ];

    public static IReadOnlyList<ShaderRampStop> Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Default;
        var parts = s.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        var stops = new List<ShaderRampStop>();
        for (var i = 0; i + 4 < parts.Length; i += 5)
            stops.Add(
                new ShaderRampStop(
                    F(parts[i]),
                    F(parts[i + 1]),
                    F(parts[i + 2]),
                    F(parts[i + 3]),
                    F(parts[i + 4])
                )
            );
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

        return string.Join(',', fields);
    }

    private static float F(string x)
    {
        return float.TryParse(
            x,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var v
        )
            ? v
            : 0f;
    }

    private static string C(float v)
    {
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}