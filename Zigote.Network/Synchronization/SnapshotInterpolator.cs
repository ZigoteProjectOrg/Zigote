namespace Zigote.Network;

/// <summary>
///     A time-stamped sample buffer that interpolates a value at a query time, hiding network jitter. Push
///     authoritative samples as they arrive (tagged with the server time they represent); sample at
///     <c>NetworkClock.RenderTime</c> to read a smooth value that trails real state by the interpolation
///     delay. Past the newest sample it extrapolates (clamped) rather than snapping.
/// </summary>
public sealed class SnapshotInterpolator<T>(Func<T, T, float, T> lerp, int capacity = 32)
{
    private readonly List<Sample> _samples = new(capacity);

    public int Count => _samples.Count;
    public bool HasData => _samples.Count > 0;

    /// <summary>The most recent sample's value (the latest known authoritative value).</summary>
    public T Latest => _samples.Count > 0 ? _samples[^1].Value : default!;

    public void Push(double time, T value)
    {
        // Keep the buffer sorted by time; arrivals are usually in order so append-and-bubble is cheap.
        var sample = new Sample(time, value);
        if (_samples.Count == 0 || time >= _samples[^1].Time)
        {
            _samples.Add(sample);
        }
        else
        {
            var i = _samples.Count - 1;
            while (i > 0 && _samples[i - 1].Time > time) i--;
            _samples.Insert(i, sample);
        }

        if (_samples.Count > capacity) _samples.RemoveAt(0);
    }

    public void Clear()
    {
        _samples.Clear();
    }

    /// <summary>Drop samples older than <paramref name="time" /> (keeping the one straddling it for interpolation).</summary>
    public void Prune(double time)
    {
        while (_samples.Count > 2 && _samples[1].Time < time) _samples.RemoveAt(0);
    }

    public bool TrySample(double renderTime, out T value)
    {
        if (_samples.Count == 0)
        {
            value = default!;
            return false;
        }

        if (_samples.Count == 1 || renderTime <= _samples[0].Time)
        {
            value = _samples[0].Value;
            return true;
        }

        var last = _samples[^1];
        if (renderTime >= last.Time)
        {
            // Extrapolate from the last two samples, clamped to a short horizon to avoid runaway.
            var prev = _samples[^2];
            var span = last.Time - prev.Time;
            if (span <= 1e-6)
            {
                value = last.Value;
                return true;
            }

            var t = (float)Math.Min(1.5, (renderTime - prev.Time) / span);
            value = lerp(prev.Value, last.Value, t);
            return true;
        }

        // Find the bracketing pair [a, b] with a.Time <= renderTime <= b.Time.
        for (var i = 0; i < _samples.Count - 1; i++)
        {
            var a = _samples[i];
            var b = _samples[i + 1];
            if (renderTime < a.Time || renderTime > b.Time) continue;

            var span = b.Time - a.Time;
            var t = span <= 1e-6 ? 0f : (float)((renderTime - a.Time) / span);
            value = lerp(a.Value, b.Value, t);
            return true;
        }

        value = last.Value;
        return true;
    }

    private readonly record struct Sample(double Time, T Value);
}
