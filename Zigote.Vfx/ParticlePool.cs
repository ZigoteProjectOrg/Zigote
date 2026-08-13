namespace Zigote.Vfx;

/// <summary>
///     Fixed-capacity, contiguous particle storage. Births append; deaths swap-remove (O(1), order is
///     irrelevant for additive/depth-sorted draws). The backing array is allocated once so the steady-state
///     simulation step never touches the heap (see <c>HotPathAllocationTests</c> discipline).
/// </summary>
public sealed class ParticlePool
{
    private Particle[] _items;

    public ParticlePool(int capacity)
    {
        _items = new Particle[Math.Max(1, capacity)];
    }

    public int Count { get; private set; }
    public int Capacity => _items.Length;

    /// <summary>Direct array access for the simulator's hot loop — index only in <c>[0, Count)</c>.</summary>
    public Particle[] Items => _items;

    /// <summary>The live particles, for readers (preview/render upload).</summary>
    public ReadOnlySpan<Particle> Live => _items.AsSpan(0, Count);

    public ref Particle At(int index)
    {
        return ref _items[index];
    }

    public bool TryEmit(out int index)
    {
        if (Count >= _items.Length)
        {
            index = -1;
            return false;
        }

        index = Count++;
        _items[index] = default;
        return true;
    }

    public void KillAt(int index)
    {
        _items[index] = _items[--Count];
    }

    public void Clear()
    {
        Count = 0;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length) return;
        Array.Resize(ref _items, capacity);
    }
}
