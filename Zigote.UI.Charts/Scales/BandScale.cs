namespace Zigote.UI.Charts.Scales;

/// <summary>
///     Discrete category scale: each distinct value (in first-seen order) owns an equal-width band,
///     and values map to band centres. Bars, grouped bars, and heat cells position within the band.
/// </summary>
public class BandScale : ChartScale
{
    private readonly List<string> _categories = [];
    private readonly Dictionary<string, int> _index = new();
    private double _viewMax = 1;
    private double _viewMin;

    public IReadOnlyList<string> Categories => _categories;

    public override bool IsBand => true;

    public override bool SupportsWindowing => true;

    /// <summary>Window units are band indices: the full extent of N categories is [0, N].</summary>
    public override (double Min, double Max) FullExtent => (0, Math.Max(1, _categories.Count));

    public override float NormalizedBandWidth =>
        _categories.Count == 0 ? 0f : (float)(1.0 / (_viewMax - _viewMin));

    public override void Reset()
    {
        base.Reset();
        _index.Clear();
        _categories.Clear();
    }

    public override void SetVisibleWindow(double min, double max)
    {
        _viewMin = min;
        _viewMax = max > min ? max : min + 1;
    }

    public override void Include(ChartValue value)
    {
        if (Finalized) return;
        var name = value.Kind == ChartValueKind.Category ? value.CategoryName : value.ToString();
        if (_index.TryAdd(name, _categories.Count))
            _categories.Add(name);
    }

    public override void FinalizeDomain()
    {
        Finalized = true;
        _viewMin = 0;
        _viewMax = Math.Max(1, _categories.Count);
    }

    /// <summary>Band index of <paramref name="value" />, or -1 when it was never included.</summary>
    public int IndexOf(ChartValue value)
    {
        var name = value.Kind == ChartValueKind.Category ? value.CategoryName : value.ToString();
        return _index.GetValueOrDefault(name, -1);
    }

    public override float Normalize(ChartValue value)
    {
        var i = IndexOf(value);
        if (i < 0 || _categories.Count == 0) return 0f;
        return (float)((i + 0.5 - _viewMin) / (_viewMax - _viewMin));
    }

    /// <summary>Band-index magnitude at a normalized position (0.5 = the centre of band 0).</summary>
    public override double NumericAt(float normalized)
    {
        return _viewMin + normalized * (_viewMax - _viewMin);
    }

    public override void BuildTicksInto(int targetCount, Func<ChartValue, string>? formatter,
        List<ChartTick> into)
    {
        into.Clear();
        var visible = Math.Max(1, (int)Math.Ceiling(_viewMax - _viewMin));
        // Thin labels when there are more visible categories than the axis can fit.
        var stride = Math.Max(1, (int)Math.Ceiling(visible / (double)Math.Max(1, targetCount)));
        for (var i = 0; i < _categories.Count; i++)
        {
            if (i % stride != 0) continue;
            var pos = (float)((i + 0.5 - _viewMin) / (_viewMax - _viewMin));
            if (pos is < -0.02f or > 1.02f) continue; // outside the visible window
            var value = ChartValue.Category(_categories[i]);
            var label = formatter?.Invoke(value) ?? _categories[i];
            into.Add(new ChartTick(pos, label, value));
        }
    }
}
