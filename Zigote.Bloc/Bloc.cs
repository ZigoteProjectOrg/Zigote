using Zigote.Core.Diagnostics;
using Zigote.Core.State;

namespace Zigote.Bloc;

/// <summary>
///     Where a bloc handler's unhandled exception goes. Mirrors <see cref="Reactive.OnError" />: a
///     bloc must not drag a logging package onto every app that uses the pattern, and one bad event
///     must not take the screen down with it. Unset, failures land in <see cref="DebugLog" /> — set
///     this once at startup to route them into the app's real log.
/// </summary>
/// <remarks>
///     Not a static member of <see cref="Bloc{TEvent}" />: a static field on a generic type is one
///     field per constructed type, so setting it would only ever reach the blocs that happened to
///     share an event type.
/// </remarks>
public static class BlocErrors
{
    public static Action<Exception, string>? OnError;
}

/// <summary>
///     Where every bloc's events and state changes go, for a log, a DevTools timeline or a replay.
///     Unset — the default — a bloc reports nothing and pays one null check per event, which is why
///     this is not behind a compile flag: an app that wants the timeline in a release build gets it
///     by assigning a hook, not by rebuilding.
///     <para>
///         Both fire on the pump, in the order things happened: an <see cref="OnEvent" /> and the
///         <see cref="OnChange" /> its handler caused arrive interleaved, so a flat append-only log
///         already reads as "this event, then this transition" with no correlation ids to thread
///         through. The bloc arrives as <see cref="object" /> for the same reason
///         <see cref="BlocErrors" /> is not a static member of the generic type — one hook has to
///         reach every bloc in the app, not one per constructed type.
///     </para>
///     <para>
///         The hooks run on whichever thread the pump is on and inside the dispatch, so a slow one
///         slows the app down. Queue the record and format it elsewhere. A throwing hook is reported
///         through <see cref="BlocErrors.OnError" /> and otherwise ignored — observation must not be
///         able to break the feature it is observing.
///     </para>
/// </summary>
/// <example>
///     <code>
/// BlocObserver.OnEvent = (bloc, e) => DebugLog.Add(DebugLogLevel.Info, $"{bloc.GetType().Name} ← {e}", "bloc");
/// BlocObserver.OnChange = (bloc, from, to) => DebugLog.Add(DebugLogLevel.Info, $"{bloc.GetType().Name} {from} → {to}", "bloc");
///     </code>
/// </example>
public static class BlocObserver
{
    /// <summary>An event came off the queue and is about to be handled.</summary>
    public static Action<object, object?>? OnEvent;

    /// <summary>
    ///     A bloc published a state that differs from the one before it. Deduplicated emits — the
    ///     ones a <see cref="Signal{T}" /> swallows because the record compares equal — do not fire:
    ///     a timeline of transitions that never happened is worse than no timeline.
    /// </summary>
    public static Action<object, object?, object?>? OnChange;
}

/// <summary>
///     Business logic as one object per feature: events in, ordered, one at a time; state out as
///     signals; no widget anywhere in the type.
///     <para>
///         What this base owns is the event pump and the lifetime, which is the part every app got
///         subtly different when each kept its own copy. Events dispatch in order through one queue —
///         an <see cref="Add" /> from inside a handler runs after the current one finishes, never
///         nested inside it — so "what happened, in what order" is one code path that can be logged
///         or replayed.
///     </para>
///     <para>
///         Dispatch is synchronous when the handler is: an <see cref="Add" /> on a quiet bloc has
///         already run its handler by the time it returns, which is what lets a tap feel immediate
///         and a test assert without polling. A handler that awaits releases the caller at its first
///         real await and the queue drains on the continuation; events that arrive meanwhile wait
///         their turn rather than interleaving.
///     </para>
///     <para>
///         Use <see cref="Bloc{TEvent,TState}" /> when the feature's state is one immutable record.
///         This base is for a bloc whose state is several independent signals, where a single record
///         would make every write a whole-state rewrite.
///     </para>
/// </summary>
/// <typeparam name="TEvent">
///     The event hierarchy this bloc accepts — usually a closed record hierarchy, or
///     <see cref="object" /> for a bloc that registers handlers per type.
/// </typeparam>
public abstract class Bloc<TEvent> : IDisposable
{
    private readonly Queue<TEvent> _events = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IDisposable> _subscriptions = [];

    /// <summary>
    ///     Taken while the source is alive, because <see cref="Dispose" /> disposes it and
    ///     <see cref="CancellationTokenSource.Token" /> throws afterwards. The token itself keeps
    ///     working — an already-cancelled token is exactly what a handler resuming into a dead bloc
    ///     should see, and that resume is the normal case, not an edge one: dispose is what cancelled
    ///     the await it was parked on.
    /// </summary>
    private readonly CancellationToken _lifetimeToken;

