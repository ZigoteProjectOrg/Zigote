using Zigote.Core.Animation;

namespace Zigote.UI.Widgets;

/// <summary>
///     A <see cref="WidgetState{TWidget}" /> that implements <see cref="ITickerProvider" /> for
///     exactly
///     one <see cref="AnimationController" />. More efficient than
///     <see cref="TickerProviderState{TWidget}" />
///     when only one controller is needed.
/// </summary>
/// <example>
///     <code>
/// class MyState : SingleTickerProviderState&lt;MyWidget&gt;
/// {
///     private AnimationController? _ctrl;
/// 
///     public override void InitState()
///     {
///         base.InitState();
///         _ctrl = new AnimationController(0.3f, vsync: this);
///         _ctrl.Forward();   // starts ticking automatically — no Tick(dt) call needed
///     }
/// }
/// </code>
/// </example>
public abstract class SingleTickerProviderState<TWidget> : WidgetState<TWidget>, ITickerProvider
    where TWidget : StatefulWidget
{
    private Ticker? _ticker;

    public Ticker CreateTicker(Action<float> onTick)
    {
        if (_ticker != null)
            throw new InvalidOperationException(
                $"{GetType().Name} already created one Ticker. Use TickerProviderState<T> for multiple controllers."
            );
        _ticker = new Ticker(onTick);
        if (!Mounted) _ticker.Muted = true;
        return _ticker;
    }

    public override void Dispose()
    {
        _ticker?.Dispose();
        _ticker = null;
        base.Dispose();
    }
}

/// <summary>
///     A <see cref="WidgetState{TWidget}" /> that implements <see cref="ITickerProvider" /> for any
///     number of <see cref="AnimationController" /> instances.
///     All tickers are muted when the state is unmounted and disposed when the state is disposed.
/// </summary>
/// <example>
///     <code>
/// class MyState : TickerProviderState&lt;MyWidget&gt;
/// {
///     private AnimationController? _enter;
///     private AnimationController? _loop;
/// 
///     public override void InitState()
///     {
///         base.InitState();
///         _enter = new AnimationController(0.4f, vsync: this);
///         _loop  = new AnimationController(1.5f, vsync: this);
///         _enter.Forward();
///         _loop.Repeat(reverse: true);   // ping-pong forever — stops automatically on Dispose
///     }
/// }
/// </code>
/// </example>
public abstract class TickerProviderState<TWidget> : WidgetState<TWidget>, ITickerProvider
    where TWidget : StatefulWidget
{
    private readonly List<Ticker> _tickers = [];

    public Ticker CreateTicker(Action<float> onTick)
    {
        var ticker = new Ticker(onTick);
        if (!Mounted) ticker.Muted = true;
        _tickers.Add(ticker);
        return ticker;
    }

    public override void Dispose()
    {
        foreach (var t in _tickers) t.Dispose();
        _tickers.Clear();
        base.Dispose();
    }
}