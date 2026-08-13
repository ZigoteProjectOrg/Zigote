using Zigote.Core.Events;

namespace Zigote.UI.Material;

/// <summary>
///     A macOS-style <c>NSStepper</c>: a compact vertical pair of up / down buttons joined in a
///     rounded group with a hairline divider between them. Clicking a half nudges <see cref="Value" />
///     by <see cref="Step" />, clamped to [<see cref="Min" />, <see cref="Max" />]. Colour, shape and
///     sizing come from the theme tokens.
/// </summary>
public class Stepper : Widget
{
    /// <summary>Half the control is up to ~18 pt wide; the whole group is two compact halves tall.</summary>
    private const float GroupWidth = 18f;

    private int _hoverHalf = -1; // -1 none, 0 up, 1 down
    private int _pressHalf = -1;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public Stepper(float value = 0f, float step = 1f, float? min = null, float? max = null,
        Action<float>? onChanged = null)
    {
        Min = min;
        Max = max;
        Step = step;
        Value = Clamp(value);
        OnChanged = onChanged;
    }

    public float Value { get; set; }
    public float Step { get; set; }
    public float? Min { get; set; }
    public float? Max { get; set; }
    public Action<float>? OnChanged { get; set; }
    public bool Enabled { get; set; } = true;

    private float HalfHeight => ControlMetrics.CompactHeight;

    private bool CanIncrement => Max is not { } mx || Value < mx;
    private bool CanDecrement => Min is not { } mn || Value > mn;

    public override bool Focusable => true;

