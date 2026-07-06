using System.Globalization;

namespace Zigote.UI.Charts.Scales;

/// <summary>
///     Continuous temporal scale. Positions are linear in time; ticks snap to calendar boundaries
///     (years, months, weeks, days, hours, minutes, seconds) chosen from the domain span, and labels
///     format per unit ("2026", "Mar", "Mar 8", "14:30").
/// </summary>
public class TimeScale : ChartScale
{
    private double _maxSec = double.NegativeInfinity;
    private double _minSec = double.PositiveInfinity;
    private double _viewMax = 1;
    private double _viewMin;

    public DateTime? Min { get; set; }
    public DateTime? Max { get; set; }

    public DateTime DomainMin => FromSeconds(_minSec);
    public DateTime DomainMax => FromSeconds(_maxSec);

    public override bool SupportsWindowing => true;

    public override (double Min, double Max) FullExtent => (_minSec, _maxSec);

    public override void Reset()
    {
        base.Reset();
        _minSec = double.PositiveInfinity;
        _maxSec = double.NegativeInfinity;
    }

    private static double ToSeconds(DateTime t)
    {
        return t.Ticks / (double)TimeSpan.TicksPerSecond;
    }

    // Clamp to the valid tick range: near DateTime.MaxValue the double→long product rounds up to
    // MaxValue.Ticks + 1 and `new DateTime(long)` throws (data at/around 9999-12-31T23:59:59.9).
    private static DateTime FromSeconds(double s)
    {
        return new DateTime(
            Math.Clamp((long)(s * TimeSpan.TicksPerSecond), 0L, DateTime.MaxValue.Ticks)
        );
    }

    public override void Include(ChartValue value)
    {
        if (Finalized || value.Kind != ChartValueKind.Time) return;
        var s = value.Numeric;
        if (s < _minSec) _minSec = s;
        if (s > _maxSec) _maxSec = s;
    }

    public override void FinalizeDomain()
    {
        if (Finalized) return;
        Finalized = true;
        if (Min.HasValue) _minSec = ToSeconds(Min.Value);
        if (Max.HasValue) _maxSec = ToSeconds(Max.Value);
        if (double.IsInfinity(_minSec))
        {
            _minSec = ToSeconds(new DateTime(2026, 1, 1));
            _maxSec = _minSec + 86400;
        }

        if (_maxSec <= _minSec) _maxSec = _minSec + 1;
        _viewMin = _minSec;
        _viewMax = _maxSec;
    }

    public override void SetVisibleWindow(double min, double max)
    {
        _viewMin = min;
        _viewMax = max > min ? max : min + 1;
    }

    public override float Normalize(ChartValue value)
    {
        return NormalizeNumeric(value.Numeric);
    }

    public override float NormalizeNumeric(double value)
    {
        return (float)((value - _viewMin) / (_viewMax - _viewMin));
    }

    /// <summary>Seconds magnitude at a normalized position (feed to <see cref="ChartValue.Time" /> via ticks).</summary>
    public override double NumericAt(float normalized)
    {
        return _viewMin + normalized * (_viewMax - _viewMin);
    }

    public override void BuildTicksInto(int targetCount, Func<ChartValue, string>? formatter,
        List<ChartTick> into)
    {
        into.Clear();
        targetCount = Math.Max(2, targetCount);
        var span = _viewMax - _viewMin;
        var (unit, every) = ChooseUnit(span, targetCount);

        var t = Align(FromSeconds(_viewMin), unit, every);
        var guard = 0;
        while (ToSeconds(t) <= _viewMax + 1e-6 && guard++ < 1000)
        {
            var pos = NormalizeNumeric(ToSeconds(t));
            if (pos >= -0.001f)
            {
                var label = formatter?.Invoke(ChartValue.Time(t)) ?? Format(t, unit, span);
                into.Add(new ChartTick(pos, label, ChartValue.Time(t)));
            }

            var next = Advance(t, unit, every);
            if (next <= t) break; // saturated at DateTime.MaxValue — no forward progress
            t = next;
        }
    }

