using Zigote.Core.Animation;
using Zigote.UI.Host;
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
        _value = Math.Clamp(value, 0f, 1f);
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
            var v = Math.Clamp(value, 0f, 1f);
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

    public override int DebugStateHash()
    {
        return HashCode.Combine(_value, _indeterminate, _anim.Progress.GetHashCode());
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        _size = c.Constrain(new Size(w, AdwMetrics.ProgressBarHeight));
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
        var radius = Bounds.Height / 2f;
        // progressbar > trough @extends %scale_trough — the same currentColor 15% every trough in
        // the stylesheet uses, not a lighter fill of its own.
        paint.AddRect(Bounds, AdwStyle.TroughFill(_theme), radius);

        if (!Indeterminate)
        {
            var fillW = Value * Bounds.Width;
            if (fillW > 0f)
                paint.AddRect(
                    new Rect(
                        Bounds.X,
                        Bounds.Y,
                        fillW,
                        Bounds.Height
                    ),
                    _theme.Accent,
                    radius
                );
            return;
        }

        // Sliding 30% segment, position driven by the repeating animation.
        var blockW = Bounds.Width * 0.30f;
        var startX = Bounds.X + (Bounds.Width + blockW) * _anim.Value - blockW;
        var visX = MathF.Max(Bounds.X, startX);
        var visW = MathF.Min(Bounds.Right, startX + blockW) - visX;
        if (visW > 0f)
            paint.AddRect(
                new Rect(
                    visX,
                    Bounds.Y,
                    visW,
                    Bounds.Height
                ),
                _theme.Accent,
                radius
            );
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

    public AdwLevelBar(float value = 0f)
    {
        _value = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Level in [0, 1].</summary>
    public float Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, 0f, 1f);
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

    public override int DebugStateHash()
    {
        return HashCode.Combine(_value, Bounds.Width);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        _size = c.Constrain(new Size(w, 6f));
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
        var radius = Bounds.Height / 2f;
        paint.AddRect(Bounds, AdwStyle.TroughFill(_theme), radius);

        var fillW = Value * Bounds.Width;
        if (fillW <= 0f) return;

        var p = AdwPalette.For(_theme);
        var color = Value >= 0.75f ? p.SuccessBg
            : Value >= 0.35f ? _theme.Accent
            : Value >= 0.15f ? p.WarningBg
            : p.DestructiveBg;
        paint.AddRect(
            new Rect(
                Bounds.X,
                Bounds.Y,
                fillW,
                Bounds.Height
            ),
            color,
            radius
        );
    }
}
