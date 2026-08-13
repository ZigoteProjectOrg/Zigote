using Zigote.Core.Animation;
using Zigote.Core.State;

namespace Zigote.UI.BottomSheets;

/// <summary>
///     The sheet's position and the handle content uses to drive it. Position is an <b>extent</b>: the
///     fraction of the available height the sheet occupies, between <see cref="MinExtent" /> and
///     <see cref="MaxExtent" /> — the same model as <c>bottom_sheet</c>'s
///     <c>minHeight</c>/<c>initHeight</c>/<c>maxHeight</c>.
///     <para>
///         <see cref="Extent" /> is a <see cref="Signal{T}" />, so content that must react to the
///         sheet
///         growing (a title that fades in, a header that swaps layout) wraps itself in a
///         <c>Watch</c> and reads it — there is no rebuild-per-frame builder callback in a retained
///         tree:
///         <code>
///   new Watch(() =&gt; new Label(sheet.Extent.Value &gt; 0.8f ? "Details" : ""))
/// </code>
///     </para>
/// </summary>
public sealed class BottomSheetController : ITickerProvider, IDisposable
{
    /// <summary>A flick beyond this many sheet-heights per second decides direction outright.</summary>
    private const float FlickVelocity = 1.5f;

    private readonly AnimationController _snap;
    private bool _dragging;
    private float _snapFrom, _snapTo;
    private Ticker? _ticker;

