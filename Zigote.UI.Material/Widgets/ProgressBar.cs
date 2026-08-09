using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     Horizontal progress bar. <see cref="Value" /> is in [0, 1].
///     Set Value to null for an indeterminate animated bar — animation runs automatically
///     via a self-owned AnimationController; no manual Tick call required.
/// </summary>
public class ProgressBar : Widget
{
    private readonly AnimationController _anim;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private float? _value;
    private float _height = ControlMetrics.SliderTrack + 2f;

    public ProgressBar(float? value = 0f)
    {
        // Duration matches the original dt*0.8 speed: 1/0.8 = 1.25 s per cycle.
        _anim = new AnimationController(1.25f, this);
        _anim.OnTick += MarkNeedsPaint;
        _value = value;
        if (value is null) _anim.Repeat(); // start indeterminate loop immediately
    }

    public float? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            if (value is null) _anim.Repeat();
            else _anim.Dismiss();
            MarkNeedsPaint();
        }
    }

    public float Height
    {
        get => _height;
        set => SetLayout(ref _height, value);
    }

    public float? Radius { get; set; }


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        // Rebind the ticker Detach disposed, so an indeterminate bar keeps sliding after a re-attach.
        _anim.AttachTicker(this);
    }


    public override int DebugStateHash()
    {
        return HashCode.Combine(_value.GetHashCode(), _anim.Progress.GetHashCode());
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsPositiveInfinity(c.MaxWidth) ? 200f : c.MaxWidth;
        _size = c.Constrain(new Size(w, Height));
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
        // Capsule track at the bar's full height; the rounded ends read as a pill.
        var radius = Radius ?? Bounds.Height / 2f;
        paint.AddRect(Bounds, _theme.Fill1, radius);

        if (_value is { } v)
        {
            var fillW = MathF.Max(0f, Math.Clamp(v, 0f, 1f) * Bounds.Width);
            if (fillW > 0)
                paint.AddRect(
                    new Rect(
                        Bounds.X,
                        Bounds.Y,
                        fillW,
                        Bounds.Height
                    ),
                    _theme.Primary,
                    radius
                );
        }
        else
        {
            // Sliding block 30% wide, position driven by animation value.
            var blockW = Bounds.Width * 0.30f;
            var startX = Bounds.X + (Bounds.Width + blockW) * _anim.Value - blockW;
            var visX = MathF.Max(Bounds.X, startX);
            var visW = MathF.Min(Bounds.X + Bounds.Width, startX + blockW) - visX;
            if (visW > 0)
                paint.AddRect(
                    new Rect(
                        visX,
                        Bounds.Y,
                        visW,
                        Bounds.Height
                    ),
                    _theme.Primary,
                    radius
                );
        }
    }
}