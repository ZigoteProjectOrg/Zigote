using System.Collections;

namespace Zigote.UI.DevTools.Diagnostics;

/// <summary>One sample of a diagnostics series: session time (seconds) and a value.</summary>
public readonly record struct TimeSample(float Time, float Value);

/// <summary>
///     Fixed-capacity chronological ring of <see cref="TimeSample" />s that chart marks can plot
///     directly — it IS an <see cref="IReadOnlyList{T}" /> (index 0 = oldest), so
///     <c>LineMark.Of(ring, s =&gt; s.Time, s =&gt; s.Value)</c> charts it with zero copying.
///     <see cref="Push" /> is allocation-free.
/// </summary>
public sealed class TimeSeriesRing(int capacity) : IReadOnlyList<TimeSample>
{
    private readonly TimeSample[] _samples = new TimeSample[Math.Max(2, capacity)];
    private int _head; // next write slot

    public int Capacity => _samples.Length;

    public TimeSample Latest => Count > 0 ? this[Count - 1] : default;

    public int Count { get; private set; }

    public TimeSample this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var start = (_head - Count + _samples.Length) % _samples.Length;
            return _samples[(start + index) % _samples.Length];
        }
    }

    public IEnumerator<TimeSample> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Push(float time, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
        _samples[_head] = new TimeSample(time, value);
        _head = (_head + 1) % _samples.Length;
        if (Count < _samples.Length) Count++;
    }

    public void Clear()
    {
        Count = 0;
        _head = 0;
    }

    /// <summary>Maximum value currently held (0 when empty).</summary>
    public float Max()
    {
        var max = 0f;
        for (var i = 0; i < Count; i++) max = MathF.Max(max, this[i].Value);
        return max;
    }
}