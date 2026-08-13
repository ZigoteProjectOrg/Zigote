using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Rebuilds its widget subtree every time the <see cref="Animation" /> value changes.
///     <para>
///         The <see cref="Builder" /> function is re-invoked whenever the animation's progress
///         changes by more than 0.001 (effectively every frame while animating).
///         The optional static <see cref="Child" /> is passed into <see cref="Builder" /> so
///         parts of the tree that don't depend on the animation value are not recreated.
///     </para>
///     <para>Example:</para>
///     <code>
///   var fade = new AnimatedBuilder(
///       ctrl,
///       (ctx, child) => new Container
///       {
///           Background   = Color.Blue.WithAlpha(ctrl.Value),
///           Child        = child,
///       },
///       child: new Label("Static content"));
/// </code>
/// </summary>
public sealed class AnimatedBuilder(
    AnimationController animation,
    Func<BuildContext, Widget?, Widget> builder,
    Widget? child = null)
    : Widget
{
    private Widget? _built;
    private float _lastValue = float.NaN;
    private Size _size;

    public AnimationController Animation { get; set; } = animation;

    /// <summary>
    ///     Builds the animated subtree. Receives the current <see cref="BuildContext" />
    ///     and the optional static <see cref="Child" />. Called when the animation value changes.
    /// </summary>
    public Func<BuildContext, Widget?, Widget> Builder { get; set; } = builder;

    /// <summary>
    ///     An optional static child passed to <see cref="Builder" />. Widgets here are
    ///     created once and not re-created on animation ticks.
    /// </summary>
    public Widget? Child { get; set; } = child;

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        RebuildIfDirty();
        _size = _built?.Measure(c) ?? Size.Zero;
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
        _built?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _built?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _built?.HitTest(point) ?? this;
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(Animation.Value.GetHashCode(), _built?.DebugStateHash() ?? 0);
    }

    // ── Rebuild logic ─────────────────────────────────────────────────────────

    private void RebuildIfDirty()
    {
        var v = Animation.Value;
        if (float.IsNaN(_lastValue) || Math.Abs(v - _lastValue) > 0.001f)
        {
            _built = Builder(BuildContext.Current, Child);
            _lastValue = v;
        }
    }
}
