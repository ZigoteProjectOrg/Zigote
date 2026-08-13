using System.Buffers;
using System.Runtime.CompilerServices;

namespace Zigote.Core;

/// <summary>
///     A method-local growable list for hot paths — replaces <c>new List&lt;T&gt;()</c> in code that
///     runs per frame. Seed it with a <c>stackalloc</c> span for unmanaged element types; reference
///     types (or the parameterless form) rent from <see cref="ArrayPool{T}" /> on first
///     <see cref="Add" />, which is allocation-free once the pool is warm. Always dispose (returns
///     the rented array):
///     <code>
///     using var pts = new ValueList&lt;float&gt;(stackalloc float[64]);
///     using var hits = new ValueList&lt;Widget&gt;();          // ref type: pool-backed
///     pts.Add(x); … var span = pts.Span;
///     </code>
///     A ref struct: cannot be stored in a field or cross an <c>await</c>/lambda — for persistent
///     per-instance scratch use <see cref="ScratchBuffer{T}" /> instead.
/// </summary>
public ref struct ValueList<T>
{
    private const int MinimumRent = 16;

    private Span<T> _span;
    private T[]? _rented;

    /// <summary>Pool-backed: rents on first <see cref="Add" />.</summary>
    public ValueList()
    {
    }

    /// <summary>
    ///     Seeded with a caller-owned buffer (typically <c>stackalloc</c>); spills to the pool on
    ///     overflow.
    /// </summary>
    public ValueList(Span<T> initialBuffer)
    {
        _span = initialBuffer;
    }

    public int Count { get; private set; }

    /// <summary>The items added so far.</summary>
    public readonly Span<T> Span => _span[..Count];

    public readonly ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) ThrowIndexOutOfRange();
            return ref _span[index];
        }
    }

    public void Add(T item)
    {
        var count = Count;
        if ((uint)count < (uint)_span.Length)
        {
            _span[count] = item;
            Count = count + 1;
        }
        else
        {
            GrowAndAdd(item);
        }
    }

    /// <summary>Resets the count; the buffer (stack or rented) is kept for reuse within the method.</summary>
    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Span.Clear();
        Count = 0;
    }

    public void Dispose()
    {
        var rented = _rented;
        if (rented is null) return;
        _rented = null;
        _span = default;
        Count = 0;
        ArrayPool<T>.Shared.Return(rented, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
    }

    private void GrowAndAdd(T item)
    {
        var next = ArrayPool<T>.Shared.Rent(Math.Max(MinimumRent, _span.Length * 2));
        _span[..Count].CopyTo(next);
        var old = _rented;
        _rented = next;
        _span = next;
        if (old is not null)
            ArrayPool<T>.Shared.Return(old, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _span[Count++] = item;
    }

    private static void ThrowIndexOutOfRange()
    {
        throw new ArgumentOutOfRangeException("index");
    }
}
