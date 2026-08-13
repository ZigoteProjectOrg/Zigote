using Zigote.Core.Events;

namespace Zigote.UI.Material;

/// <summary>
///     A flat macOS <c>NSSearchField</c>: a capsule-shaped translucent <see cref="ThemeData.Fill1" />
///     field
///     with a leading magnifier glyph, a placeholder hint, and a trailing clear button that appears
///     once the
///     field has text. Text entry is delegated to an internal <see cref="TextField" /> so the app's
///     focus and
///     text-input routing works unchanged; this widget only owns the chrome (capsule, glyph, clear
///     button).
/// </summary>
public sealed class SearchField : Widget
{
    private readonly TextField _field;
    private bool _clearHovered;
    private bool _clearPressed;
    private bool _compact;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public SearchField(string hint = "Search", Action<string>? onChanged = null)
    {
        _field = new TextField(decoration: new InputDecoration(hint)) {
            Height = ControlMetrics.RegularHeight,
            MinWidth = 80f,
            // The capsule chrome (fill + focus ring) is owned by this widget; the inner field renders
            // text-only so there's no rectangular box/border/ring drawn inside the capsule.
            ShowBackground = false,
            OnChanged = s => OnChanged?.Invoke(s),
        };
        OnChanged = onChanged;
    }

    public string Text
    {
        get => _field.Text;
        set => _field.Text = value;
    }

    public string Hint
    {
        get => _field.Hint;
        set => _field.Hint = value;
    }

    public Action<string>? OnChanged { get; set; }
    public Action? OnClear { get; set; }

    public float Height { get; set; } = ControlMetrics.RegularHeight;
    public float MinWidth { get; set; } = 140f;

    // ── Layout geometry ───────────────────────────────────────────────────────

    /// <summary>
    ///     Side inset that the leading glyph and trailing clear button reserve, square with the
    ///     height.
    /// </summary>
    private float SideInset => MathF.Min(_size.Height, ControlMetrics.RegularHeight);

