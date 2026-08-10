using System.Globalization;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Charts.Rendering;
using Zigote.UI.Charts.Scales;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Host;

namespace Zigote.UI.Charts;

/// <summary>
///     The chart container: an ordered, composable list of <see cref="ChartMark" />s sharing two
///     auto-inferred (or explicitly pinned) scales, with auto axes, grid, legend,
///     hover lollipop + tooltip, and an entrance animation.
///     <para>
///         Retained like every Zigote widget: build it once, mutate <see cref="Marks" /> or the data
///         behind them, then call <see cref="InvalidateData" />. Composes anywhere a widget goes —
///         cards, splits, HUDs — and marks compose inside it (bars + line + rule overlay naturally).
///     </para>
///     <code>
///     new Chart {
///         Marks = {
///             BarMark.Of(sales, d => d.Month, d => d.Revenue),
///             RuleMark y-target, LineMark.Of(...), …
///         },
///     }
///     </code>
/// </summary>
public class Chart : Widget
{
    public const float DefaultWidth = 420f;
    public const float DefaultHeight = 260f;

    private const float LegendRowHeight = 18f;

    private readonly List<LegendEntry> _legend = [];

    // Chart-owned hover registry, reused across relayouts (capacity persists) and rebuilt LAZILY on
    // the first hover/tap after a data change — a live chart that is never hovered skips the whole
    // per-invalidate CollectInteractive pass, the dominant resolve-path allocation.
    private readonly List<ChartDataPoint> _hoverPoints = [];
    private bool _hoverRegistryDirty = true;

    // Interaction state that survives live data updates: the last hover position (re-resolved after
    // each relayout while the pointer is over the plot) and the tap-pinned column's data x.
    private Offset? _hoverPos;
    private ChartValue? _pinnedX;

    // ── Scroll / zoom windows (see AxisWindow) ─────────────────────────────────
    private readonly AxisWindow _xWin = new() { StickToEnd = true };
    private readonly AxisWindow _yWin = new();
    private float _animTime;
    private ChartRenderContext? _ctx;
    private ChartRenderContext? _ctxSecondary;
    private Offset _cursor;
    private float _dataAnimTime;
    private int _dataEpoch;
    private float _dataProgress = 1f;
    private Offset _dragStartPoint;
    private double _dragStartX, _dragStartY;
    private bool _hasSecondaryAxis;
    private bool _hoverFromRegion;
    private Constraints _lastConstraints;
    private bool _layoutDirty = true;
    private Size _measured;
    private bool _pressed, _dragging;
    private float _progress = 1f;

    private (double Min, double Max)? _selectedRange;

    // ── X range selection ──────────────────────────────────────────────────────
    private bool _selecting;
    private float _selectStartPx, _selectEndPx;
    private ThemeData _theme = ThemeData.Dark;
    private Ticker? _ticker;

    /// <summary>
    ///     Marks paint in list order (first = back). Mutate freely, then
    ///     <see cref="InvalidateData" />.
    /// </summary>
    public List<ChartMark> Marks { get; } = [];

    /// <summary>Pin the x scale explicitly (e.g. a <see cref="LogScale" />); null = infer from the data.</summary>
    public ChartScale? XScale { get; set; }

    public ChartScale? YScale { get; set; }

    /// <summary>Pin the secondary (opposite-side) y scale; null = infer from secondary-axis marks.</summary>
    public ChartScale? YScale2 { get; set; }

    public ChartAxis XAxis { get; } = new() { ShowGrid = false };
    public ChartAxis YAxis { get; } = new();

    /// <summary>Config for the secondary y-axis (used only when a mark sets UseSecondaryYAxis).</summary>
    public ChartAxis YAxis2 { get; } = new() { ShowGrid = false };

    public ChartYAxisSide YAxisSide { get; set; } = ChartYAxisSide.Trailing;
    public ChartLegendPosition LegendPosition { get; set; } = ChartLegendPosition.Auto;

    /// <summary>Explicit series palette; null = the theme's system-color palette.</summary>
    public ChartPalette? Palette { get; set; }

    /// <summary>
    ///     Explicit theme override. Needed when the chart is driven outside the widget tree (e.g.
    ///     embedded in the immediate-mode debug menu), where no ThemeProvider ancestor exists;
    ///     null = resolve from the build context as usual.
    /// </summary>
    public ThemeData? Theme { get; set; }

    /// <summary>Optional plot-area fill behind the grid.</summary>
    public Color? PlotBackground { get; set; }

    /// <summary>Master switch for hover crosshair + tooltip + tap callbacks.</summary>
    public bool Interactive { get; set; } = true;

    /// <summary>
    ///     Show the built-in tooltip while hovering (turn off to draw your own via
    ///     <see cref="OnHoverChanged" />).
    /// </summary>
    public bool ShowTooltip { get; set; } = true;

    /// <summary>
    ///     Tap pins the hovered column's overlay/tooltip so it survives pointer exit and live data
    ///     updates (the pin is anchored by data x and re-resolved each relayout). Tap the same
    ///     column — or an empty spot in the plot — to unpin. See <see cref="PinnedHover" />.
    /// </summary>
    public bool PinOnTap { get; set; } = true;

    /// <summary>Play the entrance animation when the chart attaches (and on <see cref="AnimateIn" />).</summary>
    public bool Animated { get; set; } = true;

    public float AnimationDuration { get; set; } = 0.55f;

    /// <summary>Morph marks smoothly when <see cref="InvalidateData" /> is called with animate: true.</summary>
    public bool AnimateDataUpdates { get; set; } = true;

    public float DataAnimationDuration { get; set; } = 0.35f;

    /// <summary>
    ///     Pan the x axis (scrollable-axes model): the chart shows
    ///     <see cref="VisibleXDomainLength" /> worth of the domain and the rest is reachable by
    ///     dragging the plot or horizontal trackpad scroll. Continuous x scales only.
    /// </summary>
    public bool ScrollableX { get; set; }

    /// <summary>
    ///     Visible x window in domain units: a numeric span, seconds for a time axis, or a category
    ///     count for a band axis. Null (or a window covering everything) disables scrolling.
    /// </summary>
    public double? VisibleXDomainLength { get; set; }

    /// <summary>Pan the y axis, mirroring <see cref="ScrollableX" /> (continuous y scales only).</summary>
    public bool ScrollableY { get; set; }

    /// <summary>Visible y window in domain units (see <see cref="VisibleXDomainLength" />).</summary>
    public double? VisibleYDomainLength { get; set; }

    /// <summary>
    ///     Zoom the x axis with modifier + wheel (⌘/Ctrl-scroll) around the cursor, and via
    ///     <see cref="ZoomBy" /> (the seam a pinch gesture maps onto once the engine surfaces one).
    /// </summary>
    public bool ZoomableX { get; set; }

    /// <summary>Zoom the y axis (see <see cref="ZoomableX" />).</summary>
    public bool ZoomableY { get; set; }

    /// <summary>Smallest zoomed window as a fraction of the full extent (default 2%).</summary>
    public double MinVisibleFraction { get; set; } = 0.02;

    /// <summary>Thin position indicator alongside the plot while the chart is scrollable/zoomed.</summary>
    public bool ShowScrollIndicator { get; set; } = true;

    /// <summary>
    ///     Start of the visible x window in domain units. New charts (and charts that were left
    ///     scrolled to the end) follow the newest data automatically; setting this detaches that.
    /// </summary>
    public double ScrollOffsetX
    {
        get => _xWin.CurrentOffset;
        set
        {
            _xWin.SetOffset(value);
            MarkNeedsLayout();
        }
    }

    /// <summary>Start of the visible y window in domain units.</summary>
    public double ScrollOffsetY
    {
        get => _yWin.CurrentOffset;
        set
        {
            _yWin.SetOffset(value);
            MarkNeedsLayout();
        }
    }

    public Action<ChartHoverInfo?>? OnHoverChanged { get; set; }

    /// <summary>Fires on click with the hovered data points.</summary>
    public Action<ChartHoverInfo>? OnPointTap { get; set; }

