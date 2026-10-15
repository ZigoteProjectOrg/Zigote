using System.Globalization;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     The GNOME Clocks time editor: vertical spin buttons for hours and minutes — an arrow above,
///     the big zero-padded number, an arrow below — with an AM/PM segment when the clock is
///     12-hour. Both numbers wrap (59 → 00) without carrying into the other, as GtkSpinButton's
///     <c>wrap</c> does.
///     <para>
///         Keys: Left/Right pick a segment, Up/Down step it, digits type straight into it and move
///         on once it is full, A/P set the half of the day. The wheel steps the segment under it
///         once the picker has focus (or always, outside a scrolling page).
///     </para>
/// </summary>
public sealed class AdwTimePicker : Widget
{
    private const float ValueW = 64f;
    private const float ValueH = 52f;
    private const float ArrowH = 30f;
    private const float Gap = 4f;
    private const float ColonW = 18f;
    private const float NumberSize = 28f;

    private const int Hour = 0, Minute = 1, Meridiem = 2;

    private static readonly string[] TwoDigits = [
        .. Enumerable.Range(start: 0, count: 60).Select(i => i.ToString(
                format: "00",
                provider: CultureInfo.InvariantCulture
            )
        ),
    ];

    private int _focusSegment;
    private (int Segment, int Dir) _hover = (-1, 0);
    private (int Segment, int Dir) _pressed = (-1, 0);
    private ThemeData _theme = ThemeData.Dark;
    private int _typed; // digits typed into the focused segment so far
    private TimeOnly _value;
    private float _wheel;
    private int _wheelSegment = -1; // segment under the pointer, for the wheel

    public AdwTimePicker(TimeOnly? value = null, Action<TimeOnly>? onChanged = null)
    {
        _value = value ?? new TimeOnly(hour: 9, minute: 0);
        OnChanged = onChanged;
        string pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
        Use24Hour = pattern.Contains('H');
    }

    public TimeOnly Value
    {
        get => _value;
        set
        {
            var v = new TimeOnly(hour: value.Hour, minute: value.Minute);
            if (v == _value) return;
            _value = v;
            MarkNeedsPaint();
        }
    }

    public Action<TimeOnly>? OnChanged { get; set; }

