using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Material;

/// <summary>
///     A compact colour swatch that opens a popover picker (preset colours + live R/G/B sliders) on
///     click.
///     <see cref="OnChanged" /> fires live while dragging so callers can preview. Reusable across the
///     editor (material colours, light colours) and the gallery.
/// </summary>
public sealed class ColorSwatchField : Widget
{
    private static readonly (string Name, Color Color)[] Presets = [
        ("Racing Red", new Color(r: 0.72f, g: 0.05f, b: 0.06f)),
        ("Orange", new Color(r: 0.85f, g: 0.33f, b: 0.03f)),
        ("Yellow", new Color(r: 0.86f, g: 0.70f, b: 0.05f)),
        ("Green", new Color(r: 0.05f, g: 0.36f, b: 0.14f)),
        ("Blue", new Color(r: 0.07f, g: 0.20f, b: 0.62f)),
        ("Silver", new Color(r: 0.62f, g: 0.64f, b: 0.67f)),
        ("Grey", new Color(r: 0.30f, g: 0.30f, b: 0.32f)),
        ("Black", new Color(r: 0.02f, g: 0.02f, b: 0.02f)),
        ("White", new Color(r: 0.92f, g: 0.92f, b: 0.92f)),
        ("Maroon", new Color(r: 0.30f, g: 0.04f, b: 0.09f)),
    ];

    private readonly AppInstance _app;
    private ColorPicker? _picker;
    private Popover? _popover;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private Color _value;

    public ColorSwatchField(Color value, AppInstance? app = null)
    {
        _value = value;
        _app = app ?? AppInstance.Active ??
            throw new InvalidOperationException("No active App found.");
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
    public float Width { get; set; } = 52f;
    public float Height { get; set; } = 22f;
    public override bool Focusable => true;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        // The swatch is the whole trigger for the picker popover; 22pt tall is not a finger target.
        _size = c.Constrain(new Size(width: Width, height: TouchMetrics.AtLeast(Height)));
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
        paint.AddRect(bounds: Bounds, color: _value.WithAlpha(1f), radius: Radii.Sm);
        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: Radii.Sm);
        if (Focused) paint.AddFocusRing(bounds: Bounds, radius: Radii.Sm, theme: _theme);
    }

    public override void OnPointerDown(Offset point) => OpenPicker();

    private void Set(Color c)
    {
        _value = c;
        OnChanged?.Invoke(c);
        MarkNeedsPaint();
    }

    private void OpenPicker()
    {
        var col = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
        };

        // Live preview bar (reads _value each paint).
        col.Children.Add(
            new SizedBox(height: 18f, child: new ColorPreview(get: () => _value, theme: _theme))
        );
        col.Children.Add(new SizedBox(height: Spacing.Sm));

        // Preset swatches, two rows of five.
        for (int r = 0; r < 2; r++)
        {
            var row = new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                CrossAxisAlignment = CrossAxisAlignment.Center,
            };
            for (int i = 0; i < 5; i++)
            {
                var (_, c) = Presets[(r * 5) + i];
                row.Children.Add(
                    new ColorChip(color: c, theme: _theme, onTap: () => SetFromPreset(c))
                );
                row.Children.Add(new SizedBox(4f));
            }

            col.Children.Add(row);
            col.Children.Add(new SizedBox(height: 4f));
        }

        col.Children.Add(new SizedBox(height: Spacing.Sm));

        // Full HSV / hex / alpha picker.
        _picker = new ColorPicker(initial: _value, onChanged: Set);
        col.Children.Add(_picker);

        _popover = new Popover(child: new SizedBox(width: 220f, child: col), anchor: Bounds);
        _popover.Show();
    }

    /// <summary>Apply a preset both to our value and (if open) the live picker's HSV state.</summary>
    private void SetFromPreset(Color c)
    {
        Set(c);
        if (_picker != null) _picker.Value = c;
    }

    // ── Nested helpers ────────────────────────────────────────────────────────

    private sealed class ColorPreview(Func<Color> get, ThemeData theme) : Widget
    {
        private Size _s;

        public override Size Measure(Constraints c)
        {
            _s = c.Constrain(new Size(width: c.MaxWidth, height: 18f));
            return _s;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _s.Width,
                height: _s.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            paint.AddRect(bounds: Bounds, color: get().WithAlpha(1f), radius: Radii.Xs);
            paint.AddBorder(bounds: Bounds, color: theme.Separator, radius: Radii.Xs);
        }
    }

    private sealed class ColorChip(Color color, ThemeData theme, Action onTap) : Widget
    {
        private Size _s;

        public override Size Measure(Constraints c)
        {
            // 18pt presets with 4pt gutters are a mouse grid; five 36pt chips still fit the 220pt
            // popover and each one is finally aimable.
            float d = TouchMetrics.Pick(desktop: 18f, touch: 36f);
            _s = new Size(width: d, height: d);
            return _s;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _s.Width,
                height: _s.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            paint.AddRect(bounds: Bounds, color: color, radius: Radii.Xs);
            paint.AddBorder(bounds: Bounds, color: theme.Separator, radius: Radii.Xs);
        }

        public override void OnPointerDown(Offset point) => onTap();
    }
}
