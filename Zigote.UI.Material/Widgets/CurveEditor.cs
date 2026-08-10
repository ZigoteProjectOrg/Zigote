using Zigote.Core.Events;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     One control point on an <see cref="EditableCurve" />. <see cref="Time" /> is the X position
///     (typically [0,1] but unconstrained) and <see cref="Value" /> the Y position. The tangents are
///     the curve <i>slope</i> (dValue/dTime) entering (<see cref="InTangent" />) and leaving
///     (<see cref="OutTangent" />) the key. A tangent of exactly 0 on a fresh key is treated as
///     "auto" — <see cref="EditableCurve" /> derives a smooth Catmull-Rom slope for it.
/// </summary>
public struct CurveKey
{
    public float Time;
    public float Value;
    public float InTangent;
    public float OutTangent;

    public CurveKey(float time, float value, float inTangent = 0f, float outTangent = 0f)
    {
        Time = time;
        Value = value;
        InTangent = inTangent;
        OutTangent = outTangent;
    }
}

/// <summary>
///     A self-contained editable animation/easing curve: a sorted list of <see cref="CurveKey" />s
///     evaluated with cubic-Hermite interpolation between neighbours. Lives in Zigote.UI (no editor
///     dependency) so the <see cref="CurveEditor" /> widget is reusable anywhere.
/// </summary>
public sealed class EditableCurve
{
    public EditableCurve()
    {
        Keys = [
            new CurveKey(0f, 0f),
            new CurveKey(1f, 1f),
        ];
    }

    public EditableCurve(IEnumerable<CurveKey> keys)
    {
        Keys = [.. keys];
        Keys.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        if (Keys.Count == 0)
        {
            Keys.Add(new CurveKey(0f, 0f));
            Keys.Add(new CurveKey(1f, 1f));
        }
    }

    public List<CurveKey> Keys { get; }

    /// <summary>
    ///     Re-sort keys by time. Returns the new index of the key that was at
    ///     <paramref name="trackIndex" />.
    /// </summary>
    public int SortKeepingTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= Keys.Count) return trackIndex;
        var tracked = Keys[trackIndex];
        // Use a stable tie-break on Value so reordering is deterministic.
        Keys.Sort(static (a, b) =>
            {
                var c = a.Time.CompareTo(b.Time);
                return c != 0 ? c : a.Value.CompareTo(b.Value);
            }
        );
        for (var i = 0; i < Keys.Count; i++)
            if (Keys[i].Time == tracked.Time && Keys[i].Value == tracked.Value &&
                Keys[i].InTangent == tracked.InTangent && Keys[i].OutTangent == tracked.OutTangent)
                return i;
        return Math.Clamp(trackIndex, 0, Keys.Count - 1);
    }

    /// <summary>Sample the curve at <paramref name="t" /> using cubic-Hermite between bracketing keys.</summary>
    public float Evaluate(float t)
    {
        if (Keys.Count == 0) return 0f;
        if (Keys.Count == 1) return Keys[0].Value;

        if (t <= Keys[0].Time) return Keys[0].Value;
        if (t >= Keys[^1].Time) return Keys[^1].Value;

        // Find the segment [i, i+1] containing t.
        var i = 0;
        for (var k = 0; k < Keys.Count - 1; k++)
            if (t >= Keys[k].Time && t <= Keys[k + 1].Time)
            {
                i = k;
                break;
            }

        var a = Keys[i];
        var b = Keys[i + 1];
        var dt = b.Time - a.Time;
        if (dt <= 1e-6f) return b.Value;

        var u = (t - a.Time) / dt;
        var (mOut, mIn) = ResolveSlopes(i);
        return Hermite(
            a.Value,
            b.Value,
            mOut * dt,
            mIn * dt,
            u
        );
    }

    // Hermite basis: p0/p1 endpoints, m0/m1 scaled tangents (already × dt).
    private static float Hermite(float p0, float p1, float m0, float m1, float u)
    {
        var u2 = u * u;
        var u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * p0
               + (u3 - 2f * u2 + u) * m0
               + (-2f * u3 + 3f * u2) * p1
               + (u3 - u2) * m1;
    }

    /// <summary>
    ///     The effective leaving-slope of key <paramref name="i" /> and entering-slope of key i+1 for
    ///     the segment between them. Explicit tangents win; a 0 tangent falls back to an auto
    ///     Catmull-Rom slope derived from neighbours so a freshly-added key still draws a smooth curve.
    /// </summary>
    public (float outSlope, float inSlope) ResolveSlopes(int i)
    {
        var a = Keys[i];
        var b = Keys[i + 1];
        var outS = a.OutTangent != 0f ? a.OutTangent : AutoSlope(i);
        var inS = b.InTangent != 0f ? b.InTangent : AutoSlope(i + 1);
        return (outS, inS);
    }

    /// <summary>Catmull-Rom-style auto slope at key <paramref name="i" /> from its neighbours.</summary>
    public float AutoSlope(int i)
    {
        if (Keys.Count < 2) return 0f;
        var prev = Keys[Math.Max(0, i - 1)];
        var next = Keys[Math.Min(Keys.Count - 1, i + 1)];
        var dt = next.Time - prev.Time;
        if (MathF.Abs(dt) <= 1e-6f) return 0f;
        return (next.Value - prev.Value) / dt;
    }
}

