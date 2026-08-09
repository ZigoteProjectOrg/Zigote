using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.DragDrop;

/// <summary>
///     A drop target for in-app <see cref="Draggable{T}" /> payloads of type <typeparamref name="T" />.
///     Highlights while a compatible drag hovers (queried via <see cref="Builder" /> with the current
///     hover state) and calls <see cref="OnAccept" /> when such a payload is released over it.
///
///     <para>
///         For external OS file drops, set <typeparamref name="T" /> to <see cref="string" /> and
///         <see cref="AcceptExternalFiles" /> — each dropped file path is delivered to
///         <see cref="OnAccept" />. (SDL reveals the payload only at drop time, so external drags
///         highlight on any OS drop, not by type.)
///     </para>
/// </summary>
public class DragTarget<T> : Widget
{
    private bool _hovering;
    private Size _size;

    public DragTarget(Func<bool, Widget> builder)
    {
        Builder = builder;
        _child = builder(false);
    }

    private Widget _child;

    /// <summary>Builds the child; the argument is true while a compatible drag hovers (for highlight).</summary>
    public Func<bool, Widget> Builder { get; set; }

    /// <summary>Called with the dropped payload when a compatible drag is released over this target.</summary>
    public Action<T>? OnAccept { get; set; }

    /// <summary>Extra acceptance predicate (beyond the type match). Return false to reject a payload.</summary>
    public Func<T, bool>? WillAccept { get; set; }

    /// <summary>When <typeparamref name="T" /> is <see cref="string" />, also accept external OS file
    /// drops — each dropped path is delivered to <see cref="OnAccept" />.</summary>
    public bool AcceptExternalFiles { get; set; }

    // Outgoing subtree kept alive until the incoming one has been built — see Rebuild.
    private Widget? _retiring;

    /// <summary>
    ///     Swap in the child for the current hover state. This runs mid-drag, on every enter/leave,
    ///     so it must not tear down anything the drag depends on:
    ///     <list type="bullet">
    ///         <item>
    ///             A builder that returns the SAME widget (highlighting by mutating a retained
    ///             frame, the cheapest way) is asking for a repaint, not a remount.
    ///         </item>
    ///         <item>
    ///             Otherwise the incoming child is attached first and the outgoing one is retired
    ///             only after the next Measure. A ComposedWidget builds its subtree lazily in
    ///             Measure, so until then it has adopted nothing — detaching the old tree right
    ///             here would take shared children (the dragged Draggable among them) with it, and
    ///             App drops the pointer capture of anything it detaches, killing the drag.
    ///         </item>
    ///     </list>
    /// </summary>
    private void Rebuild()
    {
        var next = Builder(_hovering);
        if (ReferenceEquals(next, _child))
        {
            MarkNeedsPaint();
            return;
        }

        if (Owner is not null)
        {
            _retiring ??= _child;
            _child = next;
            _child.Attach(Owner, this);
        }
        else
        {
            _child = next;
        }

        MarkNeedsLayout();
    }

    public override Size Measure(Constraints c)
    {
        _size = _child.Measure(c);
        // The new subtree exists and has adopted whatever it shares with the old one; retiring it
        // now leaves those children in the live tree (Widget.Detach skips re-parented children).
        if (_retiring is { } old)
        {
            _retiring = null;
            if (!ReferenceEquals(old, _child)) old.Detach();
        }

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
        _child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        // Normal single-child delegation: the child stays interactive. The App's drop search walks up
        // the Parent chain from whatever it hits, so it still reaches this target via CanAcceptDrop.
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _child.HitTest(point) ?? this;
    }

    public override bool CanAcceptDrop(DragData data)
    {
        if (data.Payload is T payload)
            return WillAccept?.Invoke(payload) ?? true;
        return AcceptExternalFiles && data.IsExternal &&
               (data.HasFiles || typeof(T) == typeof(string));
    }

    public override void OnDragEnter(DragData data)
    {
        if (_hovering) return;
        _hovering = true;
        Rebuild();
    }

    public override void OnDragLeave()
    {
        if (!_hovering) return;
        _hovering = false;
        Rebuild();
    }

    public override void OnDrop(DragData data, Offset point)
    {
        if (_hovering)
        {
            _hovering = false;
            Rebuild();
        }

        if (data.Payload is T payload)
        {
            OnAccept?.Invoke(payload);
            return;
        }

        if (AcceptExternalFiles && data.IsExternal && OnAccept is not null)
            foreach (var file in data.Files)
                if (file is T typed)
                    OnAccept(typed);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_child);
    }
}