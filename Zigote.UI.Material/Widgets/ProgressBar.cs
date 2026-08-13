using Zigote.Core.Animation;

namespace Zigote.UI.Material;

/// <summary>
///     Horizontal progress bar. <see cref="Value" /> is in [0, 1].
///     Set Value to null for an indeterminate animated bar — animation runs automatically
///     via a self-owned AnimationController; no manual Tick call required.
/// </summary>
public class ProgressBar : Widget
{
    private readonly AnimationController _anim;
    private float _height = ControlMetrics.SliderTrack + 2f;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private float? _value;

    public ProgressBar(float? value = 0f)
    {
        // Duration matches the original dt*0.8 speed: 1/0.8 = 1.25 s per cycle.
        _anim = new AnimationController(durationSeconds: 1.25f, vsync: this);
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
        set => SetLayout(field: ref _height, value: value);
    }

    public float? Radius { get; set; }


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        // Rebind the ticker Detach disposed, so an indeterminate bar keeps sliding after a re-attach.
        _anim.AttachTicker(this);
    }


    public override int DebugStateHash() => HashCode.Combine(
        value1: _value.GetHashCode(),
        value2: _anim.Progress.GetHashCode()
    );

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float w = float.IsPositiveInfinity(c.MaxWidth) ? 200f : c.MaxWidth;
        _size = c.Constrain(new Size(width: w, height: Height));
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
    }

    public override void Paint(PaintList paint)
    {
        // Capsule track at the bar's full height; the rounded ends read as a pill.
        float radius = Radius ?? Bounds.Height / 2f;
        paint.AddRect(bounds: Bounds, color: _theme.Fill1, radius: radius);

        if (_value is { } v)
        {
            float fillW = MathF.Max(
                x: 0f,
                y: Math.Clamp(value: v, min: 0f, max: 1f) * Bounds.Width
            );
            if (fillW > 0)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X,
                        y: Bounds.Y,
                        width: fillW,
                        height: Bounds.Height
                    ),
                    color: _theme.Primary,
                    radius: radius
                );
            }
        }
        else
        {
            // Sliding block 30% wide, position driven by animation value.
            float blockW = Bounds.Width * 0.30f;
            float startX = Bounds.X + ((Bounds.Width + blockW) * _anim.Value) - blockW;
            float visX = MathF.Max(x: Bounds.X, y: startX);
            float visW = MathF.Min(x: Bounds.X + Bounds.Width, y: startX + blockW) - visX;
            if (visW > 0)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: visX,
                        y: Bounds.Y,
                        width: visW,
                        height: Bounds.Height
                    ),
                    color: _theme.Primary,
                    radius: radius
                );
            }
        }
    }
}
