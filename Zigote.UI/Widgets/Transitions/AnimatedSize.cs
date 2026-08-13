using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Eases its own size toward the child's measured size whenever that size changes — content that
///     grows or shrinks (text expanding, an image arriving, a section collapsing) reflows the
///     surrounding layout smoothly instead of snapping. While the box is smaller than the child, the
///     child is clipped to the animated bounds. The companion to <see cref="AnimatedSwitcher" />:
///     that one eases between two subtrees, this one eases one subtree's size in place.
/// </summary>
public sealed class AnimatedSize : ImplicitlyAnimatedWidget
{
    private Widget? _child;
    private Size _from;
    private bool _hasSize;
    private Size _size;
    private Size _to;

    public AnimatedSize(Widget? child = null, float duration = 0.2f,
        Func<float, float>? curve = null)
        : base(durationSeconds: duration, curve: curve) =>
        _child = child;

    public Widget? Child
    {
        get => _child;
        set
        {
            if (ReferenceEquals(objA: _child, objB: value)) return;
            var previous = _child;
            _child = value;
            SwapChild(previous: previous, next: _child); // attach-then-detach; see Widget.SwapChild
            MarkNeedsLayout();
        }
    }

    public override Size Measure(Constraints c)
    {
        var target = _child?.Measure(c) ?? Size.Zero;
        if (!_hasSize)
        {
            // First measure renders at the natural size — animating a widget's initial appearance
            // from zero is AnimatedSwitcher/Animate territory, not a size *change*.
            _hasSize = true;
            _from = _to = target;
        }
        else if (MathF.Abs(target.Width - _to.Width) > 0.5f ||
                 MathF.Abs(target.Height - _to.Height) > 0.5f)
        {
            // New target: ease from whatever size is currently on screen, so a target that changes
            // mid-flight (e.g. every frame of a window resize) chases smoothly instead of jumping.
            _from = _size;
            _to = target;
            Animate();
        }

        float t = Progress;
        _size = c.Constrain(
            new Size(
                width: _from.Width + ((_to.Width - _from.Width) * t),
                height: _from.Height + ((_to.Height - _from.Height) * t)
            )
        );
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (_child is null) return;

        // Clip only while animating — a settled box matches the child exactly, and an always-on
        // clip would cost a clip op per frame on every instance.
        bool settled = MathF.Abs(_size.Width - _to.Width) <= 0.5f &&
                       MathF.Abs(_size.Height - _to.Height) <= 0.5f;
        if (settled)
        {
            _child.Paint(paint);
            return;
        }

        paint.AddClipStart(Bounds);
        _child.Paint(paint);
        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? _child?.HitTest(point) : null;

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_child);
}
