using Zigote.Core.State;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwTabButton — the header-bar button that shows how many tabs an <see cref="AdwTabView" />
///     holds and opens the tab overview when pressed. The count sits inside a rounded outline, the
///     way GNOME Web and Console draw it; past 99 tabs it becomes an infinity sign rather than
///     widening the button, which is what libadwaita does.
/// </summary>
public sealed class AdwTabButton : ComposedWidget
{
    private const float GlyphBox = 20f;

    private readonly AdwTabView _view;

    public AdwTabButton(AdwTabView view, Action? onPressed = null)
    {
        _view = view;
        OnPressed = onPressed;
    }

    /// <summary>Invoked on press — normally opens the tab overview.</summary>
    public Action? OnPressed { get; set; }

    protected override Widget Build(BuildContext context)
    {
        // Watch: the count has to follow tabs opening and closing without the host rebuilding.
        return new Watch(() => new AdwButton("Tabs", () => OnPressed?.Invoke()) {
                Style = AdwButtonStyle.Flat,
                Circular = true,
                Content = new CountGlyph(_view.Pages.Count),
            }
        );
    }

    /// <summary>The rounded-square outline with the tab count centred in it.</summary>
    private sealed class CountGlyph(int count) : Widget
    {
        private ThemeData _theme = ThemeData.Dark;

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            return c.Constrain(new Size(GlyphBox, GlyphBox));
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                GlyphBox,
                GlyphBox
            );
        }

        public override void Paint(PaintList paint)
        {
            // The button is always .flat here, whose foreground IS the window fg, so the glyph
            // needs no tint plumbing from AdwButton.
            var fg = _theme.OnBackground;
            paint.AddBorder(
                Bounds,
                fg,
                6f,
                1.6f
            );

            // Past two digits the number stops fitting the 20px box; GNOME shows ∞ rather than
            // letting the glyph shrink into illegibility or the button grow.
            var text = count > 99 ? "∞" : count.ToString();
            const float fs = 11f;
            var w = TextShaping.TextMeasure.Width(text, fs);
            paint.AddText(
                text,
                Bounds.X + (GlyphBox - w) / 2f,
                Bounds.Y + (GlyphBox - fs) / 2f + fs * 0.82f,
                fg,
                fs
            );
        }
    }
}

/// <summary>
///     AdwWrapBox — children flowed left to right, wrapping onto new lines as the width runs out,
///     with independent gaps along and between lines. The Adwaita name for the framework's
///     <see cref="Wrap" />; use it for tag/chip rows and any button group that must survive a
///     narrow window.
/// </summary>
public sealed class AdwWrapBox : ComposedWidget
{
    private float _lineSpacing = Spacing.Sm;
    private float _childSpacing = Spacing.Sm;

    public AdwWrapBox(params Widget[] children)
    {
        Children = [.. children];
    }

    public List<Widget> Children { get; }

    /// <summary>Gap between children on the same line.</summary>
    public float ChildSpacing
    {
        get => _childSpacing;
        set => this.Set(ref _childSpacing, value);
    }

    /// <summary>Gap between lines.</summary>
    public float LineSpacing
    {
        get => _lineSpacing;
        set => this.Set(ref _lineSpacing, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var wrap = new Wrap {
            Spacing = ChildSpacing,
            RunSpacing = LineSpacing,
        };
        foreach (var child in Children) wrap.Children.Add(child);
        return wrap;
    }
}
