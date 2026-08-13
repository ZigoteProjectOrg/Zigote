using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>AdwSwitchRow — an <see cref="AdwActionRow" /> with a switch suffix; clicking the row toggles.</summary>
public sealed class AdwSwitchRow : ComposedWidget
{
    private AdwActionRow? _row;
    private AdwSwitch? _switch;
    private bool _value;
    private string _title;
    private string? _subtitle;
    private bool _enabled = true;

    public AdwSwitchRow(
        string title = "",
        string? subtitle = null,
        bool value = false,
        Action<bool>? onChanged = null)
    {
        _title = title;
        _subtitle = subtitle;
        _value = value;
        OnChanged = onChanged;
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(ref _subtitle, value);
    }

    public Action<bool>? OnChanged { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    public bool Value
    {
        get => _switch?.Value ?? _value;
        set => SetValue(value);
    }

    protected override Widget Build(BuildContext context)
    {
        _switch = new AdwSwitch(
            _value,
            v =>
            {
                SetValue(v);
                OnChanged?.Invoke(v);
            }
        ) { Enabled = Enabled };

        // Role/Checked: the row Pressable is a semantics leaf, so without these the whole row
        // announces as an unlabelled button and the switch's state never reaches the reader.
        Widget row = _row = new AdwActionRow(Title, Subtitle) {
            Suffixes = { _switch },
            OnActivated = Enabled ? Toggle : null,
            Role = SemanticsRole.Switch,
            Checked = _value,
        };
        // Adwaita disabled rows dim wholesale.
        return Enabled ? row : new Opacity(AdwStyle.DisabledOpacity, row);
    }

    private void Toggle()
    {
        SetValue(!_value);
        OnChanged?.Invoke(_value);
    }

    /// <summary>Single write path so the switch visual and the announced state never diverge.</summary>
    private void SetValue(bool value)
    {
        _value = value;
        if (_switch is not null) _switch.Value = value;
        if (_row is not null) _row.Checked = value;
    }
}

/// <summary>AdwSpinRow — an <see cref="AdwActionRow" /> with an <see cref="AdwSpinButton" /> suffix.</summary>
public sealed class AdwSpinRow : ComposedWidget
{
    private AdwSpinButton? _spin;
    private double _value;
    private string _title;
    private string? _subtitle;
    private double _min;
    private double _max;
    private double _step;
    private bool _enabled = true;

    public AdwSpinRow(
        string title = "",
        string? subtitle = null,
        double value = 0,
        double min = 0,
        double max = 100,
        double step = 1,
        Action<double>? onChanged = null)
    {
        _title = title;
        _subtitle = subtitle;
        _value = value;
        _min = min;
        _max = max;
        _step = step;
        OnChanged = onChanged;
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(ref _subtitle, value);
    }

    public Action<double>? OnChanged { get; set; }

    /// <summary>Adjustment bounds and increment. Read at build time, like AdwSpinButton's own.</summary>
    public double Min
    {
        get => _min;
        set => this.Set(ref _min, value);
    }

    public double Max
    {
        get => _max;
        set => this.Set(ref _max, value);
    }

    public double Step
    {
        get => _step;
        set => this.Set(ref _step, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    public double Value
    {
        get => _spin?.Value ?? _value;
        set
        {
            _value = value;
            if (_spin is not null) _spin.Value = value;
        }
    }

    protected override Widget Build(BuildContext context)
    {
        _spin = new AdwSpinButton(
            _value,
            Min,
            Max,
            Step,
            v =>
            {
                _value = v;
                OnChanged?.Invoke(v);
            }
        ) { Enabled = Enabled };
        Widget row = new AdwActionRow(Title, Subtitle) { Suffixes = { _spin } };
        return Enabled ? row : new Opacity(AdwStyle.DisabledOpacity, row);
    }
}

/// <summary>
///     AdwButtonRow — a boxed-list row that acts as a button: centered bold title with an optional
///     leading icon. <see cref="Destructive" /> renders the content in the destructive red.
/// </summary>
public sealed class AdwButtonRow : ComposedWidget
{
    private string _title;
    private string? _iconName;
    private string? _endIconName;
    private bool _destructive;
    private bool _suggested;
    private bool _enabled = true;

    public AdwButtonRow(string title = "", Action? onPressed = null, string? iconName = null)
    {
        _title = title;
        OnPressed = onPressed;
        _iconName = iconName;
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public Action? OnPressed { get; set; }

    public string? IconName
    {
        get => _iconName;
        set => this.Set(ref _iconName, value);
    }

    /// <summary>Icon drawn after the title (libadwaita's end-icon-name), e.g. an external-link mark.</summary>
    public string? EndIconName
    {
        get => _endIconName;
        set => this.Set(ref _endIconName, value);
    }

    public bool Destructive
    {
        get => _destructive;
        set => this.Set(ref _destructive, value);
    }

    /// <summary>.suggested-action — accent-coloured content. Destructive wins if both are set.</summary>
    public bool Suggested
    {
        get => _suggested;
        set => this.Set(ref _suggested, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        // `.suggested-action` fills the whole row with the accent; `.destructive-action` only
        // recolours the text (--accent-color remapped to --destructive-color) and keeps the
        // ordinary row wash under it.
        var fg = Suggested ? p.AccentFg
            : Destructive ? p.Destructive
            : theme.OnSurface;

        var content = new Row(
            spacing: AdwMetrics.RowSpacing,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );
        // Invisible strut so the centered content still enforces the row min-height — a button row
        // is 40px, shorter than the 50px of a titled action row.
        content.Children.Add(new SizedBox(0f, AdwMetrics.ButtonRowHeight));
        if (IconName is { } icon)
            content.Children.Add(new IconGlyph(icon, AdwMetrics.IconSize, fg));
        content.Children.Add(new Label(Title, AdwTypography.Heading, fg));
        if (EndIconName is { } endIcon)
            content.Children.Add(new IconGlyph(endIcon, AdwMetrics.IconSize, fg));

        var wash = new DecoratedBox {
            Fill = Suggested ? theme.Accent : Color.Transparent,
            Child = new Center { Child = content },
        };
        var pressable = new Pressable {
            Child = wash,
            OnPressed = () => OnPressed?.Invoke(),
            Enabled = Enabled,
            SemanticsLabel = Title,
        };
        pressable.OnStateChanged = () =>
        {
            wash.Fill = Suggested
                ? AdwStyle.Solid(theme.Accent, pressable.Hovered, pressable.Pressed)
                : AdwStyle.RowFill(theme, pressable.Hovered, pressable.Pressed);
            wash.MarkNeedsPaint();
        };
        return Enabled ? pressable : new Opacity(AdwStyle.DisabledOpacity, pressable);
    }
}
