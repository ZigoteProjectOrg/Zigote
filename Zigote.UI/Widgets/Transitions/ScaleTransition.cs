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
        _size = Child?.Measure(c) ?? new Size(0, 0);
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

        var scale = BeginScale + (1f - BeginScale) * Controller.Value;
        var sw = _size.Width * scale;
        var sh = _size.Height * scale;
        var ox = origin.X + (_size.Width - sw) / 2f;
        var oy = origin.Y + (_size.Height - sh) / 2f;

        // Measure child again at scaled size so it can fill it
        Child?.Measure(Constraints.Tight(sw, sh));
        Child?.Layout(new Offset(ox, oy));
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

    public override IEnumerable<Widget> GetChildren()
    {
        // Without this the subtree never attaches — no theme, no focus, no semantics. The other
        // implicit transitions (AnimatedOpacity, AnimatedSize) already report their child.
        return ChildOrEmpty(Child);
    }
}
