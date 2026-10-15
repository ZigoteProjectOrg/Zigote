using System.Globalization;
using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     GtkCalendar the Adwaita way: a month grid under <c>‹ Month ›  ‹ Year ›</c> steppers. Weekday
///     order and names follow <see cref="CultureInfo.CurrentCulture" />; today is accent-coloured,
///     the selection is an accent disc, days of the neighbouring months are faded and days outside
///     <see cref="MinDate" />…<see cref="MaxDate" /> cannot be picked. Changing month slides the
///     grid sideways on <see cref="AdwMotion.Reveal" />.
///     <para>
///         Keys: arrows move the selection by a day or a week, Page Up/Down by a month (with Shift,
///         a year), Home/End to the ends of the month, Enter or Space activates. A sideways swipe
///         pages through months, and so does the wheel when the calendar is not inside a scrolling
///         page (in a popover, say).
///     </para>
/// </summary>
public sealed class AdwCalendar : Widget
{
    private const int Weeks = 6; // always six rows, so the popover never changes height
    private const float HeaderH = 34f;
    private const float WeekdayH = 24f;
    private const float Gap = 4f;
    private const float StepperW = 28f;

    // Hit targets: header steppers, then 100 + cell index for days.
    private const int PrevMonth = 1, NextMonth = 2, PrevYear = 3, NextYear = 4, DayBase = 100;

    private static readonly string[] DayNumbers = [
        .. Enumerable.Range(start: 0, count: 32)
            .Select(i => i.ToString(CultureInfo.InvariantCulture)),
    ];

    private readonly AnimationController _slide;
    private float _cell = 34f;
    private DateOnly _from; // the month sliding out
    private int _hover = -1;
    private DateOnly _month; // first day of the month on screen
    private int _pressed = -1;
    private float _slideDir = 1f;
    private ThemeData _theme = ThemeData.Dark;
    private DateOnly _value;
    private float _wheel;
    private string _yearText = "";
    private int _yearTextFor = -1;

    public AdwCalendar(DateOnly? value = null, Action<DateOnly>? onChanged = null)
    {
        _value = value ?? DateOnly.FromDateTime(DateTime.Today);
        _month = FirstOfMonth(_value);
        _from = _month;
        OnChanged = onChanged;
        FirstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        _slide = new AnimationController(durationSeconds: 0.25f, vsync: this) {
            Curve = Curves.EaseOut,
            Spec = AdwMotion.Reveal, // paging twice quickly keeps the grid moving
        };
        _slide.OnTick += MarkNeedsPaint;
    }

