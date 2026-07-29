using AppInstance = Zigote.UI.Host.App;
using Zigote.UI.Host;

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
        ("Racing Red", new Color(0.72f, 0.05f, 0.06f)),
        ("Orange", new Color(0.85f, 0.33f, 0.03f)),
        ("Yellow", new Color(0.86f, 0.70f, 0.05f)),
        ("Green", new Color(0.05f, 0.36f, 0.14f)),
        ("Blue", new Color(0.07f, 0.20f, 0.62f)),
        ("Silver", new Color(0.62f, 0.64f, 0.67f)),
        ("Grey", new Color(0.30f, 0.30f, 0.32f)),
        ("Black", new Color(0.02f, 0.02f, 0.02f)),
        ("White", new Color(0.92f, 0.92f, 0.92f)),
        ("Maroon", new Color(0.30f, 0.04f, 0.09f)),
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
        _size = c.Constrain(new Size(Width, TouchMetrics.AtLeast(Height)));
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
        paint.AddRect(Bounds, _value.WithAlpha(1f), Radii.Sm);
        paint.AddBorder(Bounds, _theme.Separator, Radii.Sm);
        if (Focused) paint.AddFocusRing(Bounds, Radii.Sm, _theme);
    }

    public override void OnPointerDown(Offset point)
    {
        OpenPicker();
    }

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
        col.Children.Add(new SizedBox(height: 18f, child: new ColorPreview(() => _value, _theme)));
        col.Children.Add(new SizedBox(height: Spacing.Sm));

        // Preset swatches, two rows of five.
        for (var r = 0; r < 2; r++)
        {
            var row = new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                CrossAxisAlignment = CrossAxisAlignment.Center,
            };
            for (var i = 0; i < 5; i++)
            {
                var (_, c) = Presets[r * 5 + i];
                row.Children.Add(new ColorChip(c, _theme, () => SetFromPreset(c)));
                row.Children.Add(new SizedBox(4f));
            }

            col.Children.Add(row);
            col.Children.Add(new SizedBox(height: 4f));
        }

        col.Children.Add(new SizedBox(height: Spacing.Sm));

        // Full HSV / hex / alpha picker.
        _picker = new ColorPicker(_value, Set);
        col.Children.Add(_picker);

        _popover = new Popover(new SizedBox(220f, child: col), Bounds);
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
            _s = c.Constrain(new Size(c.MaxWidth, 18f));
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
            paint.AddRect(Bounds, get().WithAlpha(1f), Radii.Xs);
            paint.AddBorder(Bounds, theme.Separator, Radii.Xs);
        }
    }

    private sealed class ColorChip(Color color, ThemeData theme, Action onTap) : Widget
    {
        private Size _s;

        public override Size Measure(Constraints c)
        {
            // 18pt presets with 4pt gutters are a mouse grid; five 36pt chips still fit the 220pt
            // popover and each one is finally aimable.
            var d = TouchMetrics.Pick(18f, 36f);
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
            paint.AddRect(Bounds, color, Radii.Xs);
            paint.AddBorder(Bounds, theme.Separator, Radii.Xs);
        }

        public override void OnPointerDown(Offset point)
        {
            onTap();
        }
    }
}