    // ── X range selection (the chart x-selection model) ────────────────────────

    /// <summary>
    ///     Enable drag-to-select an x range: dragging the plot paints a highlighted band and reports
    ///     the selected domain interval. A plain click clears it. Mutually exclusive with drag-pan —
    ///     enable one or the other on a given chart.
    /// </summary>
    public bool EnableXSelection { get; set; }

    /// <summary>Fires as the selection changes (null when cleared). Values are x-domain magnitudes.</summary>
    public Action<(double Min, double Max)?>? OnXRangeSelected { get; set; }

    /// <summary>The current selected x-domain interval, or null. Assigning repaints the band.</summary>
    public (double Min, double Max)? SelectedXRange
    {
        get => _selectedRange;
        set
        {
            _selectedRange = value;
            MarkNeedsPaint();
        }
    }

    /// <summary>Annotations pinned to data coordinates, painted above the marks.</summary>
    public List<ChartAnnotation> Annotations { get; } = [];

    /// <summary>Accessible description; defaults to a series summary.</summary>
    public string? SemanticsLabel { get; set; }

    // Read-back for tests and advanced composition. The backing lists are reused across relayouts —
    // a scrolling chart re-ticks every pan step, so tick lists never reallocate on that warm path.
    private readonly List<ChartTick> _xTicks = [];
    private readonly List<ChartTick> _yTicks = [];
    private readonly List<ChartTick> _y2Ticks = [];
    public Rect PlotRect { get; private set; }
    public IReadOnlyList<ChartTick> XTicks => _xTicks;

    public IReadOnlyList<ChartTick> YTicks => _yTicks;

    public IReadOnlyList<LegendEntry> LegendEntries => _legend;
    public ChartHoverInfo? CurrentHover { get; private set; }

    /// <summary>
    ///     The tap-pinned column (<see cref="PinOnTap" />): persists across pointer exit and live
    ///     data updates, re-resolved by its data x each relayout and dropped when that x leaves the
    ///     data (e.g. scrolls out of a live window).
    /// </summary>
    public ChartHoverInfo? PinnedHover { get; private set; }

    /// <summary>Fired when the pinned column changes from a user tap or <see cref="ClearPin" />.</summary>
    public Action<ChartHoverInfo?>? OnPinChanged { get; set; }

    public ChartScale? ResolvedXScale => _ctx?.XScale;
    public ChartScale? ResolvedYScale => _ctx?.YScale;
    public ChartScale? ResolvedSecondaryYScale => _ctxSecondary?.YScale;
    public IReadOnlyList<ChartTick> SecondaryYTicks => _y2Ticks;

    /// <summary>
    ///     Data↔screen conversion over the laid-out scales (Swift Charts' ChartProxy) for custom
    ///     overlays and hit resolution. Invalid until the chart's first layout.
    /// </summary>
    public ChartProxy Proxy => new(_ctx, _ctxSecondary);

    /// <summary>
    ///     Custom overlay painted above the marks and annotations, clipped to the plot (the
    ///     chartOverlay analogue). Assign once; invoked every paint with the live
    ///     <see cref="ChartProxy" />, so drawings track scroll, zoom, and data morphs. Hot path —
    ///     keep the callback allocation-free (route per-frame text through <c>CachedText</c>).
    /// </summary>
    public Action<PaintList, ChartProxy>? OverlayPainter { get; set; }

    private float IndicatorSpaceX => _xWin.Active && ShowScrollIndicator ? 7f : 0f;
    private float IndicatorSpaceY => _yWin.Active && ShowScrollIndicator ? 6f : 0f;

    /// <summary>Snap the x window to the newest data and keep following it as data grows.</summary>
    public void ScrollToEnd()
    {
        _xWin.StickToEnd = true;
        MarkNeedsLayout();
    }

    /// <summary>
    ///     Zoom the enabled axes by <paramref name="factor" /> (&gt;1 zooms in) keeping the domain
    ///     point under <paramref name="focus" /> (screen coords; defaults to the plot centre)
    ///     stationary. This is the entry point a pinch gesture drives once available.
    /// </summary>
    public void ZoomBy(double factor, Offset? focus = null)
    {
        if (factor <= 0 || (!ZoomableX && !ZoomableY)) return;
        var f = focus ?? new Offset(
            PlotRect.X + PlotRect.Width / 2f,
            PlotRect.Y + PlotRect.Height / 2f
        );
        if (ZoomableX) _xWin.ApplyZoom(factor, XFocusFraction(f.X), MinVisibleFraction);
        if (ZoomableY) _yWin.ApplyZoom(factor, YFocusFraction(f.Y), MinVisibleFraction);
        MarkNeedsLayout();
    }

    /// <summary>Reset any zoom back to the base visible window.</summary>
    public void ResetZoom()
    {
        _xWin.Zoom = 1.0;
        _yWin.Zoom = 1.0;
        MarkNeedsLayout();
    }

    private float XFocusFraction(float px)
    {
        return PlotRect.Width <= 0
            ? 0.5f
            : Math.Clamp((px - PlotRect.X) / PlotRect.Width, 0f, 1f);
    }

    // Y screen grows downward while the domain grows upward, so the fraction is inverted.
    private float YFocusFraction(float py)
    {
        return PlotRect.Height <= 0
            ? 0.5f
            : Math.Clamp(1f - (py - PlotRect.Y) / PlotRect.Height, 0f, 1f);
    }

    /// <summary>
    ///     Call after mutating mark data/config so the chart re-resolves scales and geometry.
    ///     With <paramref name="animate" /> the marks morph smoothly from their previous values
    ///     (bars grow/shrink, lines bend, sectors re-sweep) instead of snapping.
    /// </summary>
    public void InvalidateData(bool animate = false)
    {
        if (animate) AnimateDataUpdate();
        MarkNeedsLayout();
    }

    /// <summary>Begin a smooth morph from the currently-shown values to the (new) data.</summary>
    public void AnimateDataUpdate()
    {
        if (!AnimateDataUpdates) return;
        _dataEpoch++;
        _dataAnimTime = 0f;
        _dataProgress = 0f;
        EnsureTicker().Start();
        MarkNeedsPaint();
    }

    /// <summary>Restart the entrance animation.</summary>
    public void AnimateIn()
    {
        _animTime = 0f;
        _progress = 0f;
        EnsureTicker().Start();
        MarkNeedsPaint();
    }

    private Ticker EnsureTicker()
    {
        return _ticker ??= new Ticker(OnTick);
    }

    /// <summary>
    ///     Advance this chart's animations (entrance, data morph, scroll easing) by
    ///     <paramref name="dt" /> seconds without the global ticker — deterministic stepping for
    ///     headless/manual control and tests. No-op semantics match a real tick.
    /// </summary>
    public void AdvanceAnimation(float dt)
    {
        OnTick(dt);
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        EnsureTicker();
        if (Animated) AnimateIn();
    }

    public override void Detach()
    {
        _ticker?.Dispose();
        _ticker = null;
        base.Detach();
    }

    private void OnTick(float dt)
    {
        var busy = false;

        if (_progress < 1f)
        {
            _animTime += dt;
            _progress = AnimationDuration <= 0f
                ? 1f
                : Math.Clamp(_animTime / AnimationDuration, 0f, 1f);
            busy |= _progress < 1f;
            MarkNeedsPaint();
        }

        if (_dataProgress < 1f)
        {
            _dataAnimTime += dt;
            _dataProgress = DataAnimationDuration <= 0f
                ? 1f
                : Math.Clamp(_dataAnimTime / DataAnimationDuration, 0f, 1f);
            busy |= _dataProgress < 1f;
            MarkNeedsPaint();
        }

        // Eased approach toward the wheel-scroll targets (drag pans set the offset directly).
        var scrolled = _xWin.Step(dt) | _yWin.Step(dt);
        if (scrolled)
        {
            busy = true;
            MarkNeedsLayout();
        }

        if (!busy) _ticker?.Stop();
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Image;
        config.Label = SemanticsLabel ?? BuildSemanticsSummary();
    }

