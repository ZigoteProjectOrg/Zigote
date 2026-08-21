using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets;

/// <summary>
///     A widget that describes its UI by composing others in <see cref="Build" />, rather than
///     measuring and painting itself. The one build-owner in the framework — there is no
///     stateless/stateful split, because widgets here are retained objects whose fields already
///     <em>are</em> their state.
///     <para>
///         The three places code goes, and the reason there are exactly three:
///         <list type="bullet">
///             <item>
///                 <b>Constructor / field initialisers</b> — compose the retained child tree and keep
///                 it
///                 in fields. Runs once per instance.
///             </item>
///             <item>
///                 <b>
///                     <see cref="Widget.OnMount" />
///                 </b>
///                 — start what must stop when the widget leaves the
///                 tree (subscriptions, tickers, async), registered via <see cref="Widget.Own{T}" /> /
///                 <see cref="Widget.OwnEffect(Action)" />. Runs once per mount period.
///             </item>
///             <item>
///                 <b>
///                     <see cref="Build" />
///                 </b>
///                 — read inherited data (theme, media query, localisations)
///                 off the <see cref="BuildContext" /> and push it into those retained children, then
///                 return the root. Runs once, then again on <see cref="Invalidate" />/
///                 <see cref="Widget.MarkNeedsBuild" />.
///             </item>
///         </list>
///     </para>
///     <para>
///         Prefer neither of the last two for ordinary state changes: an
///         <see cref="Widget.OwnEffect(Action)" /> that writes a signal straight into a retained child
///         costs no allocation and no rebuild at all, and a <see cref="Watch" /> handles the case
///         where
///         the tree's <em>shape</em> depends on a signal. Reach for
///         <see cref="Widget.MarkNeedsBuild" />
///         when neither fits.
///     </para>
/// </summary>
public abstract class ComposedWidget : Widget
{
    private Widget? _child;
    private Widget[]? _childCache;
    private int _measuredGen = -1;

    public override string? TooltipText => _child?.TooltipText;

    /// <summary>
    ///     Compose the widget tree. Called once; the result is retained across frames.
    ///     Call <see cref="Invalidate" /> to force a fresh call on the next frame.
    /// </summary>
    protected abstract Widget Build(BuildContext context);

    /// <summary>Schedule a rebuild of this widget on the next Measure pass.</summary>
    public void Invalidate() => MarkNeedsBuild();

    private void EnsureBuilt()
    {
        // A widget can be measured without ever having been attached (tests, off-tree measurement).
        // Build must never be the first thing that runs on an unmounted widget, or an OnMount that
        // starts the subscription feeding this build would run after the build that needed it.
        EnsureMounted();
        if (!NeedsBuild) return;
        if (Debug.WidgetDebug.CountersEnabled) RebuildCount++;

        // Build first, swap after — widgets that retain their root (every Adw* control that keeps a
        // Pressable field) would otherwise be torn down and re-attached on every property change,
        // which clears focus (Widget.Detach → Owner.NotifyDetached → RequestFocus(null)) and replays
        // entrance animations. See Widget.SwapChild for why the attach/detach order matters.
        var previous = _child;

        // Mark this widget as the build owner so DependOn<T>() inside Build registers it as a
        // dependent of any inherited widget it reads (theme, media query, …).
        var ctx = BuildContext.Current;
        var prevOwner = ctx.BuildOwner;
        ctx.BuildOwner = this;
        try
        {
            _child = Build(ctx);
        }
        finally
        {
            ctx.BuildOwner = prevOwner;
        }

        _childCache = _child is not null ? [_child] : null;

        SwapChild(previous: previous, next: _child);

        NeedsBuild = false;

        // The child is a brand-new instance, so it has never been measured. Invalidate the measure
        // cache or the check right below can early-return a stale MeasuredSize at an unchanged window
        // size and Layout will then walk the fresh subtree without a single Measure — a Wrap whose
        // offset table is empty, a Column with no metrics: a blank render at best, an
        // IndexOutOfRange in the frame loop at worst. Reachable whenever NeedsBuild is set without
        // NeedsLayout (hot reload, a re-attached subtree).
        _measuredGen = -1;
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        if (Debug.WidgetDebug.CountersEnabled) MeasureCount++;
        EnsureBuilt();
        int gen = BuildContext.Current.Generation;
        if (!NeedsLayout && c == LastConstraints && _measuredGen == gen) return MeasuredSize;

        LastConstraints = c;
        _measuredGen = gen;
        MeasuredSize = _child!.Measure(c);
        NeedsLayout = false;
        return MeasuredSize;
    }

    public override void Layout(Offset origin)
    {
        if (Debug.WidgetDebug.CountersEnabled) LayoutCount++;
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: MeasuredSize.Width,
            height: MeasuredSize.Height
        );
        _child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (Debug.WidgetDebug.CountersEnabled) PaintCount++;
        _child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _child?.HitTest(point) ?? this;
    }

    public override int DebugStateHash() => _child?.DebugStateHash() ?? 0;

    public override IEnumerable<Widget> GetChildren() => (IEnumerable<Widget>?)_childCache ?? [];
}