/// <summary>
///     Reusable animation/easing curve editor. Draws a grid + axes, the curve sampled via cubic
///     Bézier ribbons, and draggable key points + tangent handles. Double- or right-click adds a key
///     at the cursor; Delete/Backspace removes the selected key. Fires <see cref="OnChanged" /> after
///     every edit.
/// </summary>
public sealed class CurveEditor : Widget
{
    private const float HitRadius = 8f;

    private const float KeyRadius = 4.5f;
    private const float HandleRadius = 3.5f;
    private const float TangentLen = 36f; // screen-space length of a tangent handle arm
    private const int Samples = 48; // total samples across the visible curve

    private const uint ScDelete = 76;
    private const uint ScBackspace = 42;

    /// <summary>
    ///     Pick radius for keys and tangent handles. The drawn key stays 4.5pt; a fingertip needs a
    ///     far wider catchment, and tangent handles are only pickable on the already-selected key,
    ///     so the wider radius cannot make the wrong handle win.
    /// </summary>
    private float Grab => _compact ? TouchMetrics.MinTarget / 2f : HitRadius;

    private readonly float _maxT = 1f;

    // Data-space view window (the value range mapped to the vertical extent).
    private readonly float _minT = 0f;

    private bool _compact;

    // Active drag target.
    private DragKind _drag = DragKind.None;
    private int _hoverKey = -1;
    private Offset _lastDownPos;
    private double _lastDownTime = -1;

    private int _selected = -1;

    private ThemeData _theme = ThemeData.Dark;
    private float _w, _h;

    public CurveEditor(EditableCurve curve, Action<EditableCurve>? onChanged = null)
    {
        Curve = curve;
        OnChanged = onChanged;
    }

    public Action<EditableCurve>? OnChanged { get; set; }
    public EditableCurve Curve { get; }

    public bool Enabled { get; set; } = true;
    public override bool Focusable => true;

    /// <summary>The vertical value window. Adjust to fit curves whose values stray outside [0,1].</summary>
    public float MinValue { get; set; } = -0.1f;

    public float MaxValue { get; set; } = 1.1f;

    // ── Coordinate transforms (data <-> screen) ───────────────────────────────

