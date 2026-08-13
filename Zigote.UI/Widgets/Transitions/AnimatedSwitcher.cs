using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Cross-fades from the previous child to a new one whenever <see cref="Child" /> changes
///     (by reference, or by <see cref="Widget.Key" /> when keyed). The outgoing child fades out while
///     the incoming child fades in; once settled the old child is detached.
/// </summary>
public sealed class AnimatedSwitcher : ImplicitlyAnimatedWidget
{
    private Widget? _current;
    private Widget? _outgoing;
    private Size _size;

    public AnimatedSwitcher(Widget? child = null, float duration = 0.25f,
        Func<float, float>? curve = null)
        : base(duration, curve)
    {
        _current = child;
    }

    public Widget? Child
    {
        get => _current;
        set
        {
            if (SameChild(_current, value))
            {
                // Same key+type but a freshly-built instance: update the retained child's config in
                // place (no cross-fade), per the framework's keyed-reconcile contract, instead of
                // silently dropping the new config.
                if (!ReferenceEquals(_current, value))
                {
                    _current?.UpdateFrom(value!);
                    MarkNeedsLayout();
                }

                return;
            }

            // The child already fading out is the one genuinely dropped here (a second switch
            // landing mid-cross-fade). Attach-then-detach via SwapChild so an incoming tree that
            // re-adopts part of it keeps that part alive — see Widget.SwapChild.
            var dropped = _outgoing;
            _outgoing = _current;
            _current = value;
            SwapChild(dropped, _current);
            Animate();
        }
    }

    private static bool SameChild(Widget? a, Widget? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Key is not null && a.Key.Equals(b.Key) && a.GetType() == b.GetType();
    }

    public override Size Measure(Constraints c)
    {
        // Retire the outgoing child once the cross-fade has settled.
        if (_outgoing is not null && Progress >= 0.999f)
        {
            _outgoing.Detach();
            _outgoing = null;
        }

        var cur = _current?.Measure(c) ?? Size.Zero;
        if (_outgoing is not null && Progress < 0.999f)
        {
            // Mid-cross-fade: ease the reported size from the outgoing child's toward the incoming
            // child's, so a switch between different-sized subtrees reflows smoothly instead of
            // snapping the surrounding layout on frame one.
            var old = _outgoing.Measure(c);
            var t = Progress;
            _size = c.Constrain(
                new Size(
                    old.Width + (cur.Width - old.Width) * t,
                    old.Height + (cur.Height - old.Height) * t
                )
            );
        }
        else
        {
            _size = cur;
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
        _current?.Layout(origin);
        _outgoing?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (_outgoing is not null && Progress < 0.999f)
        {
            var outAlpha = 1f - Progress;
            if (outAlpha > 0.001f)
            {
                paint.PushAlpha(outAlpha);
                _outgoing.Paint(paint);
                paint.PopAlpha();
            }

            if (Progress > 0.001f)
            {
                paint.PushAlpha(Progress);
                _current?.Paint(paint);
                paint.PopAlpha();
            }

            return;
        }

        _current?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? _current?.HitTest(point) : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        // Both alive only mid-cross-fade — the transient two-element array is fine there.
        if (_current is not null && _outgoing is not null) return [_current, _outgoing];
        if (_current is not null) return ChildOrEmpty(_current);
        return ChildOrEmpty(_outgoing);
    }
}
