namespace Zigote.UI.Adwaita;

/// <summary>
///     One backdrop capture for every <see cref="LiquidPane" /> inside it — the cluster a toolbar,
///     a control centre or a lock screen is made of, drawn as one run of glass.
///     <para>
///         The engine captures the scene once for a run of consecutive lenses and again after
///         anything else has painted over it. A pane's content is that anything else: a toolbar of
///         three panes painted pane-by-pane is lens, content, lens, content, lens — three captures
///         and three render-pass breaks. The group draws every pane's lens first and only then lets
///         the content paint, which makes the whole cluster one run. That is sound because glass
///         never refracts sibling glass: the lenses read the same scene whatever order they land in.
///     </para>
///     <para>
///         Two rules follow. The panes inside must not overlap — they never should, glass never
///         stacks on glass — and nothing painted inside the group may need to sit <i>under</i> a
///         pane's lens, since every lens now goes down before any of it. The lens is also drawn at
///         the group's paint state, not the pane's, for every pane laid out under it: a scroll
///         viewport, a fade — <c>Opacity(0)</c> included — or anything else that hides a pane it
///         still lays out belongs outside the group, not inside it.
///     </para>
/// </summary>
public sealed class LiquidGroup : InheritedWidget
{
    // Measure order, i.e. tree order. Good enough: lenses that never overlap have no order.
    private readonly List<LiquidPane> _panes = [];

    public LiquidGroup(Widget? child = null) => Child = child;

    /// <summary>How many panes this group is drawing the glass for — a readout for tests.</summary>
    public int PaneCount => _panes.Count;

    internal void Add(LiquidPane pane) => _panes.Add(pane);

    internal void Remove(LiquidPane pane) => _panes.Remove(pane);

    // Nothing inherited but the scope itself: no dependents to notify.
    public override bool UpdateShouldNotify(InheritedWidget oldWidget) => false;

    // The group is invisible glue, and a miss inside it falls through to whatever is stacked
    // beneath — the same cure as Watch.HitTest. A group whose child blankets a window (two bars
    // in Positioned corners of a full-size Stack) otherwise answers `this` for every off-bar
    // point, eating the clicks — and, with them, the wheel — over the very content its bars
    // float above.
    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point);
    }

    public override void Paint(PaintList paint)
    {
        // Shadows first, then lenses: a shadow is an ordinary draw, and one landing between two
        // lenses would split the run and cost the second capture this widget exists to avoid.
        foreach (var pane in _panes) pane.PaintShadow(paint);
        foreach (var pane in _panes) pane.PaintLens(paint);
        Child?.Paint(paint);
    }
}