    private string BuildSemanticsSummary()
    {
        if (_legend.Count > 0)
            return
                $"Chart with {_legend.Count} series: {string.Join(", ", _legend.Select(e => e.Label))}";
        return Marks.Count == 0 ? "Empty chart" : "Chart";
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            base.DebugStateHash(),
            _progress,
            CurrentHover?.Points.Count ?? -1,
            Marks.Count
        );
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    public override void MarkNeedsLayout()
    {
        _layoutDirty = true;
        base.MarkNeedsLayout();
    }

    public override Size Measure(Constraints c)
    {
        var theme = Theme ?? ThemeProvider.Of(BuildContext.Current);
        if (!ReferenceEquals(theme, _theme))
        {
            _theme = theme;
            _layoutDirty = true; // colours + label metrics change with the theme
        }

        if (!c.Equals(_lastConstraints))
        {
            _lastConstraints = c;
            _layoutDirty = true;
        }

        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : DefaultWidth;
        var h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : DefaultHeight;
        _measured = c.Constrain(new Size(w, h));
        return _measured;
    }

    public override void Layout(Offset origin)
    {
        var bounds = new Rect(
            origin.X,
            origin.Y,
            _measured.Width,
            _measured.Height
        );

        // Change-gate the (potentially expensive) domain resolve + geometry rebuild: a chart in a
        // scrolling/animated page is asked to lay out every frame, but its scales/geometry only
        // change when the data, size, position, theme, or scroll/zoom window did. When nothing
        // relevant changed this is a no-op — the entrance/data-morph animations drive Paint, not
        // Layout, so they never hit this path.
        if (!_layoutDirty && bounds == Bounds) return;

        Bounds = bounds;
        RebuildLayout();
        _layoutDirty = false;
    }

    private bool HasCartesianMarks()
    {
        foreach (var m in Marks)
            if (!m.IsPolar)
                return true;
        return false;
    }

    private void RebuildLayout()
    {
        var palette = Palette ?? ChartPalette.For(_theme);

        _hasSecondaryAxis = false;
        foreach (var m in Marks)
            if (m is { UseSecondaryYAxis: true, IsPolar: false })
            {
                _hasSecondaryAxis = true;
                break;
            }

        // 1. Build the shared domain: user-pinned scales reset, marks accumulate, then finalize.
        XScale?.Reset();
        YScale?.Reset();
        YScale2?.Reset();
        var domain = new ChartDomain {
            XScale = XScale,
            YScale = YScale,
            YScale2 = YScale2,
            DataEpoch = _dataEpoch,
        };
        for (var i = 0; i < Marks.Count; i++)
        {
            Marks[i].MarkIndex = i;
            Marks[i].IncludeDomain(domain);
        }

        var xScale = domain.XScale ?? new LinearScale();
        var yScale = domain.YScale ?? new LinearScale();
        xScale.FinalizeDomain();
        yScale.FinalizeDomain();
        _xWin.Configure(
            ScrollableX,
            ZoomableX,
            VisibleXDomainLength,
            xScale,
            MinVisibleFraction
        );
        _yWin.Configure(
            ScrollableY,
            ZoomableY,
            VisibleYDomainLength,
            yScale,
            MinVisibleFraction
        );

        var yScale2 = domain.YScale2;
        yScale2?.FinalizeDomain();

        var cartesian = HasCartesianMarks();
        var showXAxis = XAxis.Show && cartesian;
        var showYAxis = YAxis.Show && cartesian;
        var showYAxis2 = _hasSecondaryAxis && YAxis2.Show && yScale2 is not null;

        // 2. Ticks (targets derived from the widget extent; margins barely change the counts).
        var caption = _theme.FontSizeCaption;
        var xTarget = XAxis.TickTarget ?? Math.Clamp((int)(_measured.Width / 90f), 2, 12);
        var yTarget = YAxis.TickTarget ?? Math.Clamp((int)(_measured.Height / 46f), 2, 10);
        BuildAxisTicks(
            xScale,
            XAxis,
            xTarget,
            _xTicks,
            showXAxis
        );
        BuildAxisTicks(
            yScale,
            YAxis,
            yTarget,
            _yTicks,
            showYAxis
        );
        if (showYAxis2)
            BuildAxisTicks(
                yScale2!,
                YAxis2,
                yTarget,
                _y2Ticks,
                true
            );
        else _y2Ticks.Clear();

        // 3. Series slots + legend entries (colors depend only on slot order, not geometry).
        var seed = new ChartRenderContext {
            PlotRect = Bounds,
            XScale = xScale,
            YScale = yScale,
            Theme = _theme,
            Palette = palette,
        };
        foreach (var m in Marks) m.CollectSeries(seed);
        _legend.Clear();
        foreach (var m in Marks) m.CollectLegend(seed, _legend);
        DedupeLegend();

        // 4. Margins around the plot.
        float top = 8f, bottom = 4f, leading = 8f, trailing = 8f;

        var legendVisible = LegendPosition switch {
            ChartLegendPosition.Hidden => false,
            ChartLegendPosition.Auto => _legend.Count >= 2,
            _ => _legend.Count > 0,
        };
        var legendHeight = legendVisible ? LegendRowHeight * CountLegendRows() : 0f;
        if (legendVisible && LegendPosition != ChartLegendPosition.Bottom) top += legendHeight + 6f;
        if (legendVisible && LegendPosition == ChartLegendPosition.Bottom)
            bottom += legendHeight + 8f;

        if (YAxis.Title is not null) top += caption + 6f;

        // Primary y labels on YAxisSide; secondary y labels on the opposite side.
        var primaryLeading = YAxisSide == ChartYAxisSide.Leading;
        if (showYAxis && YAxis.ShowLabels && YTicks.Count > 0)
        {
            var side = LabelBandWidth(YTicks, caption);
            if (primaryLeading) leading += side;
            else trailing += side;
        }

        if (showYAxis2 && YAxis2.ShowLabels && SecondaryYTicks.Count > 0)
        {
            var side = LabelBandWidth(SecondaryYTicks, caption);
            if (primaryLeading) trailing += side;
            else leading += side;
        }

        // Y scroll indicator sits on whichever side has no primary labels.
        if (IndicatorSpaceY > 0f)
        {
            if (primaryLeading) trailing += IndicatorSpaceY;
            else leading += IndicatorSpaceY;
        }

        bottom += IndicatorSpaceX;
        if (showXAxis && XAxis.ShowLabels && XTicks.Count > 0) bottom += caption * 1.2f + 8f;
        if (XAxis.Title is not null) bottom += caption + 6f;

        var plotW = MathF.Max(8f, _measured.Width - leading - trailing);
        var plotH = MathF.Max(8f, _measured.Height - top - bottom);
        PlotRect = new Rect(
            Bounds.X + leading,
            Bounds.Y + top,
            plotW,
            plotH
        );

        // Now the plot rect is known, apply the y window (needs FullExtent already finalized above).

        // 5. Final context(s) + interactive registry.
        _ctx = new ChartRenderContext {
            PlotRect = PlotRect,
            XScale = xScale,
            YScale = yScale,
            Theme = _theme,
            Palette = palette,
            HoverPoints = _hoverPoints,
        };
        _ctxSecondary = showYAxis2 ? _ctx.WithYScale(yScale2!) : null;

        foreach (var m in Marks) m.CollectSeries(_ctx);

        // A relayout invalidates hover geometry; the registry is re-collected on the next query.
        _hoverPoints.Clear();
        _hoverRegistryDirty = true;

        // Keep interaction alive across live data updates: re-resolve the hover under the last
        // pointer position and re-anchor the pinned column by its data x. Both are silent (no
        // OnHoverChanged/OnPinChanged) — user callbacks must not run mid-layout.
        CurrentHover = CurrentHover is not null && _hoverPos is { } hp ? ResolveHover(hp) : null;
        ResolvePinned();
    }

