using Zigote.Core.Animation;

namespace Zigote.UI.Material;

/// <summary>
///     Paint-only leaf that pops a mark in and eases it back out as <see cref="Visible" /> toggles —
///     the shared half of <see cref="CheckGlyph" /> and <see cref="RadioDotGlyph" />. Subclasses supply
///     only the drawing, in <see cref="PaintGlyph" />.
///     <para>
///         The composed control (<see cref="Checkbox" />, <see cref="Radio{T}" />) puts one of these
///         inside its box <see cref="Layout.DecoratedBox" />: the box draws the fill and border, the
///         glyph draws only the mark. The first <see cref="Visible" /> assignment snaps without
///         animating, so an initially-checked control shows its mark immediately.
///     </para>
/// </summary>
public abstract class ToggleGlyph : LeafWidget
{
    private readonly AnimationController _anim;
    private bool _initialized;
    private Size _size;
    private bool _visible;

    /// <param name="duration">Entrance/exit duration — the one thing the two glyphs disagree on.</param>
    protected ToggleGlyph(float duration)
    {
        _anim = new AnimationController(duration, this) { Curve = Curves.EaseOutBack };
        _anim.OnTick += MarkNeedsPaint;
    }

    public float GlyphSize { get; set; }
    public Color Color { get; set; } = Color.White;

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_initialized && _visible == value) return;
            _visible = value;
            if (!_initialized)
            {
                _initialized = true;
                if (value) _anim.Complete();
                else _anim.Dismiss();
            }
            else if (value)
            {
                _anim.Forward();
            }
            else
            {
                _anim.Reverse();
            }
        }
    }

    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a re-attach rebinds
    // instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        _anim.AttachTicker(this);
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(GlyphSize, GlyphSize));
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
    }

    public sealed override void Paint(PaintList paint)
    {
        var t = _anim.Value;
        if (t <= 0.001f) return;
        PaintGlyph(paint, t);
    }

    /// <summary>
    ///     Draw the mark inside <see cref="Widget.Bounds" />, scaled by <paramref name="t" /> (0 → 1 as
    ///     it pops in). Only called while <paramref name="t" /> is visibly above zero.
    /// </summary>
    protected abstract void PaintGlyph(PaintList paint, float t);

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            _visible,
            _anim.Value,
            Color,
            Bounds.Width
        );
    }
}
