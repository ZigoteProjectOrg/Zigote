using Zigote.Core;

namespace Zigote.UI.Charts.Rendering;

/// <summary>
///     Public data↔screen conversion over a laid-out chart's finalized scales — the ChartProxy
///     analogue for building custom overlays (<c>Chart.OverlayPainter</c>) and resolving pointer
///     positions back to domain values. A readonly struct over the chart's live render context, so
///     constructing and querying one allocates nothing. Positions honour the visible scroll/zoom
///     window; values scrolled out of view project past the plot rect (clip against
///     <see cref="PlotRect" />).
/// </summary>
public readonly struct ChartProxy
{
    private readonly ChartRenderContext? _ctx;
    private readonly ChartRenderContext? _secondary;

    internal ChartProxy(ChartRenderContext? ctx, ChartRenderContext? secondary)
    {
        _ctx = ctx;
        _secondary = secondary;
    }

    /// <summary>False until the chart's first layout (no scales to project through yet).</summary>
    public bool IsValid => _ctx is not null;

    /// <summary>True when the chart resolved a secondary y-axis this layout.</summary>
    public bool HasSecondaryYAxis => _secondary is not null;

    public Rect PlotRect => _ctx?.PlotRect ?? Rect.Zero;

    /// <summary>Screen x of a data x value (NaN while invalid).</summary>
    public float PositionX(ChartValue x)
    {
        return _ctx?.MapX(x) ?? float.NaN;
    }

    /// <summary>Screen y of a data y value; <paramref name="secondary" /> = the opposite-side axis.</summary>
    public float PositionY(ChartValue y, bool secondary = false)
    {
        var ctx = secondary ? _secondary : _ctx;
        return ctx?.MapY(y) ?? float.NaN;
    }

    public Offset Position(ChartValue x, ChartValue y, bool secondaryY = false)
    {
        return new Offset(PositionX(x), PositionY(y, secondaryY));
    }

    /// <summary>
    ///     x-domain magnitude under a screen x: the numeric value, seconds for a time axis, or a
    ///     band index for categories (NaN while invalid).
    /// </summary>
    public double XValueAt(float screenX)
    {
        if (_ctx is null || _ctx.PlotRect.Width <= 0f) return double.NaN;
        var t = (screenX - _ctx.PlotRect.X) / _ctx.PlotRect.Width;
        return _ctx.XScale.NumericAt(t);
    }

    /// <summary>y-domain magnitude under a screen y (screen y grows downward, so the axis inverts).</summary>
    public double YValueAt(float screenY, bool secondary = false)
    {
        var ctx = secondary ? _secondary : _ctx;
        if (ctx is null || ctx.PlotRect.Height <= 0f) return double.NaN;
        var t = (ctx.PlotRect.Bottom - screenY) / ctx.PlotRect.Height;
        return ctx.YScale.NumericAt(t);
    }
}
