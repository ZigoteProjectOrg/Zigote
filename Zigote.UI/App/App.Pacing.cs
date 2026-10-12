using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Platform;
using Zigote.Core.State;
using Zigote.UI.Widgets;

namespace Zigote.UI.Host;

// How the frame loop spends and saves time: the idle wait, what may cut it short, the Background
// budget and its reserve, and the cadence after frames the surface refused.
public partial class App
{
    /// <summary>
    ///     How long the frame loop may sleep in one go once the app has been idle for a moment, in
    ///     milliseconds. An idle app otherwise wakes once per display frame — 120 times a second on
    ///     a 120 Hz panel — to find nothing to do. OS input and every cross-thread hand-off
    ///     (<see cref="Post" />, background results, deferred reactive effects, platform messages)
    ///     end the sleep at once, so this only bounds how stale code that silently polls from the
    ///     frame loop can get. Zero, or anything at or below the frame interval, restores the
    ///     once-per-frame wake.
    /// </summary>
    public int IdleWaitMs { get; set; } = 100;

    /// <summary>Consecutive idle frames before the long wait: about a quarter second at 120 Hz.</summary>
    private const int IdleFramesBeforeLongWait = 30;

    private int _idleStreak;

    // Blink-edge tracking for a focused standard caret; see the caret block in FrameCore.
    private bool _caretShown;
    private Widget? _caretOwner;
    private int _caretChangeInMs; // 0 = no blinking caret to wake for
    private int _tooltipDueInMs; // 0 = no tooltip waiting out its hover delay
    private int _snackbarDueInMs; // 0 = no resting snackbar waiting to fade out

    /// <summary>Work handed over from another thread that an idle wait must not sleep on.</summary>
    private bool HasCrossThreadWork() =>
        !_posted.IsEmpty || !_postedAfterFrame.IsEmpty || !_crossThreadInvalidations.IsEmpty ||
        _repaint.AnyDirty || _needsLayout ||
        Reactive.HasPendingDeferred || _background is { FrameIdle: false } ||
        HotReload.HasPendingReload || PlatformChannel.HasPending;

    private int IdleWaitTimeoutMs()
    {
        int frameMs = (int)(FrameIntervalTicks * 1000 / Stopwatch.Frequency);
        // Stay at frame cadence just after activity, and for as long as something is serviced by
        // per-frame polling rather than by events: a native file dialog, a held finger ripening
        // into a long-press, a one-shot sound waiting to be silenced, secondary windows.
        if (++_idleStreak < IdleFramesBeforeLongWait || IdleWaitMs <= frameMs ||
            FileDialog.IsBusy || _touchPrimaryFinger >= 0 || _secondaryWindows.Count > 0 ||
            Engine.AudioHasActiveOneShots())
            return frameMs;
        // A blinking caret must be repainted on its next edge, and a pending tooltip shown when
        // its delay ends — not whenever the wait happens to expire.
        int wait = IdleWaitMs;
        if (_caretChangeInMs > 0) wait = Math.Min(val1: wait, val2: _caretChangeInMs);
        if (_tooltipDueInMs > 0) wait = Math.Min(val1: wait, val2: _tooltipDueInMs);
        if (_snackbarDueInMs > 0) wait = Math.Min(val1: wait, val2: _snackbarDueInMs);
        return wait;
    }

    /// <summary>
    ///     Opt-in: how long <see cref="Background" /> idle results and slices may be held back by
    ///     frames that have no slack left, in milliseconds. Zero (the default) keeps the guarantee
    ///     that every frame advances each queue by one unit even when it is already late; a positive
    ///     value lets late frames skip that unit, and guarantees service at least this often instead.
    /// </summary>
    public double BackgroundMaxDeferralMs { get; set; }

    /// <summary>The share of this frame still unspent, capped, for <see cref="Background.RunFrame" />.</summary>
    /// <remarks>
    ///     The cap used to be a flat 4 ms, which is 24% of a 60 Hz frame but 58% of a 144 Hz one — so on
    ///     a fast panel a full background slice on a frame that also lays out is the thing that drops it.
    ///     Take what is left of the frame's own budget instead, and never more than 4 ms even on a slow
    ///     panel (a 30 Hz frame does not want 33 ms of thumbnail decoding in one go).
    ///     <para>
    ///         An already-over-budget frame yields zero, which is not starvation:
    ///         <see cref="Background.RunFrame" /> runs one unit per queue before it consults the
    ///         deadline, so the queues still advance — one item per frame instead of a burst.
    ///     </para>
    /// </remarks>
    private TimeSpan BackgroundBudget() => TimeSpan.FromMilliseconds(
        BackgroundBudgetMs(
            intervalTicks: FrameIntervalTicks,
            spentTicks: _clock.ElapsedTicks - _lastTicks,
            reserveTicks: _frameReserveTicks
        )
    );

    /// <summary>
    ///     Slack left for discretionary work: the frame interval minus what is already spent and
    ///     minus <paramref name="reserveTicks" /> — the estimated cost of the layout, dispatch, paint
    ///     and submit still to come — capped at <see cref="MaxBackgroundBudgetMs" />.
    /// </summary>
    internal static double BackgroundBudgetMs(long intervalTicks, long spentTicks, long reserveTicks) =>
        Math.Clamp(
            value: (intervalTicks - spentTicks - reserveTicks) * 1000.0 / Stopwatch.Frequency,
            min: 0d,
            max: MaxBackgroundBudgetMs
        );

    /// <summary>
    ///     Fold one frame's measured remaining-work cost into the reserve: jump straight up to a
    ///     costlier frame, decay slowly (about 5% a frame) after a cheaper one — so one heavy frame
    ///     protects the next few, and a workload that got lighter gets its slack back within a second.
    /// </summary>
    internal static long NextFrameReserve(long currentTicks, long sampleTicks) =>
        sampleTicks >= currentTicks ? sampleTicks : currentTicks - (currentTicks - sampleTicks) / 20;

    private bool _presentSkipped;
    private int _dropStreak;
    private const int DropsBeforeBackgroundCadence = 3;

    /// <summary>
    ///     Frames submitted but not presented because the surface was unavailable (occluded,
    ///     outdated, acquire timeout). Each one is repaid with a full repaint.
    /// </summary>
    public long DroppedFrames { get; private set; }

    // Rolling estimate of what a painting frame costs between its Background slice and the present
    // wait. Excludes Engine.FrameEnd, whose CPU encode work is not separable from the vsync wait.
    private long _frameReserveTicks;
    private long _afterBackgroundTicks;

    /// <summary>
    ///     The estimated cost of a frame's layout, dispatch, paint and submit that
    ///     <see cref="Background" /> work is kept clear of, in milliseconds. Diagnostics only.
    /// </summary>
    public double FrameReserveMs => _frameReserveTicks * 1000.0 / Stopwatch.Frequency;
}