    protected override string DefaultTickLabel(ChartValue value)
    {
        var span = _viewMax - _viewMin;
        var (unit, _) = ChooseUnit(span, 6);
        return Format(value.DateTime, unit, span);
    }

    private static (Unit Unit, int Every) ChooseUnit(double spanSeconds, int target)
    {
        var rough = spanSeconds / target;
        return rough switch {
            < 1 => (Unit.Second, 1),
            < 5 => (Unit.Second, 5),
            < 15 => (Unit.Second, 15),
            < 45 => (Unit.Second, 30),
            < 60 * 3 => (Unit.Minute, 1),
            < 60 * 8 => (Unit.Minute, 5),
            < 60 * 22 => (Unit.Minute, 15),
            < 60 * 45 => (Unit.Minute, 30),
            < 3600 * 2 => (Unit.Hour, 1),
            < 3600 * 4 => (Unit.Hour, 3),
            < 3600 * 9 => (Unit.Hour, 6),
            < 3600 * 18 => (Unit.Hour, 12),
            < 86400 * 1.5 => (Unit.Day, 1),
            < 86400 * 4 => (Unit.Day, 2),
            < 86400 * 11 => (Unit.Week, 1),
            < 86400 * 22 => (Unit.Week, 2),
            < 86400 * 45 => (Unit.Month, 1),
            < 86400 * 135 => (Unit.Month, 3),
            < 86400 * 250 => (Unit.Month, 6),
            _ => (Unit.Year,
                Math.Max(1, (int)NiceScale.TickStep(spanSeconds / (86400.0 * 365.25), target))),
        };
    }

    private static DateTime Align(DateTime start, Unit unit, int every)
    {
        return unit switch {
            Unit.Second => new DateTime(
                start.Year,
                start.Month,
                start.Day,
                start.Hour,
                start.Minute,
                start.Second / every * every
            ),
            Unit.Minute => new DateTime(
                start.Year,
                start.Month,
                start.Day,
                start.Hour,
                start.Minute / every * every,
                0
            ),
            Unit.Hour => new DateTime(
                start.Year,
                start.Month,
                start.Day,
                start.Hour / every * every,
                0,
                0
            ),
            Unit.Day => start.Date,
            // Weeks align to Monday.
            Unit.Week => start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)),
            Unit.Month => new DateTime(start.Year, (start.Month - 1) / every * every + 1, 1),
            _ => new DateTime(start.Year / every * every, 1, 1),
        };
    }

    private static DateTime Advance(DateTime t, Unit unit, int every)
    {
        // Stepping past DateTime.MaxValue throws; saturate instead so tick generation near the top of the
        // representable range terminates cleanly (the caller breaks when Advance stops making progress).
        try
        {
            return unit switch {
                Unit.Second => t.AddSeconds(every),
                Unit.Minute => t.AddMinutes(every),
                Unit.Hour => t.AddHours(every),
                Unit.Day => t.AddDays(every),
                Unit.Week => t.AddDays(7 * every),
                Unit.Month => t.AddMonths(every),
                _ => t.AddYears(every),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MaxValue;
        }
    }

    private static string Format(DateTime t, Unit unit, double spanSeconds)
    {
        var inv = CultureInfo.InvariantCulture;
        return unit switch {
            Unit.Second => t.ToString("HH:mm:ss", inv),
            Unit.Minute or Unit.Hour => t.ToString("HH:mm", inv),
            Unit.Day or Unit.Week => t.ToString("MMM d", inv),
            // A multi-year month axis needs the year for orientation.
            Unit.Month => spanSeconds > 86400.0 * 400
                ? t.ToString("MMM yy", inv)
                : t.ToString("MMM", inv),
            _ => t.ToString("yyyy", inv),
        };
    }

    private enum Unit
    {
        Second,
        Minute,
        Hour,
        Day,
        Week,
        Month,
        Year,
    }
}