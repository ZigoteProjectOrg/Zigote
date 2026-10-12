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

    /// <summary>
    ///     Advance the things that change on a timer rather than on input — the tooltip hover delay,
    ///     snackbars, the caret blink — for this window, marking a repaint only on the frames where
    ///     one of them actually looks different, and recording when the next such frame is due so
    ///     the idle wait can end for it. Shared by the main frame and every secondary window's.
    /// </summary>
    private void AdvanceTransients(float dt)
    {
        // Advance tooltip timer
        AdvanceTooltip(dt);

        // Advance and prune finished snackbars. A toast only changes while it slides in and fades
        // out; for the seconds in between it is a static picture, so the overlay is repainted
        // only while one is animating and the idle wait is cut short to catch the next fade-out.
        _snackbarDueInMs = 0;
        for (int i = _snackbars.Count - 1; i >= 0; i--)
        {
            var s = _snackbars[i];
            s.Tick(dt);
            if (s.IsDone)
            {
                PopOverlay(s);
                _snackbars.RemoveAt(i);
            }
            else if (s.Animating)
                _repaint.MarkOverlay();
            else
            {
                int dueMs = Math.Max(val1: 1, val2: (int)MathF.Ceiling(s.RestingFor * 1000f) + 1);
                _snackbarDueInMs = _snackbarDueInMs == 0 ? dueMs : Math.Min(val1: _snackbarDueInMs, val2: dueMs);
            }
        }

        // A tooltip pending its hover delay repaints the overlay only until the bubble is up — the
        // show/hide frames are marked by PushOverlay/PopOverlay, so a settled bubble idles instead of
        // producing full-damage frames forever. A blinking caret lives in whichever layer its field is
        // in; a read-only / caret-less text client (e.g. the docked read-only CodeEditor) opts out of
        // the per-frame caret repaint via WantsCaretBlink, so a focused viewer idles too.
        // Nothing is drawn while the delay runs, so nothing is repainted: the idle wait is simply
        // cut short to end when the bubble is due (it used to repaint the overlay every frame for
        // the whole delay just to keep the loop turning).
        _tooltipDueInMs = _tooltipTimer > 0 && _tooltipOverlay is null
            ? Math.Max(val1: 1, val2: (int)MathF.Ceiling((TooltipDelaySeconds - _tooltipTimer) * 1000f) + 1)
            : 0;
        _caretChangeInMs = 0;
        if (FocusedWidget is ITextInputClient { WantsCaretBlink: true } caret)
        {
            if (caret.CaretBlinkBase is not { } blinkBase)
                MarkPaintFor(FocusedWidget); // unknown animation: every frame
            else
            {
                // A standard blink changes twice a cycle. Repaint on those edges only, and tell
                // the idle wait when the next one is due so it wakes for it.
                float time = Time;
                bool shown = CaretBlink.Shown(time: time, blinkBase: blinkBase);
                if (shown != _caretShown || !ReferenceEquals(FocusedWidget, _caretOwner))
                    MarkPaintFor(FocusedWidget);
                _caretShown = shown;
                _caretOwner = FocusedWidget;
                _caretChangeInMs = Math.Max(
                    val1: 1,
                    val2: (int)MathF.Ceiling(CaretBlink.ChangeIn(time: time, blinkBase: blinkBase) * 1000f)
                );
            }
        }
        else
            _caretOwner = null;
    }

    /// <summary>Milliseconds until this window's next timed repaint is due; 0 when none is pending.</summary>
    private int TransientDueInMs()
    {
        int due = 0;
        if (_caretChangeInMs > 0) due = _caretChangeInMs;
        if (_tooltipDueInMs > 0) due = due == 0 ? _tooltipDueInMs : Math.Min(val1: due, val2: _tooltipDueInMs);
        if (_snackbarDueInMs > 0) due = due == 0 ? _snackbarDueInMs : Math.Min(val1: due, val2: _snackbarDueInMs);
        return due;
    }

    /// <summary>Work handed over from another thread that an idle wait must not sleep on.</summary>
    private bool HasCrossThreadWork()
    {
        if (HasFrameWork() || !_postedAfterFrame.IsEmpty || !_crossThreadInvalidations.IsEmpty ||
            Reactive.HasPendingDeferred || _background is { FrameIdle: false } ||
            HotReload.HasPendingReload || PlatformChannel.HasPending)
            return true;
        // Secondary windows are serviced by this loop, so work queued on one of them counts too.
        for (int i = 0; i < _secondaryWindows.Count; i++)
        {
            if (_secondaryWindows[i].HasFrameWork()) return true;
        }

        return false;
    }

    // Only what every window's frame consumes: a secondary window's frame never drains the
    // after-frame or cross-thread-invalidation queues, so counting those there would keep the
    // loop awake forever on an entry nothing removes.
    private bool HasFrameWork() => !_posted.IsEmpty || _repaint.AnyDirty || _needsLayout;

    private int IdleWaitTimeoutMs()
    {
        int frameMs = (int)(FrameIntervalTicks * 1000 / Stopwatch.Frequency);
        // Stay at frame cadence just after activity, and for as long as something is serviced by
        // per-frame polling rather than by events: a native file dialog, a held finger ripening
        // into a long-press, a one-shot sound waiting to be silenced.
        if (++_idleStreak < IdleFramesBeforeLongWait || IdleWaitMs <= frameMs ||
            FileDialog.IsBusy || AnyTouchHeld() || Engine.AudioHasActiveOneShots())
            return frameMs;
        // A blinking caret must be repainted on its next edge, and a pending tooltip shown when
        // its delay ends — not whenever the wait happens to expire.
        // Any window's: secondary windows are driven from this loop.
        int wait = IdleWaitMs;
        if (TransientDueInMs() is > 0 and var mine) wait = Math.Min(val1: wait, val2: mine);
        for (int i = 0; i < _secondaryWindows.Count; i++)
        {
            if (_secondaryWindows[i].TransientDueInMs() is > 0 and var theirs)
                wait = Math.Min(val1: wait, val2: theirs);
        }

        return wait;
    }

    private bool AnyTouchHeld()
    {
        if (_touchPrimaryFinger >= 0) return true;
        for (int i = 0; i < _secondaryWindows.Count; i++)
        {
            if (_secondaryWindows[i]._touchPrimaryFinger >= 0) return true;
        }

        return false;
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
