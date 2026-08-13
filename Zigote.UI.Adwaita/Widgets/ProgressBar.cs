using Zigote.Core.Animation;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwProgressBar — the GNOME progress bar: a 4px fully-rounded <see cref="ThemeData.Fill2" />
///     trough with an accent fill. Set <see cref="Indeterminate" /> for a sliding 30% segment.
/// </summary>
public sealed class AdwProgressBar : Widget
{
    private readonly AnimationController _anim;
    private bool _indeterminate;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private float _value;

    public AdwProgressBar(float value = 0f)
    {
        _value = Math.Clamp(value: value, min: 0f, max: 1f);
        // No vsync here on purpose: a Ticker joins a static running list the moment it starts, so an
        // indeterminate bar that is built and dropped without ever being attached would pin the
        // frame loop awake forever. Indeterminate only parks the status at Forward; Attach's
        // AttachTicker is what actually starts it.
        _anim = new AnimationController(1.25f);
        _anim.OnTick += MarkNeedsPaint;
    }

    /// <summary>Progress in [0, 1].</summary>
    public float Value
    {
        get => _value;
        set
        {
            float v = Math.Clamp(value: value, min: 0f, max: 1f);
            if (v == _value) return;
            _value = v;
            MarkNeedsPaint();
        }
    }

    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            if (value) _anim.Repeat();
            else _anim.Dismiss();
            MarkNeedsPaint();
        }
    }


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        // Rebind the ticker Detach disposed, so an indeterminate bar keeps sliding after re-attach.
        _anim.AttachTicker(this);
    }


    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.ProgressBar;
        if (!Indeterminate) config.Value = $"{Value * 100f:F0}%";
    }

    public override int DebugStateHash() => HashCode.Combine(
        value1: _value,
        value2: _indeterminate,
        value3: _anim.Progress.GetHashCode()
    );

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        _size = c.Constrain(new Size(width: w, height: AdwMetrics.ProgressBarHeight));
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
        float radius = Bounds.Height / 2f;
        // progressbar > trough @extends %scale_trough — the same currentColor 15% every trough in
        // the stylesheet uses, not a lighter fill of its own.
        paint.AddRect(bounds: Bounds, color: AdwStyle.TroughFill(_theme), radius: radius);

        if (!Indeterminate)
        {
            float fillW = Value * Bounds.Width;
            if (fillW > 0f)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X,
                        y: Bounds.Y,
                        width: fillW,
                        height: Bounds.Height
                    ),
                    color: _theme.Accent,
                    radius: radius
                );
            }

            return;
        }

        // Sliding 30% segment, position driven by the repeating animation.
        float blockW = Bounds.Width * 0.30f;
        float startX = Bounds.X + ((Bounds.Width + blockW) * _anim.Value) - blockW;
        float visX = MathF.Max(x: Bounds.X, y: startX);
        float visW = MathF.Min(x: Bounds.Right, y: startX + blockW) - visX;
        if (visW > 0f)
        {
            paint.AddRect(
                bounds: new Rect(
                    x: visX,
                    y: Bounds.Y,
                    width: visW,
                    height: Bounds.Height
                ),
                color: _theme.Accent,
                radius: radius
            );
        }
    }
}

/// <summary>
///     AdwLevelBar — the GNOME level bar: a 6px trough whose fill colour grades by value
///     (success ≥ 0.75, accent ≥ 0.35, warning ≥ 0.15, destructive below).
/// </summary>
public sealed class AdwLevelBar : LeafWidget
{
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private float _value;

    public AdwLevelBar(float value = 0f) => _value = Math.Clamp(value: value, min: 0f, max: 1f);

    /// <summary>Level in [0, 1].</summary>
    public float Value
    {
        get => _value;
        set
        {
            float v = Math.Clamp(value: value, min: 0f, max: 1f);
            if (v == _value) return;
            _value = v;
            MarkNeedsPaint();
        }
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.ProgressBar;
        config.Value = $"{Value * 100f:F0}%";
    }

    public override int DebugStateHash() => HashCode.Combine(value1: _value, value2: Bounds.Width);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        _size = c.Constrain(new Size(width: w, height: 6f));
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
        float radius = Bounds.Height / 2f;
        paint.AddRect(bounds: Bounds, color: AdwStyle.TroughFill(_theme), radius: radius);

        float fillW = Value * Bounds.Width;
        if (fillW <= 0f) return;

        var p = AdwPalette.For(_theme);
        var color = Value >= 0.75f ? p.SuccessBg
            : Value >= 0.35f ? _theme.Accent
            : Value >= 0.15f ? p.WarningBg
            : p.DestructiveBg;
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.X,
                y: Bounds.Y,
                width: fillW,
                height: Bounds.Height
            ),
            color: color,
            radius: radius
        );
    }
}
