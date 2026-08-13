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
            Value,
            _hoverHalf,
            _pressHalf,
            Enabled,
            Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        // 18×22 halves are the smallest targets in the set. Give each half a full finger box on a
        // phone — Paint and HalfAt both derive from Bounds, so the geometry follows for free.
        _size = c.Constrain(
            new Size(TouchMetrics.Pick(GroupWidth), TouchMetrics.Pick(HalfHeight) * 2f)
        );
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        var radius = Radii.Sm;
        var halfH = Bounds.Height / 2f;
        var upRect = new Rect(
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            halfH
        );
        var downRect = new Rect(
            Bounds.X,
            Bounds.Y + halfH,
            Bounds.Width,
            Bounds.Height - halfH
        );

        // Group surface.
        var baseFill = Enabled ? _theme.Fill2 : _theme.Fill3;
        paint.AddRect(Bounds, baseFill, radius);

        // Per-half interaction tint.
        if (Enabled)
        {
            PaintHalfState(
                paint,
                upRect,
                0,
                radius,
                true
            );
            PaintHalfState(
                paint,
                downRect,
                1,
                radius,
                false
            );
        }

        // Hairline divider between the two halves.
        var sep = new Rect(
            Bounds.X,
            Bounds.Y + halfH - 0.5f,
            Bounds.Width,
            1f
        );
        paint.AddBorder(Bounds, _theme.Separator, radius);
        paint.AddRect(sep, _theme.Separator);

        // Chevron glyphs.
        var upColor = GlyphColor(0, CanIncrement);
        var downColor = GlyphColor(1, CanDecrement);
        PaintChevron(
            paint,
            upRect,
            true,
            upColor
        );
        PaintChevron(
            paint,
            downRect,
            false,
            downColor
        );

        if (Focused && Enabled)
            paint.AddFocusRing(Bounds, radius, _theme);
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
                half.X,
                half.Y,
                half.Width,
                half.Height - 0.5f
            )
            : new Rect(
                half.X,
                half.Y + 0.5f,
                half.Width,
                half.Height - 0.5f
            );
        paint.AddRect(inset, t, radius);
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
        var s = MathF.Min(box.Width, box.Height);
        var stroke = MathF.Max(1.25f, s * 0.1f);

        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var halfW = box.Width * 0.22f;
        var halfH = box.Height * 0.16f;

        // Apex sits towards the pointing direction; the two legs splay to the base.
        var apexY = up ? cy - halfH : cy + halfH;
        var baseY = up ? cy + halfH : cy - halfH;

        StrokeLine(
            paint,
            cx - halfW,
            baseY,
            cx,
            apexY,
            stroke,
            color
        );
        StrokeLine(
            paint,
            cx,
            apexY,
            cx + halfW,
            baseY,
            stroke,
            color
        );
    }

    /// <summary>Approximates a short line with a chain of small round dabs (no native line primitive).</summary>
    private static void StrokeLine(PaintList paint, float x0, float y0, float x1, float y1, float w,
        Color color)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        var steps = MathF.Max(1f, MathF.Ceiling(len / (w * 0.4f)));
        var half = w / 2f;

        for (var i = 0f; i <= steps; i++)
        {
            var t = i / steps;
            var px = x0 + dx * t;
            var py = y0 + dy * t;
            paint.AddRect(
                new Rect(
                    px - half,
                    py - half,
                    w,
                    w
                ),
                color,
                half
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
        var next = Clamp(Value + delta);
        if (next == Value) return;
        Value = next;
        OnChanged?.Invoke(Value);
        MarkNeedsPaint();
    }

    private int HalfAt(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return -1;
        return point.Y < Bounds.Y + Bounds.Height / 2f ? 0 : 1;
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
        var half = Enabled ? HalfAt(point) : -1;
        if (half == _hoverHalf) return;
        _hoverHalf = half;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled) return;
        var half = HalfAt(point);
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
