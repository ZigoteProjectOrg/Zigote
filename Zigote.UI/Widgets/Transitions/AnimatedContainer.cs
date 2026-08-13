using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Interpolates its size and background color between two states using an
///     <see cref="AnimationController" />. Call <see cref="AnimateTo" /> to start a
///     transition; it snaps the begin values and drives forward.
/// </summary>
public sealed class AnimatedContainer : Widget
{
    // When no vsync provider is supplied the widget drives the controller from its own ticker (like
    // ImplicitlyAnimatedWidget) — otherwise AnimateTo would set targets that nothing ever advances.
    private readonly bool _selfDriven;
    private Color _beginColor, _endColor;

    // Begin / end targets
    private float _beginW, _beginH, _endW, _endH;
    private Color _curColor;

    // Current interpolated values (read from Paint)
    private float _curW, _curH;

    private Size _size;
    private Ticker? _ticker;

    public AnimatedContainer(float width, float height, Color color,
        float durationSeconds = 0.3f, Widget? child = null, ITickerProvider? vsync = null)
    {
        _beginW = _endW = _curW = width;
        _beginH = _endH = _curH = height;
        _beginColor = _endColor = _curColor = color;
        Child = child;
        Controller = new AnimationController(durationSeconds, vsync);
        Controller.OnTick += UpdateInterpolated;
        _selfDriven = vsync is null;
        if (_selfDriven) _ticker = new Ticker(Step);
    }

    public Widget? Child { get; set; }
    public AnimationController Controller { get; }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        if (!_selfDriven) return;
        _ticker?.Dispose();
        _ticker = new Ticker(Step);
        if (Controller.Status is AnimationStatus.Forward or AnimationStatus.Reverse)
            _ticker.Start();
    }

    public override void Detach()
    {
        base.Detach();
        if (!_selfDriven) return;
        _ticker?.Dispose();
        _ticker = null;
    }

    private void Step(float dt)
    {
        Controller.Tick(dt);
        if (Controller.Status is AnimationStatus.Completed or AnimationStatus.Dismissed)
            _ticker?.Stop();
    }

    /// <summary>Animate to a new size and/or color. Snaps begin to current state.</summary>
    public void AnimateTo(float? width = null, float? height = null, Color? color = null)
    {
        _beginW = _curW;
        _beginH = _curH;
        _beginColor = _curColor;
        _endW = width ?? _endW;
        _endH = height ?? _endH;
        _endColor = color ?? _endColor;
        Controller.Forward();
        if (_selfDriven) _ticker?.Start();
    }

    private void UpdateInterpolated()
    {
        var t = Controller.Value;
        _curW = _beginW + (_endW - _beginW) * t;
        _curH = _beginH + (_endH - _beginH) * t;
        _curColor = new Color(
            _beginColor.R + (_endColor.R - _beginColor.R) * t,
            _beginColor.G + (_endColor.G - _beginColor.G) * t,
            _beginColor.B + (_endColor.B - _beginColor.B) * t,
            _beginColor.A + (_endColor.A - _beginColor.A) * t
        );
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(_curW, _curH));
        Child?.Measure(Constraints.Tight(_size.Width, _size.Height));
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
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, _curColor);
        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }
}