    /// <summary>
    ///     Owns Up/Down (and Left/Right) to step the value, so the app must not repurpose them for
    ///     focus.
    /// </summary>
    public override bool HandlesDirectionalKeys => true;

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is Stepper s)
        {
            Step = s.Step;
            Min = s.Min;
            Max = s.Max;
            OnChanged = s.OnChanged;
            Enabled = s.Enabled;
            Value = Clamp(s.Value);
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: Value,
            value2: _hoverHalf,
            value3: _pressHalf,
            value4: Enabled,
            value5: Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        // 18×22 halves are the smallest targets in the set. Give each half a full finger box on a
        // phone — Paint and HalfAt both derive from Bounds, so the geometry follows for free.
        _size = c.Constrain(
            new Size(
                width: TouchMetrics.Pick(GroupWidth),
                height: TouchMetrics.Pick(HalfHeight) * 2f
            )
        );
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        float radius = Radii.Sm;
        float halfH = Bounds.Height / 2f;
        var upRect = new Rect(
            x: Bounds.X,
            y: Bounds.Y,
            width: Bounds.Width,
            height: halfH
        );
        var downRect = new Rect(
            x: Bounds.X,
            y: Bounds.Y + halfH,
            width: Bounds.Width,
            height: Bounds.Height - halfH
        );

        // Group surface.
        var baseFill = Enabled ? _theme.Fill2 : _theme.Fill3;
        paint.AddRect(bounds: Bounds, color: baseFill, radius: radius);

        // Per-half interaction tint.
        if (Enabled)
        {
            PaintHalfState(
                paint: paint,
                half: upRect,
                index: 0,
                radius: radius,
                top: true
            );
            PaintHalfState(
                paint: paint,
                half: downRect,
                index: 1,
                radius: radius,
                top: false
            );
        }

        // Hairline divider between the two halves.
        var sep = new Rect(
            x: Bounds.X,
            y: Bounds.Y + halfH - 0.5f,
            width: Bounds.Width,
            height: 1f
        );
        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: radius);
        paint.AddRect(bounds: sep, color: _theme.Separator);

        // Chevron glyphs.
        var upColor = GlyphColor(index: 0, enabledForDir: CanIncrement);
        var downColor = GlyphColor(index: 1, enabledForDir: CanDecrement);
        PaintChevron(
            paint: paint,
            box: upRect,
            up: true,
            color: upColor
        );
        PaintChevron(
            paint: paint,
            box: downRect,
            up: false,
            color: downColor
        );

        if (Focused && Enabled)
            paint.AddFocusRing(bounds: Bounds, radius: radius, theme: _theme);
    }

    private void PaintHalfState(PaintList paint, Rect half, int index, float radius, bool top)
    {
        Color? tint = null;
        if (_pressHalf == index) tint = _theme.Fill1;
        else if (_hoverHalf == index) tint = _theme.Fill2;
        if (tint is not { } t) return;

        // Round only the outer corners so the inner edge meets the divider flush.
        // A simple rounded rect reads fine at this size; keep it lean.
        var inset = top
            ? new Rect(
                x: half.X,
                y: half.Y,
                width: half.Width,
                height: half.Height - 0.5f
            )
            : new Rect(
                x: half.X,
                y: half.Y + 0.5f,
                width: half.Width,
                height: half.Height - 0.5f
            );
        paint.AddRect(bounds: inset, color: t, radius: radius);
    }

    private Color GlyphColor(int index, bool enabledForDir)
    {
        var c = _theme.OnSurface;
        if (!Enabled || !enabledForDir) return StateStyle.Disabled(c);
        if (_pressHalf == index) return c.Darken(StateStyle.PressedDarken);
        return c;
    }

    /// <summary>Draws a chevron (▲ / ▼) from two short dabbed strokes meeting at the apex.</summary>
    private static void PaintChevron(PaintList paint, Rect box, bool up, Color color)
    {
        float s = MathF.Min(x: box.Width, y: box.Height);
        float stroke = MathF.Max(x: 1.25f, y: s * 0.1f);

        float cx = box.X + (box.Width / 2f);
        float cy = box.Y + (box.Height / 2f);
        float halfW = box.Width * 0.22f;
        float halfH = box.Height * 0.16f;

        // Apex sits towards the pointing direction; the two legs splay to the base.
        float apexY = up ? cy - halfH : cy + halfH;
        float baseY = up ? cy + halfH : cy - halfH;

        StrokeLine(
            paint: paint,
            x0: cx - halfW,
            y0: baseY,
            x1: cx,
            y1: apexY,
            w: stroke,
            color: color
        );
        StrokeLine(
            paint: paint,
            x0: cx,
            y0: apexY,
            x1: cx + halfW,
            y1: baseY,
            w: stroke,
            color: color
        );
    }

    /// <summary>Approximates a short line with a chain of small round dabs (no native line primitive).</summary>
    private static void StrokeLine(PaintList paint, float x0, float y0, float x1, float y1, float w,
        Color color)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float len = MathF.Sqrt((dx * dx) + (dy * dy));
        float steps = MathF.Max(x: 1f, y: MathF.Ceiling(len / (w * 0.4f)));
        float half = w / 2f;

        for (float i = 0f; i <= steps; i++)
        {
            float t = i / steps;
            float px = x0 + (dx * t);
            float py = y0 + (dy * t);
            paint.AddRect(
                bounds: new Rect(
                    x: px - half,
                    y: py - half,
                    width: w,
                    height: w
                ),
                color: color,
                radius: half
            );
        }
    }

    private float Clamp(float v)
    {
        if (Min is { } mn && v < mn) v = mn;
        if (Max is { } mx && v > mx) v = mx;
        return v;
    }

    private void Bump(float delta)
    {
        if (!Enabled) return;
        float next = Clamp(Value + delta);
        if (next == Value) return;
        Value = next;
        OnChanged?.Invoke(Value);
        MarkNeedsPaint();
    }

    private int HalfAt(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return -1;
        return point.Y < Bounds.Y + (Bounds.Height / 2f) ? 0 : 1;
    }

    public override void OnPointerEnter()
    {
        // Actual half resolved on move; nothing to do until we have a position.
    }

    public override void OnPointerExit()
    {
        if (_hoverHalf == -1 && _pressHalf == -1) return;
        _hoverHalf = -1;
        _pressHalf = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        int half = Enabled ? HalfAt(point) : -1;
        if (half == _hoverHalf) return;
        _hoverHalf = half;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled) return;
        int half = HalfAt(point);
        if (half == -1) return;
        _pressHalf = half;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_pressHalf != -1 && Enabled && HalfAt(point) == _pressHalf)
            Bump(_pressHalf == 0 ? Step : -Step);
        if (_pressHalf != -1)
        {
            _pressHalf = -1;
            MarkNeedsPaint();
        }
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!Enabled) return;
        switch (scancode)
        {
            case 82: // Up
                _pressHalf = down ? 0 : -1;
                MarkNeedsPaint();
                if (!down) Bump(Step);
                break;
            case 81: // Down
                _pressHalf = down ? 1 : -1;
                MarkNeedsPaint();
                if (!down) Bump(-Step);
                break;
        }
    }
}
