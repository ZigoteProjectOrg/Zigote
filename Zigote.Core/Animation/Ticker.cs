namespace Zigote.Core.Animation;

/// <summary>
///     Factory that creates <see cref="Ticker" /> instances tied to the provider's lifecycle.
///     Every <c>Widget</c> implements it — pass <c>vsync: this</c> from a widget.
/// </summary>
public interface ITickerProvider
{
    Ticker CreateTicker(Action<float> onTick);
}

/// <summary>
///     A handle that receives per-frame dt callbacks while running.
///     Created by an <see cref="ITickerProvider" />; disposed when the owning widget unmounts.
///     <para>
///         Set <see cref="Muted" /> to true to pause ticking without stopping (analogous to
///         TickerMode).
///     </para>
/// </summary>
public sealed class Ticker : IDisposable
{
    private static readonly List<Ticker> Active = [];
    private static Ticker[] _advanceBuffer = [];

    // Running, non-muted tickers — kept incrementally so the frame loop's AnyActive check (and
    // the idle gate behind it) is a field read instead of a per-frame list scan.
    private static int _unmutedRunning;

    private readonly Action<float> _onTick;

    // This ticker's slot in Active while running (swap-remove on Stop). -1 when not listed.
    private int _activeIndex = -1;
    private bool _disposed;
    private bool _muted;
    private bool _running;

    public Ticker(Action<float> onTick) => _onTick = onTick;

    /// <summary>When true the ticker does not fire its callback even while running.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            if (_running) _unmutedRunning += value ? -1 : 1;
        }
    }

    /// <summary>True while at least one ticker is running — the frame loop must keep pumping.</summary>
    public static bool AnyActive => _unmutedRunning > 0;

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    /// <summary>Advance all currently running, non-muted tickers by <paramref name="dt" /> seconds.</summary>
    public static void AdvanceAll(float dt)
    {
        int count = Active.Count;
        if (count == 0) return;
        // Copy into a reusable buffer so callbacks can safely call Stop() on themselves.
        if (_advanceBuffer.Length < count)
            _advanceBuffer = new Ticker[count * 2];
        Active.CopyTo(
            index: 0,
            array: _advanceBuffer,
            arrayIndex: 0,
            count: count
        );
        for (int i = 0; i < count; i++)
        {
            var t = _advanceBuffer[i];
            if (t is { _running: true, Muted: false })
                t._onTick(dt);
        }
    }

    /// <summary>Start receiving per-frame callbacks via <see cref="AdvanceAll" />.</summary>
    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        _activeIndex = Active.Count;
        Active.Add(this);
        if (!_muted) _unmutedRunning++;
    }

    /// <summary>Stop receiving per-frame callbacks. The ticker can be restarted later.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (!_muted) _unmutedRunning--;

        // Swap-remove via the stored slot: a fling ending among hundreds of live animations must
        // not pay an O(n) reference scan. Order of Active is not part of the contract (AdvanceAll
        // snapshots before invoking).
        int i = _activeIndex;
        int last = Active.Count - 1;
        if (i != last)
        {
            var moved = Active[last];
            Active[i] = moved;
            moved._activeIndex = i;
        }

        Active.RemoveAt(last);
        _activeIndex = -1;
    }
}