    /// <summary>Re-resolve the pinned column against the current layout/data (silent).</summary>
    private void ResolvePinned()
    {
        if (_pinnedX is not { } px)
        {
            PinnedHover = null;
            return;
        }

        EnsureHoverRegistry();
        List<ChartDataPoint>? cluster = null;
        for (var i = 0; i < _hoverPoints.Count; i++)
            if (_hoverPoints[i].X == px)
                (cluster ??= []).Add(_hoverPoints[i]);

        if (cluster is null)
        {
            // The pinned x left the data (e.g. scrolled out of a live window) — drop the pin.
            _pinnedX = null;
            PinnedHover = null;
            return;
        }

        cluster.Sort(static (a, b) => a.ScreenY.CompareTo(b.ScreenY));
        PinnedHover = new ChartHoverInfo {
            XLabel = FormatHoverX(px),
            X = px,
            Points = cluster,
        };
    }

    private void TogglePin(ChartHoverInfo? hover)
    {
        if (hover is null || hover.Points.Count == 0 || (_pinnedX is { } px && px == hover.X))
        {
            ClearPin();
            return;
        }

        _pinnedX = hover.X;
        PinnedHover = hover;
        OnPinChanged?.Invoke(hover);
        MarkNeedsPaint();
    }

    /// <summary>Remove the tap-pinned overlay (no-op when nothing is pinned).</summary>
    public void ClearPin()
    {
        if (_pinnedX is null) return;
        _pinnedX = null;
        PinnedHover = null;
        OnPinChanged?.Invoke(null);
        MarkNeedsPaint();
    }

    /// <summary>Collect the marks' interactive points on demand (first hover/tap after a relayout).</summary>
    private void EnsureHoverRegistry()
    {
        if (!_hoverRegistryDirty || _ctx is null) return;
        _hoverRegistryDirty = false;
        _hoverPoints.Clear();
        foreach (var m in Marks) m.CollectInteractive(ContextFor(m));
    }

    /// <summary>The render context whose y-scale this mark binds to (primary or secondary).</summary>
    private ChartRenderContext ContextFor(ChartMark mark)
    {
        return mark is { UseSecondaryYAxis: true, IsPolar: false } && _ctxSecondary is not null
            ? _ctxSecondary
            : _ctx!;
    }

    /// <summary>Automatic scale ticks, or the axis's pinned <see cref="ChartAxis.TickValues" />.</summary>
    private static void BuildAxisTicks(ChartScale scale, ChartAxis axis, int target,
        List<ChartTick> into, bool show)
    {
        if (!show)
        {
            into.Clear();
            return;
        }

        if (axis.TickValues is { } values) scale.BuildTicksFor(values, axis.Formatter, into);
        else scale.BuildTicksInto(target, axis.Formatter, into);
    }

    private static float LabelBandWidth(IReadOnlyList<ChartTick> ticks, float caption)
    {
        var maxW = 0f;
        foreach (var t in ticks) maxW = MathF.Max(maxW, TextMeasure.Width(t.Label, caption));
        return maxW + 10f;
    }

    private void DedupeLegend()
    {
        var seen = new HashSet<string>();
        var write = 0;
        for (var read = 0; read < _legend.Count; read++)
            if (seen.Add(_legend[read].Label))
                _legend[write++] = _legend[read];
        _legend.RemoveRange(write, _legend.Count - write);
    }

    private float LegendEntryWidth(LegendEntry e)
    {
        return 8f + 5f + TextMeasure.Width(e.Label, _theme.FontSizeCaption) + 16f;
    }

    private int CountLegendRows()
    {
        var rows = 1;
        var x = 0f;
        var maxW = MathF.Max(40f, _measured.Width - 16f);
        foreach (var e in _legend)
        {
            var w = LegendEntryWidth(e);
            if (x > 0f && x + w > maxW)
            {
                rows++;
                x = 0f;
            }

            x += w;
        }

        return _legend.Count == 0 ? 0 : rows;
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        var ctx = _ctx;
        if (ctx is null || Bounds.Width < 16f || Bounds.Height < 16f) return;

        // Skip charts scrolled fully out of the clip (e.g. off-screen rows of a chart dashboard) —
        // the whole projection/emit cost is the point of a chart's paint, so culling is a big win.
        if (!paint.IsVisible(Bounds)) return;

        var progress = Animated ? Curves.EaseOut(_progress) : 1f;
        var dataProgress = Curves.EaseOut(_dataProgress);
        ctx.Paint = paint;
        ctx.Progress = progress;
        ctx.DataProgress = dataProgress;
        if (_ctxSecondary is not null)
        {
            _ctxSecondary.Paint = paint;
            _ctxSecondary.Progress = progress;
            _ctxSecondary.DataProgress = dataProgress;
        }

        if (PlotBackground is { } bg) paint.AddRect(PlotRect, bg, 4f);

        var cartesian = HasCartesianMarks();
        if (cartesian) PaintGridAndAxes(paint, ctx);
        if (cartesian && _selectedRange is { } sel)
            PaintSelectionBand(
                paint,
                ctx,
                sel.Min,
                sel.Max
            );

        // Marks clip to the plot (slightly padded so edge strokes keep their full width).
        var clip = new Rect(
            PlotRect.X - 1f,
            PlotRect.Y - 4f,
            PlotRect.Width + 2f,
            PlotRect.Height + 8f
        );
        paint.AddClipStart(clip);
        foreach (var m in Marks) m.Paint(ContextFor(m));
        if (cartesian) PaintAnnotations(paint);
        OverlayPainter?.Invoke(paint, Proxy);
        paint.AddClipEnd();

        // Live drag-selection rectangle (drawn above the clip so its label isn't cut).
        if (_selecting) PaintLiveSelection(paint);

        if (cartesian) PaintAxisLabels(paint);
        if (cartesian) PaintScrollIndicator(paint);
        PaintLegend(paint);
        PaintTitles(paint);

        if (Interactive)
        {
            if (PinnedHover is { } pin)
                PaintHover(
                    paint,
                    ctx,
                    pin,
                    true
                );
            if (CurrentHover is { } hover &&
                (PinnedHover is null || !HoverEquals(hover, PinnedHover)))
                PaintHover(paint, ctx, hover);
        }

        ctx.Paint = null;
        if (_ctxSecondary is not null) _ctxSecondary.Paint = null;
    }

    private void PaintGridAndAxes(PaintList paint, ChartRenderContext ctx)
    {
        var plot = PlotRect;
        var grid = _theme.Separator;

        // Indexed loops: foreach over the IReadOnlyList props boxes an enumerator per paint.
        if (YAxis.ShowGrid)
            for (var i = 0; i < _yTicks.Count; i++)
            {
                var t = _yTicks[i];
                var y = plot.Bottom - t.Position * plot.Height;
                if (y < plot.Y - 0.5f || y > plot.Bottom + 0.5f) continue;
                var style = YAxis.TickStyle?.Invoke(t.Value) ?? default;
                if (style.HideGrid) continue;
                paint.AddRect(
                    new Rect(
                        plot.X,
                        y,
                        plot.Width,
                        style.GridWidth > 0f ? style.GridWidth : 1f
                    ),
                    style.GridColor ?? grid
                );
            }

        if (XAxis.ShowGrid)
            for (var i = 0; i < _xTicks.Count; i++)
            {
                var t = _xTicks[i];
                var x = plot.X + t.Position * plot.Width;
                if (x < plot.X - 0.5f || x > plot.Right + 0.5f) continue;
                var style = XAxis.TickStyle?.Invoke(t.Value) ?? default;
                if (style.HideGrid) continue;
                paint.AddRect(
                    new Rect(
                        x,
                        plot.Y,
                        style.GridWidth > 0f ? style.GridWidth : 1f,
                        plot.Height
                    ),
                    style.GridColor ?? grid
                );
            }

        // A slightly stronger zero line when the value axis crosses zero.
        if (ctx.YScale is LinearScale { DomainMin: < 0, DomainMax: > 0 })
        {
            var zero = ctx.MapYNumeric(0);
            paint.AddRect(
                new Rect(
                    plot.X,
                    zero,
                    plot.Width,
                    1f
                ),
                _theme.TextMuted.WithAlpha(0.45f)
            );
        }

        if (XAxis.ShowLine)
            paint.AddRect(
                new Rect(
                    plot.X,
                    plot.Bottom,
                    plot.Width,
                    1f
                ),
                _theme.TextMuted
            );
        if (YAxis.ShowLine)
        {
            var x = YAxisSide == ChartYAxisSide.Trailing ? plot.Right : plot.X - 1f;
            paint.AddRect(
                new Rect(
                    x,
                    plot.Y,
                    1f,
                    plot.Height
                ),
                _theme.TextMuted
            );
        }
    }

