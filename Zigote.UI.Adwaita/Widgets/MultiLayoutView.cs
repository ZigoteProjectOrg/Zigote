namespace Zigote.UI.Adwaita;

/// <summary>
///     One named arrangement inside an <see cref="AdwMultiLayoutView" />. The layout is a widget
///     tree containing <see cref="AdwLayoutSlot" />s; the view fills those slots with the shared
///     children at build time.
/// </summary>
public sealed class AdwLayout(string name, Widget content)
{
    public string Name { get; } = name;
    public Widget Content { get; } = content;
}

/// <summary>
///     A placeholder inside an <see cref="AdwLayout" /> naming which shared child belongs here.
///     Slots are what make a multi-layout view worth having: the children are created once and
///     re-parented between arrangements, so a text entry keeps its caret and a list keeps its
///     scroll position across a fold, which rebuilding two separate trees would lose.
/// </summary>
public sealed class AdwLayoutSlot(string id) : Widget
{
    private Size _size;

    public string Id { get; } = id;

    /// <summary>Filled in by the owning view before layout; null while unbound.</summary>
    internal Widget? Content { get; set; }

    public override Size Measure(Constraints c)
    {
        _size = Content?.Measure(c) ?? c.Constrain(Size.Zero);
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
        Content?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Content?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? Content?.HitTest(point) : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Content);
    }
}

/// <summary>
///     AdwMultiLayoutView — several named arrangements of the SAME children, one shown at a time.
///     Pair it with an <see cref="AdwBreakpointBin" /> whose breakpoints set
///     <see cref="LayoutName" /> and a window folds between a sidebar layout and a stacked one
///     without rebuilding — or losing — a single child.
/// </summary>
/// <example>
///     <code>
///     var view = new AdwMultiLayoutView {
///         Children = { ["content"] = editor, ["sidebar"] = list },
///         Layouts = {
///             new AdwLayout("wide", new Row { Children = {
///                 new SizedBox(260f, child: new AdwLayoutSlot("sidebar")),
///                 new Expanded(new AdwLayoutSlot("content")) } }),
///             new AdwLayout("narrow", new AdwLayoutSlot("content")),
///         },
///         LayoutName = "wide",
///     };
///     </code>
/// </example>
public sealed class AdwMultiLayoutView : ComposedWidget
{
    private string? _layoutName;

    /// <summary>Arrangements by name; the first is used until <see cref="LayoutName" /> is set.</summary>
    public List<AdwLayout> Layouts { get; init; } = [];

    /// <summary>The shared children, by slot id.</summary>
    public Dictionary<string, Widget> Children { get; init; } = [];

    /// <summary>Which layout is showing. Unknown names fall back to the first layout.</summary>
    public string? LayoutName
    {
        get => _layoutName;
        set => this.Set(ref _layoutName, value);
    }

    /// <summary>The resolved layout — the named one, else the first declared.</summary>
    public AdwLayout? CurrentLayout =>
        Layouts.FirstOrDefault(l => l.Name == LayoutName) ?? Layouts.FirstOrDefault();

    protected override Widget Build(BuildContext context)
    {
        var layout = CurrentLayout;
        if (layout is null) return new SizedBox();

        // Re-bind on every build: the previous layout's slots still hold these children, and a slot
        // left pointing at a child that now lives elsewhere would paint it twice.
        foreach (var other in Layouts) Unbind(other.Content);
        Bind(layout.Content);
        return layout.Content;
    }

    private void Bind(Widget widget)
    {
        if (widget is AdwLayoutSlot slot)
            slot.Content = Children.GetValueOrDefault(slot.Id);
        foreach (var child in widget.GetChildren()) Bind(child);
    }

    private static void Unbind(Widget widget)
    {
        if (widget is AdwLayoutSlot slot) slot.Content = null;
        foreach (var child in widget.GetChildren()) Unbind(child);
    }
}
