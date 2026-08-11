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
    // ── Accessibility ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Build (and cache as <see cref="SemanticsRoot" />) the current accessibility tree from the root
    ///     widget + overlays. Pure — safe to call from a debug panel each frame or from a headless test.
    /// </summary>
    public SemanticsNode BuildSemantics()
    {
        var screen = new Size(HostLogicalWidth, HostLogicalHeight);
        SemanticsRoot = SemanticsBuilder.Build(Root, _overlays, screen);
        return SemanticsRoot;
    }

    /// <summary>
    ///     Fired once per frame (after tickers advance, before layout) with the frame delta. The
    ///     devtools package subscribes to refresh its live panels/charts on the UI thread.
    /// </summary>
    public event Action<float>? FrameTick;

    /// <summary>
    ///     Register a predicate that, while true, keeps the frame loop pumping every frame (so live
    ///     metrics never freeze — even when the window is unfocused). The devtools overlay registers
    ///     one that returns true while its panel or compact stats are visible.
    /// </summary>
    public void AddContinuousFrameSource(Func<bool> wants)
    {
        _continuousFrameSources.Add(wants);
    }

    internal bool WantsContinuousFrame()
    {
        for (var i = 0; i < _continuousFrameSources.Count; i++)
            if (_continuousFrameSources[i]())
                return true;
        return false;
    }

    /// <summary>Toggle the full devtools panel (no-op until a devtools package is installed).</summary>
    public void ToggleDebugPanel()
    {
        OnToggleDevTools?.Invoke();
    }

    /// <summary>Toggle the compact always-on stats block (no-op until a devtools package is installed).</summary>
    public void ToggleCompactStats()
    {
        OnToggleDevCompact?.Invoke();
    }

    /// <summary>
    ///     Focusables in reading (tree) order within the active focus scope. The scope is the topmost
    ///     modal overlay that contains a focusable (so a dialog traps Tab), else the nearest enclosing
    ///     <see cref="FocusScope" /> of the current focus, else the whole root tree. Zero-area (collapsed
    ///     / off-screen) focusables are skipped so Tab never lands on something invisible.
    /// </summary>
    private List<Widget> GetFocusableWidgets()
    {
        var scope = ActiveFocusScope();
        return scope != null ? FocusTraversal.Focusables(scope) : [];
    }

    private Widget? ActiveFocusScope()
    {
        for (var i = _overlays.Count - 1; i >= 0; i--)
        {
            var ov = _overlays[i];
            if (FocusTraversal.HasFocusable(ov)) return ov;
        }

        if (FocusedWidget != null)
        {
            var node = FocusedWidget.Parent;
            while (node != null)
            {
                if (node is FocusScope { Trap: true }) return node;
                node = node.Parent;
            }
        }

        return Root;
    }

    private void MoveFocusByTab(bool backwards)
    {
        // Tab order, not the raw focusable list: an IFocusGroup (a navigation sidebar, any list
        // that navigates with arrows) is one stop here while arrows still reach every row.
        var scope = ActiveFocusScope();
        var next = scope is null
            ? null
            : FocusTraversal.NextInTab(
                FocusTraversal.TabOrder(scope, FocusedWidget),
                FocusedWidget,
                backwards
            );
        if (next != null)
        {
            SetFocusRingVisible(true);
            RequestFocus(next);
        }
    }

    /// <summary>
    ///     Geometric arrow-key focus move within the active scope. Only invoked when the focused widget
    ///     does not consume arrows itself (<see cref="Widget.HandlesDirectionalKeys" />).
    /// </summary>
    private bool MoveFocusDirectional(float dirX, float dirY)
    {
        if (FocusedWidget is null) return false;
        var best = FocusTraversal.Directional(
            GetFocusableWidgets(),
            FocusedWidget,
            dirX,
            dirY
        );
        if (best is null) return false;
        SetFocusRingVisible(true);
        RequestFocus(best);
        return true;
    }

    /// <summary>Run the first menu accelerator matching this chord. See <see cref="Accelerators" />.</summary>
    private bool RunAccelerator(KeyCode key, Modifiers modifiers)
    {
        foreach (var (chord, run) in Accelerators)
            if (chord.Matches(key, modifiers))
            {
                run();
                return true;
            }

        return false;
    }

    /// <summary>
    ///     Esc: dismiss the top-most dismissable overlay; if none, clear focus. Returns true if
    ///     handled.
    /// </summary>
    private bool HandleEscape()
    {
        for (var i = _overlays.Count - 1; i >= 0; i--)
            if (_overlays[i] is IDismissableOverlay d && d.RequestDismiss())
                return true;

        // A window whose ROOT is dismissable closes on Esc — this is how dialogs hosted as
        // separate OS windows (e.g. the file browser) get the same Esc-cancels behavior that
        // overlay dialogs get from the stack above. Look through the chrome wrapper: the
        // dismissable widget is the app's actual root, not the titlebar host.
        var rootTarget = Root is WindowChromeHost chromeHost ? chromeHost.Content : Root;
        if (rootTarget is IDismissableOverlay rootDismiss && rootDismiss.RequestDismiss())
            return true;

        // In-tree dismissables (a modal bottom sheet, a navigation stack) register a back handler
        // rather than sitting on the overlay stack. Escape is the desktop spelling of the same
        // gesture as the Android back button, so it runs the same handlers — otherwise Escape on a
        // modal sheet merely clears focus and leaves the sheet up.
        for (var i = _backHandlers.Count - 1; i >= 0; i--)
            if (_backHandlers[i]())
                return true;

        if (FocusedWidget != null)
        {
            ClearFocus();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Move focus to the first focusable inside <paramref name="scope" /> (used for overlay
    ///     auto-focus).
    /// </summary>
    private void FocusFirstIn(Widget scope)
    {
        var list = FocusTraversal.Focusables(scope);
        if (list.Count > 0)
        {
            SetFocusRingVisible(true);
            RequestFocus(list[0]);
        }
    }
}
