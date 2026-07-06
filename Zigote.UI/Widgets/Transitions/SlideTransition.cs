using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Slides a child in/out from a given offset direction.
///     <para>
///         <see cref="BeginOffset" /> is the starting (hidden) position relative to the
///         child's laid-out origin. When the controller reaches 1.0 the child is at its
///         natural position (offset 0,0). Call <see cref="AnimationController.Forward" />
///         to enter, <see cref="AnimationController.Reverse" /> to exit.
///     </para>
/// </summary>
public sealed class SlideTransition(AnimationController controller, Widget? child = null) : Widget
{
    private Offset _naturalOrigin;

    private Size _size;

    public Widget? Child { get; set; } = child;
    public AnimationController Controller { get; } = controller;
    public Offset BeginOffset { get; set; } = new(0f, 40f); // slide up from below

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? new Size(0f, 0f);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        _naturalOrigin = origin;
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );

        var t = Controller.Value;
        var ox = BeginOffset.X + (0f - BeginOffset.X) * t;
        var oy = BeginOffset.Y + (0f - BeginOffset.Y) * t;
        Child?.Layout(new Offset(origin.X + ox, origin.Y + oy));
    }

    public override void Paint(PaintList paint)
    {
        if (Controller.Progress < 0.01f) return;
        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (Controller.Progress < 0.01f) return null;
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }
}