    private void PaintAxisLabels(PaintList paint)
    {
        var plot = PlotRect;
        var caption = _theme.FontSizeCaption;
        var color = _theme.TextMuted;

        var primaryLeading = YAxisSide == ChartYAxisSide.Leading;

        if (YAxis.Show && YAxis.ShowLabels)
            for (var i = 0; i < _yTicks.Count; i++)
            {
                var t = _yTicks[i];
                var y = plot.Bottom - t.Position * plot.Height;
                if (y < plot.Y - 2f || y > plot.Bottom + 2f) continue;
                var style = YAxis.TickStyle?.Invoke(t.Value) ?? default;
                if (style.HideLabel) continue;
                var w = TextMeasure.Width(t.Label, caption);
                var x = primaryLeading ? plot.X - 8f - w : plot.Right + 8f;
                paint.AddText(
                    t.Label,
                    x,
                    y + caption * 0.36f,
                    style.LabelColor ?? color,
                    caption
                );
            }

        // Secondary y labels on the opposite side, tinted toward the axis's own series colour.
        if (_ctxSecondary is not null && YAxis2.Show && YAxis2.ShowLabels)
        {
            var sColor = SecondaryAxisColor();
            for (var i = 0; i < _y2Ticks.Count; i++)
            {
                var t = _y2Ticks[i];
                var y = plot.Bottom - t.Position * plot.Height;
                if (y < plot.Y - 2f || y > plot.Bottom + 2f) continue;
                var style = YAxis2.TickStyle?.Invoke(t.Value) ?? default;
                if (style.HideLabel) continue;
                var w = TextMeasure.Width(t.Label, caption);
                var x = primaryLeading ? plot.Right + 8f : plot.X - 8f - w;
                paint.AddText(
                    t.Label,
                    x,
                    y + caption * 0.36f,
                    style.LabelColor ?? sColor,
                    caption
                );
            }
        }

        if (XAxis.Show && XAxis.ShowLabels)
        {
            var baseline = plot.Bottom + 8f + IndicatorSpaceX + caption * 0.8f;
            var lastRight = float.NegativeInfinity;
            for (var i = 0; i < _xTicks.Count; i++)
            {
                var t = _xTicks[i];
                var cx = plot.X + t.Position * plot.Width;
                if (cx < plot.X - 2f || cx > plot.Right + 2f) continue;
                var style = XAxis.TickStyle?.Invoke(t.Value) ?? default;
                if (style.HideLabel) continue;
                var w = TextMeasure.Width(t.Label, caption);
                // Floor the max bound at the min: a label wider than the chart would invert the clamp
                // bounds and Math.Clamp throws on min > max (narrow grid cells / long tick labels).
                var left = Math.Clamp(
                    cx - w / 2f,
                    Bounds.X + 2f,
                    MathF.Max(Bounds.X + 2f, Bounds.Right - w - 2f)
                );
                if (left < lastRight + 6f) continue; // skip colliding labels
                paint.AddText(
                    t.Label,
                    left,
                    baseline,
                    style.LabelColor ?? color,
                    caption
                );
                lastRight = left + w;
            }
        }
    }

    private void PaintScrollIndicator(PaintList paint)
    {
        if (!ShowScrollIndicator) return;
        var plot = PlotRect;

        if (_xWin.Active)
        {
            var track = new Rect(
                plot.X,
                plot.Bottom + 3f,
                plot.Width,
                3f
            );
            paint.AddRect(track, _theme.OnSurface.WithAlpha(0.08f), 1.5f);
            var thumbW = MathF.Max(20f, plot.Width * (float)(_xWin.Len / _xWin.FullSpan));
            var t = _xWin.Max > _xWin.Min
                ? (_xWin.CurrentOffset - _xWin.Min) / (_xWin.Max - _xWin.Min)
                : 0.0;
            var thumbX = plot.X + (plot.Width - thumbW) * (float)Math.Clamp(t, 0, 1);
            paint.AddRect(
                new Rect(
                    thumbX,
                    plot.Bottom + 3f,
                    thumbW,
                    3f
                ),
                _theme.TextMuted.WithAlpha(0.6f),
                1.5f
            );
        }

        if (_yWin.Active)
        {
            // Indicator on whichever side has no primary y labels.
            var ix = YAxisSide == ChartYAxisSide.Trailing ? plot.X - 5f : plot.Right + 2f;
            paint.AddRect(
                new Rect(
                    ix,
                    plot.Y,
                    3f,
                    plot.Height
                ),
                _theme.OnSurface.WithAlpha(0.08f),
                1.5f
            );
            var thumbH = MathF.Max(20f, plot.Height * (float)(_yWin.Len / _yWin.FullSpan));
            var t = _yWin.Max > _yWin.Min
                ? (_yWin.CurrentOffset - _yWin.Min) / (_yWin.Max - _yWin.Min)
                : 0.0;
            // Y domain grows upward, so a larger offset places the thumb higher (smaller screen y).
            var thumbY = plot.Bottom - thumbH - (plot.Height - thumbH) * (float)Math.Clamp(t, 0, 1);
            paint.AddRect(
                new Rect(
                    ix,
                    thumbY,
                    3f,
                    thumbH
                ),
                _theme.TextMuted.WithAlpha(0.6f),
                1.5f
            );
        }
    }

    private Color SecondaryAxisColor()
    {
        // Tint the secondary axis toward the first secondary-bound mark's colour for legibility.
        foreach (var m in Marks)
            if (m is { UseSecondaryYAxis: true, IsPolar: false })
                return _ctxSecondary!.ColorFor(m.Name ?? "", m.Color, m.MarkIndex);
        return _theme.TextMuted;
    }

    // ── Annotations + selection ────────────────────────────────────────────────

    private void PaintAnnotations(PaintList paint)
    {
        if (Annotations.Count == 0) return;
        var caption = _theme.FontSizeCaption;
        var plot = PlotRect;

        foreach (var a in Annotations)
        {
            if (string.IsNullOrEmpty(a.Text) && !a.ShowMarker) continue;
            var ctx = a.UseSecondaryYAxis && _ctxSecondary is not null ? _ctxSecondary : _ctx!;

            var ax = a.X is { } xv ? ctx.MapX(xv) : plot.X + plot.Width;
            var ay = a.Y is { } yv ? ctx.MapY(yv) : plot.Y;
            var hasPoint = a.X is not null && a.Y is not null;

            var color = a.Color ?? _theme.TextSecondary;
            var w = TextMeasure.Width(a.Text, caption);
            var (tx, ty) = a.Placement switch {
                ChartAnnotationPlacement.Above => (ax - w / 2f, ay - 8f - caption),
                ChartAnnotationPlacement.Below => (ax - w / 2f, ay + 10f),
                ChartAnnotationPlacement.Leading => (ax - w - 8f, ay - caption / 2f),
                ChartAnnotationPlacement.Trailing => (ax + 8f, ay - caption / 2f),
                _ => (ax - w / 2f, ay - caption / 2f), // Over
            };
            // When the label is wider (or taller) than the plot, the upper clamp bound can fall below the
            // lower one — Math.Clamp throws on min > max. Keep max ≥ min so an oversized label just pins to
            // the top-left of the plot instead of crashing (narrow grid cells, tiny plots).
            tx = Math.Clamp(tx, plot.X + 2f, MathF.Max(plot.X + 2f, plot.Right - w - 2f));
            ty = Math.Clamp(ty, plot.Y + 2f, MathF.Max(plot.Y + 2f, plot.Bottom - caption - 2f));

            if (a.ShowMarker && hasPoint)
            {
                paint.AddRect(
                    new Rect(
                        ax - 3f,
                        ay - 3f,
                        6f,
                        6f
                    ),
                    color,
                    3f
                );
                paint.AddRect(
                    new Rect(
                        ax - 3.5f,
                        ay - 3.5f,
                        7f,
                        7f
                    ),
                    _theme.Background.WithAlpha(0.6f),
                    3.5f
                );
                paint.AddRect(
                    new Rect(
                        ax - 3f,
                        ay - 3f,
                        6f,
                        6f
                    ),
                    color,
                    3f
                );
            }

            if (a.Background is { } bgc)
                paint.AddRect(
                    new Rect(
                        tx - 5f,
                        ty + caption * 0.15f - caption,
                        w + 10f,
                        caption + 6f
                    ),
                    bgc,
                    4f
                );
            if (!string.IsNullOrEmpty(a.Text))
                paint.AddText(
                    a.Text,
                    tx,
                    ty + caption * 0.9f,
                    color,
                    caption,
                    fontWeight: FontWeight.W600
                );
        }
    }

