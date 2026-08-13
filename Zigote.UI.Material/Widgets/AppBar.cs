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

        float w = c.MaxWidth;

        // Reserve leading (48 px) and action slots (48 px each)
        float leadingW = Leading != null ? 56f : 16f;
        float actionsW = (Actions.Count * 48f) + 8f;
        float titleW = Math.Max(val1: 0, val2: w - leadingW - actionsW);

        Leading?.Measure(Constraints.Tight(width: 48f, height: Height));

        if (Title != null)
            _titleSize = Title.Measure(Constraints.Loose(width: titleW, height: Height));

        foreach (var a in Actions)
            a.Measure(Constraints.Tight(width: 48f, height: Height));

        _size = new Size(width: w, height: Height);
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

        float x = origin.X + 8f;

        if (Leading != null)
        {
            Leading.Layout(new Offset(x: x, y: origin.Y + ((Height - 48f) / 2f)));
            x += 56f;
        }
        else
            x += 8f;

        if (Title != null)
        {
            float ty = origin.Y + ((Height - _titleSize.Height) / 2f);
            float tx = CenterTitle ? origin.X + ((_size.Width - _titleSize.Width) / 2f) : x;
            Title.Layout(new Offset(x: tx, y: ty));
        }

        float ax = origin.X + _size.Width - 8f;
        for (int i = Actions.Count - 1; i >= 0; i--)
        {
            ax -= 48f;
            Actions[i].Layout(new Offset(x: ax, y: origin.Y + ((Height - 48f) / 2f)));
        }
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: BackgroundColor ?? _theme.Surface);

        // Bottom shadow line
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.X,
                y: Bounds.Y + Bounds.Height - 1f,
                width: Bounds.Width,
                height: 1f
            ),
            color: _theme.SurfaceAlt
        );

        Leading?.Paint(paint);
        Title?.Paint(paint);
        foreach (var a in Actions) a.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        for (int i = Actions.Count - 1; i >= 0; i--)
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
