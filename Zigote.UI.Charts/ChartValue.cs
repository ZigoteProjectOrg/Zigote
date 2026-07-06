using System.Globalization;

namespace Zigote.UI.Charts;

/// <summary>How a <see cref="ChartValue" /> participates in scale resolution.</summary>
public enum ChartValueKind : byte
{
    Number,
    Category,
    Time,
}

/// <summary>
///     A single plottable value — numeric, categorical, or temporal — the unit every mark's x/y
///     selector returns. Implicitly convertible from the common primitives, so call sites read
///     <c>x: d =&gt; d.Month</c> without wrapping. The chart infers each axis' scale type from the
///     kind of the first value it sees.
/// </summary>
public readonly struct ChartValue : IEquatable<ChartValue>
{
    public readonly ChartValueKind Kind;
    private readonly string? _category;

    private ChartValue(ChartValueKind kind, double number, string? category)
    {
        Kind = kind;
        Numeric = number;
        _category = category;
    }

    public static ChartValue Number(double value)
    {
        return new ChartValue(ChartValueKind.Number, value, null);
    }

    public static ChartValue Category(string value)
    {
        return new ChartValue(ChartValueKind.Category, 0, value);
    }

    public static ChartValue Time(DateTime value)
    {
        return new ChartValue(
            ChartValueKind.Time,
            value.Ticks / (double)TimeSpan.TicksPerSecond,
            null
        );
    }

    public static implicit operator ChartValue(double v)
    {
        return Number(v);
    }

    public static implicit operator ChartValue(float v)
    {
        return Number(v);
    }

    public static implicit operator ChartValue(int v)
    {
        return Number(v);
    }

    public static implicit operator ChartValue(long v)
    {
        return Number(v);
    }

    public static implicit operator ChartValue(string v)
    {
        return Category(v);
    }

    public static implicit operator ChartValue(DateTime v)
    {
        return Time(v);
    }

    /// <summary>Numeric magnitude: the number itself, or seconds-since-epoch for Time. 0 for categories.</summary>
    public double Numeric { get; }

    public string CategoryName => _category ?? string.Empty;

    public DateTime DateTime => Kind == ChartValueKind.Time
        // Clamp the tick count: near DateTime.MaxValue the double→long product rounds to MaxTicks+1 and
        // the DateTime ctor throws (hovering / formatting a point at ~9999-12-31T23:59:59.9).
        ? new DateTime(
            Math.Clamp((long)(Numeric * TimeSpan.TicksPerSecond), 0L, DateTime.MaxValue.Ticks)
        )
        : default;

    public bool Equals(ChartValue other)
    {
        return Kind == other.Kind && Numeric.Equals(other.Numeric) && _category == other._category;
    }

    public override bool Equals(object? obj)
    {
        return obj is ChartValue v && Equals(v);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Kind, Numeric, _category);
    }

    public static bool operator ==(ChartValue a, ChartValue b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(ChartValue a, ChartValue b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return Kind switch {
            ChartValueKind.Category => CategoryName,
            ChartValueKind.Time => DateTime.ToString(
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture
            ),
            _ => Numeric.ToString("G9", CultureInfo.InvariantCulture),
        };
    }
}