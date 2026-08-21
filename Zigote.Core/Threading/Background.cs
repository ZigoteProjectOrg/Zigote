using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Zigote.Core.Diagnostics;
using Zigote.Core.State;

namespace Zigote.Core.Threading;

/// <summary>When a result is allowed to reach the UI thread.</summary>
public enum Deliver
{
    /// <summary>
    ///     At the top of the next frame, before layout, whatever else is queued. For anything the
    ///     user is waiting on — a track's duration, a search's results, a tap's answer.
    /// </summary>
    Next,

    /// <summary>
    ///     Only while the frame still has budget left; otherwise the frame after, and the one after
    ///     that. For results that arrive in floods and are individually unimportant — a decoded
    ///     thumbnail, one of four hundred rows, a progress counter. This is what keeps a burst from
    ///     becoming a dropped frame, and it is the thing a general-purpose runtime cannot offer,
    ///     because it does not know what a frame is.
    /// </summary>
    WhenIdle,
}

/// <summary>
///     Where work runs, when its results land, and what happens when it fails — for one part of an
///     app. Scopes nest: an app owns one, a screen owns a child of it, and disposing the screen's
///     stops its work without touching anything else.
///     <para>
///         The bare form of every call site is <c>_ = Task.Run(...)</c>, and it is wrong in three
///         ways that only show up in production: the task's exception is never observed, so a failed
///         library scan is indistinguishable from an empty one; nothing is cancelled at shutdown, so
///         a result can land on a disposed bloc; and the hop back to the UI thread is hand-written
///         every time. That is the floor. What is above it:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Structured, and supervised by default.</b> A child scope dies with its parent, and
///             its failures are reported without cancelling its parent or its siblings. Kotlin makes
///             you ask for that with <c>SupervisorJob</c> — its default cancels the scope on a child's
///             failure, which in a UI app means one bad cover decode stops the library scan. For a
///             screen, supervision is the sane default and cascade is the special case.
///         </item>
///         <item>
///             <b>Frame-aware.</b> <see cref="Deliver.WhenIdle" /> results and <see cref="Slice" />
///             work run against a per-frame time budget, so four hundred results landing at once cost
///             several frames of filling in rather than one frame of stutter. <c>Dispatchers.Main</c>
///             posts to a looper and runs the lot; Dart's isolates cannot touch the UI at all.
///         </item>
///         <item>
///             <b>Deterministic under test.</b> <see cref="Manual" /> plus <see cref="Drain" /> give a
///             self-test the frame loop's side of this without a window, so an assertion about
///             background work is a fact rather than a race.
///         </item>
///         <item>
///             <b>Payload-checked in DEBUG.</b> Handing a mutable collection across the boundary is
///             the data race Dart's isolates make unrepresentable and shared memory does not. This
///             cannot forbid it, but it names it — at no cost in release. See
///             <see cref="WarnIfMutable" />.
///         </item>
///     </list>
///     <para>
///         It is still <b>not</b> a scheduler and not a new concurrency model: work goes to the .NET
///         thread pool, in the order it was queued, with no priorities. <c>async</c>/<c>await</c>
///         inside a bloc handler remains correct for work the handler awaits. This is for work nobody
///         is waiting on — which is exactly where the failures were silent.
///     </para>
/// </summary>
public sealed class Background : IDisposable
{
    /// <summary>
    ///     Where an unhandled failure in background work goes, tagged with the scope path and the
    ///     calling member (<c>"app/library.StartScan"</c>). Mirrors <see cref="Reactive.OnError" />
    ///     and <c>BlocErrors.OnError</c>: unset, failures land in <see cref="DebugLog" />.
    /// </summary>
    public static Action<Exception, string>? OnError;

    /// <summary>Time a slice takes on the frame that started it — a quarter of a 60 Hz frame.</summary>
    private static readonly TimeSpan DefaultSliceBudget = TimeSpan.FromMilliseconds(4);

    private static readonly ConcurrentDictionary<Type, bool>
        MutablePayloads = new();

    private static readonly System.Collections.Frozen.FrozenSet<Type> MutableGenerics =
        System.Collections.Frozen.FrozenSet.ToFrozenSet([
            typeof(List<>),
            typeof(Dictionary<,>),
            typeof(HashSet<>),
            typeof(Queue<>),
            typeof(Stack<>),
            typeof(SortedList<,>),
            typeof(SortedDictionary<,>),
        ]);

