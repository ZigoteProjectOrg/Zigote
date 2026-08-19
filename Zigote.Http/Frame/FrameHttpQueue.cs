using System.Buffers;
using System.Net;

namespace Zigote.Http.Frame;

/// <summary>
///     A ticket for one submitted request. A struct of two ints — the generation is what makes a
///     stale handle safe: a slot reused by a later request will not answer an older ticket.
/// </summary>
public readonly record struct HttpHandle(int Index, int Generation)
{
    /// <summary>False for <c>default</c>, and for a handle the queue refused because it was full.</summary>
    public bool IsValid => Generation != 0;
}

/// <summary>
///     A finished request, read from the frame thread. A <c>ref struct</c> because
///     <see cref="Body" /> points into a pooled buffer the queue still owns — copy what you keep,
///     then <see cref="FrameHttpQueue.Release" /> the handle.
/// </summary>
public readonly ref struct HttpOutcome
{
    internal HttpOutcome(HttpStatusCode status, HttpError? error, ReadOnlySpan<byte> body)
    {
        Status = status;
        Error = error;
        Body = body;
    }

    /// <summary>The status, or 0 when the request never got an answer.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>What went wrong, or null.</summary>
    public HttpError? Error { get; }

    /// <summary>The body, valid until the handle is released.</summary>
    public ReadOnlySpan<byte> Body { get; }

    /// <summary>True when a response arrived, whatever its status.</summary>
    public bool IsOk => Error is null;
}

/// <summary>
///     The frame-loop face of the library: submit a request, poll for it, release the slot. No
///     <c>Task</c>, no <c>await</c>, no closure, and no allocation on any of the three — which is
///     what lets gameplay and widget code talk to the network from inside Measure→Layout→Paint.
/// </summary>
/// <remarks>
///     <para>
///         <b>Threading.</b> <see cref="Submit" />, <see cref="TryTake" />, <see cref="Cancel" /> and
///         <see cref="Release" /> are for one thread — the frame thread. Completions cross back from
///         the pipeline's threads through the slot's state word, published with a release write and
///         read with an acquire read, the same way input and audio already cross that boundary.
///     </para>
///     <para>
///         <b>Bounded.</b> <see cref="Capacity" /> requests may be in flight; past that
///         <see cref="Submit" /> returns an invalid handle rather than growing a queue nobody is
///         draining. That is a backpressure signal, and the right response is to stop asking.
///     </para>
/// </remarks>
public sealed class FrameHttpQueue : IDisposable
{
    private const int StateFree = 0;
    private const int StatePending = 1;
    private const int StateDone = 2;

    private readonly int[] _free;
    private readonly HttpRunner _runner;
    // A ManualResetEventSlim signalled from the frame thread, drained by a dedicated thread.
    // Not a SemaphoreSlim: releasing one with an async waiter attached queues a thread-pool work
    // item on the releasing thread, and that is a per-submit allocation on the frame path.
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly Slot[] _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int[] _submitted;
    private int _freeTop;
    private int _generation;
    private readonly Thread _pump;
    private volatile bool _disposed;
    private int _readIndex;
    private int _writeIndex;

    /// <summary>A queue over <paramref name="runner" />, with room for <paramref name="capacity" /> in-flight requests.</summary>
    public FrameHttpQueue(HttpRunner runner, int capacity = 256)
    {
        // ponytail: fixed capacity, sized once. A frame loop that needs more than 256 concurrent
        // requests has a design problem upstream of this queue; raise the number if that is wrong.
        _runner = runner;
        _slots = new Slot[capacity];
        _free = new int[capacity];
        _submitted = new int[capacity + 1]; // one spare so full and empty are distinguishable
        for (int i = 0; i < capacity; i++)
        {
            _slots[i] = new Slot();
            _free[i] = capacity - 1 - i;
        }

        _freeTop = capacity;
        _pump = new Thread(Pump) { IsBackground = true, Name = "Zigote.Http frame pump" };
        _pump.Start();
    }

    /// <summary>How many requests can be in flight at once.</summary>
    public int Capacity => _slots.Length;

    /// <summary>How many slots are currently taken.</summary>
    public int InFlight => _slots.Length - _freeTop;

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _shutdown.Cancel();
        _signal.Set();
        _pump.Join(TimeSpan.FromSeconds(1));
        _shutdown.Dispose();
        _signal.Dispose();

