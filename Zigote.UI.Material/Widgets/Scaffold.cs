namespace Zigote.UI.Material;

/// <summary>
///     Page-level layout widget: app bar on top, body fills the rest, optional FAB at bottom-right.
/// </summary>
public sealed class Scaffold : RenderWidget
{
    private const float FabSize = 56f;
    private const float FabPadding = 16f;
    private float _barH;
    private float _bottomInset;
    private Size _size;

    private ThemeData _theme = ThemeData.Dark;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new Scaffold(appBar: new AppBar(...), body: …,
    ///         floatingActionButton: …)
    ///     </c>
    ///     . All arguments optional, so <c>new Scaffold { Body = … }</c>
    ///     still works. <c>drawer</c>/<c>bottomNavigationBar</c> slots are not modelled yet.
    /// </summary>
    public Scaffold(
        AppBar? appBar = null,
        Widget? body = null,
        Widget? floatingActionButton = null,
        Color? backgroundColor = null)
    {
        AppBar = appBar;
        Body = body;
        FloatingActionButton = floatingActionButton;
        if (backgroundColor is { } c) BackgroundColor = c;
    }

    public AppBar? AppBar { get; set; }
    public Widget? Body { get; set; }
    public Widget? FloatingActionButton { get; set; }
    public Color BackgroundColor { get; set; } = Color.Transparent;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _barH = AppBar?.Height ?? 0f;
        // Material's resizeToAvoidBottomInset: give the body only the height the soft keyboard
        // leaves, so a focused field scrolls into view instead of sitting behind the keyboard.
        // ViewInsets is zero on desktop, so the desktop layout is unchanged.
        _bottomInset = MediaQuery.Of(BuildContext.Current).ViewInsets.Bottom;

        // On an unbounded axis (e.g. inside a parent ScrollView) size to the app bar + body content
        // rather than infinity — an infinite size poisons flex layout (∞ − ∞ → NaN) and crashes paint.
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f;
        var h = float.IsFinite(c.MaxHeight)
            ? c.MaxHeight
            : _barH + (Body?.Measure(new Constraints(0, w)).Height ?? 0f);
        _size = c.Constrain(new Size(w, h));

        if (AppBar != null)
            AppBar.Measure(Constraints.Tight(_size.Width, _barH));

        var bodyH = Math.Max(0, _size.Height - _barH - _bottomInset);
        Body?.Measure(Constraints.Tight(_size.Width, bodyH));

        FloatingActionButton?.Measure(Constraints.Tight(FabSize, FabSize));

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

        AppBar?.Layout(origin);

        var bodyY = origin.Y + _barH;
        Body?.Layout(new Offset(origin.X, bodyY));

        FloatingActionButton?.Layout(
            new Offset(
                origin.X + _size.Width - FabSize - FabPadding,
                origin.Y + _size.Height - _bottomInset - FabSize - FabPadding
            )
        );
    }

    public override void Paint(PaintList paint)
    {
        var bg = BackgroundColor.A > 0f ? BackgroundColor : _theme.Background;
        paint.AddRect(Bounds, bg);

        Body?.Paint(paint);
        AppBar?.Paint(paint);
        FloatingActionButton?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        var fabHit = FloatingActionButton?.HitTest(point);
        if (fabHit != null) return fabHit;

        var barHit = AppBar?.HitTest(point);
        if (barHit != null) return barHit;

        return Body?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        if (AppBar is not null) yield return AppBar;
        if (Body is not null) yield return Body;
        if (FloatingActionButton is not null) yield return FloatingActionButton;
    }
}