    private readonly List<Background> _children = [];

    /// <summary>Root-only. Results that asked to wait for a frame with room, in arrival order.</summary>
    private readonly Queue<Action>? _idle;

    private readonly CancellationTokenSource _lifetime;

    private readonly Background? _parent;
    private readonly Action? _requestFrame;

    /// <summary>Root-only. Reused by <see cref="RunFrame" /> so walking the slices allocates nothing.</summary>
    private readonly List<SliceJob>? _sliceScratch;

    /// <summary>Root-only. Work that is spread over frames a slice at a time.</summary>
    private readonly List<SliceJob>? _slices;

    private readonly Action<Action>? _toUi;
    private int _pending;

    /// <summary>Which slice gets the frame first. Bumped per frame so none of them starves.</summary>
    private int _sliceCursor;

    /// <param name="toUi">
    ///     Marshals a callback onto the host's UI thread — <c>App.Post</c> in a Zigote app. Every
    ///     <see cref="Deliver.Next" /> result goes through it, so a handler may touch widgets and
    ///     blocs without checking a thread.
    /// </param>
    /// <param name="requestFrame">
    ///     Asks the host for another frame — <c>App.RequestLayout</c>. Called when budgeted work is
    ///     left over, because a frame loop that only wakes on input would otherwise go quiet with a
    ///     half-filled list on screen. Optional: without it, leftovers wait for whatever the next
    ///     frame happens to be.
    /// </param>
    public Background(Action<Action> toUi, Action? requestFrame = null)
    {
        _toUi = toUi;
        _requestFrame = requestFrame;
        _lifetime = new CancellationTokenSource();
        _idle = new Queue<Action>();
        _slices = [];
        _sliceScratch = [];
        Path = "app";
    }

