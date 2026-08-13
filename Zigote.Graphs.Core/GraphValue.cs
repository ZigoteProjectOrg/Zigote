namespace Zigote.Graphs.Core;

/// <summary>
///     A tagged union that can hold any primitive graph value.
///     Used for node property values, parameter defaults, and pin defaults.
/// </summary>
public sealed class GraphValue
{
    private readonly object? _raw;

    private GraphValue(GraphValueKind kind, object? raw)
    {
        Kind = kind;
        _raw = raw;
    }

    public GraphValueKind Kind { get; }

    public bool IsNull => Kind == GraphValueKind.Null;

    public static GraphValue Null() => new(kind: GraphValueKind.Null, raw: null);

    public static GraphValue FromBool(bool v) => new(kind: GraphValueKind.Bool, raw: v);

    public static GraphValue FromInt(int v) => new(kind: GraphValueKind.Int, raw: v);

    public static GraphValue FromFloat(float v) => new(kind: GraphValueKind.Float, raw: v);

    public static GraphValue FromString(string v) => new(kind: GraphValueKind.String, raw: v);

    public static GraphValue FromFloat2(float x, float y)
    {
        return new GraphValue(
            kind: GraphValueKind.Float2,
            raw: new[] {
                x,
                y,
            }
        );
    }

    public static GraphValue FromFloat3(float x, float y, float z)
    {
        return new GraphValue(
            kind: GraphValueKind.Float3,
            raw: new[] {
                x,
                y,
                z,
            }
        );
    }

    public static GraphValue FromFloat4(float x, float y, float z, float w)
    {
        return new GraphValue(
            kind: GraphValueKind.Float4,
            raw: new[] {
                x,
                y,
                z,
                w,
            }
        );
    }

    public bool AsBool() => (bool)_raw!;

    public int AsInt() => (int)_raw!;

    public float AsFloat() => (float)_raw!;

    public string AsString() => (string)_raw!;

    public float[] AsFloat2() => (float[])_raw!;

    public float[] AsFloat3() => (float[])_raw!;

    public float[] AsFloat4() => (float[])_raw!;

    public override string ToString()
    {
        return Kind switch {
            GraphValueKind.Null => "null",
            GraphValueKind.Bool => AsBool().ToString(),
            GraphValueKind.Int => AsInt().ToString(),
            GraphValueKind.Float => AsFloat().ToString("G"),
            GraphValueKind.String => AsString(),
            GraphValueKind.Float2 => $"({AsFloat2()[0]}, {AsFloat2()[1]})",
            GraphValueKind.Float3 => $"({AsFloat3()[0]}, {AsFloat3()[1]}, {AsFloat3()[2]})",
            GraphValueKind.Float4 =>
                $"({AsFloat4()[0]}, {AsFloat4()[1]}, {AsFloat4()[2]}, {AsFloat4()[3]})",
            _ => _raw?.ToString() ?? "null",
        };
    }
}

public enum GraphValueKind
{
    Null,
    Bool,
    Int,
    Float,
    Float2,
    Float3,
    Float4,
    String,
}
