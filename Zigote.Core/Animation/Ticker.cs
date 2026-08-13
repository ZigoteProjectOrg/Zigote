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

    private readonly Action<float> _onTick;
    private bool _disposed;
    private bool _running;

    public Ticker(Action<float> onTick)
    {
        _onTick = onTick;
    }

    /// <summary>When true the ticker does not fire its callback even while running.</summary>
    public bool Muted { get; set; }

    /// <summary>True while at least one ticker is running — the frame loop must keep pumping.</summary>
    public static bool AnyActive
    {
        get
        {
            foreach (var t in Active)
                if (t is { _running: true, Muted: false })
                    return true;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    /// <summary>Advance all currently running, non-muted tickers by <paramref name="dt" /> seconds.</summary>
    public static void AdvanceAll(float dt)
    {
        var count = Active.Count;
        if (count == 0) return;
        // Copy into a reusable buffer so callbacks can safely call Stop() on themselves.
        if (_advanceBuffer.Length < count)
            _advanceBuffer = new Ticker[count * 2];
        Active.CopyTo(
            0,
            _advanceBuffer,
            0,
            count
        );
        for (var i = 0; i < count; i++)
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
        Active.Add(this);
    }

    /// <summary>Stop receiving per-frame callbacks. The ticker can be restarted later.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Active.Remove(this);
    }
}