    private bool ShowClear => _field.Text.Length > 0;

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            _field.Text,
            _clearHovered,
            _clearPressed,
            Focused
        );
    }

    private Rect GlyphBox()
    {
        var s = _theme.FontSizeBody;
        var cy = Bounds.Y + (Bounds.Height - s) / 2f;
        return new Rect(
            Bounds.X + Spacing.Sm,
            cy,
            s,
            s
        );
    }

    private Rect ClearBox()
    {
        var d = _theme.FontSizeBody;
        var cy = Bounds.Y + (Bounds.Height - d) / 2f;
        return new Rect(
            Bounds.Right - Spacing.Sm - d,
            cy,
            d,
            d
        );
    }

    /// <summary>
    ///     Where the clear button is *pressed*, as opposed to drawn: the 13pt glyph keeps its size
    ///     on every platform, but a finger gets the whole trailing inset to aim at.
    /// </summary>
    private Rect ClearHitBox()
    {
        var box = ClearBox();
        if (!_compact) return box;
        var grow = MathF.Max(0f, (TouchMetrics.MinTarget - box.Width) / 2f);
        return new Rect(
            box.X - grow,
            MathF.Max(Bounds.Y, box.Y - grow),
            box.Width + grow * 2f,
            MathF.Min(Bounds.Height, box.Height + grow * 2f)
        );
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
        _size = c.Constrain(
            new Size(MathF.Max(MinWidth, SideInset * 2f + 40f), TouchMetrics.AtLeast(Height))
        );

        // The inner field occupies the area between the leading glyph and trailing clear button.
        var inner = MathF.Max(0f, _size.Width - SideInset * 2f);
        _field.Height = _size.Height;
        _field.MinWidth = inner;
        _field.Measure(
            new Constraints(
                inner,
                inner,
                _size.Height,
                _size.Height
            )
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
        _field.Layout(new Offset(origin.X + SideInset, origin.Y));
    }

    public override void Paint(PaintList paint)
    {
        var radius = Radii.Capsule;

        // Flat translucent capsule. No border — the fill alone reads as a search field on macOS.
        paint.AddRect(Bounds, _theme.Fill1, radius);

        PaintMagnifier(paint, GlyphBox(), _theme.Hint);

        _field.Paint(paint);

        if (ShowClear)
            PaintClear(paint, ClearBox());

        // This widget isn't itself focusable (hit-testing routes into the inner field), so the ring
        // tracks the field's focus and follows the capsule shape.
        if (_field.Focused)
            paint.AddFocusRing(Bounds, radius, _theme);
    }

    /// <summary>Vector magnifier: a hairline ring plus a short diagonal handle, theme-tinted.</summary>
    private static void PaintMagnifier(PaintList paint, Rect box, Color color)
    {
        // Ring occupies the upper-left ~70% of the glyph box; handle trails to the lower-right.
        var ring = new Rect(
            box.X,
            box.Y,
            box.Width * 0.7f,
            box.Height * 0.7f
        );
        paint.AddBorder(
            ring,
            color,
            Radii.Capsule,
            1.5f
        );

        var hx = ring.Right - ring.Width * 0.12f;
        var hy = ring.Bottom - ring.Height * 0.12f;
        var len = box.Width * 0.32f;
        // Approximate the 45° handle with a short rounded bar.
        paint.AddRect(
            new Rect(
                hx,
                hy,
                len,
                1.5f
            ),
            color,
            Radii.Capsule
        );
        paint.AddRect(
            new Rect(
                hx,
                hy,
                1.5f,
                len
            ),
            color,
            Radii.Capsule
        );
    }

    private void PaintClear(PaintList paint, Rect box)
    {
        var bg = StateStyle.Fill(_theme.Fill2, _clearHovered, _clearPressed);
        paint.AddRect(box, bg, Radii.Capsule);

        // Centred "×" glyph drawn as two crossed bars to stay crisp at small sizes.
        var pad = box.Width * 0.3f;
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var arm = box.Width / 2f - pad;
        var fg = _theme.OnSurface.WithAlpha(0.8f);
        paint.AddRect(
            new Rect(
                cx - arm,
                cy - 0.75f,
                arm * 2f,
                1.5f
            ),
            fg,
            Radii.Capsule
        );
        paint.AddRect(
            new Rect(
                cx - 0.75f,
                cy - arm,
                1.5f,
                arm * 2f
            ),
            fg,
            Radii.Capsule
        );
    }

    // ── Hit-testing & input ───────────────────────────────────────────────────
    //
    // The clear button is owned by this widget; everywhere else inside the capsule routes to the
    // inner TextField so the app focuses it (and starts text input) directly.

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        if (ShowClear && ClearHitBox().Contains(point.X, point.Y)) return this;

        var hit = _field.HitTest(point);
        if (hit != null) return hit;

        // Tapping the glyph / padding still focuses the field.
        return _field;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_field];
    }

    private void Clear()
    {
        if (_field.Text.Length == 0) return;
        _field.Text = string.Empty;
        OnChanged?.Invoke(string.Empty);
        OnClear?.Invoke();
        MarkNeedsPaint();
    }

    public override void OnPointerEnter()
    {
        if (_clearHovered) return;
        _clearHovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (!_clearHovered && !_clearPressed) return;
        _clearHovered = false;
        _clearPressed = false;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!ShowClear || !ClearHitBox().Contains(point.X, point.Y)) return;
        _clearPressed = true;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_clearPressed && ClearHitBox().Contains(point.X, point.Y))
            Clear();
        if (_clearPressed)
        {
            _clearPressed = false;
            MarkNeedsPaint();
        }
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        // Escape (scancode 41) clears the field, matching macOS search behaviour.
        if (down && scancode == 41 && ShowClear) Clear();
    }
}
