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

    public static GraphValue Null()
    {
        return new GraphValue(GraphValueKind.Null, null);
    }

    public static GraphValue FromBool(bool v)
    {
        return new GraphValue(GraphValueKind.Bool, v);
    }

    public static GraphValue FromInt(int v)
    {
        return new GraphValue(GraphValueKind.Int, v);
    }

    public static GraphValue FromFloat(float v)
    {
        return new GraphValue(GraphValueKind.Float, v);
    }

    public static GraphValue FromString(string v)
    {
        return new GraphValue(GraphValueKind.String, v);
    }

    public static GraphValue FromFloat2(float x, float y)
    {
        return new GraphValue(
            GraphValueKind.Float2,
            new[] {
                x,
                y,
            }
        );
    }

    public static GraphValue FromFloat3(float x, float y, float z)
    {
        return new GraphValue(
            GraphValueKind.Float3,
            new[] {
                x,
                y,
                z,
            }
        );
    }

    public static GraphValue FromFloat4(float x, float y, float z, float w)
    {
        return new GraphValue(
            GraphValueKind.Float4,
            new[] {
                x,
                y,
                z,
                w,
            }
        );
    }

    public bool AsBool()
    {
        return (bool)_raw!;
    }

    public int AsInt()
    {
        return (int)_raw!;
    }

    public float AsFloat()
    {
        return (float)_raw!;
    }

    public string AsString()
    {
        return (string)_raw!;
    }

    public float[] AsFloat2()
    {
        return (float[])_raw!;
    }

    public float[] AsFloat3()
    {
        return (float[])_raw!;
    }

    public float[] AsFloat4()
    {
        return (float[])_raw!;
    }

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
