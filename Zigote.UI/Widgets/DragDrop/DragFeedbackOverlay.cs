using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.DragDrop;

/// <summary>
///     Internal overlay that paints an in-app drag's feedback widget following the pointer.
///     Full-screen
///     and transparent to hit-testing (so the <see cref="DragTarget{T}" /> under the pointer is
///     found),
///     it positions the feedback at <c>pointer − grabAnchor</c>. Owned by <see cref="App" />; pushed
///     on <c>StartDrag</c>, popped on <c>EndDrag</c>. Not constructed by app code.
/// </summary>
public sealed class DragFeedbackOverlay(Widget feedback, Offset pointer, Offset grabAnchor) : Widget
{
    private readonly Offset _grabAnchor = grabAnchor;
    private Size _feedbackSize;
    private Offset _pointer = pointer;
    private Size _screen;

    /// <summary>The ghost widget — the App damages its old/new regions on each pointer move.</summary>
    internal Widget Feedback { get; } = feedback;

    public void SetPointer(Offset pointer)
    {
        _pointer = pointer;
        // Re-run only this overlay's Layout (_screen/_feedbackSize are cached from the last Measure)
        // — a pointer move must not invalidate the whole tree.
        Layout(new Offset(x: Bounds.X, y: Bounds.Y));
    }

    public override Size Measure(Constraints c)
    {
        _screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        _feedbackSize = Feedback.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: c.MaxWidth,
                minHeight: 0f,
                maxHeight: c.MaxHeight
            )
        );
        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _screen.Width,
            height: _screen.Height
        );

        // Anchor the feedback so the grab point tracks the cursor; keep it on-screen.
        float fx = _pointer.X - _grabAnchor.X;
        float fy = _pointer.Y - _grabAnchor.Y;
        fx = MathF.Max(x: 0f, y: MathF.Min(x: fx, y: _screen.Width - _feedbackSize.Width));
        fy = MathF.Max(x: 0f, y: MathF.Min(x: fy, y: _screen.Height - _feedbackSize.Height));
        Feedback.Layout(new Offset(x: fx, y: fy));
    }

    public override void Paint(PaintList paint) => Feedback.Paint(paint);

    // Transparent to hit-testing — the drop target beneath must be reachable.
    public override Widget? HitTest(Offset point) => null;

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Feedback);
}
