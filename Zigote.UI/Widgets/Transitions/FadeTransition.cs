using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Fades a child widget in or out using <see cref="AnimationController" />.
///     Implemented via PaintList.PushAlpha/PopAlpha — no Zig renderer changes required.
///     <para>
///         Note: alpha is multiplied into every paint command color in the subtree. Overlapping
///         semi-transparent children within the fading widget will not composite correctly with
///         each other, but this is correct for the vast majority of use cases.
///     </para>
/// </summary>
public sealed class FadeTransition(AnimationController controller, Widget? child = null) : Widget
{
    private Size _size;

    public Widget? Child { get; set; } = child;
    public AnimationController Controller { get; } = controller;

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? new Size(0f, 0f);
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
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        var alpha = Controller.Value;
        if (alpha < 0.01f) return;

        if (alpha >= 0.999f)
        {
            Child?.Paint(paint);
            return;
        }

        paint.PushAlpha(alpha);
        Child?.Paint(paint);
        paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        if (Controller.Progress < 0.01f) return null;
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }
}