    private void PaintSelectionBand(PaintList paint, ChartRenderContext ctx, double min, double max)
    {
        var x0 = Math.Clamp(ctx.MapXNumeric(min), PlotRect.X, PlotRect.Right);
        var x1 = Math.Clamp(ctx.MapXNumeric(max), PlotRect.X, PlotRect.Right);
        if (x1 < x0) (x0, x1) = (x1, x0);
        paint.AddRect(
            new Rect(
                x0,
                PlotRect.Y,
                MathF.Max(1f, x1 - x0),
                PlotRect.Height
            ),
            _theme.Primary.WithAlpha(0.14f)
        );
        paint.AddRect(
            new Rect(
                x0,
                PlotRect.Y,
                1f,
                PlotRect.Height
            ),
            _theme.Primary.WithAlpha(0.7f)
        );
        paint.AddRect(
            new Rect(
                x1,
                PlotRect.Y,
                1f,
                PlotRect.Height
            ),
            _theme.Primary.WithAlpha(0.7f)
        );
    }

    private void PaintLiveSelection(PaintList paint)
    {
        var x0 = Math.Min(_selectStartPx, _selectEndPx);
        var x1 = Math.Max(_selectStartPx, _selectEndPx);
        x0 = Math.Clamp(x0, PlotRect.X, PlotRect.Right);
        x1 = Math.Clamp(x1, PlotRect.X, PlotRect.Right);
        paint.AddRect(
            new Rect(
                x0,
                PlotRect.Y,
                MathF.Max(1f, x1 - x0),
                PlotRect.Height
            ),
            _theme.Primary.WithAlpha(0.18f)
        );
        paint.AddRect(
            new Rect(
                x0,
                PlotRect.Y,
                1f,
                PlotRect.Height
            ),
            _theme.Primary.WithAlpha(0.8f)
        );
        paint.AddRect(
            new Rect(
                x1,
                PlotRect.Y,
                1f,
                PlotRect.Height
            ),
            _theme.Primary.WithAlpha(0.8f)
        );
    }

    private void PaintTitles(PaintList paint)
    {
        var caption = _theme.FontSizeCaption;
        if (YAxis.Title is not null)
        {
            var y = PlotRect.Y - 10f;
            var x = YAxisSide == ChartYAxisSide.Trailing
                ? PlotRect.Right - TextMeasure.Width(YAxis.Title, caption)
                : PlotRect.X;
            paint.AddText(
                YAxis.Title,
                x,
                y,
                _theme.TextSecondary,
                caption
            );
        }

        if (_ctxSecondary is not null && YAxis2.Title is not null)
        {
            var y = PlotRect.Y - 10f;
            var x = YAxisSide == ChartYAxisSide.Trailing
                ? PlotRect.X
                : PlotRect.Right - TextMeasure.Width(YAxis2.Title, caption);
            paint.AddText(
                YAxis2.Title,
                x,
                y,
                SecondaryAxisColor(),
                caption
            );
        }

        if (XAxis.Title is not null)
        {
            var w = TextMeasure.Width(XAxis.Title, caption);
            var x = PlotRect.X + (PlotRect.Width - w) / 2f;
            paint.AddText(
                XAxis.Title,
                x,
                Bounds.Bottom - 6f,
                _theme.TextSecondary,
                caption
            );
        }
    }

    private void PaintLegend(PaintList paint)
    {
        var visible = LegendPosition switch {
            ChartLegendPosition.Hidden => false,
            ChartLegendPosition.Auto => _legend.Count >= 2,
            _ => _legend.Count > 0,
        };
        if (!visible) return;

        var caption = _theme.FontSizeCaption;
        var rows = CountLegendRows();
        var y = LegendPosition == ChartLegendPosition.Bottom
            ? Bounds.Bottom - rows * LegendRowHeight - 2f
            : Bounds.Y + 4f;

        var x = Bounds.X + 8f;
        var maxRight = Bounds.Right - 8f;
        foreach (var e in _legend)
        {
            var w = LegendEntryWidth(e);
            if (x > Bounds.X + 8f && x + w > maxRight)
            {
                x = Bounds.X + 8f;
                y += LegendRowHeight;
            }

            paint.AddRect(
                new Rect(
                    x,
                    y + LegendRowHeight / 2f - 4f,
                    8f,
                    8f
                ),
                e.Color,
                4f
            );
            paint.AddText(
                e.Label,
                x + 13f,
                y + LegendRowHeight / 2f + caption * 0.36f,
                _theme.TextSecondary,
                caption
            );
            x += w;
        }
    }

