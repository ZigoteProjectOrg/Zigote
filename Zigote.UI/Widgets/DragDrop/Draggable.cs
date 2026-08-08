using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.DragDrop;

/// <summary>
///     Wraps a <see cref="Child" /> so it can be dragged. Once the pointer moves past
///     <see cref="DragThreshold" /> the App starts a drag carrying <see cref="Data" /> (see
///     <see cref="App.StartDrag" />), paints a feedback ghost that follows the pointer, and — on
///     release over a compatible <see cref="DragTarget{T}" /> — hands the payload over.
///
///     <para>
///         The Draggable intercepts the pointer gesture over its bounds (drag-handle semantics), so wrap
///         a dedicated handle rather than an interactive control if you need the child to stay clickable.
///     </para>
/// </summary>
public class Draggable<T> : Widget, IPointerCapture
{
    private bool _armed;
    private bool _dragging;
    private Size _size;
    private Offset _startPoint;

    public Draggable(T data, Widget child, Func<Widget>? feedbackBuilder = null,
        string? dragText = null)
    {
        Data = data;
        Child = child;
        FeedbackBuilder = feedbackBuilder;
        DragText = dragText;
    }

    /// <summary>The payload delivered to the drop target (and matched by <see cref="DragTarget{T}" />).</summary>
    public T Data { get; set; }

    public Widget Child { get; set; }

    /// <summary>
    ///     Builds the ghost painted under the pointer while dragging. Must return a <b>fresh</b> widget
    ///     (never <see cref="Child" />, which is already in the tree). Defaults to a small translucent
    ///     label chip showing <see cref="DragText" /> / <c>Data</c>.
    /// </summary>
    public Func<Widget>? FeedbackBuilder { get; set; }

    /// <summary>Optional plain-text form of the payload — used as the label of the default ghost and as
    /// the text carried when the drag is routed out to the OS via <c>App.BeginDragOut</c>.</summary>
    public string? DragText { get; set; }

    /// <summary>Invoked when a drag ends; the argument is true if a target accepted the payload.</summary>
    public Action<bool>? OnDragCompleted { get; set; }

    /// <summary>Pointer travel (px) before a press becomes a drag.</summary>
    public double DragThreshold { get; set; } = 6;

    /// <summary>
    ///     Also offer the payload to the OS as a drag-OUT (app → Finder / another app), using
    ///     <see cref="DragText" /> and <see cref="DragOutFiles" />. macOS-only best-effort (see
    ///     <c>ZigoteEngine.BeginDragOut</c>); if the OS accepts the drag the in-app drag is skipped.
    /// </summary>
    public bool AllowDragOut { get; set; }

    /// <summary>Absolute file paths to carry when dragging out to the OS (with <see cref="AllowDragOut" />).</summary>
    public IReadOnlyList<string>? DragOutFiles { get; set; }

    public override Size Measure(Constraints c)
    {
        _size = Child.Measure(c);
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
        Child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override void OnPointerDown(Offset point)
    {
        _startPoint = point;
        _armed = true;
        _dragging = false;
    }

    public override void OnPointerMove(Offset point)
    {
        if (_dragging)
        {
            Owner?.UpdateDrag(point);
            return;
        }

        if (!_armed) return;
        var dx = point.X - _startPoint.X;
        var dy = point.Y - _startPoint.Y;
        if (dx * dx + dy * dy >= DragThreshold * DragThreshold) BeginDrag(point);
    }

    public override void OnPointerUp(Offset point)
    {
        if (_dragging)
        {
            var accepted = Owner?.EndDrag(point) ?? false;
            _dragging = false;
            OnDragCompleted?.Invoke(accepted);
        }

        _armed = false;
    }

    /// <summary>
    ///     Press-and-hold lifts the payload on a touchscreen. A finger drag alone can't: the
    ///     surrounding scroller claims it after 12 px of slop, so a drag toward a drop zone below
    ///     just scrolls the page. Holding first commits the gesture here (see App.Touch) — the
    ///     platform convention, and the only sequence that can complete a drop by finger. This
    ///     replaces the default long-press → context-menu mapping for draggables.
    /// </summary>
    public override void OnLongPress(Offset point)
    {
        if (_armed && !_dragging) BeginDrag(point);
    }

    public override void OnPointerCancel()
    {
        if (_dragging)
        {
            Owner?.EndDrag(_startPoint, true);
            _dragging = false;
            OnDragCompleted?.Invoke(false);
        }

        _armed = false;
    }

    private void BeginDrag(Offset point)
    {
        if (Owner is null) return;

        // Optionally hand the payload to the OS first. If it takes the drag, the pointer now belongs to
        // the system, so we don't also start an in-app drag.
        if (AllowDragOut && (DragText is not null || DragOutFiles is { Count: > 0 }) &&
            Owner.Engine.BeginDragOut(DragText, DragOutFiles))
        {
            _armed = false;
            return;
        }

        var feedback = FeedbackBuilder?.Invoke() ?? BuildDefaultFeedback();
        var anchor = new Offset(point.X - Bounds.X, point.Y - Bounds.Y);
        Owner.StartDrag(DragData.ForPayload(Data!, DragText), feedback, anchor);
        _dragging = true;
    }

    private Opacity BuildDefaultFeedback()
    {
        var label = DragText ?? Data?.ToString() ?? "Dragging…";
        return new Opacity(
            0.85,
            new DecoratedBox {
                Fill = Color.Black.WithAlpha(0.72f),
                Radius = Radii.Sm,
                BorderWidth = 0f,
                Child = new Padding(
                    EdgeInsets.Symmetric(Spacing.Sm, Spacing.Xs),
                    new Label(label, Typography.Footnote.Size, Color.White)
                ),
            }
        );
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}