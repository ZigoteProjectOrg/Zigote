namespace Zigote.Core;

/// <summary>
///     Grow-on-demand reusable scratch storage for per-frame hot loops — the field-scratch pattern
///     (<c>if (_sx.Length &lt; n) _sx = new float[n]</c>) as a primitive. Embed one per instance and
///     per logical buffer:
///     <code>
///     private ScratchBuffer&lt;float&gt; _xs;                 // field — NOT readonly
///     var xs = _xs.Get(pointCount);                          // Span&lt;float&gt; of exactly pointCount
///     </code>
///     <see cref="Get" /> returns a span of exactly the requested length over an internal array that
///     grows geometrically and never shrinks, so steady-state frames allocate nothing. Growth does
///     <b>not</b> preserve contents (per-frame scratch is rewritten anyway) — use
///     <see cref="GetPreserving" /> for accumulation buffers that must survive growth.
///     A mutable struct: never mark the field <c>readonly</c> (the defensive copy would drop growth),
///     and don't copy it into a local before calling <see cref="Get" />.
/// </summary>
public struct ScratchBuffer<T>
{
    /// <summary>
    ///     The current backing array (grown to at least the largest requested count), or null before
    ///     first use.
    /// </summary>
    public T[]? Array { get; private set; }

    /// <summary>Capacity of the backing array (0 before first use).</summary>
    public readonly int Capacity => Array?.Length ?? 0;

    /// <summary>A span of exactly <paramref name="count" /> items. Contents are undefined after growth.</summary>
    public Span<T> Get(int count)
    {
        var a = Array;
        if (a is null || a.Length < count) Array = a = new T[GrownCapacity(count)];
        return a.AsSpan(0, count);
    }

    /// <summary>Like <see cref="Get" />, but growth copies the previous contents (accumulation buffers).</summary>
    public Span<T> GetPreserving(int count)
    {
        var a = Array;
        if (a is null || a.Length < count)
        {
            var next = new T[GrownCapacity(count)];
            a?.AsSpan().CopyTo(next);
            Array = a = next;
        }

        return a.AsSpan(0, count);
    }

    private readonly int GrownCapacity(int count)
    {
        var doubled = (Array?.Length ?? 0) * 2;
        return Math.Max(Math.Max(8, doubled), count);
    }
}
