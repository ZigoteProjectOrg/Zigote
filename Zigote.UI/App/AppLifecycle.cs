using Zigote.Core.Events;

namespace Zigote.UI.Host;

/// <summary>
///     Coarse application lifecycle, driven by OS signals: window focus on desktop, the
///     suspend/resume transitions on mobile.
/// </summary>
public enum AppLifecycleState
{
    /// <summary>Visible, focused, rendering normally.</summary>
    Resumed,

    /// <summary>
    ///     Visible but not focused (another window has focus; on mobile: system sheet or
    ///     app-switcher over the app). Rendering continues — hosts may throttle it.
    /// </summary>
    Inactive,

    /// <summary>
    ///     Backgrounded/suspended. Rendering is fully stopped (on iOS, GPU work while
    ///     backgrounded terminates the app) and no frames run beyond event draining until
    ///     <see cref="Resumed" />. Persist anything important when entering this state — on
    ///     mobile there is no guarantee any code runs afterwards.
    /// </summary>
    Paused,
}

/// <summary>
///     Observer for app-level lifecycle transitions — register via
///     <see cref="App.AddLifecycleObserver" /> (and remove in the widget's detach path).
///     The Flutter analogue is <c>WidgetsBindingObserver.didChangeAppLifecycleState</c>.
/// </summary>
public interface IAppLifecycleObserver
{
    /// <summary>The app moved between <see cref="AppLifecycleState" />s.</summary>
    void OnLifecycleChanged(AppLifecycleState state);

    /// <summary>
    ///     The OS is low on memory: drop caches that can be rebuilt (decoded images, pooled
    ///     buffers). The framework already drops the native text caches. Default: no-op.
    /// </summary>
    void OnLowMemory()
    {
    }
}

public partial class App
{
    private readonly List<IAppLifecycleObserver> _lifecycleObservers = [];

    /// <summary>Current lifecycle state. Starts <see cref="AppLifecycleState.Resumed" />.</summary>
    public AppLifecycleState LifecycleState { get; private set; } = AppLifecycleState.Resumed;

    /// <summary>
    ///     Whether the mobile on-screen keyboard is up (always false on desktop). The platform
    ///     already pans the view to keep the focused text area visible; this is for layout
    ///     decisions on top (e.g. hiding a bottom bar the keyboard covers).
    /// </summary>
    public bool ScreenKeyboardVisible { get; private set; }

    /// <summary>Raised when the mobile on-screen keyboard appears/disappears.</summary>
    public event Action<bool>? ScreenKeyboardChanged;

    /// <summary>True while backgrounded — <see cref="Frame" /> drains events but renders nothing.</summary>
    public bool IsPaused => LifecycleState == AppLifecycleState.Paused;

    /// <summary>Delegate-style companion to <see cref="IAppLifecycleObserver" />.</summary>
    public event Action<AppLifecycleState>? LifecycleChanged;

    /// <summary>Raised on an OS low-memory warning, after observers ran.</summary>
    public event Action? LowMemory;

    public void AddLifecycleObserver(IAppLifecycleObserver observer)
    {
        if (!_lifecycleObservers.Contains(observer)) _lifecycleObservers.Add(observer);
    }

    public void RemoveLifecycleObserver(IAppLifecycleObserver observer)
    {
        _lifecycleObservers.Remove(observer);
    }

    private void SetLifecycleState(AppLifecycleState state)
    {
        if (LifecycleState == state) return;
        LifecycleState = state;
        // Backwards: observers may remove themselves (or their widget) during the callback.
        for (var i = _lifecycleObservers.Count - 1; i >= 0; i--)
            _lifecycleObservers[i].OnLifecycleChanged(state);
        LifecycleChanged?.Invoke(state);
    }

    private void HandleAppLifecycleEvent(InputEvent evt)
    {
        switch (evt)
        {
            case AppBackgroundEvent:
                // The OS suspends the app after this frame. Fingers die with it; text input
                // and pending interactions must not half-commit on resume.
                CancelActiveTouch();
                SetLifecycleState(AppLifecycleState.Paused);
                break;

            case AppForegroundEvent:
                SetLifecycleState(AppLifecycleState.Resumed);
                // Everything may be stale (surface recreated, clock jumped) — repaint fully.
                _repaint.MarkAll();
                break;

            case LowMemoryEvent:
                for (var i = _lifecycleObservers.Count - 1; i >= 0; i--)
                    _lifecycleObservers[i].OnLowMemory();
                LowMemory?.Invoke();
                // Shaped runs and glyph atlases rebuild lazily — the cheapest big cache to give
                // back under pressure.
                Engine.ResetTextCaches();
                break;

            case ScreenKeyboardEvent kb:
                if (ScreenKeyboardVisible == kb.Shown) break;
                ScreenKeyboardVisible = kb.Shown;
                ScreenKeyboardChanged?.Invoke(kb.Shown);
                // The platform pans the whole view while the keyboard is up (against the
                // focused widget's SetTextInputArea rect), so no relayout is required for
                // visibility — repaint so anything reading the flag refreshes promptly.
                _repaint.MarkAll();
                break;
        }
    }
}