    /// <param name="minExtent">Smallest fraction of the available height the sheet settles at.</param>
    /// <param name="initExtent">Fraction it opens at.</param>
    /// <param name="maxExtent">Largest fraction it can be dragged to.</param>
    /// <param name="anchors">
    ///     Optional detents (fractions) a released drag settles onto. Null (the default, matching
    ///     <c>bottom_sheet</c>) leaves the sheet wherever the finger left it.
    /// </param>
    /// <param name="isCollapsible">Dragging below <paramref name="minExtent" /> dismisses the sheet.</param>
    public BottomSheetController(
        float minExtent = 0.25f,
        float initExtent = 0.5f,
        float maxExtent = 1f,
        IReadOnlyList<float>? anchors = null,
        bool isCollapsible = true)
    {
        MinExtent = Math.Clamp(value: minExtent, min: 0f, max: 1f);
        MaxExtent = Math.Clamp(value: maxExtent, min: MinExtent, max: 1f);
        Anchors = anchors;
        IsCollapsible = isCollapsible;
        Extent = new Signal<float>(Math.Clamp(value: initExtent, min: MinExtent, max: MaxExtent));

        _snap = new AnimationController(durationSeconds: 0.22f, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _snap.OnTick += () =>
            SetExtent(_snapFrom + ((_snapTo - _snapFrom) * _snap.Value));
    }

    /// <summary>Seconds a snap to an anchor (or an <see cref="AnimateTo" />) takes.</summary>
    public float SnapDuration
    {
        get => _snap.Duration;
        set => _snap.Duration = value;
    }

    /// <summary>Current fraction of the available height the sheet occupies. Reactive.</summary>
    public Signal<float> Extent { get; }

    /// <summary>Current extent without registering a reactive dependency at a call site that has none.</summary>
    public float Value => Extent.Value;

    public float MinExtent { get; }
    public float MaxExtent { get; }

    /// <summary>Detents a released drag settles onto, or null for free positioning.</summary>
    public IReadOnlyList<float>? Anchors { get; }

    /// <summary>Whether dragging below <see cref="MinExtent" /> dismisses the sheet.</summary>
    public bool IsCollapsible { get; }

    /// <summary>Height a fraction of 1.0 corresponds to; set by the sheet each layout.</summary>
    public float AvailableHeight { get; internal set; }

    /// <summary>Sheet height in logical pixels at the current extent.</summary>
    public float PixelHeight => Value * AvailableHeight;

    /// <summary>Set by the route: closes the sheet, carrying <c>result</c> back to the caller.</summary>
    public Action<object?>? OnClose { get; set; }

    // ── Drag ──────────────────────────────────────────────────────────────────

    /// <summary>Can the sheet still grow / shrink? Used to arbitrate against content scrolling.</summary>
    internal bool CanGrow => Value < MaxExtent - 0.0005f;

    internal bool CanShrink => Value > MinExtent + 0.0005f || IsCollapsible;

    public void Dispose()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    /// <summary>
    ///     Raised after every extent change, with the new value — the imperative counterpart of
    ///     watching <see cref="Extent" />. The sheet widget uses it to re-lay-out; a host that mirrors
    ///     the sheet's state elsewhere (an "open" flag, a toggle button) hooks it too.
    /// </summary>
    public event Action<float>? ExtentChanged;

    /// <summary>
    ///     Raised when the sheet is sent somewhere — a released drag settling onto an anchor, an
    ///     explicit <see cref="AnimateTo" />/<see cref="JumpTo" /> — with the extent it is heading for.
    ///     A host that mirrors "is the sheet open?" hooks this rather than <see cref="ExtentChanged" />,
    ///     so it flips when the gesture decides rather than when the animation finishes.
    /// </summary>
    public event Action<float>? Settling;

    /// <summary>Rebind the driving ticker after a detach disposed the previous one.</summary>
    internal void AttachTicker() => _snap.AttachTicker(this);

    /// <summary>Dismiss the sheet, completing the <c>Show…</c> task with <paramref name="result" />.</summary>
    public void Close(object? result = null) => OnClose?.Invoke(result);

    /// <summary>Animate to <paramref name="extent" /> (clamped to the sheet's range).</summary>
    public void AnimateTo(float extent)
    {
        _snapTo = Math.Clamp(value: extent, min: MinExtent, max: MaxExtent);
        _snapFrom = Value;
        Settling?.Invoke(_snapTo);
        if (MathF.Abs(_snapTo - _snapFrom) < 0.001f)
        {
            SetExtent(_snapTo);
            return;
        }

        _snap.Dismiss();
        _snap.Forward();
    }

    /// <summary>Jump to <paramref name="extent" /> without animating.</summary>
    public void JumpTo(float extent)
    {
        StopSnap();
        float target = Math.Clamp(value: extent, min: MinExtent, max: MaxExtent);
        Settling?.Invoke(target);
        SetExtent(target);
    }

    /// <summary>
    ///     Move by a finger delta in logical pixels (positive = downwards = smaller sheet).
    ///     <paramref name="allowCollapse" /> lets the drag pull below <see cref="MinExtent" /> — only
    ///     drag surfaces that reliably see the release (the handle/header) may, so the sheet can never
    ///     be stranded below its minimum.
    /// </summary>
    internal void DragBy(float dyPixels, bool allowCollapse)
    {
        if (AvailableHeight <= 0f) return;
        StopSnap();
        _dragging = true;
        float floor = allowCollapse && IsCollapsible ? 0f : MinExtent;
        SetExtent(
            Math.Clamp(value: Value - (dyPixels / AvailableHeight), min: floor, max: MaxExtent)
        );
    }

    /// <summary>
    ///     Release: settle onto an anchor, or dismiss when collapsible and the sheet was pulled below
    ///     (or flicked down at) its minimum. <paramref name="velocityPixels" /> is the lift-off speed in
    ///     logical px/s, positive downwards.
    /// </summary>
    internal void EndDrag(float velocityPixels)
    {
        if (!_dragging) return;
        _dragging = false;

        float velocity =
            AvailableHeight > 0f ? velocityPixels / AvailableHeight : 0f; // fractions/s
        if (IsCollapsible &&
            (Value < MinExtent - 0.001f ||
             (velocity > FlickVelocity && Value <= MinExtent + 0.05f)))
        {
            Close();
            return;
        }

        if (NearestAnchor(velocity) is { } target) AnimateTo(target);
        else
        {
            JumpTo(
                Value
            ); // clamp back into range after a collapse-eligible drag that didn't dismiss
        }
    }

    /// <summary>
    ///     <see cref="EndDrag" /> if this controller was actually being dragged; reports whether it
    ///     consumed the release, so a caller that shares the gesture (the scroll body) can fall back to
    ///     its own behaviour when the sheet never moved.
    /// </summary>
    internal bool EndDragIfActive(float velocityPixels)
    {
        if (!_dragging) return false;
        EndDrag(velocityPixels);
        return true;
    }

    private float? NearestAnchor(float velocity)
    {
        if (Anchors is not { Count: > 0 } anchors) return null;

        // A flick takes the next detent in the direction it was thrown, however far away it is —
        // that is what makes a small upward flick open a sheet rather than snap it back.
        if (MathF.Abs(velocity) > FlickVelocity)
        {
            float? best = null;
            foreach (float a in anchors)
            {
                if (velocity < 0f ? a > Value + 0.01f : a < Value - 0.01f)
                {
                    if (best is null || MathF.Abs(a - Value) < MathF.Abs(best.Value - Value))
                        best = a;
                }
            }

            if (best is not null) return best;
        }

        float nearest = anchors[0];
        foreach (float a in anchors)
        {
            if (MathF.Abs(a - Value) < MathF.Abs(nearest - Value))
                nearest = a;
        }

        return nearest;
    }

    private void StopSnap()
    {
        if (_snap.Status is AnimationStatus.Forward or AnimationStatus.Reverse) _snap.Dismiss();
    }

    private void SetExtent(float value)
    {
        if (MathF.Abs(Extent.Value - value) < 0.00001f) return;
        Extent.Value = value;
        ExtentChanged?.Invoke(value);
    }
}
