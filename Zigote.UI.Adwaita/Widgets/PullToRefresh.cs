using Zigote.Core.Animation;

namespace Zigote.UI.Adwaita;

/// <summary>
///     Pull-to-refresh around a scrolling child — macOS 27's <c>NSRefreshController</c>, drawn the
///     Adwaita way. Pull past the top of <see cref="Child" /> (a <see cref="ScrollView" /> or
///     <see cref="ListView" />, or content too short to scroll) and the content follows the pull
///     with resistance, uncovering an <see cref="AdwSpinner" /> that fades in on the way to
///     <see cref="Threshold" />. Let go past it and <see cref="OnRefresh" /> runs, the content
///     parked below the spinning indicator until its task finishes.
///     <para>
///         The pull is the scroll the child could not use: this names itself the child's scroll
///         parent in hit testing, so a scroller already at its top bubbles the rest of a touch
///         drag or a trackpad swipe here. Notched mouse wheels never pull — a wheel has no "let
///         go", and a refresh that fires because someone scrolled up one notch too many is a bug.
///     </para>
/// </summary>
public sealed class AdwPullToRefresh : Widget
{
    // Content travels half as far as the finger, and never past two thresholds.
    private const float Resistance = 0.5f;

    // The wheel bubbles in ticks; ScrollView's own multiplier turns them into pixels.
    private const float WheelPixels = 40f;

    private const float SpinnerSize = 24f;

    // No gesture-end event reaches here from a trackpad (or a finger lifted without a fling), so a
    // pull that stops moving this long counts as released.
    // ponytail: idle-release; a trackpad's momentum tail can pull after a fling hits the top —
    // gate on the scroll phase if the engine ever forwards it.
    private readonly AnimationController _idle;
    private readonly AnimationController _settle;
    private float _from;
    private float _pull;
    private bool _refreshing;
    private Size _size;
    private AdwSpinner? _spinner;
    private float _to;

    public AdwPullToRefresh(Widget? child = null, Func<Task>? onRefresh = null)
    {
        Child = child;
        OnRefresh = onRefresh;
        _idle = new AnimationController(durationSeconds: 0.15f) { Curve = Curves.Linear };
        _idle.OnCompleted += Release;
        _settle = new AnimationController(durationSeconds: 0.25f) { Curve = Curves.EaseOut };
        _settle.OnTick += () => SetPull(_from + ((_to - _from) * _settle.Value));
    }

    public Widget? Child { get; set; }

    /// <summary>The refresh. The indicator spins until the task completes; null disables pulling.</summary>
    public Func<Task>? OnRefresh { get; set; }

    /// <summary>How far the content must travel (after resistance) for a release to refresh.</summary>
    public float Threshold { get; set; } = 64f;

    // Mount-scoped, as AdwSpinner does: a ticker handed out before mount is disposed on unmount,
    // and a page that is detached and re-attached would otherwise never settle again.
    protected override void OnMount()
    {
        _idle.AttachTicker(this);
        _settle.AttachTicker(this);
    }

    public bool IsRefreshing => _refreshing;

    /// <summary>Where the content rests while the refresh runs: just below the spinner.</summary>
    private float Hold => SpinnerSize + 24f;

    private bool CanPull => OnRefresh is not null && !_refreshing;

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
        _spinner?.Measure(Constraints.Loose(width: SpinnerSize, height: SpinnerSize));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);
        Child?.Layout(new Offset(x: origin.X, y: origin.Y + _pull));
        _spinner?.Layout(new Offset(
            x: origin.X + ((_size.Width - SpinnerSize) / 2f),
            y: origin.Y + MathF.Max(x: 0f, y: (_pull - SpinnerSize) / 2f)
        ));
    }

    public override void Paint(PaintList paint)
    {
        if (_pull <= 0f)
        {
            Child?.Paint(paint);
            return;
        }

        paint.AddClipStart(Bounds);
        if (_spinner is not null)
        {
            paint.PushAlpha(_refreshing ? 1f : Math.Clamp(value: _pull / Threshold, min: 0f, max: 1f));
            _spinner.Paint(paint);
            paint.PopAlpha();
        }

        Child?.Paint(paint);
        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        // As ScrollView does: anything the child can't scroll comes here, and past the pull it
        // chains on to whoever was going to get it.
        var outer = CurrentScrollParent;
        CurrentScrollParent = this;
        var hit = Child?.HitTest(point);
        if (hit is null) CurrentScrollParent = outer;
        else ScrollParent = outer;
        return hit;
    }

    public override void OnScroll(float dx, float dy)
    {
        // dy > 0 scrolls toward the top.
        if (!CanPull || Owner?.CurrentScrollPrecise != true || (dy <= 0f && _pull <= 0f))
        {
            base.OnScroll(dx: dx, dy: dy);
            return;
        }

        PullBy(dy * WheelPixels);
    }

    public override bool CanTouchScroll(bool vertical) => vertical && CanPull;

    public override void OnTouchScroll(float dx, float dy)
    {
        // dy > 0 is the finger moving down.
        if (!CanPull || (dy <= 0f && _pull <= 0f))
        {
            base.OnTouchScroll(dx: dx, dy: dy);
            return;
        }

        PullBy(dy);
    }

    public override void OnTouchFling(float velocityX, float velocityY)
    {
        if (_pull > 0f && !_refreshing) Release();
        else base.OnTouchFling(velocityX: velocityX, velocityY: velocityY);
    }

    private void PullBy(float delta)
    {
        _settle.Dismiss();
        SetPull(Math.Clamp(value: _pull + (delta * Resistance), min: 0f, max: Threshold * 2f));
        _idle.Seek(0f);
        _idle.Forward();
    }

    private void Release()
    {
        _idle.Dismiss();
        if (_refreshing || _pull <= 0f) return;
        if (_pull < Threshold || OnRefresh is not { } refresh)
        {
            SettleTo(0f);
            return;
        }

        _refreshing = true;
        SettleTo(Hold);
        Task task;
        try
        {
            task = refresh();
        }
        catch (Exception e)
        {
            task = Task.FromException(e);
        }

        task.ContinueWith(_ =>
        {
            if (Owner is { } app) app.Post(Done);
            else Done();
        }, TaskScheduler.Default);
    }

    private void Done()
    {
        _refreshing = false;
        SettleTo(0f);
    }

    private void SettleTo(float target)
    {
        _from = _pull;
        _to = target;
        _settle.Seek(0f);
        _settle.Forward();
    }

    private void SetPull(float pull)
    {
        if (_pull.Equals(pull)) return;
        _pull = pull;
        // The spinner only exists while it can be seen: a mounted one ticks, and an idle page must
        // not keep the frame loop awake.
        bool show = _pull > 0f || _refreshing;
        if (show && _spinner is null) SwapChild(previous: null, next: _spinner = new AdwSpinner(SpinnerSize));
        else if (!show && _spinner is not null)
        {
            var gone = _spinner;
            _spinner = null;
            SwapChild(previous: gone, next: null);
        }

        MarkNeedsLayout();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        if (Child is not null) yield return Child;
        if (_spinner is not null) yield return _spinner;
    }
}
