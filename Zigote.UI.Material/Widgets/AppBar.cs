namespace Zigote.UI.Material;

/// <summary>
///     A Material-style application bar with an optional leading widget, title, and actions.
///     Reads <see cref="ThemeData" /> from <see cref="BuildContext.Current" /> during Measure
///     so it always reflects the ambient <see cref="ZigoteTheme" />.
/// </summary>
public sealed class AppBar : Widget
{
    public const float DefaultHeight = 56f;

    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private Size _titleSize;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new AppBar(title: new Text("Home"), centerTitle: true,
    ///         actions: [ … ])
    ///     </c>
    ///     . All arguments optional, so <c>new AppBar { Title = … }</c> still works.
    /// </summary>
    public AppBar(
        Widget? title = null,
        Widget? leading = null,
        List<Widget>? actions = null,
        double? toolbarHeight = null,
        bool centerTitle = false,
        Color? backgroundColor = null)
    {
        Title = title;
        Leading = leading;
        if (actions is not null) Actions.AddRange(actions);
        if (toolbarHeight is { } h) Height = (float)h;
        CenterTitle = centerTitle;
        BackgroundColor = backgroundColor;
    }

    public Widget? Leading { get; set; }
    public Widget? Title { get; set; }
    public List<Widget> Actions { get; } = [];
    public float Height { get; set; } = DefaultHeight;

    /// <summary>Center the title in the bar rather than left-aligning after the leading slot.</summary>
    public bool CenterTitle { get; set; }

    /// <summary>Bar fill. Null uses the theme surface colour.</summary>
    public Color? BackgroundColor { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);

        var w = c.MaxWidth;

        // Reserve leading (48 px) and action slots (48 px each)
        var leadingW = Leading != null ? 56f : 16f;
        var actionsW = Actions.Count * 48f + 8f;
        var titleW = Math.Max(0, w - leadingW - actionsW);

        Leading?.Measure(Constraints.Tight(48f, Height));

        if (Title != null) _titleSize = Title.Measure(Constraints.Loose(titleW, Height));

        foreach (var a in Actions)
            a.Measure(Constraints.Tight(48f, Height));

        _size = new Size(w, Height);
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

        var x = origin.X + 8f;

        if (Leading != null)
        {
            Leading.Layout(new Offset(x, origin.Y + (Height - 48f) / 2f));
            x += 56f;
        }
        else
        {
            x += 8f;
        }

        if (Title != null)
        {
            var ty = origin.Y + (Height - _titleSize.Height) / 2f;
            var tx = CenterTitle ? origin.X + (_size.Width - _titleSize.Width) / 2f : x;
            Title.Layout(new Offset(tx, ty));
        }

        var ax = origin.X + _size.Width - 8f;
        for (var i = Actions.Count - 1; i >= 0; i--)
        {
            ax -= 48f;
            Actions[i].Layout(new Offset(ax, origin.Y + (Height - 48f) / 2f));
        }
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, BackgroundColor ?? _theme.Surface);

        // Bottom shadow line
        paint.AddRect(
            new Rect(
                Bounds.X,
                Bounds.Y + Bounds.Height - 1f,
                Bounds.Width,
                1f
            ),
            _theme.SurfaceAlt
        );

        Leading?.Paint(paint);
        Title?.Paint(paint);
        foreach (var a in Actions) a.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        for (var i = Actions.Count - 1; i >= 0; i--)
        {
            var hit = Actions[i].HitTest(point);
            if (hit != null) return hit;
        }

        var lhit = Leading?.HitTest(point);
        if (lhit != null) return lhit;
        return this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        if (Leading is not null) yield return Leading;
        if (Title is not null) yield return Title;
        foreach (var a in Actions) yield return a;
    }
}
