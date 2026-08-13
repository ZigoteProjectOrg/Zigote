using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.UI.Host;

public partial class App
{
    // ── Overlay management ────────────────────────────────────────────────────

    // Push/pop requested while InTreeWalk, applied once the walk is over (see PushOverlay).
    private readonly List<(Widget Overlay, bool Push)> _deferredOverlayOps = [];

    /// <summary>Apply the overlay pushes/pops that arrived during a tree walk.</summary>
    private void DrainDeferredOverlayOps()
    {
        if (_deferredOverlayOps.Count == 0 || InTreeWalk) return;
        // Snapshot: applying an op can queue another (a popped overlay closing its child).
        var ops = _deferredOverlayOps.ToArray();
        _deferredOverlayOps.Clear();
        foreach ((var overlay, bool push) in ops)
        {
            if (push) PushOverlay(overlay);
            else PopOverlay(overlay);
        }
    }

    public void PushOverlay(Widget overlay)
    {
        // Mutating the overlay list from inside a tree walk invalidates the walk's own enumerator
        // (layout and paint both iterate it) — and a widget CAN legitimately push one while being
        // measured or painted. Defer to the end of the walk, the same way Watch defers a rebuild.
        if (InTreeWalk)
        {
            _deferredOverlayOps.Add((overlay, true));
            RequestLayout();
            return;
        }

        _overlays.Add(overlay);
        overlay.Attach(owner: this, parent: null);
        // Auto-focus the first focusable inside a newly-pushed overlay (modal forms/dialogs) once it has
        // been laid out — deferred to Frame so Bounds are valid for the visibility check.
        _pendingAutoFocus.Add(overlay);
        RequestLayout();
    }

    public void PopOverlay(Widget overlay)
    {
        if (InTreeWalk)
        {
            _deferredOverlayOps.Add((overlay, false));
            RequestLayout();
            return;
        }

        if (!_overlays.Remove(overlay)) return;

        _pendingAutoFocus.Remove(overlay);

        // If focus lived inside this overlay, drop it before detaching (Parent links go away on Detach).
        bool focusedInside = FocusedWidget != null &&
                             IsDescendant(node: FocusedWidget, ancestor: overlay);

        overlay.Detach();
        RequestLayout();

        // Restore the focus that was active before this overlay auto-focused (if it's still in the tree).
        bool restored = false;
        for (int i = _focusRestore.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(objA: _focusRestore[i].Overlay, objB: overlay))
            {
                var prev = _focusRestore[i].PrevFocus;
                _focusRestore.RemoveAt(i);
                RequestFocus(prev is { Owner: not null } ? prev : null);
                restored = true;
                break;
            }
        }

        if (!restored && focusedInside) ClearFocus();
    }

    private static bool IsDescendant(Widget? node, Widget ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(objA: node, objB: ancestor)) return true;
            node = node.Parent;
        }

        return false;
    }

    public void ClearOverlays()
    {
        foreach (var overlay in _overlays) overlay.Detach();
        _overlays.Clear();
        // Drop bookkeeping that referenced the now-detached overlays, otherwise their subtrees (and the
        // saved prior-focus widgets) stay pinned in these lists after a ClearOverlays that bypasses
        // PopOverlay's per-overlay cleanup.
        _pendingAutoFocus.Clear();
        _focusRestore.Clear();
        _snackbars.Clear();
        // …including pushes queued mid-walk, which would otherwise resurrect an overlay after this.
        _deferredOverlayOps.Clear();
        _tooltipOverlay = null;
        _tooltipTimer = 0f;
        RequestLayout();
    }

    /// <summary>
    ///     Drop app-level references (focus, hover, pointer capture) to <paramref name="w" /> when it
    ///     leaves the tree. Called from <see cref="Widget.Detach" /> for every detached widget so a
    ///     removed control can never remain the focused/hovered/captured target. Idempotent and
    ///     allocation-free (reference compares); the single matching slot, if any, is cleared.
    /// </summary>
    internal void NotifyDetached(Widget w)
    {
        if (ReferenceEquals(objA: FocusedWidget, objB: w)) RequestFocus(null);
        if (ReferenceEquals(objA: _hoveredWidget, objB: w))
        {
            _hoveredWidget = null;
            HideTooltip();
        }

        if (ReferenceEquals(objA: _capturedWidget, objB: w))
        {
            _capturedWidget = null;
            // The captured widget is the one that would deliver the pointer-up that ends a drag. If
            // it leaves the tree first — a list rebuilt under the pointer, a page swapped, a route
            // popped — nothing ever calls EndDrag: the session stays open and its ghost floats over
            // every later page. Cancel it instead. Deferred to the top of the next frame because
            // this can run mid-walk, and ending a drag pops an overlay off the list being iterated.
            if (IsDragging)
            {
                Post(() =>
                    {
                        if (IsDragging) EndDrag(pointer: _mousePos, cancelled: true);
                    }
                );
            }
        }

        if (ReferenceEquals(objA: _rightCapturedWidget, objB: w)) _rightCapturedWidget = null;
    }

    // ── Snackbar ──────────────────────────────────────────────────────────────

    public void ShowSnackbar(string message, float duration = 3f,
        string? actionLabel = null, Action? onAction = null)
    {
        var snack = new Snackbar(
            app: this,
            message: message,
            duration: duration,
            actionLabel: actionLabel,
            onAction: onAction
        );
        _snackbars.Add(snack);
        PushOverlay(snack);
    }
}
