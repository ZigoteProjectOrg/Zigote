using Zigote.Core.Events;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     A single color stop on a <see cref="ColorGradient" />. <see cref="Position" /> is in [0,
///     1].
/// </summary>
public struct GradientStop
{
    public float Position;
    public Color Color;

    public GradientStop(float position, Color color)
    {
        Position = position;
        Color = color;
    }
}

/// <summary>
///     A linear color ramp defined by a sorted list of <see cref="GradientStop" />s.
///     <see cref="Sample" />
///     returns the linearly-interpolated color (including alpha) at a parameter <c>t</c> in [0, 1].
/// </summary>
public sealed class ColorGradient
{
    public ColorGradient()
    {
        Stops = [new GradientStop(0f, Color.Black), new GradientStop(1f, Color.White)];
    }

    public ColorGradient(IEnumerable<GradientStop> stops)
    {
        Stops = [.. stops];
        if (Stops.Count == 0)
        {
            Stops.Add(new GradientStop(0f, Color.Black));
            Stops.Add(new GradientStop(1f, Color.White));
        }

        Sort();
    }

    public List<GradientStop> Stops { get; }

    /// <summary>
    ///     Re-sort the stops by ascending position. Call after mutating a stop's
    ///     <see cref="GradientStop.Position" />.
    /// </summary>
    public void Sort()
    {
        Stops.Sort(static (a, b) => a.Position.CompareTo(b.Position));
    }

    /// <summary>
    ///     Linearly interpolate the ramp color at <paramref name="t" /> (0..1). Assumes the stops are
    ///     sorted.
    /// </summary>
    public Color Sample(float t)
    {
        var n = Stops.Count;
        if (n == 0) return Color.Black;
        if (n == 1) return Stops[0].Color;

        t = Math.Clamp(t, 0f, 1f);

        // Before the first / after the last stop: clamp to the endpoint color.
        if (t <= Stops[0].Position) return Stops[0].Color;
        if (t >= Stops[n - 1].Position) return Stops[n - 1].Color;

        for (var i = 0; i < n - 1; i++)
        {
            var a = Stops[i];
            var b = Stops[i + 1];
            if (t < a.Position || t > b.Position) continue;

            var span = b.Position - a.Position;
            var f = span > 1e-6f ? (t - a.Position) / span : 0f;
            return LerpColor(a.Color, b.Color, f);
        }

        return Stops[n - 1].Color;
    }

    private static Color LerpColor(Color a, Color b, float f)
    {
        return new Color(
            a.R + (b.R - a.R) * f,
            a.G + (b.G - a.G) * f,
            a.B + (b.B - a.B) * f,
            a.A + (b.A - a.A) * f
        );
    }

    /// <summary>A deep copy — independent stop list — so edits don't alias an externally-held gradient.</summary>
    public ColorGradient Clone()
    {
        return new ColorGradient(Stops);
    }
}

/// <summary>
///     A flat, macOS-style color-ramp (gradient) editor. The ramp is rasterised on the CPU into an
///     RGBA8 buffer (over a checkerboard so alpha reads through) and blitted via
///     <see cref="PaintList.AddImage" />. Draggable triangle handles below the ramp move each stop's
///     position; double-click / right-click on the ramp inserts a stop (colored by sampling the ramp
///     there); <c>Delete</c>/<c>Backspace</c> removes the selected stop (a minimum of two are kept);
///     double-clicking a handle opens a <see cref="Popover" /> hosting a <c>ColorPicker</c> bound live
///     to that stop's color. <see cref="OnChanged" /> fires after every edit.
/// </summary>
public sealed class GradientEditor : Widget
{
    // ── Layout metrics ──────────────────────────────────────────────────────
    private const float RampHeight = 26f;
    private const float HandleStripHeight = 14f;
    private const float HandleHalfWidth = 6f;
    private const float Gap = 4f;
    private const float CheckerSize = 6f;
    private const int RampPixels = 256; // internal raster width; AddImage scales to bounds
    private const double DoubleClickSeconds = 0.35;

    // Effective metrics. A 12×14 colour stop is a mouse affordance; on a phone the handle grows and
    // the pick tolerance grows further still, so a fingertip lands on the stop it aimed at.
    private float HalfW => _compact ? 10f : HandleHalfWidth;

    private float StripH => _compact ? 24f : HandleStripHeight;

    private bool _compact;
    private bool _draggingStop;
    private int _lastClickStop = -1;
    private double _lastClickTime;
    private float _lastClickX;

    private float _measureH;
    private float _measureW;
    private Popover? _picker;
    private byte[]? _rampPixels;

    // Cached geometry from Layout (screen space).
    private Rect _rampRect;
    private int _selected = -1;
    private ThemeData _theme = ThemeData.Dark;

    public GradientEditor(ColorGradient gradient, Action<ColorGradient>? onChanged = null)
    {
        Gradient = gradient;
        Gradient.Sort();
        OnChanged = onChanged;
    }

    public Action<ColorGradient>? OnChanged { get; set; }

    /// <summary>The gradient being edited (mutated in place). Read-only handle for callers.</summary>
    public ColorGradient Gradient { get; }

    public override bool Focusable => true;

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Gradient.Stops.Count,
            _selected,
            _draggingStop,
            Focused,
            Bounds.X,
            Bounds.Width
        );
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
        var rawW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        var h = RampHeight + Gap + StripH;
        var sz = c.Constrain(new Size(rawW, h));
        _measureW = sz.Width;
        _measureH = sz.Height;
        return sz;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _measureW,
            _measureH
        );
        // Inset the ramp by the handle half-width so a stop at t=0 or t=1 stays fully on-screen.
        var rampX = Bounds.X + HalfW;
        var rampW = MathF.Max(1f, _measureW - HalfW * 2f);
        _rampRect = new Rect(
            rampX,
            Bounds.Y,
            rampW,
            RampHeight
        );
    }

    // ── Paint ───────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        PaintCheckerboard(paint, _rampRect);

        EnsureRamp();
        if (_rampPixels is not null)
            paint.AddImage(
                _rampRect,
                RampPixels,
                1,
                _rampPixels
            );

        paint.AddBorder(_rampRect, _theme.Separator, Radii.Sm);

        PaintHandles(paint);

        if (Focused)
            paint.AddFocusRing(_rampRect, Radii.Sm, _theme);
    }

    private void PaintCheckerboard(PaintList paint, Rect r)
    {
        var light = new Color(0.78f, 0.78f, 0.78f);
        var dark = new Color(0.55f, 0.55f, 0.55f);
        paint.AddRect(r, light, Radii.Sm);

        var cols = (int)MathF.Ceiling(r.Width / CheckerSize);
        var rows = (int)MathF.Ceiling(r.Height / CheckerSize);
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < cols; col++)
        {
            if ((row + col) % 2 == 0) continue;
            var x = r.X + col * CheckerSize;
            var y = r.Y + row * CheckerSize;
            var w = MathF.Min(CheckerSize, r.Right - x);
            var h = MathF.Min(CheckerSize, r.Bottom - y);
            if (w <= 0f || h <= 0f) continue;
            paint.AddRect(
                new Rect(
                    x,
                    y,
                    w,
                    h
                ),
                dark
            );
        }
    }

    private void PaintHandles(PaintList paint)
    {
        var stripY = _rampRect.Bottom + Gap;
        var stops = Gradient.Stops;
        for (var i = 0; i < stops.Count; i++)
        {
            var cx = StopCenterX(stops[i].Position);
            var selected = i == _selected;

            // Triangle marker pointing up at the ramp, built from narrowing dabs (flat, AA-free).
            var tipY = stripY;
            var fill = stops[i].Color.A > 0.02f ? stops[i].Color.WithAlpha(1f) : Color.White;
            var border = selected ? _theme.Primary : _theme.Separator;

            const int steps = 7;
            for (var s = 0; s < steps; s++)
            {
                var t = (s + 0.5f) / steps; // 0 at tip, 1 at base
                var half = HalfW * t;
                var y = tipY + t * StripH;
                var thickness = StripH / steps + 1f;
                paint.AddRect(
                    new Rect(
                        cx - half,
                        y,
                        MathF.Max(half * 2f, 1f),
                        thickness
                    ),
                    fill
                );
            }

            // Outline: base bar + a hairline frame so the marker reads against any ramp color.
            var baseRect = new Rect(
                cx - HalfW,
                stripY + StripH - 2f,
                HalfW * 2f,
                2f
            );
            paint.AddRect(baseRect, border);
            if (selected)
                paint.AddBorder(
                    new Rect(
                        cx - HalfW - 1f,
                        stripY,
                        HalfW * 2f + 2f,
                        StripH
                    ),
                    _theme.Primary,
                    0f,
                    1.5f
                );
        }
    }

    private float StopCenterX(float position)
    {
        return _rampRect.X + Math.Clamp(position, 0f, 1f) * _rampRect.Width;
    }

    // ── Ramp rasterisation ──────────────────────────────────────────────────

    private void EnsureRamp()
    {
        _rampPixels ??= new byte[RampPixels * 4];
        RasterizeRamp();
    }

    private void RasterizeRamp()
    {
        var px = _rampPixels!;
        for (var x = 0; x < RampPixels; x++)
        {
            var t = RampPixels > 1 ? x / (float)(RampPixels - 1) : 0f;
            var c = Gradient.Sample(t);
            var idx = x * 4;
            px[idx] = ToByte(c.R);
            px[idx + 1] = ToByte(c.G);
            px[idx + 2] = ToByte(c.B);
            px[idx + 3] = ToByte(c.A);
        }
    }

    private static byte ToByte(float v)
    {
        return (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
    }

    // ── Hit-testing ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Index of the handle under <paramref name="x" /> (screen), or -1. Picks the nearest within
    ///     tolerance.
    /// </summary>
    private int HandleAt(float x, float y)
    {
        var stripTop = _rampRect.Bottom + Gap;
        var stripBottom = stripTop + StripH;
        if (y < stripTop - 2f || y > stripBottom + 2f)
            // Also allow grabbing along the whole strip height with a slightly wider band.
            if (y < _rampRect.Bottom || y > stripBottom + 4f)
                return -1;

        var best = -1;
        var bestDist = _compact ? TouchMetrics.MinTarget / 2f : HalfW + 4f;
        var stops = Gradient.Stops;
        for (var i = 0; i < stops.Count; i++)
        {
            var cx = StopCenterX(stops[i].Position);
            var d = MathF.Abs(x - cx);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);

        var now = TimeNow();
        var isDouble = now - _lastClickTime < DoubleClickSeconds &&
                       MathF.Abs(point.X - _lastClickX) < 6f;
        _lastClickTime = now;
        _lastClickX = point.X;

        var handle = HandleAt(point.X, point.Y);
        if (handle >= 0)
        {
            _selected = handle;
            if (isDouble && handle == _lastClickStop)
            {
                OpenPicker(handle);
                MarkNeedsPaint();
                return;
            }

            _lastClickStop = handle;
            _draggingStop = true;
            MarkNeedsPaint();
            return;
        }

        _lastClickStop = -1;

        // Double-click on the ramp itself adds a stop at that position.
        if (isDouble && IsOnRamp(point))
        {
            AddStopAt(RampParam(point.X));
            return;
        }

        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_draggingStop || _selected < 0) return;
        MoveSelectedTo(RampParam(point.X));
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_draggingStop) return;
        _draggingStop = false;
        MarkNeedsPaint();
    }

    public override void OnRightClick(Offset point)
    {
        // Right-click on the ramp adds a stop (mirrors the double-click affordance).
        if (IsOnRamp(point))
        {
            App.Active?.RequestFocus(this);
            AddStopAt(RampParam(point.X));
        }
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        const uint scDelete = 76;
        const uint scBackspace = 42;
        if (scancode == scDelete || scancode == scBackspace)
            RemoveSelected();
    }

    private bool IsOnRamp(Offset point)
    {
        return point.Y >= _rampRect.Y && point.Y <= _rampRect.Bottom &&
               point.X >= Bounds.X && point.X <= Bounds.Right;
    }

    private float RampParam(float screenX)
    {
        return _rampRect.Width > 0f
            ? Math.Clamp((screenX - _rampRect.X) / _rampRect.Width, 0f, 1f)
            : 0f;
    }

    // ── Edits ───────────────────────────────────────────────────────────────

    private void MoveSelectedTo(float position)
    {
        var stops = Gradient.Stops;
        if (_selected < 0 || _selected >= stops.Count) return;

        var moved = stops[_selected];
        if (MathF.Abs(moved.Position - position) < 1e-4f) return;
        moved.Position = Math.Clamp(position, 0f, 1f);
        stops[_selected] = moved;

        // Keep sorted; track the moved stop across the re-sort so the selection follows it.
        Gradient.Sort();
        _selected = IndexOfPosition(moved.Position, moved.Color);

        MarkNeedsPaint();
        OnChanged?.Invoke(Gradient);
    }

    private int IndexOfPosition(float position, Color color)
    {
        var stops = Gradient.Stops;
        for (var i = 0; i < stops.Count; i++)
            if (MathF.Abs(stops[i].Position - position) < 1e-4f &&
                stops[i].Color.ApproxEquals(color))
                return i;
        // Fallback: nearest by position.
        var best = 0;
        var bestDist = float.MaxValue;
        for (var i = 0; i < stops.Count; i++)
        {
            var d = MathF.Abs(stops[i].Position - position);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }

    private void AddStopAt(float position)
    {
        position = Math.Clamp(position, 0f, 1f);
        var color = Gradient.Sample(position);
        Gradient.Stops.Add(new GradientStop(position, color));
        Gradient.Sort();
        _selected = IndexOfPosition(position, color);
        MarkNeedsPaint();
        OnChanged?.Invoke(Gradient);
    }

    private void RemoveSelected()
    {
        var stops = Gradient.Stops;
        if (_selected < 0 || _selected >= stops.Count) return;
        if (stops.Count <= 2) return; // always keep a valid two-endpoint ramp

        stops.RemoveAt(_selected);
        _selected = Math.Clamp(_selected, 0, stops.Count - 1);
        MarkNeedsPaint();
        OnChanged?.Invoke(Gradient);
    }

    // ── Color picker popover ────────────────────────────────────────────────

    private void OpenPicker(int stopIndex)
    {
        var stops = Gradient.Stops;
        if (stopIndex < 0 || stopIndex >= stops.Count) return;

        _picker?.Dismiss();

        // Anchor the popover at the handle, in screen space.
        var cx = StopCenterX(stops[stopIndex].Position);
        var anchor = new Rect(
            cx - HalfW,
            _rampRect.Bottom + Gap,
            HalfW * 2f,
            StripH
        );

        // Track the stop by identity-ish key (position+color at open) so it survives re-sorts.
        var openPos = stops[stopIndex].Position;
        var openColor = stops[stopIndex].Color;

        var picker = new ColorPicker(
            openColor,
            c =>
            {
                var idx = IndexOfPosition(openPos, openColor);
                var list = Gradient.Stops;
                if (idx < 0 || idx >= list.Count) return;
                var st = list[idx];
                st.Color = c;
                list[idx] = st;
                // Update the tracking key so the next callback finds the just-edited stop.
                openColor = c;
                _selected = idx;
                MarkNeedsPaint();
                OnChanged?.Invoke(Gradient);
            }
        );

        _picker = new Popover(picker, anchor) { PreferredSide = OverlaySide.Below };
        _picker.Show();
    }

    private static double TimeNow()
    {
        return Environment.TickCount64 / 1000.0;
    }
}