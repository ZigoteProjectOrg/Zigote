namespace Zigote.UI.Widgets;

/// <summary>The lifecycle phase of the async computation a <see cref="FutureBuilder{T}" /> watches.</summary>
public enum ConnectionState
{
    /// <summary>No future is being observed.</summary>
    None,

    /// <summary>The future is still running.</summary>
    Waiting,

    /// <summary>The future has completed (with data or an error).</summary>
    Done,
}

/// <summary>An immutable snapshot of a <see cref="System.Threading.Tasks.Task{T}" />'s current status.</summary>
public readonly struct AsyncSnapshot<T>(ConnectionState connectionState, T? data, Exception? error)
{
    public ConnectionState ConnectionState { get; } = connectionState;
    public T? Data { get; } = data;
    public Exception? Error { get; } = error;

    public bool HasData =>
        ConnectionState == ConnectionState.Done && Error is null && Data is not null;

    public bool HasError => Error is not null;
    public bool IsWaiting => ConnectionState == ConnectionState.Waiting;
}

/// <summary>
///     Builds from a one-shot <see cref="System.Threading.Tasks.Task{T}" />.
///     It watches the task on the <b>UI thread</b> via a ticker and rebuilds when it completes,
///     handing
///     <see cref="Builder" /> an <see cref="AsyncSnapshot{T}" /> (waiting → data / error). Use it for
///     one-shot async UI (loading a detail record, a computed result) where a full signal store is
///     overkill.
/// </summary>
public sealed class FutureBuilder<T> : ComposedWidget
{
    private bool _settled;

    public FutureBuilder(Task<T>? future, Func<BuildContext, AsyncSnapshot<T>, Widget> builder)
    {
        Future = future;
        Builder = builder;
    }

    public Task<T>? Future { get; }
    public Func<BuildContext, AsyncSnapshot<T>, Widget> Builder { get; }

    protected override void OnMount()
    {
        _settled = Future is null or { IsCompleted: true };
        CreateTicker(OnTick).Start();
    }

    protected override Widget Build(BuildContext context) =>
        Builder(arg1: context, arg2: Snapshot());

    private AsyncSnapshot<T> Snapshot()
    {
        var future = Future;
        if (future is null)
        {
            return new AsyncSnapshot<T>(
                connectionState: ConnectionState.None,
                data: default,
                error: null
            );
        }

        if (!future.IsCompleted)
        {
            return new AsyncSnapshot<T>(
                connectionState: ConnectionState.Waiting,
                data: default,
                error: null
            );
        }

        if (future.IsFaulted)
        {
            return new AsyncSnapshot<T>(
                connectionState: ConnectionState.Done,
                data: default,
                error: future.Exception?.GetBaseException()
            );
        }

        if (future.IsCanceled)
        {
            return new AsyncSnapshot<T>(
                connectionState: ConnectionState.Done,
                data: default,
                error: new TaskCanceledException()
            );
        }

        return new AsyncSnapshot<T>(
            connectionState: ConnectionState.Done,
            data: future.Result,
            error: null
        );
    }

    private void OnTick(float dt)
    {
        if (!Mounted || _settled) return;
        if (Future is { IsCompleted: true })
        {
            _settled = true;
            MarkNeedsBuild();
        }
    }
}
