using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     Paint-only leaf: the filled centre dot of a selected radio button. Used by the composed
///     <see cref="Radio{T}" /> as the child of its circular <see cref="Layout.DecoratedBox" />; the
///     box
///     draws the ring/fill, this draws only the dot. Toggling <see cref="Visible" /> scales the dot in
///     and out — the first assignment snaps without animating.
/// </summary>
public sealed class RadioDotGlyph : LeafWidget, ITickerProvider
{
    private readonly AnimationController _anim;
    private bool _initialized;
    private Size _size;
    private Ticker? _ticker;
    private bool _visible;

    public RadioDotGlyph()
    {
        _anim = new AnimationController(Motion.Fast, this) { Curve = Curves.EaseOutBack };
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

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _anim.AttachTicker(this);
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose();
        _ticker = null;
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

    public override void Paint(PaintList paint)
    {
        var t = _anim.Value;
        if (t <= 0.001f) return;

        var radius = MathF.Min(Bounds.Width, Bounds.Height) / 2f;
        var inner = radius * 2f * 0.28f * MathF.Max(0f, t); // scale the dot about the centre
        var dot = new Rect(
            Bounds.X + radius - inner,
            Bounds.Y + radius - inner,
            inner * 2f,
            inner * 2f
        );
        paint.AddRect(dot, Color.WithAlpha(Math.Clamp(t, 0f, 1f)), inner);
    }

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