    private void PaintHover(PaintList paint, ChartRenderContext ctx, ChartHoverInfo hover,
        bool pinned = false)
    {
        if (hover.Points.Count == 0) return;
        var plot = PlotRect;

        // Crosshair + point rings (cartesian hovers only — sector hits highlight via tooltip alone).
        // A pinned column always draws its crosshair (accent-tinted, with a pin dot at the top).
        if (pinned || !_hoverFromRegion)
        {
            var cx = hover.Points[0].ScreenX;
            paint.AddRect(
                new Rect(
                    cx,
                    plot.Y,
                    1f,
                    plot.Height
                ),
                pinned ? _theme.Primary.WithAlpha(0.65f) : _theme.TextMuted.WithAlpha(0.5f)
            );
            if (pinned)
            {
                var dot = new Rect(
                    cx - 3.5f,
                    plot.Y - 3.5f,
                    7f,
                    7f
                );
                paint.AddRect(dot, _theme.Primary, 3.5f);
            }

            foreach (var p in hover.Points)
            {
                if (p.ScreenY < plot.Y - 2f || p.ScreenY > plot.Bottom + 2f) continue;
                var ring = new Rect(
                    p.ScreenX - 5f,
                    p.ScreenY - 5f,
                    10f,
                    10f
                );
                paint.AddRect(ring, _theme.Background, 5f);
                paint.AddBorder(
                    ring,
                    p.Color,
                    5f,
                    2.5f
                );
            }
        }

        if (!ShowTooltip) return;

        // Tooltip card near the cursor, clamped into the widget.
        var caption = _theme.FontSizeCaption;
        const float pad = 9f;
        const float rowH = 17f;
        var titleW = TextMeasure.Width(hover.XLabel, caption, FontWeight.W600);
        var maxRowW = titleW;
        foreach (var p in hover.Points)
        {
            var label = p.Series.Length > 0 ? p.Series : hover.XLabel;
            var w = 11f + TextMeasure.Width(label, caption) + 14f +
                    TextMeasure.Width(p.ValueLabel, caption, FontWeight.W600);
            maxRowW = MathF.Max(maxRowW, w);
        }

        var cardW = maxRowW + pad * 2f;
        var cardH = pad * 2f + rowH * (hover.Points.Count + 1) - 4f;
        // A pinned card anchors at its column near the top of the plot (the cursor may be gone);
        // a live card follows the cursor.
        var anchorX = pinned ? hover.Points[0].ScreenX + 14f : _cursor.X + 14f;
        var anchorY = pinned ? plot.Y + 6f : _cursor.Y - cardH - 10f;
        // Floor each max bound at its min: a tooltip card larger than the (narrow/short) chart would
        // invert the clamp bounds and Math.Clamp throws on min > max — crashes on hover in a small cell.
        var cardX = Math.Clamp(
            anchorX,
            Bounds.X + 2f,
            MathF.Max(Bounds.X + 2f, Bounds.Right - cardW - 2f)
        );
        var cardY = Math.Clamp(
            anchorY,
            Bounds.Y + 2f,
            MathF.Max(Bounds.Y + 2f, Bounds.Bottom - cardH - 2f)
        );
        var card = new Rect(
            cardX,
            cardY,
            cardW,
            cardH
        );

        paint.AddElevation(card, 6f, Elevation.Z2);
        paint.AddRect(card, _theme.CardRaised, 6f);
        paint.AddBorder(card, _theme.Border, 6f);

        var textY = cardY + pad + caption * 0.8f;
        paint.AddText(
            hover.XLabel,
            cardX + pad,
            textY,
            _theme.TextSecondary,
            caption,
            fontWeight: FontWeight.W600
        );
        foreach (var p in hover.Points)
        {
            textY += rowH;
            var dotY = textY - caption * 0.36f - 4f;
            paint.AddRect(
                new Rect(
                    cardX + pad,
                    dotY,
                    8f,
                    8f
                ),
                p.Color,
                4f
            );
            var label = p.Series.Length > 0 ? p.Series : hover.XLabel;
            paint.AddText(
                label,
                cardX + pad + 11f,
                textY,
                _theme.TextSecondary,
                caption
            );
            var vw = TextMeasure.Width(p.ValueLabel, caption, FontWeight.W600);
            paint.AddText(
                p.ValueLabel,
                cardX + cardW - pad - vw,
                textY,
                _theme.OnSurface,
                caption,
                fontWeight: FontWeight.W600
            );
        }
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    public override void OnPointerMove(Offset point)
    {
        _cursor = point;

        // Live range selection.
        if (_selecting)
        {
            _selectEndPx = point.X;
            UpdateSelectionFromPixels(false);
            MarkNeedsPaint();
            return;
        }

        // Drag-pan the scrollable/zoomed axes; a >3px drag suppresses hover + tap.
        if (_pressed && !EnableXSelection && (_xWin.Active || _yWin.Active))
        {
            var dx = point.X - _dragStartPoint.X;
            var dy = point.Y - _dragStartPoint.Y;
            if (_dragging || MathF.Abs(dx) > 3f || MathF.Abs(dy) > 3f)
            {
                _dragging = true;
                if (_xWin.Active)
                {
                    var perPx = _xWin.Len / Math.Max(1f, PlotRect.Width);
                    _xWin.PanTargetTo(_dragStartX - dx * perPx);
                }

                if (_yWin.Active)
                {
                    // Screen y grows down while the domain grows up: dragging down reveals higher values.
                    var perPx = _yWin.Len / Math.Max(1f, PlotRect.Height);
                    _yWin.PanTargetTo(_dragStartY + dy * perPx);
                }

                if (CurrentHover is not null)
                {
                    CurrentHover = null;
                    OnHoverChanged?.Invoke(null);
                }

                MarkNeedsLayout();
                return;
            }
        }

        if (!Interactive) return;
        _hoverPos = point;
        var hover = ResolveHover(point);
        var changed = !HoverEquals(CurrentHover, hover);
        CurrentHover = hover;
        if (changed)
        {
            OnHoverChanged?.Invoke(hover);
            MarkNeedsPaint();
        }
        else if (hover is not null && ShowTooltip)
        {
            MarkNeedsPaint(); // tooltip follows the cursor
        }
    }

    public override void OnPointerExit()
    {
        _pressed = false;
        _dragging = false;
        _selecting = false;
        _hoverPos = null;
        if (CurrentHover is null) return;
        CurrentHover = null;
        OnHoverChanged?.Invoke(null);
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        _cursor = point;
        _pressed = true;
        _dragging = false;
        _dragStartPoint = point;
        _dragStartX = _xWin.CurrentOffset;
        _dragStartY = _yWin.CurrentOffset;

        if (EnableXSelection && PlotRect.Contains(point.X, point.Y))
        {
            _selecting = true;
            _selectStartPx = _selectEndPx = point.X;
        }
    }

    public override void OnPointerUp(Offset point)
    {
        _cursor = point;

        if (_selecting)
        {
            _selecting = false;
            _selectEndPx = point.X;
            // A near-zero drag is a click → clear the selection.
            if (MathF.Abs(_selectEndPx - _selectStartPx) < 3f)
            {
                if (_selectedRange is not null)
                {
                    _selectedRange = null;
                    OnXRangeSelected?.Invoke(null);
                }
            }
            else
            {
                UpdateSelectionFromPixels(true);
            }

            MarkNeedsPaint();
            return;
        }

        var wasDrag = _dragging;
        _pressed = false;
        _dragging = false;
        if (wasDrag || !Interactive) return;
        var hover = CurrentHover ?? ResolveHover(point);
        if (hover is not null) OnPointTap?.Invoke(hover);
        if (PinOnTap && PlotRect.Contains(point.X, point.Y)) TogglePin(hover);
    }

    private void UpdateSelectionFromPixels(bool commit)
    {
        if (_ctx is null) return;
        var scale = _ctx.XScale;
        // Invert the plot-x mapping to domain magnitudes.
        var f0 = Math.Clamp(
            (Math.Min(_selectStartPx, _selectEndPx) - PlotRect.X) / Math.Max(1f, PlotRect.Width),
            0f,
            1f
        );
        var f1 = Math.Clamp(
            (Math.Max(_selectStartPx, _selectEndPx) - PlotRect.X) / Math.Max(1f, PlotRect.Width),
            0f,
            1f
        );
        var (min, max) = DomainRangeForFractions(scale, f0, f1);
        _selectedRange = (min, max);
        OnXRangeSelected?.Invoke((min, max));
    }

    private static (double Min, double Max) DomainRangeForFractions(ChartScale scale, float f0,
        float f1)
    {
        // Windowed continuous scales expose their view via FullExtent + the applied window; recover
        // the visible [min,max] by probing NormalizeNumeric is unnecessary — use the window bounds.
        var (fullMin, fullMax) = scale.FullExtent;
        // For a windowed scale, fraction 0..1 maps across the *visible* window, which equals the
        // scale's current view. Reconstruct it from two probe inversions.
        var span = fullMax - fullMin;
        // Binary-free inverse: the scale is affine in the visible window, so sample two known points.
        var a = scale.NormalizeNumeric(fullMin);
        var b = scale.NormalizeNumeric(fullMax);
        if (Math.Abs(b - a) < 1e-9)
            return (fullMin + f0 * span, fullMin + f1 * span);

        // value = fullMin + (frac - a)/(b - a) * span
        double Invert(float frac)
        {
            return fullMin + (frac - a) / (b - a) * span;
        }

        return (Invert(f0), Invert(f1));
    }

    public override void OnScroll(float dx, float dy)
    {
        var mods = Owner?.CurrentModifiers ?? Modifiers.None;
        var zoomMod = (mods & (Modifiers.Cmd | Modifiers.Ctrl)) != 0;

        // Modifier + wheel = zoom the enabled axes around the cursor.
        if (zoomMod && (ZoomableX || ZoomableY))
        {
            var factor = Math.Pow(1.0015, dy * 28f); // wheel up (dy>0) zooms in
            ZoomBy(factor, _cursor);
            return;
        }

        // Otherwise pan a scrollable window (horizontal delta for x, vertical for y).
        var handled = false;
        if (_xWin.Active && MathF.Abs(dx) > 0.01f)
        {
            var perPx = _xWin.Len / Math.Max(1f, PlotRect.Width);
            _xWin.PanTargetBy(dx * 28f * perPx);
            handled = true;
        }

        if (_yWin.Active && !_xWin.Active && MathF.Abs(dy) > 0.01f)
        {
            var perPx = _yWin.Len / Math.Max(1f, PlotRect.Height);
            _yWin.PanTargetBy(dy * 28f * perPx);
            handled = true;
        }

        if (handled)
        {
            EnsureTicker().Start();
            return;
        }

        base.OnScroll(dx, dy);
    }

    /// <summary>Pinch zooms the chart wherever ⌘/Ctrl-wheel would (<see cref="ZoomableX" />).</summary>
    public override bool CanTouchScale()
    {
        return ZoomableX || ZoomableY;
    }

    /// <summary>
    ///     The press was taken over (pinch, app background): abandon the pan or the range selection
    ///     rather than committing it — and stop claiming the gesture.
    /// </summary>
    public override void OnPointerCancel()
    {
        if (!_pressed && !_dragging && !_selecting) return;
        _pressed = false;
        _dragging = false;
        _selecting = false;
        MarkNeedsPaint();
    }

    /// <summary>
    ///     While a press is panning a zoomed axis (or dragging out a range selection), the finger
    ///     belongs to the chart on that axis — otherwise the page it sits in swallows the pan.
    ///     A press on a chart with nothing to pan claims nothing and still scrolls the page.
    /// </summary>
    public override bool CanTouchDrag(bool vertical)
    {
        if (!_pressed) return false;
        if (_selecting) return !vertical; // range selection runs along X only
        return vertical ? _yWin.Active : _xWin.Active;
    }

    public override void OnTouchScale(float scale, Offset focus)
    {
        ZoomBy(scale, focus);
    }

    private static bool HoverEquals(ChartHoverInfo? a, ChartHoverInfo? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.X == b.X && a.Points.Count == b.Points.Count;
    }

    private ChartHoverInfo? ResolveHover(Offset point)
    {
        var ctx = _ctx;
        if (ctx is null) return null;
        EnsureHoverRegistry();

        // Region hits first (sectors/cells), topmost mark wins.
        for (var i = Marks.Count - 1; i >= 0; i--)
            if (Marks[i].TryHitTest(
                    ctx,
                    point.X,
                    point.Y,
                    out var hit
                ))
            {
                _hoverFromRegion = true;
                return new ChartHoverInfo {
                    XLabel = hit.X.Kind == ChartValueKind.Category
                        ? hit.X.CategoryName
                        : FormatHoverX(hit.X),
                    X = hit.X,
                    Points = [hit],
                };
            }

        _hoverFromRegion = false;
        var pad = 24f;
        var plot = PlotRect;
        if (point.X < plot.X - pad || point.X > plot.Right + pad ||
            point.Y < plot.Y - pad || point.Y > plot.Bottom + pad) return null;
        if (ctx.HoverPoints.Count == 0) return null;

        // Nearest x column, then every series' point at that x (the lollipop model).
        ChartDataPoint best = default;
        var bestDist = float.MaxValue;
        foreach (var p in ctx.HoverPoints)
        {
            var d = MathF.Abs(p.ScreenX - point.X);
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }

        if (bestDist > 48f) return null;

        var cluster = new List<ChartDataPoint>();
        foreach (var p in ctx.HoverPoints)
            if (p.X == best.X)
                cluster.Add(p);
        cluster.Sort((a, b) => a.ScreenY.CompareTo(b.ScreenY));

        return new ChartHoverInfo {
            XLabel = FormatHoverX(best.X),
            X = best.X,
            Points = cluster,
        };
    }

    private string FormatHoverX(ChartValue x)
    {
        if (XAxis.Formatter is { } f) return f(x);
        return x.Kind switch {
            ChartValueKind.Number => NiceScale.FormatNumber(x.Numeric),
            ChartValueKind.Time => FormatHoverTime(x.DateTime),
            _ => x.CategoryName,
        };
    }

    private static string FormatHoverTime(DateTime t)
    {
        var inv = CultureInfo.InvariantCulture;
        return t.TimeOfDay == TimeSpan.Zero
            ? t.ToString("MMM d, yyyy", inv)
            : t.ToString("MMM d, HH:mm", inv);
    }

    /// <summary>
    ///     One axis' scroll/zoom window over a scale's full domain extent. Holds the visible-length,
    ///     the eased pan offset, and the zoom factor; <see cref="Configure" /> re-derives everything
    ///     each layout from the (possibly grown) extent and applies the window to the scale. Pure
    ///     state — no widget/paint dependency — so both the x and y windows reuse it.
    /// </summary>
    private sealed class AxisWindow
    {
        private double _fullMin, _fullMax, _baseLen = 1;
        private double _offset = double.NaN; // window start in domain units; NaN = never positioned
        private double _target;

        public bool StickToEnd;
        public double Zoom = 1.0;

        public bool Active { get; private set; }
        public double Len { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }

        public double CurrentOffset => double.IsNaN(_offset) ? 0 : _offset;

        /// <summary>Total visible-plus-scrollable span (for the indicator thumb size).</summary>
        public double FullSpan => Max - Min + Len;

        public void SetOffset(double value)
        {
            _offset = _target = value;
            StickToEnd = false;
        }

        public void Configure(bool scrollable, bool zoomable, double? baseVisibleLen,
            ChartScale scale,
            double minFraction)
        {
            Active = false;
            if (!(scrollable || zoomable) || !scale.SupportsWindowing) return;
            (_fullMin, _fullMax) = scale.FullExtent;
            var full = _fullMax - _fullMin;
            if (full <= 1e-9) return;

            _baseLen = baseVisibleLen is > 0 ? baseVisibleLen.Value : full;
            var minLen = full * Math.Clamp(minFraction, 0.001, 1.0);
            var len = zoomable ? _baseLen / Zoom : _baseLen;
            len = Math.Clamp(len, minLen, full);
            Zoom = _baseLen / len; // keep the factor consistent with the clamped length

            if (len >= full - 1e-9)
            {
                scale.SetVisibleWindow(_fullMin, _fullMax); // everything fits — no scrolling
                return;
            }

            Active = true;
            Len = len;
            Min = _fullMin;
            Max = _fullMax - len;

            if (double.IsNaN(_offset)) _offset = _target = StickToEnd ? Max : Min;
            else if (StickToEnd) _offset = _target = Max;

            _offset = Math.Clamp(_offset, Min, Max);
            _target = Math.Clamp(_target, Min, Max);
            scale.SetVisibleWindow(_offset, _offset + len);
        }

        /// <summary>
        ///     Zoom by <paramref name="factor" /> keeping the domain point at
        ///     <paramref name="focusFraction" /> fixed.
        /// </summary>
        public void ApplyZoom(double factor, double focusFraction, double minFraction)
        {
            if (factor <= 0) return;
            var full = _fullMax - _fullMin;
            if (full <= 1e-9) return;

            var minLen = full * Math.Clamp(minFraction, 0.001, 1.0);
            var curLen = Active ? Len : full;
            var curOffset = Active ? CurrentOffset : _fullMin;
            var newLen = Math.Clamp(curLen / factor, minLen, full);
            var focusDomain = curOffset + focusFraction * curLen;

            _offset = _target = Math.Clamp(
                focusDomain - focusFraction * newLen,
                _fullMin,
                _fullMax - newLen
            );
            Zoom = _baseLen / newLen;
            StickToEnd = false;
        }

        /// <summary>Set the pan target directly (drag); the eased step chases it.</summary>
        public void PanTargetTo(double offset)
        {
            _target = Math.Clamp(offset, Min, Max);
            _offset = _target; // drag pans immediately (no easing lag under the finger)
            StickToEnd = _target >= Max - Len * 1e-6;
        }

        /// <summary>Nudge the eased pan target by a domain delta (wheel).</summary>
        public void PanTargetBy(double delta)
        {
            _target = Math.Clamp(_target + delta, Min, Max);
            StickToEnd = _target >= Max - Len * 1e-6;
        }

        public bool Step(float dt)
        {
            if (!Active || Math.Abs(_target - _offset) <= 1e-12) return false;
            _offset += (_target - _offset) * Math.Min(1f, dt * 14f);
            if (Math.Abs(_target - _offset) < Len * 0.001)
            {
                _offset = _target;
                return false;
            }

            return true;
        }
    }
}