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
    public ColorGradient() => Stops = [
        new GradientStop(position: 0f, color: Color.Black),
        new GradientStop(position: 1f, color: Color.White),
    ];

    public ColorGradient(IEnumerable<GradientStop> stops)
    {
        Stops = [.. stops];
        if (Stops.Count == 0)
        {
            Stops.Add(new GradientStop(position: 0f, color: Color.Black));
            Stops.Add(new GradientStop(position: 1f, color: Color.White));
        }

        Sort();
    }

    public List<GradientStop> Stops { get; }

    /// <summary>
    ///     Re-sort the stops by ascending position. Call after mutating a stop's
    ///     <see cref="GradientStop.Position" />.
    /// </summary>
    public void Sort() => Stops.Sort(static (a, b) => a.Position.CompareTo(b.Position));

    /// <summary>
    ///     Linearly interpolate the ramp color at <paramref name="t" /> (0..1). Assumes the stops are
    ///     sorted.
    /// </summary>
    public Color Sample(float t)
    {
        int n = Stops.Count;
        if (n == 0) return Color.Black;
        if (n == 1) return Stops[0].Color;

        t = Math.Clamp(value: t, min: 0f, max: 1f);

        // Before the first / after the last stop: clamp to the endpoint color.
        if (t <= Stops[0].Position) return Stops[0].Color;
        if (t >= Stops[n - 1].Position) return Stops[n - 1].Color;

        for (int i = 0; i < n - 1; i++)
        {
            var a = Stops[i];
            var b = Stops[i + 1];
            if (t < a.Position || t > b.Position) continue;

            float span = b.Position - a.Position;
            float f = span > 1e-6f ? (t - a.Position) / span : 0f;
            return LerpColor(a: a.Color, b: b.Color, f: f);
        }

        return Stops[n - 1].Color;
    }

    private static Color LerpColor(Color a, Color b, float f)
    {
        return new Color(
            r: a.R + ((b.R - a.R) * f),
            g: a.G + ((b.G - a.G) * f),
            b: a.B + ((b.B - a.B) * f),
            a: a.A + ((b.A - a.A) * f)
        );
    }

    /// <summary>A deep copy — independent stop list — so edits don't alias an externally-held gradient.</summary>
    public ColorGradient Clone() => new(Stops);
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

    // Effective metrics. A 12×14 colour stop is a mouse affordance; on a phone the handle grows and
    // the pick tolerance grows further still, so a fingertip lands on the stop it aimed at.
    private float HalfW => _compact ? 10f : HandleHalfWidth;

    private float StripH => _compact ? 24f : HandleStripHeight;

    public Action<ColorGradient>? OnChanged { get; set; }

    /// <summary>The gradient being edited (mutated in place). Read-only handle for callers.</summary>
    public ColorGradient Gradient { get; }

    public override bool Focusable => true;

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: Gradient.Stops.Count,
            value2: _selected,
            value3: _draggingStop,
            value4: Focused,
            value5: Bounds.X,
            value6: Bounds.Width
        );
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
        float rawW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        float h = RampHeight + Gap + StripH;
        var sz = c.Constrain(new Size(width: rawW, height: h));
        _measureW = sz.Width;
        _measureH = sz.Height;
        return sz;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _measureW,
            height: _measureH
        );
        // Inset the ramp by the handle half-width so a stop at t=0 or t=1 stays fully on-screen.
        float rampX = Bounds.X + HalfW;
        float rampW = MathF.Max(x: 1f, y: _measureW - (HalfW * 2f));
        _rampRect = new Rect(
            x: rampX,
            y: Bounds.Y,
            width: rampW,
            height: RampHeight
        );
    }

    // ── Paint ───────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        PaintCheckerboard(paint: paint, r: _rampRect);

        EnsureRamp();
        if (_rampPixels is not null)
        {
            paint.AddImage(
                bounds: _rampRect,
                pixelWidth: RampPixels,
                pixelHeight: 1,
                pixels: _rampPixels
            );
        }

        paint.AddBorder(bounds: _rampRect, color: _theme.Separator, radius: Radii.Sm);

        PaintHandles(paint);

        if (Focused)
            paint.AddFocusRing(bounds: _rampRect, radius: Radii.Sm, theme: _theme);
    }

    private void PaintCheckerboard(PaintList paint, Rect r)
    {
        var light = new Color(r: 0.78f, g: 0.78f, b: 0.78f);
        var dark = new Color(r: 0.55f, g: 0.55f, b: 0.55f);
        paint.AddRect(bounds: r, color: light, radius: Radii.Sm);

        int cols = (int)MathF.Ceiling(r.Width / CheckerSize);
        int rows = (int)MathF.Ceiling(r.Height / CheckerSize);
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < cols; col++)
        {
            if ((row + col) % 2 == 0) continue;
            float x = r.X + (col * CheckerSize);
            float y = r.Y + (row * CheckerSize);
            float w = MathF.Min(x: CheckerSize, y: r.Right - x);
            float h = MathF.Min(x: CheckerSize, y: r.Bottom - y);
            if (w <= 0f || h <= 0f) continue;
            paint.AddRect(
                bounds: new Rect(
                    x: x,
                    y: y,
                    width: w,
                    height: h
                ),
                color: dark
            );
        }
    }

    private void PaintHandles(PaintList paint)
    {
        float stripY = _rampRect.Bottom + Gap;
        var stops = Gradient.Stops;
        for (int i = 0; i < stops.Count; i++)
        {
            float cx = StopCenterX(stops[i].Position);
            bool selected = i == _selected;

            // Triangle marker pointing up at the ramp, built from narrowing dabs (flat, AA-free).
            float tipY = stripY;
            var fill = stops[i].Color.A > 0.02f ? stops[i].Color.WithAlpha(1f) : Color.White;
            var border = selected ? _theme.Primary : _theme.Separator;

            const int steps = 7;
            for (int s = 0; s < steps; s++)
            {
                float t = (s + 0.5f) / steps; // 0 at tip, 1 at base
                float half = HalfW * t;
                float y = tipY + (t * StripH);
                float thickness = (StripH / steps) + 1f;
                paint.AddRect(
                    bounds: new Rect(
                        x: cx - half,
                        y: y,
                        width: MathF.Max(x: half * 2f, y: 1f),
                        height: thickness
                    ),
                    color: fill
                );
            }

            // Outline: base bar + a hairline frame so the marker reads against any ramp color.
            var baseRect = new Rect(
                x: cx - HalfW,
                y: stripY + StripH - 2f,
                width: HalfW * 2f,
                height: 2f
            );
            paint.AddRect(bounds: baseRect, color: border);
            if (selected)
            {
                paint.AddBorder(
                    bounds: new Rect(
                        x: cx - HalfW - 1f,
                        y: stripY,
                        width: (HalfW * 2f) + 2f,
                        height: StripH
                    ),
                    color: _theme.Primary,
                    radius: 0f,
                    width: 1.5f
                );
            }
        }
    }

    private float StopCenterX(float position) => _rampRect.X +
                                                 (Math.Clamp(value: position, min: 0f, max: 1f) *
                                                  _rampRect.Width);

    // ── Ramp rasterisation ──────────────────────────────────────────────────

    private void EnsureRamp()
    {
        _rampPixels ??= new byte[RampPixels * 4];
        RasterizeRamp();
    }

    private void RasterizeRamp()
    {
        byte[] px = _rampPixels!;
        for (int x = 0; x < RampPixels; x++)
        {
            float t = RampPixels > 1 ? x / (float)(RampPixels - 1) : 0f;
            var c = Gradient.Sample(t);
            int idx = x * 4;
            px[idx] = ToByte(c.R);
            px[idx + 1] = ToByte(c.G);
            px[idx + 2] = ToByte(c.B);
            px[idx + 3] = ToByte(c.A);
        }
    }

    private static byte ToByte(float v) =>
        (byte)((Math.Clamp(value: v, min: 0f, max: 1f) * 255f) + 0.5f);

    // ── Hit-testing ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Index of the handle under <paramref name="x" /> (screen), or -1. Picks the nearest within
    ///     tolerance.
    /// </summary>
    private int HandleAt(float x, float y)
    {
        float stripTop = _rampRect.Bottom + Gap;
        float stripBottom = stripTop + StripH;
        if (y < stripTop - 2f || y > stripBottom + 2f)
            // Also allow grabbing along the whole strip height with a slightly wider band.
        {
            if (y < _rampRect.Bottom || y > stripBottom + 4f)
                return -1;
        }

        int best = -1;
        float bestDist = _compact ? TouchMetrics.MinTarget / 2f : HalfW + 4f;
        var stops = Gradient.Stops;
        for (int i = 0; i < stops.Count; i++)
        {
            float cx = StopCenterX(stops[i].Position);
            float d = MathF.Abs(x - cx);
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

        double now = TimeNow();
        bool isDouble = now - _lastClickTime < DoubleClickSeconds &&
                        MathF.Abs(point.X - _lastClickX) < 6f;
        _lastClickTime = now;
        _lastClickX = point.X;

        int handle = HandleAt(x: point.X, y: point.Y);
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

    /// <summary>The press was taken over (pinch, app background): drop the held stop.</summary>
    public override void OnPointerCancel() => OnPointerUp(Offset.Zero);

    /// <summary>
    ///     A held stop owns the gesture: the finger is moving it along the ramp, and a page that
    ///     stole the vertical half would drop the stop mid-drag. A press that grabbed no stop
    ///     claims nothing.
    /// </summary>
    public override bool CanTouchDrag(bool vertical) => _draggingStop;

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
            ? Math.Clamp(value: (screenX - _rampRect.X) / _rampRect.Width, min: 0f, max: 1f)
            : 0f;
    }

    // ── Edits ───────────────────────────────────────────────────────────────

    private void MoveSelectedTo(float position)
    {
        var stops = Gradient.Stops;
        if (_selected < 0 || _selected >= stops.Count) return;

        var moved = stops[_selected];
        if (MathF.Abs(moved.Position - position) < 1e-4f) return;
        moved.Position = Math.Clamp(value: position, min: 0f, max: 1f);
        stops[_selected] = moved;

        // Keep sorted; track the moved stop across the re-sort so the selection follows it.
        Gradient.Sort();
        _selected = IndexOfPosition(position: moved.Position, color: moved.Color);

        MarkNeedsPaint();
        OnChanged?.Invoke(Gradient);
    }

    private int IndexOfPosition(float position, Color color)
    {
        var stops = Gradient.Stops;
        for (int i = 0; i < stops.Count; i++)
        {
            if (MathF.Abs(stops[i].Position - position) < 1e-4f &&
                stops[i].Color.ApproxEquals(color))
                return i;
        }

        // Fallback: nearest by position.
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < stops.Count; i++)
        {
            float d = MathF.Abs(stops[i].Position - position);
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
        position = Math.Clamp(value: position, min: 0f, max: 1f);
        var color = Gradient.Sample(position);
        Gradient.Stops.Add(new GradientStop(position: position, color: color));
        Gradient.Sort();
        _selected = IndexOfPosition(position: position, color: color);
        MarkNeedsPaint();
        OnChanged?.Invoke(Gradient);
    }

    private void RemoveSelected()
    {
        var stops = Gradient.Stops;
        if (_selected < 0 || _selected >= stops.Count) return;
        if (stops.Count <= 2) return; // always keep a valid two-endpoint ramp

        stops.RemoveAt(_selected);
        _selected = Math.Clamp(value: _selected, min: 0, max: stops.Count - 1);
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
        float cx = StopCenterX(stops[stopIndex].Position);
        var anchor = new Rect(
            x: cx - HalfW,
            y: _rampRect.Bottom + Gap,
            width: HalfW * 2f,
            height: StripH
        );

        // Track the stop by identity-ish key (position+color at open) so it survives re-sorts.
        float openPos = stops[stopIndex].Position;
        var openColor = stops[stopIndex].Color;

        var picker = new ColorPicker(
            initial: openColor,
            onChanged: c =>
            {
                int idx = IndexOfPosition(position: openPos, color: openColor);
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

        _picker = new Popover(child: picker, anchor: anchor) { PreferredSide = OverlaySide.Below };
        _picker.Show();
    }

    private static double TimeNow() => Environment.TickCount64 / 1000.0;
}
