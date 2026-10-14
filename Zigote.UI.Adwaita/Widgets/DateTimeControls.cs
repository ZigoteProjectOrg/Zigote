using System.Globalization;

namespace Zigote.UI.Adwaita;

/// <summary>How the date and time controls print a value, in the current culture.</summary>
public static class AdwDateFormat
{
    /// <summary>
    ///     A medium date — "Oct 14, 2026", "14 Oct 2026", "14. Okt. 2026": the culture's long
    ///     pattern without the weekday and with the month abbreviated, which is what a button or a
    ///     row has room for. .NET has no medium style of its own.
    /// </summary>
    public static string Medium(DateOnly date)
    {
        var f = CultureInfo.CurrentCulture.DateTimeFormat;
        string pattern = f.LongDatePattern
            .Replace(oldValue: "dddd, ", newValue: "")
            .Replace(oldValue: ", dddd", newValue: "")
            .Replace(oldValue: "dddd ", newValue: "")
            .Replace(oldValue: "dddd", newValue: "")
            .Replace(oldValue: "MMMM", newValue: "MMM")
            .Trim(' ', ',');
        return date.ToString(format: pattern, provider: CultureInfo.CurrentCulture);
    }

    /// <summary>The culture's short time — "9:30 AM", "09:30".</summary>
    public static string Time(TimeOnly time) => time.ToString(
        format: "t",
        provider: CultureInfo.CurrentCulture
    );
}

/// <summary>
///     A date in a button — SwiftUI's compact <c>DatePicker</c>, Compose's docked picker. Shows the
///     date with a calendar icon; clicking opens an <see cref="AdwCalendar" /> in an
///     <see cref="AdwPopover" />, and picking a day closes it.
/// </summary>
public sealed class AdwDateButton : ComposedWidget
{
    private AdwButton? _button;
    private AdwButtonContent? _content;
    private DateOnly _value;

    public AdwDateButton(DateOnly? value = null, Action<DateOnly>? onChanged = null)
    {
        _value = value ?? DateOnly.FromDateTime(DateTime.Today);
        OnChanged = onChanged;
    }

    public DateOnly Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            if (_content is not null) _content.Label = Format(value);
        }
    }

    public Action<DateOnly>? OnChanged { get; set; }

    /// <inheritdoc cref="AdwCalendar.MinDate" />
    public DateOnly? MinDate { get; set; }

    /// <inheritdoc cref="AdwCalendar.MaxDate" />
    public DateOnly? MaxDate { get; set; }

    /// <inheritdoc cref="AdwCalendar.IsMarked" />
    public Func<DateOnly, bool>? IsMarked { get; set; }

    /// <summary>How the button prints the date. <see cref="AdwDateFormat.Medium" /> unless set.</summary>
    public Func<DateOnly, string> Format { get; set; } = AdwDateFormat.Medium;

    public bool Enabled
    {
        get;
        set => this.Set(field: ref field, value: value);
    } = true;

    protected override Widget Build(BuildContext context)
    {
        _content = new AdwButtonContent(
            iconName: MaterialIcons.CalendarToday,
            label: Format(_value)
        );
        _button = new AdwButton(onPressed: Enabled ? Open : null) { Content = _content };
        return _button;
    }

    private void Open()
    {
        if (_button is null) return;
        var calendar = new AdwCalendar(_value) {
            MinDate = MinDate,
            MaxDate = MaxDate,
            IsMarked = IsMarked,
        };
        var popover = new AdwPopover(child: calendar, anchor: _button.Bounds, app: Owner);
        calendar.OnChanged = d =>
        {
            Value = d;
            OnChanged?.Invoke(d);
        };
        calendar.OnDayActivated = _ => popover.Close();
        popover.Show();
    }
}

/// <summary>
///     A time in a button, the time twin of <see cref="AdwDateButton" />: clicking opens an
///     <see cref="AdwTimePicker" /> in a popover. The value updates as the spinners move; clicking
///     outside or Escape closes it.
/// </summary>
public sealed class AdwTimeButton : ComposedWidget
{
    private AdwButton? _button;
    private AdwButtonContent? _content;
    private TimeOnly _value;

    public AdwTimeButton(TimeOnly? value = null, Action<TimeOnly>? onChanged = null)
    {
        _value = value ?? new TimeOnly(hour: 9, minute: 0);
        OnChanged = onChanged;
    }

    public TimeOnly Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            if (_content is not null) _content.Label = AdwDateFormat.Time(value);
        }
    }

    public Action<TimeOnly>? OnChanged { get; set; }

    /// <inheritdoc cref="AdwTimePicker.MinuteStep" />
    public int MinuteStep { get; set; } = 1;

    /// <summary>Force a 24- or 12-hour picker; null follows the culture.</summary>
    public bool? Use24Hour { get; set; }

    public bool Enabled
    {
        get;
        set => this.Set(field: ref field, value: value);
    } = true;

    protected override Widget Build(BuildContext context)
    {
        _content = new AdwButtonContent(
            iconName: MaterialIcons.Schedule,
            label: AdwDateFormat.Time(_value)
        );
        _button = new AdwButton(onPressed: Enabled ? Open : null) { Content = _content };
        return _button;
    }

    private void Open()
    {
        if (_button is null) return;
        var picker = new AdwTimePicker(_value) { MinuteStep = MinuteStep };
        if (Use24Hour is { } h24) picker.Use24Hour = h24;
        picker.OnChanged = t =>
        {
            Value = t;
            OnChanged?.Invoke(t);
        };
        new AdwPopover(child: picker, anchor: _button.Bounds, app: Owner).Show();
    }
}

