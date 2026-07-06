using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     Paint-only leaf: the check-mark tick, drawn as two rounded strokes inside its bounds. Used by
///     the
///     composed <see cref="Checkbox" /> as the child of its box <see cref="Layout.DecoratedBox" />;
///     the
///     box draws the fill/border, this draws only the mark. Toggling <see cref="Visible" /> pops the
///     tick in (scale + fade) and eases it back out — the first assignment snaps without animating so
///     an initially-checked box shows its mark immediately.
/// </summary>
public sealed class CheckGlyph : LeafWidget, ITickerProvider
{
    private readonly AnimationController _anim;
    private bool _initialized;
    private Size _size;
    private Ticker? _ticker;
    private bool _visible;

    public CheckGlyph()
    {
        _anim = new AnimationController(Motion.Standard, this) { Curve = Curves.EaseOutBack };
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

        var s = MathF.Min(Bounds.Width, Bounds.Height);
        var stroke = MathF.Max(1.5f, s * 0.12f);
        var color = Color.WithAlpha(Math.Clamp(t, 0f, 1f));

        // Scale the tick about the glyph centre so it pops in and eases out.
        var mx = Bounds.X + Bounds.Width / 2f;
        var my = Bounds.Y + Bounds.Height / 2f;

        // Geometry of a tick: short leg from the lower-left, long leg up to the upper-right.
        var cx = Bounds.X;
        var cy = Bounds.Y;
        var ax = cx + s * 0.26f;
        var ay = cy + s * 0.52f;
        var bx = cx + s * 0.42f;
        var by = cy + s * 0.68f;
        var dx = cx + s * 0.74f;
        var dy = cy + s * 0.34f;

        StrokeLine(
            paint,
            mx + (ax - mx) * t,
            my + (ay - my) * t,
            mx + (bx - mx) * t,
            my + (by - my) * t,
            stroke,
            color
        );
        StrokeLine(
            paint,
            mx + (bx - mx) * t,
            my + (by - my) * t,
            mx + (dx - mx) * t,
            my + (dy - my) * t,
            stroke,
            color
        );
    }

    /// <summary>Approximates a short line with a chain of small square dabs (no native line primitive).</summary>
    private static void StrokeLine(PaintList paint, float x0, float y0, float x1, float y1, float w,
        Color color)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        var steps = MathF.Max(1f, MathF.Ceiling(len / (w * 0.4f)));
        var half = w / 2f;

        for (var i = 0f; i <= steps; i++)
        {
            var t = i / steps;
            var px = x0 + dx * t;
            var py = y0 + dy * t;
            paint.AddRect(
                new Rect(
                    px - half,
                    py - half,
                    w,
                    w
                ),
                color,
                half
            );
        }
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