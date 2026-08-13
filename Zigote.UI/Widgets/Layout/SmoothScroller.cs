using Zigote.Core.Animation;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     One axis of smooth scrolling: the rendered <see cref="Offset" /> eases toward a target each
///     frame via a <see cref="Ticker" /> (the app keeps pumping frames while any ticker is active).
///     Owns the ticker — dispose via <see cref="Dispose" /> (typically from the widget's
///     <c>Detach()</c>). <paramref name="onChanged" /> fires whenever the offset moves, so the owner
///     can relayout / repaint.
/// </summary>
public sealed class SmoothScroller(Action onChanged) : IDisposable
{
    private const float Ease = 22f; // ease rate — higher = snappier

    // Fling physics: exponential velocity decay, tuned between iOS's floaty glide and
    // Android's brisker stop. Halts below MinFlingSpeed (sub-pixel-per-frame crawl) or at an
    // edge (hard clamp — no overscroll/bounce model yet).
    private const float FlingFriction = 3.2f; // 1/s — higher = stops sooner
    private const float MinFlingSpeed = 40f; // logical px/s
    private float _flingVelocity; // ±px/s while flinging, 0 otherwise
    private Ticker? _ticker;

    /// <summary>The rendered offset (animated).</summary>
    public float Offset { get; private set; }

    /// <summary>Where the offset is heading.</summary>
    public float Target { get; private set; }

    /// <summary>Max scrollable offset = content − viewport. Set by the owner each Layout.</summary>
    public float Max { get; set; }

    /// <summary>True while inertial (fling) scrolling is running.</summary>
    public bool IsFlinging => _flingVelocity != 0f;

    public void Dispose()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    /// <summary>
    ///     Scroll by <paramref name="delta" /> (animated when <paramref name="animate" /> is true).
    ///     Returns false if it couldn't move (already at the edge) so the caller can bubble the wheel
    ///     to a parent scrollable.
    /// </summary>
    public bool MoveBy(float delta, bool animate)
    {
        _flingVelocity = 0f; // direct input overrides inertia
        var clamped = Math.Clamp(Target + delta, 0f, MathF.Max(0f, Max));
        if (MathF.Abs(clamped - Offset) < 0.05f && MathF.Abs(clamped - Target) < 0.05f)
            return false;
        Target = clamped;
        if (animate) (_ticker ??= new Ticker(Tick)).Start();
        else Settle(clamped);
        return true;
    }

    /// <summary>Jump straight to an absolute offset, no animation (caret-follow, scrollbar drag).</summary>
    public void JumpTo(float offset)
    {
        _flingVelocity = 0f;
        Settle(Math.Clamp(offset, 0f, MathF.Max(0f, Max)));
    }

    /// <summary>Ease toward an absolute offset (reveal-into-view). No-op if already there.</summary>
    public void AnimateTo(float offset)
    {
        _flingVelocity = 0f;
        var clamped = Math.Clamp(offset, 0f, MathF.Max(0f, Max));
        if (MathF.Abs(clamped - Target) < 0.05f && MathF.Abs(clamped - Offset) < 0.05f) return;
        Target = clamped;
        (_ticker ??= new Ticker(Tick)).Start();
    }

    /// <summary>
    ///     Start inertial scrolling at <paramref name="velocity" /> logical px/s (sign = offset
    ///     direction: positive scrolls toward <see cref="Max" />). The velocity decays
    ///     exponentially; an edge stops it dead. Returns false when there is nothing to glide
    ///     (too slow, or already at the edge it points at) so the caller can bubble the fling.
    /// </summary>
    public bool Fling(float velocity)
    {
        var max = MathF.Max(0f, Max);
        if (MathF.Abs(velocity) < MinFlingSpeed) return false;
        if (velocity < 0f && Offset <= 0f) return false;
        if (velocity > 0f && Offset >= max) return false;
        _flingVelocity = velocity;
        Target = Offset; // keep Target coherent for MoveBy/Reclamp while gliding
        (_ticker ??= new Ticker(Tick)).Start();
        return true;
    }

    /// <summary>Re-clamp current + target after a content/viewport size change (call in Layout).</summary>
    public void Reclamp()
    {
        var max = MathF.Max(0f, Max);
        Target = Math.Clamp(Target, 0f, max);
        Offset = Math.Clamp(Offset, 0f, max);
    }

    private void Settle(float v)
    {
        Offset = Target = v;
        _ticker?.Stop();
        onChanged();
    }

    private void Tick(float dt)
    {
        if (_flingVelocity != 0f)
        {
            var max = MathF.Max(0f, Max);
            var next = Offset + _flingVelocity * dt;
            _flingVelocity *= MathF.Exp(-dt * FlingFriction);
            if (next <= 0f || next >= max || MathF.Abs(_flingVelocity) < MinFlingSpeed)
            {
                _flingVelocity = 0f;
                Settle(Math.Clamp(next, 0f, max));
                return;
            }

            Offset = Target = next;
            onChanged();
            return;
        }

        var k = 1f - MathF.Exp(-dt * Ease); // frame-rate independent
        Offset += (Target - Offset) * k;
        if (MathF.Abs(Target - Offset) < 0.4f)
        {
            Offset = Target;
            _ticker?.Stop();
        }

        onChanged();
    }
}
