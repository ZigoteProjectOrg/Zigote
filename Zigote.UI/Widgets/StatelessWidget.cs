using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets;

/// <summary>
///     A widget that describes its UI through a <see cref="Build" /> method.
///     Build is called once and the result cached; call <see cref="Invalidate" /> to
///     trigger a rebuild on the next Measure pass.
///     Access inherited data inside Build via the supplied <see cref="BuildContext" />:
///     <code>
///   protected override Widget Build(BuildContext ctx)
///   {
///       var theme = ctx.FindAncestor&lt;MyTheme&gt;();
///       return new Label("Hello", theme?.TextColor ?? Color.White);
///   }
/// </code>
/// </summary>
public abstract class StatelessWidget : Widget
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
    public void Invalidate()
    {
        MarkNeedsBuild();
    }

    private void EnsureBuilt()
    {
        if (!NeedsBuild) return;
        RebuildCount++;

        // Build first, detach after, and only if Build actually returned a different subtree —
        // widgets that retain their root (every Adw* control that keeps a Pressable field) would
        // otherwise be torn down and re-attached on every property change, which clears focus
        // (Widget.Detach → Owner.NotifyDetached → RequestFocus(null)) and replays entrance
        // animations. Watch.Apply has always done it in this order.
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

        // ATTACH FIRST, then detach what the new tree did not take over. The attach must run even
        // when Build handed back the same instance: a retained root whose contents changed (a
        // Container given a fresh child, an overlay re-pointed at a new page) has newly-inserted
        // descendants that have never been mounted, and this cascade is what gives them an Owner.
        // Skipping it leaves them with a null Owner, so every Watch inside them never starts and the
        // subtree renders blank.
        //
        // The order is load-bearing for the common "wrap/unwrap a retained subtree" build — a sheet
        // or scrim that returns `content` when closed and `new Stack { content, … }` when open.
        // Detaching first tears `content` (and every StatefulWidget state, scroll offset and focus
        // inside it) down, only for the very next line to re-attach it. Attaching first re-parents
        // the shared subtree, so the guard below sees it was re-adopted and leaves it alone; only
        // the genuinely-dropped wrapper is detached, and Widget.Detach's own re-adoption check keeps
        // its cascade off the shared child.
        if (_child != null && Owner != null) _child.Attach(Owner, this);
        if (!ReferenceEquals(previous, _child) &&
            (previous?.Parent is null || ReferenceEquals(previous.Parent, this)))
            previous?.Detach();

        NeedsBuild = false;

        // The child is a brand-new instance, so it has never been measured. Invalidate the measure
        // cache (as StatefulWidget.RebuildIfNeeded does) or the check right below can early-return a
        // stale MeasuredSize at an unchanged window size and Layout will then walk the fresh subtree
        // without a single Measure — a Wrap whose offset table is empty, a Column with no metrics:
        // a blank render at best, an IndexOutOfRange in the frame loop at worst. Reachable whenever
        // NeedsBuild is set without NeedsLayout (hot reload, a re-attached subtree).
        _measuredGen = -1;
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        MeasureCount++;
        EnsureBuilt();
        var gen = BuildContext.Current.Generation;
        if (!NeedsLayout && c == LastConstraints && _measuredGen == gen) return MeasuredSize;

        LastConstraints = c;
        _measuredGen = gen;
        MeasuredSize = _child!.Measure(c);
        NeedsLayout = false;
        return MeasuredSize;
    }

    public override void Layout(Offset origin)
    {
        LayoutCount++;
        Bounds = new Rect(
            origin.X,
            origin.Y,
            MeasuredSize.Width,
            MeasuredSize.Height
        );
        _child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        PaintCount++;
        _child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _child?.HitTest(point) ?? this;
    }

    public override int DebugStateHash()
    {
        return _child?.DebugStateHash() ?? 0;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return (IEnumerable<Widget>?)_childCache ?? [];
    }
}