    /// <summary>The selected day. Setting it also brings its month into view.</summary>
    public DateOnly Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            ShowMonth(value);
            MarkNeedsPaint();
        }
    }

    /// <summary>The selection changed through the calendar itself (click, key).</summary>
    public Action<DateOnly>? OnChanged { get; set; }

    /// <summary>
    ///     A day was clicked, or Enter/Space pressed — the moment a date button closes its popover.
    ///     Fires after <see cref="OnChanged" />.
    /// </summary>
    public Action<DateOnly>? OnDayActivated { get; set; }

    /// <summary>Earliest selectable day; earlier ones are faded and inert.</summary>
    public DateOnly? MinDate { get; set; }

    /// <summary>Latest selectable day.</summary>
    public DateOnly? MaxDate { get; set; }

    /// <summary>The leftmost column. Defaults to the current culture's.</summary>
    public DayOfWeek FirstDayOfWeek { get; set; }

    /// <summary>
    ///     GtkCalendar's day marks: days for which this returns true get an accent dot under the
    ///     number — events, deadlines, entries in a journal.
    /// </summary>
    public Func<DateOnly, bool>? IsMarked { get; set; }

    /// <summary>Accessible name; "Calendar" when unset.</summary>
    public string? SemanticsLabel { get; set; }

    /// <summary>The first day of the month currently shown.</summary>
    public DateOnly DisplayedMonth => _month;

    public override bool Focusable => true;
    public override bool HandlesDirectionalKeys => true;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    // ── Geometry ──────────────────────────────────────────────────────────────

    private Rect GridRect => new(
        x: Bounds.X,
        y: Bounds.Y + HeaderH + Gap + WeekdayH,
        width: _cell * 7f,
        height: _cell * Weeks
    );

    private static DateOnly FirstOfMonth(DateOnly d) => new(year: d.Year, month: d.Month, day: 1);

    /// <summary>Bring the month containing <paramref name="day" /> into view, sliding toward it.</summary>
    public void ShowMonth(DateOnly day)
    {
        var target = FirstOfMonth(day);
        if (target == _month) return;
        _from = _month;
        _slideDir = target > _month ? 1f : -1f;
        _month = target;
        _slide.Dismiss();
        // Off screen there is nothing to watch slide: arrive already settled.
        if (Owner is null) _slide.Complete();
        else _slide.Forward();
        MarkNeedsPaint();
    }

    protected override void OnMount() => _slide.AttachTicker(this);

    private DateOnly GridStart(DateOnly month)
    {
        int lead = ((int)month.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        return month.AddDays(-lead);
    }

    private Rect CellRect(Rect grid, int index) => new(
        x: grid.X + (index % 7 * _cell),
        y: grid.Y + (index / 7 * _cell),
        width: _cell,
        height: _cell
    );

    private Rect StepperRect(int target)
    {
        float half = Bounds.Width / 2f;
        float x = target switch {
            PrevMonth => Bounds.X,
            NextMonth => Bounds.X + half - StepperW,
            PrevYear => Bounds.X + half,
            _ => Bounds.Right - StepperW,
        };
        return new Rect(
            x: x,
            y: Bounds.Y + ((HeaderH - StepperW) / 2f),
            width: StepperW,
            height: StepperW
        );
    }

    private bool InRange(DateOnly d) =>
        (MinDate is not { } min || d >= min) && (MaxDate is not { } max || d <= max);

    private int TargetAt(Offset point)
    {
        for (int t = PrevMonth; t <= NextYear; t++)
        {
            if (StepperRect(t).Contains(px: point.X, py: point.Y))
                return t;
        }

        var grid = GridRect;
        if (!grid.Contains(px: point.X, py: point.Y)) return -1;
        int col = Math.Clamp(value: (int)((point.X - grid.X) / _cell), min: 0, max: 6);
        int row = Math.Clamp(value: (int)((point.Y - grid.Y) / _cell), min: 0, max: Weeks - 1);
        int index = (row * 7) + col;
        return InRange(GridStart(_month).AddDays(index)) ? DayBase + index : -1;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        // A finger needs the full target; on a pointer GTK's ~34px day cells.
        _cell = AdwTouchMetrics.IsCompact ? 44f : 34f;
        return c.Constrain(
            new Size(width: _cell * 7f, height: HeaderH + Gap + WeekdayH + (_cell * Weeks))
        );
    }

    public override void Layout(Offset origin)
    {
        var size = new Size(width: _cell * 7f, height: HeaderH + Gap + WeekdayH + (_cell * Weeks));
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
        PaintHeader(paint: paint, p: p);
        PaintWeekdays(paint: paint, p: p);

        var grid = GridRect;
        float t = _slide.Status is AnimationStatus.Forward
            ? Math.Clamp(value: _slide.Value, min: 0f, max: 1f)
            : 1f;
        if (t >= 1f)
        {
            PaintMonth(
                paint: paint,
                p: p,
                month: _month,
                grid: grid,
                interactive: true
            );
            return;
        }

        // Both months slide by one grid width; the clip keeps them inside the calendar.
        paint.AddClipStart(grid);
        float w = grid.Width;
        PaintMonth(
            paint: paint,
            p: p,
            month: _from,
            grid: Shift(rect: grid, dx: -_slideDir * t * w),
            interactive: false
        );
        PaintMonth(
            paint: paint,
            p: p,
            month: _month,
            grid: Shift(rect: grid, dx: _slideDir * (1f - t) * w),
            interactive: false
        );
        paint.AddClipEnd();
    }

    private static Rect Shift(Rect rect, float dx) =>
        new(
            x: MathF.Round(rect.X + dx),
            y: rect.Y,
            width: rect.Width,
            height: rect.Height
        );

    private void PaintHeader(PaintList paint, AdwColors p)
    {
        float half = Bounds.Width / 2f;
        string monthName = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames[_month.Month - 1];
        if (_yearTextFor != _month.Year)
        {
            _yearTextFor = _month.Year;
            _yearText = _month.Year.ToString(CultureInfo.CurrentCulture);
        }

        var fg = p.WindowFg;
        CenterText(
            paint: paint,
            text: monthName,
            box: new Rect(
                x: Bounds.X,
                y: Bounds.Y,
                width: half,
                height: HeaderH
            ),
            color: fg,
            size: AdwTypography.Heading.Size,
            weight: FontWeight.Bold
        );
        CenterText(
            paint: paint,
            text: _yearText,
            box: new Rect(
                x: Bounds.X + half,
                y: Bounds.Y,
                width: half,
                height: HeaderH
            ),
            color: fg,
            size: AdwTypography.Heading.Size,
            weight: FontWeight.Bold
        );

        for (int target = PrevMonth; target <= NextYear; target++)
        {
            var r = StepperRect(target);
            // Flat circular buttons: no fill at rest, the hover/press wash otherwise.
            if (_hover == target || _pressed == target)
            {
                paint.AddRect(
                    bounds: r,
                    color: _pressed == target ? p.ActiveFill : p.HoverFill,
                    radius: StepperW / 2f
                );
            }

            bool back = target is PrevMonth or PrevYear;
            Icons.Draw(
                paint: paint,
                glyph: back ? Icons.ChevronLeft : Icons.ChevronRight,
                box: r,
                color: fg,
                size: 18f
            );
        }
    }

    private void PaintWeekdays(PaintList paint, AdwColors p)
    {
        string[] names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        float y = Bounds.Y + HeaderH + Gap;
        for (int col = 0; col < 7; col++)
        {
            int dow = ((int)FirstDayOfWeek + col) % 7;
            CenterText(
                paint: paint,
                text: names[dow],
                box: new Rect(
                    x: Bounds.X + (col * _cell),
                    y: y,
                    width: _cell,
                    height: WeekdayH
                ),
                color: p.DimLabel,
                size: AdwTypography.Caption.Size,
                weight: FontWeight.Bold
            );
        }
    }

    private void PaintMonth(PaintList paint, AdwColors p, DateOnly month, Rect grid,
        bool interactive)
    {
        var start = GridStart(month);
        var today = Today;
        float fs = AdwTypography.Body.Size;
        for (int i = 0; i < 7 * Weeks; i++)
        {
            var day = start.AddDays(i);
            var cell = CellRect(grid: grid, index: i);
            var disc = new Rect(
                x: cell.X + 2f,
                y: cell.Y + 2f,
                width: cell.Width - 4f,
                height: cell.Height - 4f
            );
            bool inMonth = day.Month == month.Month;
            bool enabled = InRange(day);
            bool selected = day == _value && inMonth;
            int target = DayBase + i;

            if (selected)
                paint.AddRect(bounds: disc, color: p.AccentBg, radius: disc.Height / 2f);
            else if (interactive && enabled && (_hover == target || _pressed == target))
            {
                paint.AddRect(
                    bounds: disc,
                    color: _pressed == target ? p.ActiveFill : p.HoverFill,
                    radius: disc.Height / 2f
                );
            }

            var color = selected ? p.AccentFg
                : day == today ? p.Accent
                : p.WindowFg;
            // `.other-month` and insensitive days: the label at 30%.
            if (!inMonth || !enabled) color = color.WithAlpha(color.A * 0.3f);
            bool strong = selected || day == today;
            CenterText(
                paint: paint,
                text: DayNumbers[day.Day],
                box: cell,
                color: color,
                size: fs,
                weight: strong ? FontWeight.Bold : FontWeight.Normal
            );

            if (IsMarked is { } marked && inMonth && marked(day))
            {
                float r = 2f;
                paint.AddRect(
                    bounds: new Rect(
                        x: cell.X + (cell.Width / 2f) - r,
                        y: cell.Bottom - 9f,
                        width: r * 2f,
                        height: r * 2f
                    ),
                    color: selected ? p.AccentFg : p.Accent,
                    radius: r
                );
            }

            if (selected && interactive && Focused)
                paint.AddFocusRing(bounds: disc, radius: disc.Height / 2f, theme: _theme);
        }
    }

    private static void CenterText(PaintList paint, string text, Rect box, Color color, float size,
        FontWeight weight)
    {
        float w = TextMeasure.Width(text: text, fontSize: size, weight: weight);
        float baseline = TextBaseline.Centered(box.Y, box.Height, size);
        paint.AddText(
            text: text,
            baselineX: box.X + ((box.Width - w) / 2f),
            baselineY: baseline,
            color: color,
            fontSize: size,
            fontWeight: weight
        );
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public override MouseCursor? GetCursor(Offset point) =>
        TargetAt(point) >= 0 ? MouseCursor.Pointer : null;

    public override void OnPointerMove(Offset point)
    {
        int target = TargetAt(point);
        if (target == _hover) return;
        _hover = target;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (_hover < 0) return;
        _hover = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);
        _pressed = TargetAt(point);
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        int pressed = _pressed;
        _pressed = -1;
        MarkNeedsPaint();
        if (pressed < 0 || TargetAt(point) != pressed) return;

        switch (pressed)
        {
            case PrevMonth:
                ShowMonth(_month.AddMonths(-1));
                return;
            case NextMonth:
                ShowMonth(_month.AddMonths(1));
                return;
            case PrevYear:
                ShowMonth(_month.AddYears(-1));
                return;
            case NextYear:
                ShowMonth(_month.AddYears(1));
                return;
        }

        Select(day: GridStart(_month).AddDays(pressed - DayBase), activate: true);
    }

    public override void OnPointerCancel()
    {
        _pressed = -1;
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        // A sideways swipe pages months. The wheel does too, but only where it has nothing else
        // to do — in a scrolling page it must keep scrolling the page, not hijack it.
        float delta = MathF.Abs(dx) > MathF.Abs(dy) ? -dx : ScrollParent is null ? dy : 0f;
        if (delta == 0f)
        {
            base.OnScroll(dx: dx, dy: dy);
            return;
        }

        // A wheel notch is one month; a trackpad's small deltas add up to one.
        _wheel += delta;
        if (MathF.Abs(_wheel) < 1f) return;
        ShowMonth(_month.AddMonths(_wheel > 0f ? -1 : 1));
        _wheel = 0f;
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        bool shift = (mods & Modifiers.Shift) != 0;
        var v = _value;
        DateOnly? next = (KeyCode)scancode switch {
            KeyCode.Left => v.AddDays(-1),
            KeyCode.Right => v.AddDays(1),
            KeyCode.Up => v.AddDays(-7),
            KeyCode.Down => v.AddDays(7),
            KeyCode.PageUp => shift ? v.AddYears(-1) : v.AddMonths(-1),
            KeyCode.PageDown => shift ? v.AddYears(1) : v.AddMonths(1),
            KeyCode.Home => FirstOfMonth(v),
            KeyCode.End => FirstOfMonth(v).AddMonths(1).AddDays(-1),
            _ => null,
        };

        if (next is { } day)
        {
            Select(day: Clamp(day), activate: false);
            return;
        }

        if ((KeyCode)scancode is KeyCode.Enter or KeyCode.KpEnter or KeyCode.Space)
            OnDayActivated?.Invoke(_value);
    }

    private DateOnly Clamp(DateOnly day)
    {
        if (MinDate is { } min && day < min) return min;
        if (MaxDate is { } max && day > max) return max;
        return day;
    }

    private void Select(DateOnly day, bool activate)
    {
        if (!InRange(day)) return;
        bool changed = day != _value;
        _value = day;
        ShowMonth(day);
        MarkNeedsPaint();
        if (changed) OnChanged?.Invoke(day);
        if (activate) OnDayActivated?.Invoke(day);
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Group;
        config.Label = SemanticsLabel ?? "Calendar";
        config.Value = _value.ToString(format: "D", provider: CultureInfo.CurrentCulture);
        config.Actions = SemanticsAction.Focus;
        config.AddFlag(flag: SemanticsFlags.Focusable, on: true).AddFlag(
            flag: SemanticsFlags.Focused,
            on: Focused
        );
    }

    public override int DebugStateHash() => HashCode.Combine(
        value1: _value,
        value2: _month,
        value3: _hover,
        value4: _pressed
    );
}
