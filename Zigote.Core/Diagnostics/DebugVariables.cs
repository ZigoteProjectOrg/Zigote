using System.Globalization;

namespace Zigote.Core.Diagnostics;

public enum DebugVarType
{
    Bool,
    Int,
    Float,
    Enum,
    String,
}

/// <summary>
///     A runtime-editable engine value (design doc §11). Backed by getter/setter delegates so it never
///     owns state — registration sites bind it straight to the live field. Surfaced by the debug
///     menu's
///     Variables panel and reachable from the console (<c>get</c>/<c>set</c>).
/// </summary>
public sealed class DebugVariable
{
    public required string Name { get; init; }
    public string Category { get; init; } = "general";
    public string? Description { get; init; }
    public DebugVarType Type { get; init; }
    public required Func<object> Getter { get; init; }
    public Action<object>? Setter { get; init; }
    public object? Min { get; init; }
    public object? Max { get; init; }

    /// <summary>Display names for <see cref="DebugVarType.Enum" /> values, indexed by the int value.</summary>
    public string[]? EnumNames { get; init; }

    public bool IsReadOnly => Setter is null;

    public object Value
    {
        get
        {
            try
            {
                return Getter();
            }
            catch
            {
                return "<error>";
            }
        }
    }

    public string Display()
    {
        object v = Value;
        if (Type == DebugVarType.Enum && EnumNames is { } names && v is int ei &&
            (uint)ei < names.Length)
            return names[ei];
        if (v is bool b) return b ? "true" : "false";
        if (v is float f) return f.ToString("0.###");
        return v.ToString() ?? "";
    }

    /// <summary>
    ///     Parse <paramref name="text" /> against this variable's type and apply it. Returns an error
    ///     message, or null on success.
    /// </summary>
    public string? TrySet(string text)
    {
        if (Setter is null) return $"'{Name}' is read-only";
        try
        {
            switch (Type)
            {
                case DebugVarType.Bool:
                    Setter(ParseBool(text));
                    break;
                case DebugVarType.Int:
                    if (!int.TryParse(s: text, result: out int iv))
                        return $"'{text}' is not an integer";
                    Setter(Clamp(iv));
                    break;
                case DebugVarType.Float:
                    if (!float.TryParse(
                            s: text,
                            style: NumberStyles.Float,
                            provider: CultureInfo.InvariantCulture,
                            result: out float fv
                        ))
                        return $"'{text}' is not a number";
                    Setter(Clamp(fv));
                    break;
                case DebugVarType.Enum:
                    Setter(ParseEnum(text));
                    break;
                default:
                    Setter(text);
                    break;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private int Clamp(int v)
    {
        if (Min is int mn) v = Math.Max(val1: mn, val2: v);
        if (Max is int mx) v = Math.Min(val1: mx, val2: v);
        return v;
    }

    private float Clamp(float v)
    {
        if (Min is float mn) v = MathF.Max(x: mn, y: v);
        if (Max is float mx) v = MathF.Min(x: mx, y: v);
        return v;
    }

    private static bool ParseBool(string s) => s is "1" or "true" or "on" or "yes" ||
                                               (bool.TryParse(value: s, result: out bool b) && b);

    private int ParseEnum(string text)
    {
        if (EnumNames is { } names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(
                        a: names[i],
                        b: text,
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    ))
                    return i;
            }
        }

        return int.TryParse(s: text, result: out int v) ? v : 0;
    }
}

/// <summary>
///     Greenfield registry of <see cref="DebugVariable" />s (design doc §11). Process-wide so any
///     subsystem can register; the debug menu and console read it.
/// </summary>
public static class DebugVariables
{
    private static readonly Dictionary<string, DebugVariable> Map = new(
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly List<DebugVariable> Ordered = [];

    public static int Version { get; private set; }

    public static IReadOnlyList<DebugVariable> All => Ordered;

    public static DebugVariable? Find(string name) => Map.GetValueOrDefault(name);

    public static void Register(DebugVariable v)
    {
        if (Map.TryGetValue(key: v.Name, value: out var existing)) Ordered.Remove(existing);
        Map[v.Name] = v;
        Ordered.Add(v);
        Version++;
    }

    public static void RegisterBool(string name, Func<bool> getter, Action<bool>? setter = null,
        string category = "general", string? description = null)
    {
        Register(
            new DebugVariable {
                Name = name,
                Category = category,
                Description = description,
                Type = DebugVarType.Bool,
                Getter = () => getter(),
                Setter = setter is null ? null : o => setter((bool)o),
            }
        );
    }

    public static void RegisterInt(string name, Func<int> getter, Action<int>? setter = null,
        int? min = null, int? max = null, string category = "general", string? description = null)
    {
        Register(
            new DebugVariable {
                Name = name,
                Category = category,
                Description = description,
                Type = DebugVarType.Int,
                Getter = () => getter(),
                Setter = setter is null ? null : o => setter((int)o),
                Min = min,
                Max = max,
            }
        );
    }

    public static void RegisterFloat(string name, Func<float> getter, Action<float>? setter = null,
        float? min = null, float? max = null, string category = "general",
        string? description = null)
    {
        Register(
            new DebugVariable {
                Name = name,
                Category = category,
                Description = description,
                Type = DebugVarType.Float,
                Getter = () => getter(),
                Setter = setter is null ? null : o => setter((float)o),
                Min = min,
                Max = max,
            }
        );
    }

    public static void RegisterEnum<T>(string name, Func<T> getter, Action<T>? setter = null,
        string category = "general", string? description = null) where T : struct, Enum
    {
        Register(
            new DebugVariable {
                Name = name,
                Category = category,
                Description = description,
                Type = DebugVarType.Enum,
                EnumNames = Enum.GetNames(typeof(T)),
                Getter = () => Convert.ToInt32(getter()),
                Setter = setter is null
                    ? null
                    : o => setter((T)Enum.ToObject(enumType: typeof(T), value: (int)o)),
            }
        );
    }
}
