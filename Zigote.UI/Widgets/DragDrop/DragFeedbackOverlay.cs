using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.DragDrop;

/// <summary>
///     Internal overlay that paints an in-app drag's feedback widget following the pointer. Full-screen
///     and transparent to hit-testing (so the <see cref="DragTarget{T}" /> under the pointer is found),
///     it positions the feedback at <c>pointer − grabAnchor</c>. Owned by <see cref="App" />; pushed
///     on <c>StartDrag</c>, popped on <c>EndDrag</c>. Not constructed by app code.
/// </summary>
public sealed class DragFeedbackOverlay(Widget feedback, Offset pointer, Offset grabAnchor) : Widget
{
    private readonly Widget _feedback = feedback;
    private readonly Offset _grabAnchor = grabAnchor;
    private Size _feedbackSize;
    private Offset _pointer = pointer;
    private Size _screen;

    /// <summary>The ghost widget — the App damages its old/new regions on each pointer move.</summary>
    internal Widget Feedback => _feedback;

    public void SetPointer(Offset pointer)
    {
        _pointer = pointer;
        // Re-run only this overlay's Layout (_screen/_feedbackSize are cached from the last Measure)
        // — a pointer move must not invalidate the whole tree.
        Layout(new Offset(Bounds.X, Bounds.Y));
    }

    public override Size Measure(Constraints c)
    {
        _screen = new Size(c.MaxWidth, c.MaxHeight);
        _feedbackSize = _feedback.Measure(
            new Constraints(
                0f,
                c.MaxWidth,
                0f,
                c.MaxHeight
            )
        );
        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _screen.Width,
            _screen.Height
        );

        // Anchor the feedback so the grab point tracks the cursor; keep it on-screen.
        var fx = _pointer.X - _grabAnchor.X;
        var fy = _pointer.Y - _grabAnchor.Y;
        fx = MathF.Max(0f, MathF.Min(fx, _screen.Width - _feedbackSize.Width));
        fy = MathF.Max(0f, MathF.Min(fy, _screen.Height - _feedbackSize.Height));
        _feedback.Layout(new Offset(fx, fy));
    }

    public override void Paint(PaintList paint)
    {
        _feedback.Paint(paint);
    }

    // Transparent to hit-testing — the drop target beneath must be reachable.
    public override Widget? HitTest(Offset point)
    {
        return null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_feedback);
    }
}