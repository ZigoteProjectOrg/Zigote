using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets;

/// <summary>
///     A widget that owns mutable state via a companion <see cref="WidgetState" /> object.
///     <para>
///         Usage:
///         <code>
///   class Counter : StatefulWidget
///   {
///       protected override WidgetState CreateState() => new CounterState();
///   }
/// 
///   class CounterState : WidgetState&lt;Counter&gt;
///   {
///       private readonly Label _label = new("0");
///       private int _count;
/// 
///       public override Widget Build(BuildContext ctx) =>
///           new Column
///           {
///               Children =
///               {
///                   _label,
///                   new Button("Increment", () => SetState(() =>
///                   {
///                       _count++;
///                       _label.Text = _count.ToString();
///                   })),
///               }
///           };
///   }
/// </code>
///     </para>
///     <para>
///         In the retained widget model, <see cref="WidgetState.Build" /> is called once and the
///         child tree is cached. <see cref="WidgetState.SetState" /> mutates widget properties in
///         place — the frame loop handles the repaint automatically.
///         Call <see cref="RequestRebuild" /> if you genuinely need to recreate the child tree.
///     </para>
/// </summary>
public abstract class StatefulWidget : Widget
{
    private Widget? _child;
    private Widget[]? _childCache;
    private int _measuredGen = -1;

    public WidgetState? InternalState { get; private set; }

    public override string? TooltipText => _child?.TooltipText;

    protected abstract WidgetState CreateState();

    /// <summary>
    ///     Recreate the child tree from <see cref="WidgetState.Build" /> on the next frame.
    ///     Normally not needed; prefer mutating widget properties inside SetState.
    /// </summary>
    public void RequestRebuild()
    {
        MarkNeedsBuild();
    }

    internal void MarkDirty()
    {
        MarkNeedsBuild();
    }

    private void EnsureInitialized()
    {
        if (InternalState != null) return;
        InternalState = CreateState();
        InternalState.Widget = this;
        InternalState.Attach();
        InternalState.InitState();
    }

    private void RebuildIfNeeded()
    {
        EnsureInitialized();
        if (!NeedsBuild) return;
        RebuildCount++;
        _child?.Detach();

        // Mark this widget as the build owner so DependOn<T>() inside State.Build registers it
        // as a dependent of any inherited widget it reads.
        var ctx = BuildContext.Current;
        var prevOwner = ctx.BuildOwner;
        ctx.BuildOwner = this;
        try
        {
            _child = InternalState!.Build(ctx);
        }
        finally
        {
            ctx.BuildOwner = prevOwner;
        }

        _childCache = _child is not null ? [_child] : null;
        if (_child != null && Owner != null) _child.Attach(Owner, this);
        NeedsBuild = false;

        // The child is a brand-new instance (unmeasured). Invalidate the measure cache so Measure below
        // can't early-return a stale MeasuredSize and lay the new child out without ever measuring it —
        // that would leave e.g. a rebuilt Column with an empty metrics buffer (blank render / crash).
        // This matters on detach→re-attach (DisposeState sets NeedsBuild directly, so NeedsLayout and
        // LastConstraints stay stale) of a cached StatefulWidget at an unchanged window size.
        _measuredGen = -1;
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        MeasureCount++;
        RebuildIfNeeded();
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

    public override void Detach()
    {
        base.Detach();
        DisposeState();
    }

    public void DisposeState()
    {
        if (InternalState != null)
        {
            InternalState.Detach();
            InternalState.Dispose();
            InternalState = null;
        }

        _child?.Detach();
        _childCache = null;
        NeedsBuild = true;
    }
}

/// <summary>
///     The mutable state for a <see cref="StatefulWidget" />.
///     Subclass this directly or use <see cref="WidgetState{TWidget}" /> for typed widget access.
/// </summary>
public abstract class WidgetState
{
    internal StatefulWidget Widget { get; set; } = null!;

    public bool Mounted { get; private set; }
    public bool Disposed { get; private set; }

    internal void Attach()
    {
        ThrowIfDisposed();
        Mounted = true;
    }

    internal void Detach()
    {
        Mounted = false;
    }

    protected void ThrowIfDisposed()
    {
        if (Disposed)
            throw new ObjectDisposedException(
                GetType().Name,
                "This state object has already been disposed."
            );
    }

    /// <summary>
    ///     Compose the child tree. Called once; result is cached by the parent widget.
    ///     Retain references to child widgets as fields so SetState can mutate them.
    /// </summary>
    public abstract Widget Build(BuildContext context);

    /// <summary>Called when this state object is first attached to its widget.</summary>
    public virtual void InitState()
    {
    }

    /// <summary>Called when the owning widget is removed from the tree.</summary>
    public virtual void Dispose()
    {
        Disposed = true;
        Mounted = false;
    }

    /// <summary>
    ///     Mutate retained widget state inside <paramref name="action" /> and mark this widget dirty
    ///     for re-layout and repaint. This is the everyday state-change call: it does <b>not</b> re-run
    ///     <see cref="Build" /> (the retained child tree is preserved) but does pick up size changes,
    ///     so mutating a child's text or visibility is always reflected. Use
    ///     <see cref="SetStateRebuild" /> only when the child tree itself must be recomposed.
    /// </summary>
    protected void SetState(Action action)
    {
        if (Disposed) return;
        action();
        Widget.MarkNeedsLayout();
    }

    /// <summary>Alias for <see cref="SetState" /> (kept for call-site clarity; both relayout + repaint).</summary>
    protected void SetStateLayout(Action action)
    {
        SetState(action);
    }

    /// <summary>
    ///     Mutate state inside <paramref name="action" /> and request a widget rebuild, layout, and
    ///     repaint.
    /// </summary>
    protected void SetStateRebuild(Action action)
    {
        if (Disposed) return;
        action();
        Widget.MarkNeedsBuild();
    }

    /// <summary>
    ///     Request a repaint only — no relayout, no rebuild. Use for visual-only state changes
    ///     (an animation tick, a hover recolour) where the widget's measured size cannot have changed.
    /// </summary>
    protected void MarkNeedsPaint()
    {
        if (Disposed) return;
        Widget.MarkNeedsPaint();
    }

    /// <summary>
    ///     Request a relayout (and repaint) without re-running <see cref="Build" />. Use when a
    ///     visual change may have altered the widget's measured size but the child tree is unchanged.
    /// </summary>
    protected void MarkNeedsLayout()
    {
        if (Disposed) return;
        Widget.MarkNeedsLayout();
    }
}

/// <summary>
///     <see cref="WidgetState" /> with a typed <see cref="Widget" /> property for convenience.
/// </summary>
public abstract class WidgetState<TWidget> : WidgetState where TWidget : StatefulWidget
{
    /// <summary>The owning <see cref="StatefulWidget" />, typed as <typeparamref name="TWidget" />.</summary>
    public new TWidget Widget => (TWidget)base.Widget;
}