using Zigote.UI.Material;
using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwColorButton — GTK's colour button: a rounded swatch of the current colour that opens a
///     chooser popover with the GNOME palette on top (the nine named accent hues plus black/grey/
///     white) and a full HSV picker below. <see cref="OnChanged" /> fires live while the user drags
///     in the picker, so callers can preview.
///     ponytail: the HSV picker itself is <see cref="Zigote.UI.Material.ColorPicker" /> — a
///     design-system-neutral widget that happens to live in the Material package. Move it here when
///     that package is retired; nothing about it needs rewriting.
/// </summary>
public sealed class AdwColorButton : Widget
{
    /// <summary>libadwaita's named palette, plus the neutrals GNOME's chooser offers.</summary>
    private static readonly Color[] Palette = [
        AdwAccentColors.Bg(AdwAccent.Blue),
        AdwAccentColors.Bg(AdwAccent.Teal),
        AdwAccentColors.Bg(AdwAccent.Green),
        AdwAccentColors.Bg(AdwAccent.Yellow),
        AdwAccentColors.Bg(AdwAccent.Orange),
        AdwAccentColors.Bg(AdwAccent.Red),
        AdwAccentColors.Bg(AdwAccent.Pink),
        AdwAccentColors.Bg(AdwAccent.Purple),
        AdwAccentColors.Bg(AdwAccent.Slate),
        Color.Rgb(0, 0, 0),
        Color.Rgb(119, 118, 123),
        Color.Rgb(255, 255, 255),
    ];

    private readonly AppInstance? _app;
    private ColorPicker? _picker;
    private float _height = AdwMetrics.CompactControlHeight;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private Color _value;
    private float _width = 52f;

    /// <param name="app">
    ///     Window the chooser popover opens in. Null resolves it when the popover is actually opened
    ///     — constructing a widget must not require a running app, or the control cannot be built
    ///     during startup, in a test, or anywhere off the UI thread.
    /// </param>
    public AdwColorButton(Color value, AppInstance? app = null)
    {
        _value = value;
        _app = app;
    }

    public Color Value
    {
        get => _value;
        set
        {
            _value = value;
            MarkNeedsPaint();
        }
    }

    public Action<Color>? OnChanged { get; set; }

    // Read in Measure, so a plain setter would be accepted and then ignored until something else
    // relaid out.
    public float Width
    {
        get => _width;
        set => SetLayout(ref _width, value);
    }

    public float Height
    {
        get => _height;
        set => SetLayout(ref _height, value);
    }
    public override bool Focusable => true;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        // The swatch is the entire trigger for the chooser; a 28px button is not a finger target.
        var compact = MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact;
        _size = c.Constrain(
            new Size(Width, compact ? MathF.Max(Height, ControlMetrics.MinTouchTarget) : Height)
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
        // `button.color { padding: 5px }` with `> colorswatch { border-radius: $button_radius -
        // 4.5px }`: the swatch is a chip INSIDE a normal button, not a button-sized slab of colour.
        // Without the surrounding fill the control loses its button reading entirely when the
        // chosen colour happens to match the page.
        const float pad = 5f;
        var p = AdwPalette.For(_theme);
        var swatch = new Rect(
            Bounds.X + pad,
            Bounds.Y + pad,
            MathF.Max(0f, Bounds.Width - pad * 2f),
            MathF.Max(0f, Bounds.Height - pad * 2f)
        );
        var swatchRadius = AdwMetrics.ControlRadius - 4.5f;

        paint.AddRect(Bounds, p.ButtonFill, AdwMetrics.ControlRadius);
        paint.AddRect(swatch, _value.WithAlpha(1f), swatchRadius);
        // `.light > overlay { border-color: view-fg 10% }` — a hairline so a white swatch still
        // reads as a swatch.
        paint.AddBorder(swatch, p.ViewFg.WithAlpha(0.1f), swatchRadius);
        if (Focused) paint.AddFocusRing(Bounds, AdwMetrics.ControlRadius, _theme);
    }

    public override void OnPointerDown(Offset point)
    {
        OpenChooser();
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        return MouseCursor.Pointer;
    }

    private void Set(Color c)
    {
        _value = c;
        OnChanged?.Invoke(c);
        MarkNeedsPaint();
    }

    private void OpenChooser()
    {
        // Owner first (the window this widget is actually in), then the explicit app, then whatever
        // is active. No window at all means nothing to open a popover over.
        if ((Owner ?? _app ?? AppInstance.Active) is null) return;

        var col = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
        };

        // Palette grid: two rows of six, GNOME's colour-chooser layout.
        for (var r = 0; r < 2; r++)
        {
            var row = new Row(spacing: 4f, mainAxisSize: MainAxisSize.Min);
            for (var i = 0; i < 6; i++)
            {
                var c = Palette[r * 6 + i];
                row.Children.Add(new Swatch(c, _theme, () => SetFromPalette(c)));
            }

            col.Children.Add(row);
            col.Children.Add(new SizedBox(height: 4f));
        }

        col.Children.Add(new SizedBox(height: Spacing.Sm));
        _picker = new ColorPicker(_value, Set);
        col.Children.Add(_picker);

        new Popover(new SizedBox(220f, child: col), Bounds).Show();
    }

    /// <summary>Apply a palette entry to both our value and (while open) the picker's HSV state.</summary>
    private void SetFromPalette(Color c)
    {
        Set(c);
        if (_picker is { } p) p.Value = c;
    }

    /// <summary>One palette cell — a rounded chip that applies its colour on click.</summary>
    private sealed class Swatch(Color color, ThemeData theme, Action onTap) : Widget
    {
        private Size _s;

        public override Size Measure(Constraints c)
        {
            // 24px cells with 4px gutters fit the 220px popover on a pointer; a finger gets the
            // full minimum target, which overflows to a scrollable popover rather than misfiring.
            var compact = MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact;
            var d = compact ? ControlMetrics.MinTouchTarget : 24f;
            _s = new Size(d, d);
            return _s;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _s.Width,
                _s.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            paint.AddRect(Bounds, color, 6f);
            paint.AddBorder(Bounds, theme.Separator, 6f);
        }

        public override void OnPointerDown(Offset point)
        {
            onTap();
        }

        public override MouseCursor? GetCursor(Offset point)
        {
            return MouseCursor.Pointer;
        }
    }
}
