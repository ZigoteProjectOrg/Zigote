namespace Zigote.UI.Charts.Scales;

/// <summary>
///     Base-10 logarithmic scale for positive data spanning orders of magnitude. Non-positive values
///     are clamped to the smallest positive datum seen (they cannot be represented in log space).
///     Ticks land on powers of ten, with 2×/5× subdivisions when the domain spans few decades.
/// </summary>
public class LogScale : ChartScale
{
    private double _dataMax = double.NegativeInfinity;
    private double _dataMin = double.PositiveInfinity;
    private double _logMax = 1;
    private double _logMin;

    public double? Min { get; set; }
    public double? Max { get; set; }

    public double DomainMin => Math.Pow(x: 10, y: _logMin);
    public double DomainMax => Math.Pow(x: 10, y: _logMax);

    public override void Reset()
    {
        base.Reset();
        _dataMin = double.PositiveInfinity;
        _dataMax = double.NegativeInfinity;
    }

    public override void Include(ChartValue value)
    {
        if (value.Kind == ChartValueKind.Category) return;
        IncludeNumeric(value.Numeric);
    }

    public override void IncludeNumeric(double value)
    {
        if (Finalized || value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return;
        if (value < _dataMin) _dataMin = value;
        if (value > _dataMax) _dataMax = value;
    }

    public override void FinalizeDomain()
    {
        if (Finalized) return;
        Finalized = true;

        double min = Min ?? (double.IsInfinity(_dataMin) ? 1 : _dataMin);
        double max = Max ?? (double.IsInfinity(_dataMax) ? 10 : _dataMax);
        if (min <= 0) min = 1e-9;
        if (max <= min) max = min * 10;

        _logMin = Math.Floor(Math.Log10(min) + 1e-9);
        _logMax = Math.Ceiling(Math.Log10(max) - 1e-9);
        if (_logMax <= _logMin) _logMax = _logMin + 1;
    }

    public override float Normalize(ChartValue value) => NormalizeNumeric(value.Numeric);

    public override float NormalizeNumeric(double value)
    {
        double clamped = Math.Max(val1: value, val2: Math.Pow(x: 10, y: _logMin));
        return (float)((Math.Log10(clamped) - _logMin) / (_logMax - _logMin));
    }

    public override double NumericAt(float normalized) => Math.Pow(
        x: 10,
        y: _logMin + (normalized * (_logMax - _logMin))
    );

    public override void BuildTicksInto(int targetCount, Func<ChartValue, string>? formatter,
        List<ChartTick> into)
    {
        into.Clear();
        int decades = (int)Math.Round(_logMax - _logMin);
        // Few decades → subdivide each with the 1-2-5 mantissas; many → decade marks only.
        double[] mantissas = decades <= 2 ? [1.0, 2.0, 5.0] : [1.0];
        for (int d = (int)_logMin; d <= (int)_logMax; d++)
        {
            foreach (double m in mantissas)
            {
                double v = m * Math.Pow(x: 10, y: d);
                float pos = NormalizeNumeric(v);
                if (pos is < -0.001f or > 1.001f) continue;
                string label = formatter?.Invoke(ChartValue.Number(v)) ?? NiceScale.FormatNumber(v);
                into.Add(new ChartTick(position: pos, label: label, value: ChartValue.Number(v)));
            }
        }
    }

    protected override string DefaultTickLabel(ChartValue value) =>
        NiceScale.FormatNumber(value.Numeric);
}
