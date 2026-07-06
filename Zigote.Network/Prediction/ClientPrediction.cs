namespace Zigote.Network;

/// <summary>
///     Client-side prediction with server reconciliation. The game supplies a deterministic step function
///     <c>(state, input, dt) → state</c>. Each tick, <see cref="Predict" /> stamps the input, advances the
///     local state immediately (so control feels instant) and buffers the input. When the server's
///     authoritative state for a processed input sequence arrives, <see cref="Reconcile" /> snaps to it and
///     replays the inputs the server hasn't acknowledged yet — correcting prediction error without losing
///     responsiveness. The step function MUST match the server's (same math, same dt) or prediction will
///     constantly fight the server.
/// </summary>
public sealed class ClientPrediction<TInput, TState> where TInput : IInputCommand
{
    private readonly InputBuffer<TInput> _buffer = new();
    private readonly Func<TState, TInput, float, TState> _step;

    public ClientPrediction(TState initial, Func<TState, TInput, float, TState> step)
    {
        State = initial;
        _step = step;
    }

    /// <summary>The locally predicted state — what the owning client renders.</summary>
    public TState State { get; private set; }

    public uint LastInputSequence { get; private set; }

    public int PendingInputs => _buffer.Count;

    /// <summary>Stamp the input, predict locally, buffer it, and return the stamped command to send to the server.</summary>
    public TInput Predict(TInput input, float dt)
    {
        input.Sequence = ++LastInputSequence;
        State = _step(State, input, dt);
        _buffer.Add(input);
        return input;
    }

    /// <summary>
    ///     Apply the server's authoritative state (which reflects inputs up to <paramref name="lastProcessedSequence" />)
    ///     and replay the rest.
    /// </summary>
    public void Reconcile(TState authoritative, uint lastProcessedSequence, float dt)
    {
        _buffer.AckThrough(lastProcessedSequence);
        State = authoritative;
        var pending = _buffer.Pending;
        for (var i = 0; i < pending.Count; i++) State = _step(State, pending[i], dt);
    }

    public void Reset(TState state)
    {
        State = state;
        _buffer.Clear();
        LastInputSequence = 0;
    }
}