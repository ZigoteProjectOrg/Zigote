using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Scales a child from a given start scale to 1.0 as the controller progresses 0→1.
///     The child is centered within its allocated space throughout the transition.
/// </summary>
public sealed class ScaleTransition(AnimationController controller, Widget? child = null) : Widget
{
    private Offset _naturalOrigin;

    private Size _size;

    public Widget? Child { get; set; } = child;
    public AnimationController Controller { get; } = controller;
    public float BeginScale { get; set; } = 0f;

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? new Size(width: 0, height: 0);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        _naturalOrigin = origin;
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        float scale = BeginScale + ((1f - BeginScale) * Controller.Value);
        float sw = _size.Width * scale;
        float sh = _size.Height * scale;
        float ox = origin.X + ((_size.Width - sw) / 2f);
        float oy = origin.Y + ((_size.Height - sh) / 2f);

        // Measure child again at scaled size so it can fill it
        Child?.Measure(Constraints.Tight(width: sw, height: sh));
        Child?.Layout(new Offset(x: ox, y: oy));
    }

    public override void Paint(PaintList paint)
    {
        if (Controller.Progress < 0.01f) return;
        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (Controller.Progress < 0.01f) return null;
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        // Without this the subtree never attaches — no theme, no focus, no semantics. The other
        // implicit transitions (AnimatedOpacity, AnimatedSize) already report their child.
        return ChildOrEmpty(Child);
    }
}
