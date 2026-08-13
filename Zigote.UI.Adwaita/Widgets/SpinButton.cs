using System.Globalization;
using Zigote.Core.Events;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSpinButton — the GNOME horizontal spin button: one linked
///     <see cref="AdwColors.ButtonFill" />
///     box (radius 9) with a flat minus button, the centred value, and a flat plus button,
///     separated by hairlines. Clamps to [Min, Max]; the unavailable side's glyph drops to half
///     opacity. Arrow keys step the value. While focused the value area is typeable, GNOME-style:
///     digits / '-' / '.' start a fresh pending buffer, Enter or focus loss commits (parse + clamp;
///     invalid text reverts), stepping cancels the buffer. Disabled drops the whole control to
///     <see cref="AdwStyle.DisabledOpacity" />.
/// </summary>
public sealed class AdwSpinButton : Widget, ITextInputClient
{
    private const float SegmentW = AdwMetrics.ButtonHeight; // square end buttons
    private const float MinValueW = 48f;
    private bool _compact;

    private string? _edit; // pending typed text; null = not editing
    private bool _enabled = true;
    private int _hoverZone; // -1 minus, +1 plus, 0 none
    private double _max;
    private double _min;
    private int _pressZone;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private double _value;

    public AdwSpinButton(double value = 0, double min = 0, double max = 100, double step = 1,
        Action<double>? onChanged = null)
    {
        _min = min;
        _max = max;
        _value = Math.Clamp(value: value, min: min, max: Math.Max(val1: min, val2: max));
        Step = step;
        OnChanged = onChanged;
    }

    public double Value
    {
        get => _value;
        set
        {
            // A value pushed from outside wins over whatever the user has half-typed: without this
            // the stale buffer keeps rendering (DisplayText prefers it) and the blur-commit would
            // later write the old text back over the new value.
            CancelEdit();
            // Range-safe: an inverted [Min, Max] is transient while a caller moves both ends, and
            // Math.Clamp throws on min > max — so no ordering constraint is imposed on setters.
            double v = Math.Clamp(value: value, min: _min, max: Math.Max(val1: _min, val2: _max));
            if (v == _value) return;
            _value = v;
            MarkNeedsLayout(); // the value text can change the centre width
        }
    }

    public double Min
    {
        get => _min;
        set
        {
            _min = value;
            Value = _value; // re-clamp into the new range
            MarkNeedsPaint(); // the minus glyph's availability may have flipped
        }
    }

    public double Max
    {
        get => _max;
        set
        {
            _max = value;
            Value = _value;
            MarkNeedsPaint();
        }
    }

    public double Step { get; set; }
    public Action<double>? OnChanged { get; set; }