    private Background(Background parent, string name)
    {
        _parent = parent;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent.Lifetime);
        Path = $"{parent.Path}/{name}";
    }

    /// <summary>This scope's place in the tree — <c>"app/library"</c>. What failures are reported as.</summary>
    public string Path { get; }

    /// <summary>Cancelled when this scope or any ancestor is disposed.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>
    ///     Units of work in flight, this scope and everything under it. For a diagnostics readout and
    ///     for <see cref="Drain" />; not for control flow.
    /// </summary>
    public int Pending
    {
        get
        {
            int total = Volatile.Read(ref _pending);
            lock (_children)
            {
                foreach (var child in _children)
                    total += child.Pending;
            }

            return total;
        }
    }

    /// <summary>Nothing is queued for a frame — no idle results, no unfinished slices.</summary>
    public bool FrameIdle
    {
        get
        {
            var root = Root;
            lock (root._idle!) return root._idle.Count == 0 && root._slices!.Count == 0;
        }
    }

    private Background Root => _parent?.Root ?? this;

    /// <summary>
    ///     Stop this scope and everything under it. In-flight work is cancelled and its results are
    ///     dropped rather than awaited — a shutdown that blocks on a slow network read is a hang, and
    ///     every result here is by definition destined for a UI that is going away. A write already
    ///     inside <c>File.Move</c> still finishes; what is dropped is the callback, not the syscall.
    /// </summary>
    /// <remarks>
    ///     The token source is cancelled and deliberately not disposed. Reading
    ///     <see cref="CancellationTokenSource.Token" /> on a disposed source throws, and a bloc that
    ///     outlives its scope by one event — an <c>Add</c> from a D-Bus thread mid-shutdown — would
    ///     take that exception instead of quietly doing nothing.
    /// </remarks>
    public void Dispose()
    {
        Background[] children;
        lock (_children)
        {
            children = [.. _children];
            _children.Clear();
        }

        foreach (var child in children) child.Dispose();
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();

        if (_parent is null) return;
        lock (_parent._children) _parent._children.Remove(this);
    }

    /// <summary>
    ///     A scope that dies with this one: a screen, a feature, a page's search box. Its failures are
    ///     reported and contained — a child throwing does not cancel its parent or its siblings, which
    ///     is the opposite of a Kotlin <c>coroutineScope</c> and the right default for a UI, where one
    ///     failed thumbnail must not take the library down with it.
    /// </summary>
    public Background Child(string name)
    {
        var child = new Background(parent: this, name: name);
        lock (_children) _children.Add(child);

        if (_lifetime.IsCancellationRequested) child.Dispose();
        return child;
    }

    // ── starting work ─────────────────────────────────────────────────────────

    /// <summary>Fire and forget on a worker — a checkpoint write, a cache eviction. Failures are reported.</summary>
    public void Run(Action work, [CallerMemberName] string origin = "")
    {
        Launch(
            origin: origin,
            body: _ =>
            {
                work();
                return Task.CompletedTask;
            }
        );
    }

    /// <summary>
    ///     Compute on a worker, deliver on the UI thread. The shape of nearly every load: read the
    ///     file, parse it, hand the finished immutable value to a bloc.
    /// </summary>
    public void Run<T>(Func<T> work, Action<T> onUi, Deliver deliver = Deliver.Next,
        [CallerMemberName] string origin = "")
    {
        Launch(
            origin: origin,
            body: _ =>
            {
                var result = work();
                WarnIfMutable(value: result, origin: origin);
                Post(ui: () => onUi(result), deliver: deliver, origin: origin);
                return Task.CompletedTask;
            }
        );
    }

    /// <summary>
    ///     Await something genuinely asynchronous (an HTTP fetch, a portal round trip). The token is
    ///     <see cref="Lifetime" />, so the work stops when the scope does.
    /// </summary>
    public void
        RunAsync(Func<CancellationToken, Task> work, [CallerMemberName] string origin = "") =>
        Launch(origin: origin, body: work);

    /// <summary>
    ///     A slot for work that supersedes itself: a search box, a regroup, a debounced save. Starting
    ///     a new run cancels the one before it, so the last thing asked for is the thing that lands.
    ///     Hold one per independent unit — a bloc that scans <i>and</i> filters needs two.
    /// </summary>
    public Latest Latest() => new(this);

    // ── the UI thread's side ──────────────────────────────────────────────────

    /// <summary>Run something on the UI thread from wherever you are. A no-op once disposed.</summary>
    public void Post(Action ui, Deliver deliver = Deliver.Next,
        [CallerMemberName] string origin = "")
    {
        if (_lifetime.IsCancellationRequested) return;

        if (deliver == Deliver.Next)
        {
            Root._toUi!(() => Guarded(ui: ui, origin: origin));
            return;
        }

        var root = Root;
        lock (root._idle!) root._idle.Enqueue(() => Guarded(ui: ui, origin: origin));

        root._requestFrame?.Invoke();
    }

    /// <summary>
    ///     Do <paramref name="count" /> units of UI work across as many frames as the budget takes,
    ///     instead of all in one. Building fifty thousand list rows is O(n) on the UI thread and a
    ///     frozen window; the same work a slice per frame is a list that fills in.
    ///     <para>
    ///         <paramref name="key" /> identifies the job: starting a slice with a key already running
    ///         replaces it, which is what makes "the query changed, rebuild the list" safe to call on
    ///         every keystroke. The first slice runs immediately, so the calling frame always shows
    ///         something.
    ///     </para>
    /// </summary>
    /// <param name="firstFrame">
    ///     What the starting frame may spend before deferring the rest. Defaults to a quarter of a
    ///     60 Hz frame; pass <see cref="TimeSpan.Zero" /> for one unit now and the rest later, which
    ///     is what a caller whose unit is expensive (a page, not a row) wants.
    /// </param>
    public void Slice(object key, int count, Action<int> step, Action? onDone = null,
        TimeSpan? firstFrame = null, [CallerMemberName] string origin = "")
    {
        if (_lifetime.IsCancellationRequested || count <= 0)
        {
            onDone?.Invoke();
            return;
        }

        var root = Root;
        var job = new SliceJob(
            key: key,
            count: count,
            step: step,
            onDone: onDone,
            owner: this,
            origin: origin
        );

        lock (root._idle!)
        {
            root._slices!.RemoveAll(existing => Equals(objA: existing.Key, objB: key));
            root._slices.Add(job);
        }

        // A first slice on this frame, so a list is never empty for a frame it did not have to be.
        // Nullable, not a TimeSpan.Zero sentinel: zero is default(TimeSpan), so a caller asking for
        // "one unit now, the rest later" would be indistinguishable from one that said nothing.
        Advance(
            job: job,
            deadline: Stopwatch.GetTimestamp() + BudgetTicks(firstFrame ?? DefaultSliceBudget)
        );
        if (!job.Done) return;

        lock (root._idle) root._slices!.Remove(job);
    }

    /// <summary>
    ///     The frame loop's side of all this: deliver what has been waiting and advance unfinished
    ///     slices, for as long as <paramref name="budget" /> allows. Call once per frame from the
    ///     host's update, before layout, so anything produced here is laid out on the same frame.
    ///     <para>
    ///         Cheap and allocation-free when there is nothing to do, which is almost always. Whatever
    ///         does not fit asks for another frame and continues there, in order.
    ///     </para>
    /// </summary>
    public void RunFrame(TimeSpan budget)
    {
        var root = Root;
        if (root._lifetime.IsCancellationRequested) return;

        long deadline = Stopwatch.GetTimestamp() + BudgetTicks(budget);

        // Results first, slices second: a result may replace the very list a slice is filling, and
        // doing it the other way round means building rows that are thrown away on the same frame.
        //
        // Each queue makes at least one unit of progress before the budget is consulted. Checking
        // first instead would mean a frame that was already over budget when it got here does
        // nothing at all — and a unit that costs more than the whole budget would never run, which
        // is a list that stays empty forever rather than a list that fills in slowly.
        while (true)
        {
            Action? next;
            lock (root._idle!) next = root._idle.Count > 0 ? root._idle.Dequeue() : null;

            if (next is null) break;
            next();
            if (Stopwatch.GetTimestamp() >= deadline) break;
        }

        // The overwhelmingly common frame: nothing deferred, nothing filling. Checked before any
        // copy, because this method runs once per frame for the life of the process and a per-frame
        // allocation on the idle path is the kind of garbage that only shows up as a GC every few
        // seconds with no line of code to blame.
        bool anySlices;
        lock (root._idle!) anySlices = root._slices!.Count > 0;

        if (!anySlices)
        {
            if (root._idle!.Count > 0) root._requestFrame?.Invoke();
            return;
        }

        // Snapshotted into a reusable scratch list — a slice may finish and remove itself while this
        // is walking, and the alternative to a copy is mutating the list under its own enumerator.
        var jobs = root._sliceScratch!;
        jobs.Clear();
        lock (root._idle!) jobs.AddRange(root._slices!);

        // Round-robin start, so two lists filling at once share the frame instead of the second
        // one sitting empty until the first finishes.
        int start = root._sliceCursor++ % jobs.Count;
        for (int n = 0; n < jobs.Count; n++)
        {
            var job = jobs[(start + n) % jobs.Count];
            if (job.Owner._lifetime.IsCancellationRequested) job.Fail();
            else Advance(job: job, deadline: deadline);

            if (job.Done)
            {
                lock (root._idle!)
                    root._slices!.Remove(job);
            }

            if (Stopwatch.GetTimestamp() >= deadline) break;
        }

        jobs.Clear(); // do not pin finished jobs until the next frame
        if (!FrameIdle) root._requestFrame?.Invoke();
    }

    // ── tests and headless tools ──────────────────────────────────────────────

    /// <summary>
    ///     A scope with no host behind it: every delivery, whatever its <see cref="Deliver" />, queues
    ///     until <see cref="RunFrame" /> or <see cref="Drain" /> runs it — on the calling thread. What
    ///     a self-test uses, so "the result landed" is an assertion rather than a sleep.
    /// </summary>
    public static Background Manual()
    {
        Background? made = null;
        // The closure needs the instance it is being handed to; assigned before anything can post.
        made = new Background(action =>
            {
                var root = made!;
                lock (root._idle!) root._idle.Enqueue(action);
            }
        );
        return made;
    }

    /// <summary>
    ///     Run every queued callback and wait for every worker, until both are empty or
    ///     <paramref name="timeout" /> passes. Returns false on timeout, which a test should treat as
    ///     a failure rather than retry. For tests and headless tools only — a frame loop calls
    ///     <see cref="RunFrame" />.
    /// </summary>
    public bool Drain(TimeSpan timeout)
    {
        var root = Root;
        long deadline = Stopwatch.GetTimestamp() + BudgetTicks(timeout);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            RunFrame(timeout);
            if (Pending == 0 && FrameIdle) return true;
            Thread.Sleep(1); // a worker is still running; nothing to do but let it
        }

        return false;
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    internal void Launch(string origin, Func<CancellationToken, Task> body,
        CancellationToken token = default)
    {
        if (_lifetime.IsCancellationRequested) return;
        var work = token == default ? _lifetime.Token : token;

        Interlocked.Increment(ref _pending);
        // The token is not passed to Task.Run: a pre-cancelled one would skip the body *and* the
        // finally, leaking the pending count. Cancellation is the body's business anyway.
        _ = Task.Run(async () =>
            {
                try
                {
                    await body(work);
                }
                catch (OperationCanceledException)
                {
                    // Superseded, or the scope is closing. Both are ordinary.
                }
                catch (Exception ex)
                {
                    // Reported, not rethrown, and the scope stays usable: a failed unit of work is
                    // not a reason to tear down the feature that started it.
                    Report(ex: ex, origin: origin);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        );
    }

    /// <summary>
    ///     Deliver a result unless this slot was superseded in the meantime. Kept here rather than in
    ///     <see cref="Latest" /> so the cancelled checks are written once.
    /// </summary>
    internal void DeliverResult(Action ui, CancellationToken token, Deliver deliver,
        string origin)
    {
        if (token.IsCancellationRequested || _lifetime.IsCancellationRequested) return;
        Post(
            ui: () =>
            {
                if (!token.IsCancellationRequested) ui();
            },
            deliver: deliver,
            origin: origin
        );
    }

    private void Advance(SliceJob job, long deadline)
    {
        try
        {
            // At least one unit, then as many as the budget allows: forward progress must not
            // depend on the budget being generous enough for a single step.
            do
                job.Step(job.Next++);
            while (!job.Done && Stopwatch.GetTimestamp() < deadline);
        }
        catch (Exception ex)
        {
            job.Fail();
            Report(ex: ex, origin: job.Origin);
            return;
        }

        if (job.Done) job.Finish(this);
    }

    private void Guarded(Action ui, string origin)
    {
        if (_lifetime.IsCancellationRequested) return;
        try
        {
            ui();
        }
        catch (Exception ex)
        {
            Report(ex: ex, origin: origin);
        }
    }

    internal void Report(Exception ex, string origin)
    {
        string where = $"{Path}.{origin}";
        try
        {
            if (OnError is { } hook) hook(arg1: ex, arg2: where);
            else
            {
                DebugLog.Add(
                    level: DebugLogLevel.Error,
                    message: $"background {where} failed — {ex}",
                    category: "background"
                );
            }
        }
        catch
        {
            // A failing error reporter must not become the failure.
        }
    }

    private static long BudgetTicks(TimeSpan budget)
    {
        double seconds = budget.TotalSeconds;
        // TimeSpan.MaxValue would overflow the tick arithmetic; a century is the same thing here.
        return seconds >= int.MaxValue / (double)Stopwatch.Frequency
            ? long.MaxValue / 2
            : (long)(seconds * Stopwatch.Frequency);
    }

    /// <summary>
    ///     Name a mutable payload crossing the thread boundary. Handing a <c>List&lt;T&gt;</c> to the
    ///     UI thread and continuing to hold it is the data race that shared memory permits and Dart's
    ///     isolates make unrepresentable by copying everything. Copying is the wrong trade here — the
    ///     library hands over fifty thousand records by reference precisely because they are immutable
    ///     — so instead the known-dangerous shapes are named, loudly, in DEBUG.
    /// </summary>
    /// <remarks>
    ///     A denylist rather than a proof: <c>ImmutableArray</c>, records and primitives pass silently,
    ///     and something exotic and mutable gets through. It costs one cached type lookup per delivery
    ///     and it catches the mistake people actually make. Compiled out of release entirely.
    /// </remarks>
    [Conditional("DEBUG")]
    private void WarnIfMutable<T>(T value, string origin)
    {
        if (value is null) return;
        var type = value.GetType();
        if (!MutablePayloads.TryGetValue(key: type, value: out bool mutable))
        {
            mutable = type.IsArray || (type.IsGenericType && MutableGenerics.Contains(
                          type.GetGenericTypeDefinition()
                      )) ||
                      type == typeof(StringBuilder);
            MutablePayloads[type] = mutable;
        }

        if (mutable)
        {
            DebugLog.Add(
                level: DebugLogLevel.Warning,
                message:
                $"{Path}.{origin} hands a mutable {type.Name} to the UI thread — the worker can still " +
                "write it. Hand over an ImmutableArray, a record, or a copy.",
                category: "background"
            );
        }
    }

    private sealed class SliceJob(
        object key,
        int count,
        Action<int> step,
        Action? onDone,
        Background owner,
        string origin)
    {
        public readonly object Key = key;
        public readonly string Origin = origin;
        public readonly Background Owner = owner;
        public readonly Action<int> Step = step;
        public int Next;

        public bool Done => Next >= count;

        /// <summary>Stop the job where it is: a step threw, and repeating it every frame is worse.</summary>
        public void Fail()
        {
            Next = count;
            onDone = null;
        }

        public void Finish(Background scope)
        {
            var callback = onDone;
            onDone = null; // exactly once, however many frames it took
            if (callback is null) return;
            scope.Guarded(ui: callback, origin: Origin);
        }
    }
}

/// <summary>
///     One unit of latest-wins background work. Every app grows the same three lines around a
///     <see cref="CancellationTokenSource" /> — cancel the old one, make a new one, remember to check
///     the token before touching state — and gets one of them wrong; this is those three lines, once.
/// </summary>
public sealed class Latest : IDisposable
{
    private readonly Background _owner;
    private CancellationTokenSource? _current;

    internal Latest(Background owner) => _owner = owner;

    public void Dispose() => Cancel();

    /// <summary>
    ///     Compute on a worker and deliver on the UI thread, cancelling whatever was running.
    ///     <paramref name="delay" /> is the debounce: a keystroke starts the clock, the next keystroke
    ///     cancels it, and only the pause at the end does any work.
    /// </summary>
    public void Run<T>(Func<CancellationToken, T> work, Action<T> onUi, TimeSpan delay = default,
        Deliver deliver = Deliver.Next, [CallerMemberName] string origin = "")
    {
        var token = Restart();
        _owner.Launch(
            origin: origin,
            body: async ct =>
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay: delay, cancellationToken: ct);
                ct.ThrowIfCancellationRequested();
                var result = work(ct);
                _owner.DeliverResult(
                    ui: () => onUi(result),
                    token: ct,
                    deliver: deliver,
                    origin: origin
                );
            },
            token: token
        );
    }

    /// <summary>
    ///     Latest-wins work with no result to deliver — the debounced save. A slider drag emits an
    ///     event per frame; each one cancels the write before it, so the disk sees the value you let
    ///     go of rather than sixty on the way there.
    /// </summary>
    public void Run(Action<CancellationToken> work, TimeSpan delay = default,
        [CallerMemberName] string origin = "")
    {
        var token = Restart();
        _owner.Launch(
            origin: origin,
            body: async ct =>
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay: delay, cancellationToken: ct);
                ct.ThrowIfCancellationRequested();
                work(ct);
            },
            token: token
        );
    }

    /// <inheritdoc cref="Run{T}" />
    public void RunAsync(Func<CancellationToken, Task> work, TimeSpan delay = default,
        [CallerMemberName] string origin = "")
    {
        var token = Restart();
        _owner.Launch(
            origin: origin,
            body: async ct =>
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay: delay, cancellationToken: ct);
                ct.ThrowIfCancellationRequested();
                await work(ct);
            },
            token: token
        );
    }

    /// <summary>Stop the outstanding run without starting a replacement.</summary>
    public void Cancel()
    {
        var previous = Interlocked.Exchange(location1: ref _current, value: null);
        previous?.Cancel();
        previous?.Dispose();
    }

    /// <summary>
    ///     Cancel the previous run and publish the token for its replacement. Linked to the scope's
    ///     lifetime, so disposing the <see cref="Background" /> — or any ancestor of it — stops these
    ///     too.
    /// </summary>
    private CancellationToken Restart()
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(_owner.Lifetime);
        var previous = Interlocked.Exchange(location1: ref _current, value: next);
        previous?.Cancel();
        previous?.Dispose();
        return next.Token;
    }
}