    /// <summary>24-hour clock (no AM/PM segment). Defaults to the current culture's convention.</summary>
    public bool Use24Hour
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (_focusSegment == Meridiem) _focusSegment = Minute;
            MarkNeedsLayout();
        }
    }

    /// <summary>Minutes per step of the minute spinner — 5 or 15 for alarm-style pickers.</summary>
    public int MinuteStep
    {
        get;
        set => field = Math.Clamp(value: value, min: 1, max: 30);
    } = 1;

    /// <summary>Accessible name; "Time" when unset.</summary>
    public string? SemanticsLabel { get; set; }

    public override bool Focusable => true;
    public override bool HandlesDirectionalKeys => true;

    private int Segments => Use24Hour ? 2 : 3;

    private Size Natural => new(
        width: (ValueW * 2f) + ColonW + (Use24Hour ? 0f : (Gap * 2f) + ValueW),
        height: (ArrowH * 2f) + ValueH + (Gap * 2f)
    );

    private static string Designator(bool pm)
    {
        var f = CultureInfo.CurrentCulture.DateTimeFormat;
        string d = pm ? f.PMDesignator : f.AMDesignator;
        return d.Length > 0 ? d : pm ? "PM" : "AM";
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private float SegmentX(int segment) => Bounds.X + segment switch {
        Hour => 0f,
        Minute => ValueW + ColonW,
        _ => (ValueW * 2f) + ColonW + (Gap * 2f),
    };

    private Rect ValueRect(int segment) =>
        new(
            x: SegmentX(segment),
            y: Bounds.Y + ArrowH + Gap,
            width: ValueW,
            height: ValueH
        );

    private Rect ArrowRect(int segment, int dir) => new(
        x: SegmentX(segment),
        y: dir > 0 ? Bounds.Y : Bounds.Y + ArrowH + ValueH + (Gap * 2f),
        width: ValueW,
        height: ArrowH
    );

    /// <summary>Segment under the point and what part: +1 up arrow, -1 down arrow, 0 the number.</summary>
    private (int Segment, int Dir) PartAt(Offset point)
    {
        for (int s = 0; s < Segments; s++)
        {
            if (ArrowRect(segment: s, dir: 1).Contains(px: point.X, py: point.Y)) return (s, 1);
            if (ArrowRect(segment: s, dir: -1).Contains(px: point.X, py: point.Y)) return (s, -1);
            if (ValueRect(s).Contains(px: point.X, py: point.Y)) return (s, 0);
        }

        return (-1, 0);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        return c.Constrain(Natural);
    }

    public override void Layout(Offset origin)
    {
        var size = Natural;
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: size.Width,
            height: size.Height
        );
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        var p = AdwPalette.For(_theme);
        int hour = _value.Hour;
        string hourText = Use24Hour ? TwoDigits[hour] : TwoDigits[hour % 12 == 0 ? 12 : hour % 12];
        PaintSegment(
            paint: paint,
            p: p,
            segment: Hour,
            text: hourText,
            size: NumberSize
        );
        PaintSegment(
            paint: paint,
            p: p,
            segment: Minute,
            text: TwoDigits[_value.Minute],
            size: NumberSize
        );
        if (!Use24Hour)
        {
            PaintSegment(
                paint: paint,
                p: p,
                segment: Meridiem,
                text: Designator(hour >= 12),
                size: AdwTypography.Title4.Size
            );
        }

        var colon = new Rect(
            x: Bounds.X + ValueW,
            y: ValueRect(Hour).Y,
            width: ColonW,
            height: ValueH
        );
        CenterText(
            paint: paint,
            text: ":",
            box: colon,
            color: p.WindowFg,
            size: NumberSize
        );
    }

    private void PaintSegment(PaintList paint, AdwColors p, int segment, string text, float size)
    {
        var box = ValueRect(segment);
        paint.AddRect(bounds: box, color: p.ButtonFill, radius: AdwMetrics.ControlRadius);
        CenterText(
            paint: paint,
            text: text,
            box: box,
            color: p.WindowFg,
            size: size
        );
        if (Focused && segment == _focusSegment)
            paint.AddFocusRing(bounds: box, radius: AdwMetrics.ControlRadius, theme: _theme);

        for (int dir = 1; dir >= -1; dir -= 2)
        {
            var r = ArrowRect(segment: segment, dir: dir);
            if (_pressed == (segment, dir) || _hover == (segment, dir))
            {
                paint.AddRect(
                    bounds: r,
                    color: _pressed == (segment, dir) ? p.ActiveFill : p.HoverFill,
                    radius: AdwMetrics.ControlRadius
                );
            }

            Icons.Draw(
                paint: paint,
                glyph: dir > 0 ? MaterialIcons.ExpandLess : MaterialIcons.ExpandMore,
                box: r,
                color: p.WindowFg,
                size: 20f
            );
        }
    }

    private static void CenterText(PaintList paint, string text, Rect box, Color color, float size)
    {
        float w = TextMeasure.Width(text: text, fontSize: size, weight: FontWeight.Bold);
        float baseline = TextBaseline.Centered(box.Y, box.Height, size);
        paint.AddText(
            text: text,
            baselineX: box.X + ((box.Width - w) / 2f),
            baselineY: baseline,
            color: color,
            fontSize: size,
            fontWeight: FontWeight.Bold
        );
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    /// <summary>Step <paramref name="segment" /> by <paramref name="dir" />, wrapping within it.</summary>
    public void Step(int segment, int dir)
    {
        int h = _value.Hour, m = _value.Minute;
        switch (segment)
        {
            case Hour:
                h = (((h + dir) % 24) + 24) % 24;
                break;
            case Minute:
                // Snap onto the step grid first, so 07 with a step of 5 goes to 10, not 12.
                int snapped = dir > 0
                    ? (m / MinuteStep * MinuteStep) + MinuteStep
                    : ((m + MinuteStep - 1) / MinuteStep * MinuteStep) - MinuteStep;
                m = ((snapped % 60) + 60) % 60;
                break;
            default:
                h = (h + 12) % 24;
                break;
        }

        Commit(new TimeOnly(hour: h, minute: m));
    }

    private void Commit(TimeOnly next)
    {
        if (next == _value) return;
        _value = next;
        MarkNeedsPaint();
        OnChanged?.Invoke(next);
    }

    private void Focus(int segment)
    {
        _focusSegment = Math.Clamp(value: segment, min: 0, max: Segments - 1);
        _typed = 0;
        MarkNeedsPaint();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Left:
                Focus(_focusSegment - 1);
                break;
            case KeyCode.Right:
                Focus(_focusSegment + 1);
                break;
            case KeyCode.Up:
                _typed = 0;
                Step(segment: _focusSegment, dir: 1);
                break;
            case KeyCode.Down:
                _typed = 0;
                Step(segment: _focusSegment, dir: -1);
                break;
        }
    }

    public override void OnTextInput(string text)
    {
        foreach (char ch in text)
        {
            if (_focusSegment == Meridiem || char.ToUpperInvariant(ch) is 'A' or 'P')
            {
                bool pm = char.ToUpperInvariant(ch) == 'P';
                if (char.ToUpperInvariant(ch) is 'A' or 'P' && !Use24Hour &&
                    (_value.Hour >= 12) != pm)
                    Step(segment: Meridiem, dir: 1);
                continue;
            }

            if (ch is < '0' or > '9') continue;
            TypeDigit(ch - '0');
        }
    }

    /// <summary>
    ///     Two digits per segment, the first one alone when no second could follow it ("7" in the
    ///     hours is 07 — nothing above 2 starts a two-digit hour); a full segment moves on.
    /// </summary>
    private void TypeDigit(int digit)
    {
        bool hours = _focusSegment == Hour;
        int max = hours ? Use24Hour ? 23 : 12 : 59;
        int current = hours ? Use24Hour ? _value.Hour : _value.Hour % 12 : _value.Minute;
        int typed = _typed == 0 ? digit : Math.Min(val1: (current * 10) + digit, val2: max);
        _typed++;

        if (hours)
        {
            int h = typed;
            if (!Use24Hour)
            {
                bool pm = _value.Hour >= 12;
                h = (typed % 12) + (pm ? 12 : 0);
            }

            Commit(new TimeOnly(hour: h, minute: _value.Minute));
        }
        else
            Commit(new TimeOnly(hour: _value.Hour, minute: typed));

        if (_typed >= 2 || typed * 10 > max) Focus(_focusSegment + 1);
        else MarkNeedsPaint();
    }

    public override MouseCursor? GetCursor(Offset point) =>
        PartAt(point).Segment >= 0 ? MouseCursor.Pointer : null;

    public override void OnPointerMove(Offset point)
    {
        var part = PartAt(point);
        _wheelSegment = part.Segment;
        if (part.Dir == 0) part = (-1, 0); // only the arrows have a hover state
        if (part == _hover) return;
        _hover = part;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        _wheelSegment = -1;
        if (_hover.Segment < 0) return;
        _hover = (-1, 0);
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);
        var part = PartAt(point);
        if (part.Segment < 0) return;
        Focus(part.Segment);
        if (part.Dir == 0)
        {
            // The AM/PM box is a toggle; a number box just takes focus for typing.
            if (part.Segment == Meridiem) Step(segment: Meridiem, dir: 1);
            return;
        }

        _pressed = part;
        Step(segment: part.Segment, dir: part.Dir);
    }

    public override void OnPointerUp(Offset point)
    {
        if (_pressed.Segment < 0) return;
        _pressed = (-1, 0);
        MarkNeedsPaint();
    }

    public override void OnPointerCancel() => OnPointerUp(default);

    public override void OnScroll(float dx, float dy)
    {
        // Spin only once focused: hovering past a picker while scrolling a form must scroll it.
        if (!Focused && ScrollParent is not null)
        {
            base.OnScroll(dx: dx, dy: dy);
            return;
        }

        _wheel += dy;
        if (MathF.Abs(_wheel) < 1f) return;
        Step(
            segment: _wheelSegment >= 0 ? _wheelSegment : _focusSegment,
            dir: _wheel > 0f ? 1 : -1
        );
        _wheel = 0f;
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Group;
        config.Label = SemanticsLabel ?? "Time";
        config.Value = _value.ToString(format: "t", provider: CultureInfo.CurrentCulture);
        config.Actions =
            SemanticsAction.Increase | SemanticsAction.Decrease | SemanticsAction.Focus;
        config.AddFlag(flag: SemanticsFlags.Focusable, on: true).AddFlag(
            flag: SemanticsFlags.Focused,
            on: Focused
        );
    }

    public override int DebugStateHash() =>
        HashCode.Combine(
            value1: _value,
            value2: _focusSegment,
            value3: _hover,
            value4: _pressed,
            value5: Use24Hour
        );
}