/// <summary>
///     A boxed-list row holding a date, laid out like <see cref="AdwComboRow" />: title (and
///     subtitle) on the left, the date dimmed at the end with a calendar icon. Activating the row
///     opens the calendar popover at the value.
/// </summary>
public sealed class AdwDateRow : ComposedWidget
{
    private IconGlyph? _icon;
    private DateOnly _value;
    private Label? _valueLabel;

    public AdwDateRow(string title = "", DateOnly? value = null, Action<DateOnly>? onChanged = null,
        string? subtitle = null)
    {
        Title = title;
        Subtitle = subtitle;
        _value = value ?? DateOnly.FromDateTime(DateTime.Today);
        OnChanged = onChanged;
    }

    public string Title { get; set; }
    public string? Subtitle { get; set; }

    public DateOnly Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            if (_valueLabel is not null) _valueLabel.Text = AdwDateFormat.Medium(value);
        }
    }

    public Action<DateOnly>? OnChanged { get; set; }

    /// <inheritdoc cref="AdwCalendar.MinDate" />
    public DateOnly? MinDate { get; set; }

    /// <inheritdoc cref="AdwCalendar.MaxDate" />
    public DateOnly? MaxDate { get; set; }

    /// <inheritdoc cref="AdwCalendar.IsMarked" />
    public Func<DateOnly, bool>? IsMarked { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        _valueLabel = new Label(
            text: AdwDateFormat.Medium(_value),
            style: AdwTypography.Body,
            color: p.DimLabel
        ) {
            MaxLines = 1,
        };
        _icon = new IconGlyph(
            glyph: MaterialIcons.CalendarToday,
            size: AdwMetrics.IconSize,
            color: p.DimLabel
        );
        return new AdwActionRow(title: Title, subtitle: Subtitle) {
            Suffixes = {
                _valueLabel,
                _icon,
            },
            OnActivated = Open,
        };
    }

    private void Open()
    {
        var calendar = new AdwCalendar(_value) {
            MinDate = MinDate,
            MaxDate = MaxDate,
            IsMarked = IsMarked,
        };
        var popover = new AdwPopover(
            child: calendar,
            anchor: AdwRowAnchor.Suffix(row: Bounds, a: _valueLabel, b: _icon),
            app: Owner
        );
        calendar.OnChanged = d =>
        {
            Value = d;
            OnChanged?.Invoke(d);
        };
        calendar.OnDayActivated = _ => popover.Close();
        popover.Show();
    }
}

/// <summary>The time twin of <see cref="AdwDateRow" />: the time at the end, a time picker on activation.</summary>
public sealed class AdwTimeRow : ComposedWidget
{
    private IconGlyph? _icon;
    private TimeOnly _value;
    private Label? _valueLabel;

    public AdwTimeRow(string title = "", TimeOnly? value = null, Action<TimeOnly>? onChanged = null,
        string? subtitle = null)
    {
        Title = title;
        Subtitle = subtitle;
        _value = value ?? new TimeOnly(hour: 9, minute: 0);
        OnChanged = onChanged;
    }

    public string Title { get; set; }
    public string? Subtitle { get; set; }

    public TimeOnly Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            if (_valueLabel is not null) _valueLabel.Text = AdwDateFormat.Time(value);
        }
    }

    public Action<TimeOnly>? OnChanged { get; set; }

    /// <inheritdoc cref="AdwTimePicker.MinuteStep" />
    public int MinuteStep { get; set; } = 1;

    /// <summary>Force a 24- or 12-hour picker; null follows the culture.</summary>
    public bool? Use24Hour { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        _valueLabel = new Label(
            text: AdwDateFormat.Time(_value),
            style: AdwTypography.Body,
            color: p.DimLabel
        ) {
            MaxLines = 1,
        };
        _icon = new IconGlyph(
            glyph: MaterialIcons.Schedule,
            size: AdwMetrics.IconSize,
            color: p.DimLabel
        );
        return new AdwActionRow(title: Title, subtitle: Subtitle) {
            Suffixes = {
                _valueLabel,
                _icon,
            },
            OnActivated = Open,
        };
    }

    private void Open()
    {
        var picker = new AdwTimePicker(_value) { MinuteStep = MinuteStep };
        if (Use24Hour is { } h24) picker.Use24Hour = h24;
        picker.OnChanged = t =>
        {
            Value = t;
            OnChanged?.Invoke(t);
        };
        new AdwPopover(
            child: picker,
            anchor: AdwRowAnchor.Suffix(row: Bounds, a: _valueLabel, b: _icon),
            app: Owner
        ).Show();
    }
}

internal static class AdwRowAnchor
{
    /// <summary>
    ///     libadwaita parents a row's popover to the value box at its end, not the whole row: the
    ///     span of the two suffix widgets, stopped short of the row's bottom so the arrow tip tucks
    ///     into the row padding (the <see cref="AdwComboRow" /> rule).
    /// </summary>
    public static Rect Suffix(Rect row, Widget? a, Widget? b)
    {
        if (a is null || b is null) return row;
        float left = MathF.Min(x: a.Bounds.X, y: b.Bounds.X);
        float right = MathF.Max(x: a.Bounds.Right, y: b.Bounds.Right);
        return new Rect(
            x: left,
            y: row.Y,
            width: right - left,
            height: row.Height - 6f
        );
    }
}
