using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.Core.Rendering;
using Zigote.Core.State;
using Zigote.UI.Debug;
using Zigote.UI.Licensing;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Focus;
using MediaQueryData = Zigote.UI.Widgets.MediaQueryData;

namespace Zigote.UI.Host;

public partial class App
{
    // ── System back ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Handlers for the system back action, innermost last. Each returns true if it consumed
    ///     the gesture. A Navigator registers itself here, and so can anything transient that
    ///     should close first — a sheet, a drawer, a search overlay.
    /// </summary>
    private readonly List<Func<bool>> _backHandlers = [];

    /// <summary>
    ///     Register a system-back handler; the most recently added runs first, so a dialog opened
    ///     over a page closes before the page pops. Remove it when the widget detaches.
    /// </summary>
    public void AddBackHandler(Func<bool> handler)
    {
        _backHandlers.Add(handler);
    }

    public void RemoveBackHandler(Func<bool> handler)
    {
        _backHandlers.Remove(handler);
    }

    /// <summary>
    ///     Whether a back action would go anywhere. Used to arm the edge-swipe gesture only when
    ///     there is somewhere to return to — swiping at the root should not close the app the way
    ///     a deliberate back press does.
    /// </summary>
    public bool CanHandleSystemBack => _overlays.Count > 0 || _backHandlers.Count > 0;

    /// <summary>
    ///     Run the system-back chain. An open overlay is dismissed first (it is visually on top,
    ///     so that is what "back" means to the user), then the registered handlers innermost
    ///     first. Returns false when nothing could consume it — the caller then closes the app.
    /// </summary>
    public bool HandleSystemBack()
    {
        // Dismiss the topmost dismissable overlay — a dialog or menu is visually on top, so that
        // is what "back" means while one is open. This is the same seam Escape uses, and it is
        // deliberately opt-in: tooltips, snackbars, the drag ghost and the devtools HUD are also
        // overlays, and letting any of them swallow the gesture would make back appear dead.
        for (var i = _overlays.Count - 1; i >= 0; i--)
            if (_overlays[i] is IDismissableOverlay dismissable && dismissable.RequestDismiss())
                return true;

        for (var i = _backHandlers.Count - 1; i >= 0; i--)
            if (_backHandlers[i]())
                return true;

        return false;
    }

    // SDL text input is per OS window: a focused text field in a secondary window must engage the
    // IME on THAT window or composed text never arrives (SDL routes it by keyboard focus).
    private void StartHostTextInput()
    {
        if (NativeWindow is not null) NativeWindow.StartTextInput();
        else Engine.StartTextInput();
    }

    private void StopHostTextInput()
    {
        if (NativeWindow is not null) NativeWindow.StopTextInput();
        else Engine.StopTextInput();
    }
}
