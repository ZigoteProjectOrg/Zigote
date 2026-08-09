namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSeparator — GTK's separator line: a 1px hairline in the theme's border colour, running
///     across the box it sits in. Horizontal by default (a rule between stacked sections); set
///     <see cref="Vertical" /> for the group divider between header-bar / toolbar button clusters.
///     <see cref="Margin" /> insets both ends, which is how GNOME keeps a vertical rule from
///     touching the bar edges.
/// </summary>
public sealed class AdwSeparator : ComposedWidget
{
    private Color? _color;
    private float? _length;
    private float _margin;
    private bool _vertical;

    public AdwSeparator(bool vertical = false, float margin = 0f)
    {
        _vertical = vertical;
        _margin = margin;
    }

    /// <summary>A vertical rule (divides side-by-side content) instead of a horizontal one.</summary>
    public bool Vertical
    {
        get => _vertical;
        set => this.Set(ref _vertical, value);
    }

    /// <summary>Inset at both ends, along the line's own direction.</summary>
    public float Margin
    {
        get => _margin;
        set => this.Set(ref _margin, value);
    }

    /// <summary>Overrides the theme's border colour.</summary>
    public Color? Color
    {
        get => _color;
        set => this.Set(ref _color, value);
    }

    /// <summary>
    ///     Length along the line. Null fills the parent — right for a rule inside a stretching
    ///     box, wrong inside a centre-aligned toolbar Row, where GNOME gives its group dividers a
    ///     fixed height instead.
    /// </summary>
    public float? Length
    {
        get => _length;
        set => this.Set(ref _length, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var line = new Container {
            Background = Color ?? theme.Separator,
            Width = Vertical ? 1f : Length,
            Height = Vertical ? Length : 1f,
        };
        return Margin > 0f
            ? new Padding(
                Vertical
                    ? EdgeInsets.Symmetric(0f, Margin)
                    : EdgeInsets.Symmetric(Margin, 0f),
                line
            )
            : line;
    }
}
