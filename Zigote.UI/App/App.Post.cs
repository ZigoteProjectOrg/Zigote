using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Platform;
using Zigote.Core.State;
using Zigote.UI.Widgets;

namespace Zigote.UI.Host;

// The cross-thread hand-off into the UI thread: Post / TryPost, the bounded drain, and its stats.
public partial class App
{
    /// <summary>
    ///     Thread-safe: run <paramref name="action" /> on the UI thread at the top of the next frame,
    ///     before layout. The seam for finishing async work that has to touch widgets — an image
    ///     decoded on a worker thread swapping itself in, a fetch completing — without the caller
    ///     hand-rolling a queue per call site. Actions run in enqueue order; one that throws is
    ///     reported and skipped rather than killing the frame loop.
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _posted.Enqueue((action, Stopwatch.GetTimestamp()));
        // No RequestPaint: the idle gate checks _posted.IsEmpty, and the action marks its own
        // damage — SetTexture calls MarkNeedsLayout, a signal write invalidates its binds.
        // Marking full-frame damage here defeated partial repaint for every background completion.
        // If the UI thread is parked in WaitEvents, wake it now instead of riding out the timeout
        // — an image decode landing mid-idle paints immediately, not one frame-budget later.
        WakeIfWaiting();
    }

    /// <summary>
    ///     Thread-safe: end the frame loop's idle wait now, if it is in one. Everything that hands
    ///     the UI thread work from another thread calls this — the idle wait can be many frames
    ///     long (<see cref="IdleWaitMs" />), so work that only sets a flag would sit until it expired.
    ///     The loop raises the flag BEFORE its last check for such work, so a caller that queues
    ///     first and wakes second can never slip between the check and the sleep.
    /// </summary>
    public void WakeIfWaiting()
    {
        if (Volatile.Read(ref _inWaitEvents)) Engine.WakeEventLoop();
    }

    /// <summary>
    ///     Opt-in ceiling on callbacks waiting in the <see cref="Post" /> queue, enforced only by
    ///     <see cref="TryPost" />. Zero (the default) means no ceiling.
    /// </summary>
    public int PostCapacity { get; set; }

    /// <summary>
    ///     <see cref="Post" /> for work the caller can afford to have refused: returns false, without
    ///     queueing, when <see cref="PostCapacity" /> callbacks are already waiting. For replaceable
    ///     or retryable producers (progress ticks, prefetch results) — the caller keeps ownership of
    ///     whatever the callback would have delivered and must release or retry it. Plain
    ///     <see cref="Post" /> never refuses.
    /// </summary>
    public bool TryPost(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        int capacity = PostCapacity;
        // ponytail: count-then-enqueue is not atomic, so racing producers can overshoot the cap by
        // one each; take a lock here if the cap ever has to be exact.
        if (capacity > 0 && _posted.Count >= capacity)
        {
            Interlocked.Increment(ref _postRejected);
            return false;
        }

        Post(action);
        return true;
    }

    /// <summary>
    ///     Opt-in cap, in milliseconds, on the time one frame spends running <see cref="Post" />
    ///     callbacks. Zero (the default) drains until the queue is empty, including callbacks posted
    ///     by the drain itself. When positive, a frame runs only what was queued when its drain began
    ///     and stops once the budget is spent; the rest keeps its order and runs on following frames,
    ///     with input polled in between. Nothing is dropped, and at least one callback runs per frame.
    ///     A callback is never interrupted, so one slow callback still overruns the budget.
    /// </summary>
    public double PostDrainBudgetMs { get; set; }

    /// <summary>Callbacks queued by <see cref="Post" /> that have not run yet.</summary>
    public int PostedPending => _posted.Count;

    private int _postRejected;

    /// <summary>Callbacks <see cref="TryPost" /> has refused since the app started.</summary>
    public int PostRejected => Volatile.Read(ref _postRejected);

    /// <summary>
    ///     What the most recent frame that ran any <see cref="Post" /> callbacks did. UI thread.
    /// </summary>
    public PostDrainStats LastPostDrain { get; private set; }

    internal void DrainPosted()
    {
        if (_posted.IsEmpty) return; // the common frame: no timestamps, no stats
        double budgetMs = PostDrainBudgetMs;
        bool bounded = budgetMs > 0;
        // Bounded: callbacks posted during this drain wait for the next frame, so a self-reposting
        // callback cannot extend it.
        int remaining = bounded ? _posted.Count : int.MaxValue;
        long start = Stopwatch.GetTimestamp();
        long last = start, oldest = start, slowest = 0;
        int ran = 0;
        while (remaining-- > 0 && _posted.TryDequeue(out var entry))
        {
            if (ran++ == 0) oldest = entry.Queued;
            try
            {
                entry.Action();
            }
            catch (Exception ex)
            {
                DebugLog.Error($"App.Post action threw: {ex}");
            }

            // Per-callback timing only where the budget already pays for the clock read.
            if (!bounded) continue;
            long now = Stopwatch.GetTimestamp();
            slowest = Math.Max(val1: slowest, val2: now - last);
            last = now;
            if (Stopwatch.GetElapsedTime(startingTimestamp: start, endingTimestamp: now)
                    .TotalMilliseconds >= budgetMs)
                break;
        }

        if (ran == 0) return;
        long end = bounded ? last : Stopwatch.GetTimestamp();
        LastPostDrain = new PostDrainStats(
            Ran: ran,
            Pending: _posted.Count,
            ElapsedMs: Stopwatch.GetElapsedTime(startingTimestamp: start, endingTimestamp: end)
                .TotalMilliseconds,
            OldestWaitMs: Math.Max(
                val1: 0d,
                val2: Stopwatch.GetElapsedTime(startingTimestamp: oldest, endingTimestamp: start)
                    .TotalMilliseconds
            ),
            SlowestMs: Stopwatch.GetElapsedTime(startingTimestamp: 0, endingTimestamp: slowest)
                .TotalMilliseconds
        );
    }
}

/// <summary>One frame's <see cref="App.Post" /> drain, as <see cref="App.LastPostDrain" /> reports it.</summary>
/// <param name="Ran">Callbacks run by the drain.</param>
/// <param name="Pending">Callbacks still queued when it stopped.</param>
/// <param name="ElapsedMs">Time the drain took.</param>
/// <param name="OldestWaitMs">How long the first callback it ran had been queued.</param>
/// <param name="SlowestMs">
///     The longest single callback. Measured only under <see cref="App.PostDrainBudgetMs" />; zero
///     otherwise.
/// </param>
public readonly record struct PostDrainStats(
    int Ran,
    int Pending,
    double ElapsedMs,
    double OldestWaitMs,
    double SlowestMs);