    private Rect Plot => Bounds; // the whole widget is the plot area

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Curve.Keys.Count,
            _selected,
            _hoverKey,
            (int)_drag,
            Focused
        );
    }

    private Offset DataToScreen(float time, float value)
    {
        var p = Plot;
        var tx = _maxT > _minT ? (time - _minT) / (_maxT - _minT) : 0f;
        var ty = MaxValue > MinValue ? (value - MinValue) / (MaxValue - MinValue) : 0f;
        var x = p.X + tx * p.Width;
        var y = p.Y + (1f - ty) * p.Height; // Y up in data, down in screen
        return new Offset(x, y);
    }

    private (float time, float value) ScreenToData(Offset s)
    {
        var p = Plot;
        var tx = p.Width > 0f ? (s.X - p.X) / p.Width : 0f;
        var ty = p.Height > 0f ? (s.Y - p.Y) / p.Height : 0f;
        var time = _minT + tx * (_maxT - _minT);
        var value = MinValue + (1f - ty) * (MaxValue - MinValue);
        return (time, value);
    }

    // ── Measure / Layout ──────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        var h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : 160f;
        var sz = c.Constrain(new Size(w, h));
        _w = sz.Width;
        _h = sz.Height;
        return sz;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _w,
            _h
        );
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        // Backdrop.
        paint.AddRect(Bounds, _theme.SurfaceAlt, Radii.Md);
        paint.AddClipStart(Bounds);

        DrawGrid(paint);
        DrawCurve(paint);
        DrawTangents(paint);
        DrawKeys(paint);

        paint.AddClipEnd();

        paint.AddBorder(Bounds, _theme.Separator, Radii.Md);
        if (Focused && Enabled)
            paint.AddFocusRing(Bounds, Radii.Md, _theme);
    }

    private void DrawGrid(PaintList paint)
    {
        var grid = _theme.Separator;
        var axis = _theme.Separator.WithAlpha(MathF.Min(1f, _theme.Separator.A * 2.5f));

        // Vertical time grid lines at 0, 0.25, 0.5, 0.75, 1.
        for (var i = 0; i <= 4; i++)
        {
            var t = _minT + (_maxT - _minT) * (i / 4f);
            var s = DataToScreen(t, MinValue);
            paint.AddRect(
                new Rect(
                    s.X,
                    Bounds.Y,
                    1f,
                    Bounds.Height
                ),
                grid
            );
        }

        // Horizontal value grid lines.
        for (var i = 0; i <= 4; i++)
        {
            var v = MinValue + (MaxValue - MinValue) * (i / 4f);
            var s = DataToScreen(_minT, v);
            paint.AddRect(
                new Rect(
                    Bounds.X,
                    s.Y,
                    Bounds.Width,
                    1f
                ),
                grid
            );
        }

        // Emphasised value = 0 and value = 1 reference lines (the easing baseline).
        if (MinValue <= 0f && MaxValue >= 0f)
        {
            var z = DataToScreen(_minT, 0f);
            paint.AddRect(
                new Rect(
                    Bounds.X,
                    z.Y,
                    Bounds.Width,
                    1f
                ),
                axis
            );
        }

        if (MinValue <= 1f && MaxValue >= 1f)
        {
            var o = DataToScreen(_minT, 1f);
            paint.AddRect(
                new Rect(
                    Bounds.X,
                    o.Y,
                    Bounds.Width,
                    1f
                ),
                axis
            );
        }
    }

    private void DrawCurve(PaintList paint)
    {
        var keys = Curve.Keys;
        var color = Enabled ? _theme.Primary : StateStyle.Disabled(_theme.Primary);
        const float width = 2f;

        if (keys.Count == 0) return;

        if (keys.Count == 1)
        {
            var s = DataToScreen(keys[0].Time, keys[0].Value);
            paint.AddRect(
                new Rect(
                    Bounds.X,
                    s.Y - width / 2f,
                    Bounds.Width,
                    width
                ),
                color
            );
            return;
        }

        // Flat extension before the first key.
        var first = DataToScreen(keys[0].Time, keys[0].Value);
        if (first.X > Bounds.X)
            paint.AddRect(
                new Rect(
                    Bounds.X,
                    first.Y - width / 2f,
                    first.X - Bounds.X,
                    width
                ),
                color
            );

        // Flat extension after the last key.
        var last = DataToScreen(keys[^1].Time, keys[^1].Value);
        if (last.X < Bounds.Right)
            paint.AddRect(
                new Rect(
                    last.X,
                    last.Y - width / 2f,
                    Bounds.Right - last.X,
                    width
                ),
                color
            );

        // Each segment becomes a cubic Bézier. Convert Hermite slopes to Bézier control points:
        // c1 = p0 + m0/3 (in data units of value over a third of the segment time span).
        for (var i = 0; i < keys.Count - 1; i++)
        {
            var a = keys[i];
            var b = keys[i + 1];
            var dt = b.Time - a.Time;
            if (dt <= 1e-6f) continue;

            var (mOut, mIn) = Curve.ResolveSlopes(i);

            var p0 = DataToScreen(a.Time, a.Value);
            var p3 = DataToScreen(b.Time, b.Value);
            var c1 = DataToScreen(a.Time + dt / 3f, a.Value + mOut * dt / 3f);
            var c2 = DataToScreen(b.Time - dt / 3f, b.Value - mIn * dt / 3f);

            paint.AddBezier(
                p0.X,
                p0.Y,
                c1.X,
                c1.Y,
                c2.X,
                c2.Y,
                p3.X,
                p3.Y,
                color,
                width
            );
        }
    }

    private void DrawTangents(PaintList paint)
    {
        if (_selected < 0 || _selected >= Curve.Keys.Count) return;

        var keys = Curve.Keys;
        var key = keys[_selected];
        var center = DataToScreen(key.Time, key.Value);
        var handleColor = _theme.Accent;
        var armColor = _theme.Label2;

        // Out tangent (to the right): use explicit or auto slope.
        if (_selected < keys.Count - 1)
        {
            var slope = key.OutTangent != 0f ? key.OutTangent : Curve.AutoSlope(_selected);
            var h = TangentScreenPoint(center, slope, 1f);
            paint.AddBezier(
                center.X,
                center.Y,
                center.X,
                center.Y,
                h.X,
                h.Y,
                h.X,
                h.Y,
                armColor,
                1f
            );
            DrawDot(
                paint,
                h,
                HandleRadius,
                handleColor
            );
        }

        // In tangent (to the left).
        if (_selected > 0)
        {
            var slope = key.InTangent != 0f ? key.InTangent : Curve.AutoSlope(_selected);
            var h = TangentScreenPoint(center, slope, -1f);
            paint.AddBezier(
                center.X,
                center.Y,
                center.X,
                center.Y,
                h.X,
                h.Y,
                h.X,
                h.Y,
                armColor,
                1f
            );
            DrawDot(
                paint,
                h,
                HandleRadius,
                handleColor
            );
        }
    }

    // Build a fixed-screen-length tangent arm from a data-space slope. dir = +1 right, -1 left.
    private Offset TangentScreenPoint(Offset center, float slope, float dir)
    {
        // Convert the data slope to a screen-space direction, then normalise to a fixed pixel length.
        var p = Plot;
        var sxPerT = p.Width > 0f && _maxT > _minT ? p.Width / (_maxT - _minT) : 1f;
        var syPerV = p.Height > 0f && MaxValue > MinValue ? p.Height / (MaxValue - MinValue) : 1f;

        var dxData = dir; // one unit of time in the given direction
        var dyData = slope * dir;
        var dxScreen = dxData * sxPerT;
        var dyScreen = -dyData * syPerV; // screen Y is inverted
        var len = MathF.Sqrt(dxScreen * dxScreen + dyScreen * dyScreen);
        if (len <= 1e-4f) return new Offset(center.X + dir * TangentLen, center.Y);
        var k = TangentLen / len;
        return new Offset(center.X + dxScreen * k, center.Y + dyScreen * k);
    }

    private void DrawKeys(PaintList paint)
    {
        var keys = Curve.Keys;
        for (var i = 0; i < keys.Count; i++)
        {
            var s = DataToScreen(keys[i].Time, keys[i].Value);
            var fill = i == _selected
                ? _theme.Accent
                : i == _hoverKey
                    ? _theme.OnSurface
                    : _theme.OnSurface.WithAlpha(0.75f);
            DrawDot(
                paint,
                s,
                KeyRadius,
                fill
            );
            paint.AddBorder(
                new Rect(
                    s.X - KeyRadius,
                    s.Y - KeyRadius,
                    KeyRadius * 2f,
                    KeyRadius * 2f
                ),
                _theme.SurfaceAlt,
                KeyRadius
            );
        }
    }

    private static void DrawDot(PaintList paint, Offset c, float r, Color color)
    {
        paint.AddRect(
            new Rect(
                c.X - r,
                c.Y - r,
                r * 2f,
                r * 2f
            ),
            color,
            r
        );
    }

    // ── Hit testing helpers ───────────────────────────────────────────────────

    private int KeyAt(Offset p)
    {
        var keys = Curve.Keys;
        var best = -1;
        var bestD = Grab * Grab;
        for (var i = 0; i < keys.Count; i++)
        {
            var s = DataToScreen(keys[i].Time, keys[i].Value);
            var dx = s.X - p.X;
            var dy = s.Y - p.Y;
            var d = dx * dx + dy * dy;
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }

    private DragKind TangentHandleAt(Offset p)
    {
        if (_selected < 0 || _selected >= Curve.Keys.Count) return DragKind.None;
        var keys = Curve.Keys;
        var key = keys[_selected];
        var center = DataToScreen(key.Time, key.Value);

        if (_selected < keys.Count - 1)
        {
            var slope = key.OutTangent != 0f ? key.OutTangent : Curve.AutoSlope(_selected);
            var h = TangentScreenPoint(center, slope, 1f);
            if (Within(p, h, Grab)) return DragKind.OutTangent;
        }

        if (_selected > 0)
        {
            var slope = key.InTangent != 0f ? key.InTangent : Curve.AutoSlope(_selected);
            var h = TangentScreenPoint(center, slope, -1f);
            if (Within(p, h, Grab)) return DragKind.InTangent;
        }

        return DragKind.None;
    }

    private static bool Within(Offset a, Offset b, float r)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy <= r * r;
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled) return;
        App.Active?.RequestFocus(this);

        // Double-click → add a key at the cursor.
        var now = App.Active?.Time ?? 0f;
        var isDouble = _lastDownTime >= 0 && now - _lastDownTime < 0.35 &&
                       Within(point, _lastDownPos, 6f);
        _lastDownTime = now;
        _lastDownPos = point;
        if (isDouble)
        {
            AddKeyAt(point);
            _drag = DragKind.None;
            return;
        }

        // Tangent handle of the selected key takes priority over keys behind it.
        var th = TangentHandleAt(point);
        if (th != DragKind.None)
        {
            _drag = th;
            MarkNeedsPaint();
            return;
        }

        var hit = KeyAt(point);
        if (hit >= 0)
        {
            _selected = hit;
            _drag = DragKind.Key;
            MarkNeedsPaint();
            return;
        }

        // Empty space: deselect.
        _selected = -1;
        _drag = DragKind.None;
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        if (!Enabled)
            return;

        if (_drag == DragKind.None)
        {
            var h = KeyAt(point);
            if (h != _hoverKey)
            {
                _hoverKey = h;
                MarkNeedsPaint();
            }

            return;
        }

        switch (_drag)
        {
            case DragKind.Key:
                DragKey(point);
                break;
            case DragKind.InTangent:
            case DragKind.OutTangent:
                DragTangent(point, _drag == DragKind.InTangent);
                break;
        }
    }

    public override void OnPointerUp(Offset point)
    {
        if (_drag != DragKind.None)
        {
            _drag = DragKind.None;
            MarkNeedsPaint();
        }
    }

    /// <summary>The press was taken over (pinch, app background): drop the grabbed handle.</summary>
    public override void OnPointerCancel()
    {
        OnPointerUp(Offset.Zero);
    }

    /// <summary>
    ///     Dragging a key or a tangent moves freely in both axes, so a grabbed handle owns the
    ///     gesture outright — a press on empty graph space grabs nothing and still scrolls the page.
    /// </summary>
    public override bool CanTouchDrag(bool vertical)
    {
        return _drag != DragKind.None;
    }

    public override void OnPointerExit()
    {
        if (_hoverKey != -1)
        {
            _hoverKey = -1;
            MarkNeedsPaint();
        }
    }

    public override void OnRightClick(Offset point)
    {
        if (!Enabled) return;
        App.Active?.RequestFocus(this);
        AddKeyAt(point);
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || !Enabled) return;
        if (scancode is ScDelete or ScBackspace && _selected >= 0)
            DeleteSelected();
    }

    // ── Edits ─────────────────────────────────────────────────────────────────

    private void DragKey(Offset point)
    {
        if (_selected < 0 || _selected >= Curve.Keys.Count) return;
        var keys = Curve.Keys;
        var (time, value) = ScreenToData(point);

        // Clamp time strictly between the neighbours so order is preserved without re-sorting jumps.
        var lo = _selected > 0 ? keys[_selected - 1].Time : float.NegativeInfinity;
        var hi = _selected < keys.Count - 1 ? keys[_selected + 1].Time : float.PositiveInfinity;
        const float eps = 1e-4f;
        if (float.IsFinite(lo)) time = MathF.Max(time, lo + eps);
        if (float.IsFinite(hi)) time = MathF.Min(time, hi - eps);
        value = Math.Clamp(value, MinValue, MaxValue);

        var k = keys[_selected];
        k.Time = time;
        k.Value = value;
        keys[_selected] = k;

        MarkNeedsPaint();
        OnChanged?.Invoke(Curve);
    }

    private void DragTangent(Offset point, bool incoming)
    {
        if (_selected < 0 || _selected >= Curve.Keys.Count) return;
        var keys = Curve.Keys;
        var key = keys[_selected];
        var center = DataToScreen(key.Time, key.Value);

        // Convert the handle position back into a data-space slope.
        var p = Plot;
        var sxPerT = p.Width > 0f && _maxT > _minT ? p.Width / (_maxT - _minT) : 1f;
        var syPerV = p.Height > 0f && MaxValue > MinValue ? p.Height / (MaxValue - MinValue) : 1f;

        var dxScreen = point.X - center.X;
        var dyScreen = point.Y - center.Y;
        var dxData = dxScreen / sxPerT;
        var dyData = -dyScreen / syPerV; // invert screen Y

        // Avoid a vertical (infinite) slope; keep a minimum horizontal extent.
        const float minDx = 1e-3f;
        var horiz = incoming ? MathF.Min(dxData, -minDx) : MathF.Max(dxData, minDx);
        var slope = dyData / horiz;
        // A genuine zero slope would re-trigger auto-tangent mode; nudge to a tiny non-zero value.
        if (slope == 0f) slope = incoming ? -1e-4f : 1e-4f;

        if (incoming) key.InTangent = slope;
        else key.OutTangent = slope;
        keys[_selected] = key;

        MarkNeedsPaint();
        OnChanged?.Invoke(Curve);
    }

    private void AddKeyAt(Offset point)
    {
        var (time, value) = ScreenToData(point);
        value = Math.Clamp(value, MinValue, MaxValue);

        Curve.Keys.Add(new CurveKey(time, value));
        // Sort and select the newly-added key.
        var idx = Curve.SortKeepingTrack(Curve.Keys.Count - 1);
        _selected = idx;
        _drag = DragKind.None;

        MarkNeedsPaint();
        OnChanged?.Invoke(Curve);
    }

    private void DeleteSelected()
    {
        // Keep at least two keys so the curve stays well-defined.
        if (Curve.Keys.Count <= 2 || _selected < 0 || _selected >= Curve.Keys.Count) return;
        Curve.Keys.RemoveAt(_selected);
        _selected = -1;
        MarkNeedsPaint();
        OnChanged?.Invoke(Curve);
    }

    private enum DragKind
    {
        None,
        Key,
        InTangent,
        OutTangent,
    }
}