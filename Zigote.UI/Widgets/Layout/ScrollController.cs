namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Owns a scroll position outside the widget tree. Hand the same controller to a
///     <see cref="ScrollView" /> or <see cref="ListView" /> and the offset survives anything that
///     replaces the scroller — a rebuild, a page swapped out and back, a route re-created — and can
///     be read or driven from app code:
///     <code>
///   readonly ScrollController _feed = new();
///   …
///   new ListView { Controller = _feed };
///   _feed.AnimateTo(0);   // back to top
/// </code>
///     Rebuilds that return a same-shaped tree already keep their offsets without one; a controller is
///     for positions that must outlive the widget or be commanded.
/// </summary>
public sealed class ScrollController(float initialOffset = 0f)
{
    private float _offset = initialOffset;
    private SmoothScroller? _scroller;

    /// <summary>The current offset (where a smooth scroll is heading, while one runs).</summary>
    public float Offset => _scroller?.Target ?? _offset;

    /// <summary>Max scrollable offset of the attached scroller; 0 while none is attached.</summary>
    public float MaxExtent => _scroller?.Max ?? 0f;

    /// <summary>True while a scroller is attached and rendering this position.</summary>
    public bool HasClients => _scroller is not null;

    public void JumpTo(float offset)
    {
        if (_scroller is null) _offset = offset;
        else _scroller.JumpTo(offset);
    }

    public void AnimateTo(float offset)
    {
        if (_scroller is null) _offset = offset;
        else _scroller.AnimateTo(offset);
    }

    // A rebuilt scroller binds before the one it replaces unbinds, so take the live position.
    internal void Bind(SmoothScroller scroller)
    {
        float offset = Offset;
        _scroller = scroller;
        scroller.Restore(offset);
    }

    internal void Unbind(SmoothScroller scroller)
    {
        if (!ReferenceEquals(objA: _scroller, objB: scroller)) return;
        _offset = scroller.Target;
        _scroller = null;
    }
}
