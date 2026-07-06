namespace Zigote.UI.Charts.Scales;

/// <summary>
///     Continuous numeric scale with a "nice" auto domain. <see cref="IncludeZero" /> forces the
///     baseline into the domain (bars/areas measure from zero); <see cref="Min" />/<see cref="Max" />
///     pin either end explicitly.
/// </summary>
public class LinearScale : ChartScale
{
    private double _dataMax = double.NegativeInfinity;
    private double _dataMin = double.PositiveInfinity;
    private double _viewMax = 1;
    private double _viewMin;

    /// <summary>Explicit domain override; null = derive from the data.</summary>
    public double? Min { get; set; }

    public double? Max { get; set; }

    /// <summary>Force 0 into the domain. Marks with a zero baseline (bar/area) set this automatically.</summary>
    public bool IncludeZero { get; set; }

    /// <summary>Round the derived domain outward to nice tick multiples (default on).</summary>
    public bool Nice { get; set; } = true;

    public double DomainMin { get; private set; }

    public double DomainMax { get; private set; } = 1;

    public override bool SupportsWindowing => true;

    public override (double Min, double Max) FullExtent => (DomainMin, DomainMax);

    public override void Reset()
    {
        base.Reset();
        _dataMin = double.PositiveInfinity;
        _dataMax = double.NegativeInfinity;
    }

    public override void Include(ChartValue value)
    {
        if (Finalized || value.Kind == ChartValueKind.Category) return;
        IncludeNumeric(value.Numeric);
    }

    public override void IncludeNumeric(double value)
    {
        if (Finalized || double.IsNaN(value) || double.IsInfinity(value)) return;
        if (value < _dataMin) _dataMin = value;
        if (value > _dataMax) _dataMax = value;
    }

    public override void FinalizeDomain()
    {
        if (Finalized) return;
        Finalized = true;

        var min = _dataMin;
        var max = _dataMax;
        if (double.IsInfinity(min)) (min, max) = (0, 1); // no data at all
        if (IncludeZero)
        {
            min = Math.Min(min, 0);
            max = Math.Max(max, 0);
        }

        if (Nice)
        {
            var (nMin, nMax, _) = NiceScale.NiceDomain(min, max, 5);
            min = nMin;
            max = nMax;
        }
        else if (min == max)
        {
            min -= 0.5;
            max += 0.5;
        }

        DomainMin = Min ?? min;
        DomainMax = Max ?? max;
        if (DomainMax <= DomainMin) DomainMax = DomainMin + 1;
        _viewMin = DomainMin;
        _viewMax = DomainMax;
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

    public override double NumericAt(float normalized)
    {
        return _viewMin + normalized * (_viewMax - _viewMin);
    }

    public override void BuildTicksInto(int targetCount, Func<ChartValue, string>? formatter,
        List<ChartTick> into)
    {
        into.Clear();
        var step = NiceScale.TickStep(_viewMax - _viewMin, targetCount);
        var values = NiceScale.Ticks(_viewMin, _viewMax, step);
        foreach (var v in values)
        {
            var label = formatter?.Invoke(ChartValue.Number(v)) ?? NiceScale.FormatNumber(v);
            into.Add(new ChartTick(NormalizeNumeric(v), label, ChartValue.Number(v)));
        }
    }

    protected override string DefaultTickLabel(ChartValue value)
    {
        return NiceScale.FormatNumber(value.Numeric);
    }
}