    private bool _disposed;
    private bool _pumping;
    private CancellationTokenSource? _work;

    protected Bloc()
    {
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>
    ///     Cancelled when the bloc is disposed. Handed to every handler, and safe to read at any
    ///     point in a handler's life — including after the bloc has gone, where it reads cancelled.
    /// </summary>
    protected CancellationToken Lifetime => _lifetimeToken;

    public void Dispose()
    {
        lock (_events)
        {
            if (_disposed) return;
            _disposed = true;
            _events.Clear();
        }

        _lifetime.Cancel();
        Cancel();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        OnDispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Queue an event. Safe from any thread, never blocks on another event, never throws — after
    ///     dispose, events are dropped like any other work arriving at a dead object.
    /// </summary>
    /// <remarks>
    ///     Virtual for the one thing a pump cannot do on a subclass's behalf: reject an event before
    ///     it is queued. A bloc that dispatches by registered handler type wants a typo'd event to
    ///     fail at the call site — once it is on the queue, the failure is a log line at best, and a
    ///     button that does nothing at worst.
    /// </remarks>
    public virtual void Add(TEvent @event)
    {
        lock (_events)
        {
            if (_disposed) return;

            _events.Enqueue(@event);
            if (_pumping) return; // whoever is pumping will get to it, in order

            _pumping = true;
        }

        Pump();
    }

    /// <summary>
    ///     Handle one event. Runs on the pump, one at a time, and may await. Throwing is allowed:
    ///     the pump reports it through <see cref="BlocErrors.OnError" /> and carries on with the next event
    ///     rather than tearing the bloc down.
    /// </summary>
    protected abstract ValueTask OnEventAsync(TEvent @event, CancellationToken ct);

    /// <summary>
    ///     Tie a subscription to this bloc's lifetime — the shape of every "repository stream in,
    ///     event out" wire, disposed with the bloc so a closed bloc stops receiving.
    ///     <para>
    ///         Safe from any thread, and safe after <see cref="Dispose" />: a wire registered against
    ///         a bloc that is already gone is disposed on the spot rather than added to a list nobody
    ///         will walk again. Both matter now that <see cref="Bloc{TEvent,TState}.Select{T}" /> goes
    ///         through here — a projection can be built off the pump, and a view that outlives its
    ///         bloc by a frame is ordinary.
    ///     </para>
    /// </summary>
    protected void Track(IDisposable subscription)
    {
        lock (_events)
        {
            if (!_disposed)
            {
                _subscriptions.Add(subscription);
                return;
            }
        }

        subscription.Dispose();
    }

    /// <summary>
    ///     Cancel whatever this bloc was doing and get a token for its replacement. An entry point
    ///     that starts async work calls this first, so a user who types, switches source and hits
    ///     refresh ends up with the result of the refresh rather than whichever request happened to
    ///     land last.
    ///     <para>
    ///         Safe against a concurrent <see cref="Dispose" />, which is not a theoretical pairing: a
    ///         handler that resumed on a pool thread and restarts its work is racing whatever closed
    ///         the screen. The source published here can be taken and torn down by that dispose before
    ///         this method has finished with it, so the token is read out first and the tidy-up
    ///         tolerates finding it already gone.
    ///     </para>
    /// </summary>
    protected CancellationToken Restart()
    {
        if (_disposed) return new CancellationToken(true);

        var next = new CancellationTokenSource();

        // Before publishing: once `next` is reachable from _work, a concurrent Dispose may take it and
        // dispose it, and CancellationTokenSource.Token throws after that. The token itself keeps
        // working — reading a captured token never throws, whatever happened to its source.
        var token = next.Token;

        var previous = Interlocked.Exchange(ref _work, next);

        previous?.Cancel();
        previous?.Dispose();

        // Disposed between the exchange and here: cancel the source we just published rather than
        // leaving work running against a dead bloc.
        if (_disposed)
            try
            {
                next.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Dispose got to it first, and it cancels before it disposes — so the token this
                // returns is already cancelled and there is nothing left to do.
            }

        return token;
    }

    /// <summary>Cancel the in-flight unit of work without starting a replacement.</summary>
    protected void Cancel()
    {
        var work = Interlocked.Exchange(ref _work, null);
        work?.Cancel();
        work?.Dispose();
    }

    /// <summary>Release anything the subclass owns beyond its tracked subscriptions.</summary>
    protected virtual void OnDispose()
    {
    }

    /// <summary>
    ///     Drain the queue on this thread, for as long as the handlers let us.
    ///     <para>
    ///         Deliberately not an <c>async</c> method. A dispatch sits between every user action and
    ///         the state it changes, and most handlers never await — an async loop would box a state
    ///         machine and hand back a <see cref="Task" /> for every one of them, which is ~88 bytes
    ///         of garbage per tap. A handler that does await falls through to <see cref="DrainAsync" />
    ///         and pays for its state machine there, once.
    ///     </para>
    /// </summary>
    private void Pump()
    {
        while (true)
        {
            TEvent next;

            lock (_events)
            {
                // The empty-check and the flag drop are one atomic step: an Add that lands between
                // them would otherwise see _pumping still true, enqueue, return — and strand its
                // event until the next one arrives.
                if (_disposed || _events.Count == 0)
                {
                    _pumping = false;
                    return;
                }

                next = _events.Dequeue();
            }

            // Before the handler, so the log reads "event, then what it did" — and so an event whose
            // handler hangs or throws is still on the timeline that has to explain it.
            if (BlocObserver.OnEvent is { } observer)
                try
                {
                    observer(this, next);
                }
                catch (Exception ex)
                {
                    Report(ex, $"{GetType().Name} observer failed on {next?.GetType().Name ?? "null"}");
                }

            ValueTask handling;

            try
            {
                handling = OnEventAsync(next, _lifetimeToken);
            }
            catch (Exception ex)
            {
                // Thrown before the first await, so there is no task to observe it.
                ReportFailure(ex, next);
                continue;
            }

            if (handling.IsCompletedSuccessfully) continue;

            // Faulted-but-synchronous lands here too, which is fine: DrainAsync awaits an already
            // completed task and reports, at the cost of one state machine on a path that is a bug.
            _ = DrainAsync(handling, next);
            return;
        }
    }

    /// <summary>
    ///     Wait out the one handler that awaited, then go back to draining synchronously. Events
    ///     that arrived meanwhile are still queued behind it — <c>_pumping</c> stayed true, so
    ///     nobody else started a second pump.
    /// </summary>
    private async Task DrainAsync(ValueTask handling, TEvent current)
    {
        try
        {
            await handling;
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            lock (_events)
            {
                _pumping = false;
            }

            return;
        }
        catch (Exception ex)
        {
            ReportFailure(ex, current);
        }

        Pump();
    }

    private void ReportFailure(Exception ex, TEvent @event)
    {
        Report(ex, $"{GetType().Name} failed handling {@event?.GetType().Name ?? "null"}");
    }

    private protected static void Report(Exception ex, string context)
    {
        try
        {
            if (BlocErrors.OnError is { } hook) hook(ex, context);
            else DebugLog.Add(DebugLogLevel.Error, $"{context} — {ex}", "bloc");
        }
        catch
        {
            // A failing error reporter must not become the failure that kills the pump.
        }
    }
}

/// <summary>
///     A bloc whose state is one immutable record: events in, one state out, views a pure function
///     of it.
///     <para>
///         <see cref="State" /> is a <see cref="Signal{T}" />, which is what makes this fit the
///         widget tree — a <c>Watch</c> over it rebuilds exactly the subtree that read it, with no
///         subscribe/unsubscribe bookkeeping in the widget. A bloc never touches a widget and a
///         widget never calls anything but <see cref="Bloc{TEvent}.Add" />.
///     </para>
/// </summary>
/// <typeparam name="TEvent">The closed event hierarchy this bloc accepts.</typeparam>
/// <typeparam name="TState">Immutable state. A record, so <see cref="Emit(Func{TState,TState})" /> is a <c>with</c>.</typeparam>
public abstract class Bloc<TEvent, TState> : Bloc<TEvent>
{
    private readonly Signal<TState> _state;

    protected Bloc(TState initial)
    {
        _state = new Signal<TState>(initial);
    }

    /// <summary>
    ///     The state the widget tree watches. Written only by <see cref="Emit(TState)" />.
    ///     <para>
    ///         <b>It re-emits whenever <i>any</i> field moves.</b> A state that carries a clock — a
    ///         playback position, a countdown, a progress fraction — therefore changes several times
    ///         a second, and a <c>Watch</c> that reads it to answer a question as slow as "which row
    ///         is playing?" rebuilds its whole subtree at the clock's rate. That is invisible in a
    ///         small view and ruinous in a list. When a view needs one fact rather than the whole
    ///         state, subscribe to <see cref="Select{T}" /> instead.
    ///     </para>
    /// </summary>
    public IReadableSignal<TState> State => _state;

    /// <summary>
    ///     One fact from the state, as something a <c>Watch</c> can read without inheriting the rest
    ///     of it. The projection re-runs on every emit, but observers are only woken when its
    ///     <i>result</i> changes — so a list keyed on "what is playing" rebuilds when the track
    ///     changes and not when the position does.
    ///     <para>
    ///         Hold the returned value for the lifetime of the view rather than calling this inside a
    ///         build: it is a live node in the graph, and a fresh one per build would subscribe, fire
    ///         once and be thrown away every frame — and, because the bloc keeps every projection it
    ///         hands out alive until it is itself disposed, one per build is an unbounded leak rather
    ///         than garbage.
    ///     </para>
    ///     <para>
    ///         Dropping one without disposing it is safe: an unobserved <see cref="Computed{T}" />
    ///         detaches from its sources, so a projection whose view has gone is ordinary garbage and
    ///         the bloc does not keep it alive. Do <b>not</b> expect the reverse — a projection you
    ///         hold past the bloc's own disposal keeps answering, but with whatever it last computed.
    ///     </para>
    /// </summary>
    /// <example>
    ///     <code>
    ///  // in the view's constructor
    ///  _playing = player.Select(s => s.Current?.Path);
    ///  // in its Watch
    ///  var playing = _playing.Value;
    ///     </code>
    /// </example>
    public Computed<T> Select<T>(Func<TState, T> project)
    {
        return Computed.From(() => project(_state.Value));
    }

    /// <summary>The current state without subscribing — for a handler deciding what to do next.</summary>
    public TState Current => _state.Peek();

    /// <summary>
    ///     Run <paramref name="listener" /> with the state now, and again whenever it changes.
    ///     <para>
    ///         For the parts of the app that are not views: a widget rebuilds itself by reading
    ///         <see cref="State" /> inside a <c>Watch</c>, but something that has to turn a state
    ///         change into an effect — a preference into a device setting, say — needs to be told.
    ///         Because it fires immediately, the startup path and every later change are the same
    ///         code, which is the usual place these two drift apart.
    ///     </para>
    /// </summary>
    public IDisposable Subscribe(Action<TState> listener)
    {
        return _state.Subscribe(listener);
    }

    /// <summary>
    ///     Publish a new state under the graph lock — a handler may resume on any thread, and every
    ///     signal write belongs under that lock.
    /// </summary>
    protected void Emit(TState next)
    {
        // The read is only worth its trip through the graph lock when somebody is listening; with no
        // observer attached this is the same two lines it always was.
        var observed = BlocObserver.OnChange is not null;
        var previous = observed ? _state.Peek() : default!;

        Reactive.Sync(() => _state.Value = next);

        if (observed) Notify(previous, next);
    }

    /// <inheritdoc cref="Emit(TState)" />
    protected void Emit(Func<TState, TState> next)
    {
        var observed = BlocObserver.OnChange is not null;
        var previous = observed ? _state.Peek() : default!;

        Reactive.Sync(() => _state.Update(next));

        if (observed) Notify(previous, _state.Peek());
    }

    /// <summary>
    ///     Report a transition, skipping the emits the signal deduplicated — <see cref="Emit(TState)" />
    ///     with the state the bloc is already in is a no-op the widget tree never sees, and a timeline
    ///     that shows it as a change sends whoever is reading it looking for a rebuild that never
    ///     happened.
    /// </summary>
    private void Notify(TState previous, TState next)
    {
        if (BlocObserver.OnChange is not { } hook) return;
        if (EqualityComparer<TState>.Default.Equals(previous, next)) return;

        try
        {
            hook(this, previous, next);
        }
        catch (Exception ex)
        {
            Report(ex, $"{GetType().Name} observer failed on change");
        }
    }
}

/// <summary>
///     A bloc whose handler never awaits: it decides, emits, and starts whatever background work it
///     needs without waiting for it.
///     <para>
///         Same pump, same ordering, same failure handling as <see cref="Bloc{TEvent,TState}" /> —
///         the only difference is the signature. A handler that is a <c>switch</c> over a dozen
///         events with an early <c>return</c> in half of them reads as exactly that, rather than as
///         a dozen <c>return ValueTask.CompletedTask</c>. Move a bloc to the async base the day it
///         has something real to await.
///     </para>
/// </summary>
public abstract class SyncBloc<TEvent, TState>(TState initial) : Bloc<TEvent, TState>(initial)
{
    protected abstract void OnEvent(TEvent @event);

    protected sealed override ValueTask OnEventAsync(TEvent @event, CancellationToken ct)
    {
        OnEvent(@event);
        return default;
    }
}