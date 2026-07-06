namespace Zigote.Core.State;

/// <summary>
///     A side effect wired into the reactive graph: runs immediately and re-runs whenever a
///     <see cref="Signal{T}" />/<see cref="Computed{T}" /> it read while running changes — the same
///     auto-tracking as <see cref="Computed{T}" />, but for effects instead of a derived value. An
///     effect is a graph <b>root</b> (always watched), so it drives the subscription of the computeds it
///     reads and settles glitch-free — it runs at most once per change cascade.
///     <para>
///         The body returns a cleanup <see cref="Action" /> run before each re-run and on
///         <see cref="Dispose" /> (React-<c>useEffect</c> style) — use it to release the previous run's
///         resources (timer, subscription, …). <see cref="Dispose" /> stops the effect, detaches from
///         all sources, and runs the final cleanup.
///     </para>
/// </summary>
public sealed class Effect : Reaction, IDisposable
{
    private static readonly Action Noop = () => { };

    private readonly Func<Action> _body;
    private Action _cleanup = Noop;

    /// <summary>An effect with no cleanup.</summary>
    public Effect(Action body) : this(() =>
    {
        body();
        return Noop;
    })
    {
    }

    /// <summary>An effect whose body returns a cleanup thunk (run before each re-run and on dispose).</summary>
    public Effect(Func<Action> body)
    {
        _body = body;
        lock (Reactive.Gate)
        {
            State = NodeState.Dirty;
            Refresh(); // run now (an effect is always watched, so this subscribes to its sources)
        }
    }

    public void Dispose()
    {
        lock (Reactive.Gate)
        {
            if (Disposed) return;
            Disposed = true;
            DetachFromSources();
            _cleanup();
            _cleanup = Noop;
        }
    }

    internal override bool IsWatched => true;

    private protected override void OnScheduled()
    {
        Reactive.ScheduleEffect(this);
    }

    private protected override void BeforeExecute()
    {
        // Untracked teardown of the previous run before the new one tracks.
        _cleanup();
        _cleanup = Noop;
    }

    private protected override void Execute()
    {
        _cleanup = _body() ?? Noop;
    }

    /// <summary>Invoked by the batch drain: resolve (Check → recompute-if-dirty) and re-run the body if dirty.</summary>
    internal void RunFromQueue()
    {
        Refresh();
    }
}
