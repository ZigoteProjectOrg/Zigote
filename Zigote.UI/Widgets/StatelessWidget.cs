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
        _child?.Detach();

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
        if (_child != null && Owner != null) _child.Attach(Owner, this);
        NeedsBuild = false;
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