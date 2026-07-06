namespace Zigote.Network;

/// <summary>
///     Server-side authoritative simulation of one owned object from client inputs. Buffered inputs are
///     applied in strict sequence order each server tick using the same deterministic step the client
///     predicts with; <see cref="LastProcessedSequence" /> is sent back so the client can reconcile.
///     Tolerant of duplicate, stale and reordered inputs (the client sends recent inputs redundantly over an
///     unreliable channel): duplicates/stale are ignored, and a missing middle input is skipped rather than
///     stalling the simulation.
/// </summary>
public sealed class ServerReconciler<TInput, TState> where TInput : IInputCommand
{
    private readonly SortedList<uint, TInput> _pending = new();
    private readonly Func<TState, TInput, float, TState> _step;

    public ServerReconciler(TState initial, Func<TState, TInput, float, TState> step)
    {
        State = initial;
        _step = step;
    }

    /// <summary>The authoritative state.</summary>
    public TState State { get; private set; }

    /// <summary>Highest input sequence applied so far — echoed to the client for reconciliation.</summary>
    public uint LastProcessedSequence { get; private set; }

    public void Enqueue(TInput input)
    {
        if (input.Sequence <= LastProcessedSequence) return; // already processed or stale
        _pending[input.Sequence] = input; // keyed by sequence → dedupes and orders
    }

    /// <summary>Apply all buffered inputs newer than the last processed, in ascending sequence order.</summary>
    public void Step(float dt)
    {
        if (_pending.Count == 0) return;

        var keys = _pending.Keys;
        for (var i = 0; i < keys.Count; i++)
        {
            var seq = keys[i];
            if (seq <= LastProcessedSequence) continue;
            State = _step(State, _pending[seq], dt);
            LastProcessedSequence = seq;
        }

        _pending.Clear();
    }

    public void SetState(TState state)
    {
        State = state;
    }
}