        // Give completed slots' pooled buffers back. A request still in flight may finish after
        // this — RunAsync checks _disposed before renting, so at worst its body is dropped, never
        // a buffer leaked from the pool.
        foreach (var slot in _slots)
        {
            if (slot.Buffer is { } buffer)
            {
                slot.Buffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    ///     Queues <paramref name="spec" /> and returns a ticket. Allocates nothing: the spec is a
    ///     reference the caller already built, and the slot and the ring were allocated at
    ///     construction. Returns an invalid handle when the queue is full.
    /// </summary>
    public HttpHandle Submit(HttpSpec spec)
    {
        if (_freeTop == 0) return default;

        int index = _free[--_freeTop];
        var slot = _slots[index];
        int generation = ++_generation;
        if (generation == 0) generation = ++_generation; // 0 means "invalid", so skip it on wrap

        slot.Generation = generation;
        slot.Spec = spec;
        slot.Canceled = false;
        slot.Error = null;
        slot.Status = 0;
        slot.Length = 0;
        slot.Cancellation = null;
        Volatile.Write(ref slot.State, StatePending);

        _submitted[_writeIndex] = index;
        // Release write: the pump must not see the new write index before the slot it points at.
        Volatile.Write(ref _writeIndex, _writeIndex + 1 == _submitted.Length ? 0 : _writeIndex + 1);
        _signal.Set();
        return new HttpHandle(index, generation);
    }

    /// <summary>
    ///     True once the request behind <paramref name="handle" /> has finished, with its outcome.
    ///     False while it is still running, and false forever for a stale or released handle.
    ///     <paramref name="outcome" />'s body stays valid until <see cref="Release" />.
    /// </summary>
    public bool TryTake(HttpHandle handle, out HttpOutcome outcome)
    {
        outcome = default;
        if (!handle.IsValid || (uint)handle.Index >= (uint)_slots.Length) return false;

        var slot = _slots[handle.Index];
        if (slot.Generation != handle.Generation) return false;
        if (Volatile.Read(ref slot.State) != StateDone) return false;

        outcome = new HttpOutcome(slot.Status, slot.Error,
            slot.Buffer is null ? default : slot.Buffer.AsSpan(0, slot.Length));
        return true;
    }

    /// <summary>
    ///     Asks the request behind <paramref name="handle" /> to stop. The slot still has to be
    ///     taken or released — cancelling does not free it, because the caller may still want to see
    ///     that it was cancelled.
    /// </summary>
    public void Cancel(HttpHandle handle)
    {
        if (!handle.IsValid || (uint)handle.Index >= (uint)_slots.Length) return;

        var slot = _slots[handle.Index];
        if (slot.Generation != handle.Generation) return;

        slot.Canceled = true;
        slot.Cancellation?.Cancel();
    }

    /// <summary>
    ///     Returns the slot and its buffer. Any <see cref="HttpOutcome" /> taken from this handle is
    ///     invalid afterwards, and the handle will never answer again.
    /// </summary>
    public void Release(HttpHandle handle)
    {
        if (!handle.IsValid || (uint)handle.Index >= (uint)_slots.Length) return;

        var slot = _slots[handle.Index];
        if (slot.Generation != handle.Generation) return;
        if (Volatile.Read(ref slot.State) != StateDone) return; // a running request keeps its slot

        if (slot.Buffer is { } buffer)
        {
            slot.Buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }

        slot.Spec = null;
        slot.Error = null;
        slot.Generation = 0;
        Volatile.Write(ref slot.State, StateFree);
        _free[_freeTop++] = handle.Index;
    }

    private void Pump()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Reset before draining: anything submitted during the drain re-signals, and the worst
            // case is one extra loop that finds the ring empty.
            _signal.Reset();

            while (_readIndex != Volatile.Read(ref _writeIndex))
            {
                int index = _submitted[_readIndex];
                _readIndex = _readIndex + 1 == _submitted.Length ? 0 : _readIndex + 1;

                // Fire and forget on purpose: the pump dispatches, it does not wait. Everything
                // that bounds this — deadlines, retries, the breaker — lives in the pipeline.
                _ = RunAsync(_slots[index]);
            }
        }
    }

    private async Task RunAsync(Slot slot)
    {
        var spec = slot.Spec;
        if (spec is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        slot.Cancellation = cts;
        if (slot.Canceled) cts.Cancel();

        try
        {
            var result = await _runner.SendAsync(spec, cts.Token).ConfigureAwait(false);
            if (result.IsOk)
            {
                using var response = result.Value;
                slot.Status = response.Status;
                int length = response.BodyLength;
                if (length > 0 && !_disposed)
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                    response.Body.Span.CopyTo(buffer);
                    slot.Buffer = buffer;
                    slot.Length = length;
                }
            }
            else
            {
                slot.Error = result.Error;
            }
        }
        catch (Exception e)
        {
            // The frame thread must never see an exception it cannot catch. Anything unexpected
            // down there becomes an error value up here.
            slot.Error = new HttpError.Transport(TransportFault.Unknown, e);
        }
        finally
        {
            slot.Cancellation = null;
            // Release write: everything above is visible to the frame thread before it sees Done.
            Volatile.Write(ref slot.State, StateDone);
        }
    }

    private sealed class Slot
    {
        public byte[]? Buffer;
        public volatile bool Canceled;
        public volatile CancellationTokenSource? Cancellation;
        public HttpError? Error;
        public volatile int Generation;
        public int Length;
        public HttpSpec? Spec;
        public int State;
        public HttpStatusCode Status;
    }
}