    /// <summary>Disabled paints at <see cref="AdwStyle.DisabledOpacity" />, so flipping it repaints.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetPaint(field: ref _enabled, value: value);
    }

    /// <summary>Optional accessible name (the setting this spin button edits).</summary>
    public string? SemanticsLabel { get; set; }

    /// <inheritdoc cref="AdwEntry.Compact" />
    public bool Compact
    {
        get => _compact;
        set => SetLayout(field: ref _compact, value: value);
    }

    public override bool Focusable => Enabled;

    /// <summary>Arrow keys step the value, so they are never repurposed for focus traversal.</summary>
    public override bool HandlesDirectionalKeys => true;

    private string ValueText => _value.ToString("0.###");

    private string DisplayText => _edit ?? ValueText;

    private Rect MinusRect => new(
        x: Bounds.X,
        y: Bounds.Y,
        width: SegmentW,
        height: Bounds.Height
    );

    private Rect PlusRect => new(
        x: Bounds.Right - SegmentW,
        y: Bounds.Y,
        width: SegmentW,
        height: Bounds.Height
    );

    /// <summary>Static caret (no blink) so the frame loop can idle while focused.</summary>
    bool ITextInputClient.WantsCaretBlink => false;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Slider; // closest role: a steppable numeric value
        config.Label = SemanticsLabel;
        config.Value = DisplayText;
        config.Actions =
            SemanticsAction.Increase | SemanticsAction.Decrease | SemanticsAction.Focus;
        config.AddFlag(flag: SemanticsFlags.Focusable, on: Enabled)
            .AddFlag(flag: SemanticsFlags.Focused, on: Focused)
            .AddFlag(flag: SemanticsFlags.Disabled, on: !Enabled);
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is AdwSpinButton s)
        {
            Min = s.Min;
            Max = s.Max;
            Step = s.Step;
            Value = s.Value;
            OnChanged = s.OnChanged;
            Enabled = s.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: Value,
            value2: _edit,
            value3: _hoverZone,
            value4: _pressZone,
            value5: Enabled,
            value6: Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float textW = TextMeasure.Width(text: DisplayText, fontSize: _theme.FontSizeBody);
        float valueW = MathF.Max(x: MinValueW, y: textW + Spacing.Md);
        _size = c.Constrain(
            new Size(
                width: (SegmentW * 2f) + 2f + valueW,
                height: Compact ? AdwMetrics.CompactControlHeight : AdwMetrics.ButtonHeight
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
        if (!Enabled) paint.PushAlpha(AdwStyle.DisabledOpacity);

        // `spinbutton @extend %entry` — the box is an entry, so it carries the entry/button fill.
        var p = AdwPalette.For(_theme);
        paint.AddRect(bounds: Bounds, color: p.ButtonFill, radius: AdwMetrics.ControlRadius);

        // The +/− are FLAT buttons over that fill (7% hover, 16% press), clipped to the box; the
        // wash's square corners are invisible under the faint fill at a 9px radius.
        int zone = _pressZone != 0 ? _pressZone : _hoverZone;
        if (Enabled && zone != 0 && SideAvailable(zone))
        {
            paint.AddClipStart(Bounds);
            paint.AddRect(
                bounds: zone < 0 ? MinusRect : PlusRect,
                color: _pressZone != 0 ? p.ActiveFill : p.HoverFill
            );
            paint.AddClipEnd();
        }

        // `> button { border-color: color-mix(in srgb, currentColor 10%, transparent) }` — the
        // buttons are divided from the value by their own 1px border, not by the theme hairline.
        paint.AddRect(
            bounds: new Rect(
                x: MinusRect.Right,
                y: Bounds.Y,
                width: 1f,
                height: Bounds.Height
            ),
            color: p.ButtonFill
        );
        paint.AddRect(
            bounds: new Rect(
                x: PlusRect.X - 1f,
                y: Bounds.Y,
                width: 1f,
                height: Bounds.Height
            ),
            color: p.ButtonFill
        );

        // Glyphs — the unavailable side at half opacity.
        var fg = _theme.OnBackground;
        Icons.Draw(
            paint: paint,
            glyph: MaterialIcons.Remove,
            box: MinusRect,
            color: SideAvailable(-1) ? fg : fg.WithAlpha(fg.A * 0.5f),
            size: AdwMetrics.IconSize
        );
        Icons.Draw(
            paint: paint,
            glyph: Icons.Add,
            box: PlusRect,
            color: SideAvailable(1) ? fg : fg.WithAlpha(fg.A * 0.5f),
            size: AdwMetrics.IconSize
        );

        // Centred value text (the pending edit buffer while typing).
        float fs = _theme.FontSizeBody;
        float textW = TextMeasure.Width(text: DisplayText, fontSize: fs);
        float cx = Bounds.X + (Bounds.Width / 2f);
        float baseline = Bounds.Y + ((Bounds.Height - fs) / 2f) + (fs * 0.8f);
        paint.AddText(
            text: DisplayText,
            baselineX: cx - (textW / 2f),
            baselineY: baseline,
            color: fg,
            fontSize: fs
        );

        // ponytail: static caret bar as the editing cue — no blink, WantsCaretBlink is false so
        // the frame loop idles; upgrade to a Ticker-driven blink if it ever reads as dead.
        if (_edit is not null)
        {
            paint.AddRect(
                bounds: new Rect(
                    x: cx + (textW / 2f) + 1f,
                    y: Bounds.Y + ((Bounds.Height - fs) / 2f),
                    width: 1.5f,
                    height: fs
                ),
                color: fg
            );
        }

        if (Focused && Enabled)
            paint.AddFocusRing(bounds: Bounds, radius: AdwMetrics.ControlRadius, theme: _theme);

        if (!Enabled) paint.PopAlpha();
    }

    private bool SideAvailable(int zone) => zone < 0 ? Value > Min : Value < Max;

    private void StepBy(int dir)
    {
        CancelEdit(); // stepping always acts on the committed value
        double next = Math.Clamp(value: Value + (dir * Step), min: Min, max: Max);
        if (next == Value) return;
        Value = next;
        OnChanged?.Invoke(next);
    }

    private void CancelEdit()
    {
        if (_edit is null) return;
        _edit = null;
        MarkNeedsLayout();
    }

    /// <summary>Parse + clamp the pending buffer; invalid text reverts to the current value.</summary>
    private void CommitEdit()
    {
        if (_edit is null) return;
        string text = _edit;
        _edit = null;
        if (double.TryParse(
                s: text,
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out double v
            ))
        {
            v = Math.Clamp(value: v, min: Min, max: Max);
            if (v != _value)
            {
                _value = v;
                OnChanged?.Invoke(v);
            }
        }

        MarkNeedsLayout();
    }

    private int ZoneAt(Offset point)
    {
        if (MinusRect.Contains(px: point.X, py: point.Y)) return -1;
        if (PlusRect.Contains(px: point.X, py: point.Y)) return 1;
        return 0;
    }

    public override void OnPointerMove(Offset point)
    {
        int zone = ZoneAt(point);
        if (zone == _hoverZone) return;
        _hoverZone = zone;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (_hoverZone == 0 && _pressZone == 0) return;
        _hoverZone = 0;
        _pressZone = 0;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled) return;
        int zone = ZoneAt(point);
        if (zone == 0) return;
        _pressZone = zone;
        StepBy(zone);
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_pressZone == 0) return;
        _pressZone = 0;
        MarkNeedsPaint();
    }

    public override void OnPointerCancel() => OnPointerUp(default);

    // ponytail: raw key handling only for editing keys; printable characters arrive via
    // OnTextInput (ITextInputClient engages the host IME), same route as Material TextField.
    // Escape never reaches OnKey — the app's HandleEscape clears focus first, so Escape ends as a
    // blur-commit instead of a revert; a true revert needs IKeyboardTrap, which would also steal
    // Tab traversal. Tab itself commits via the focus-loss path.
    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || !Enabled) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Right or KeyCode.Up:
                StepBy(1);
                break;
            case KeyCode.Left or KeyCode.Down:
                StepBy(-1);
                break;
            case KeyCode.Enter or KeyCode.KpEnter:
                CommitEdit();
                break;
            case KeyCode.Escape:
                CancelEdit(); // reached only when focus isn't cleared first (see above)
                break;
            case KeyCode.Backspace:
                if (_edit is { Length: > 0 })
                {
                    _edit = _edit[..^1];
                    MarkNeedsLayout();
                }

                break;
        }
    }

    public override void OnTextInput(string text)
    {
        if (!Enabled) return;
        bool changed = false;
        foreach (char ch in text)
        {
            if (char.IsAsciiDigit(ch) || ch is '-' or '.')
            {
                _edit = (_edit ?? "") + ch; // first char starts a fresh buffer
                changed = true;
            }
        }

        if (changed) MarkNeedsLayout();
    }

    protected override void OnFocusChanged(bool focused)
    {
        if (!focused) CommitEdit();
    }
}
