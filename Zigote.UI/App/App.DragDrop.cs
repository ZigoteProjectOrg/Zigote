using Zigote.Core;
using Zigote.Core.Events;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.DragDrop;

namespace Zigote.UI.Host;

// Drag-and-drop coordination — two sources, one target model.
//
//   • External (OS → app): SDL drop events (files / text) are aggregated here into a DragData and
//     delivered to the widget under the drop point. See HandleExternalDropEvent.
//   • In-app (widget → widget): a Draggable calls StartDrag/UpdateDrag/EndDrag; the App paints a
//     feedback ghost that follows the pointer and routes the payload to a DragTarget.
//
// Both funnel through the same target search (FindDropTarget) and the same Widget.CanAcceptDrop /
// OnDragEnter / OnDragLeave / OnDrop hooks, so a widget accepts either kind of drop with one API.
public partial class App
{
    // External-drop accumulation (a begin…complete run of DROP_FILE/DROP_TEXT events).
    private readonly List<string> _dropFiles = [];

    // Active in-app drag session (null when none).
    private DragFeedbackOverlay? _dragOverlay;

    // The target currently highlighted under the pointer (shared by external + in-app drags).
    private Widget? _dropHoverTarget;
    private Offset _dropPoint;
    private string? _dropText;

    /// <summary>True while an in-app drag session is active (a Draggable is being dragged).</summary>
    public bool IsDragging => ActiveDrag is not null;

    /// <summary>The payload of the active in-app drag, or null when not dragging.</summary>
    public DragData? ActiveDrag { get; private set; }

    private static DragData ExternalProbe { get; } = new() { IsExternal = true };

    /// <summary>
    ///     Raised after any external OS drop (files/text), regardless of whether a widget accepted
    ///     it — lets a host handle drops globally (e.g. "open the dropped file").
    /// </summary>
    public event Action<DragData>? ExternalDropped;

    // ── In-app drag session (driven by Draggable) ───────────────────────────────

    /// <summary>
    ///     Begin an in-app drag. <paramref name="feedback" /> is a widget painted following the pointer
    ///     (offset by <paramref name="grabAnchor" />, the pointer's position within the source). Call
    ///     <see cref="UpdateDrag" /> as the pointer moves and <see cref="EndDrag" /> on release.
    /// </summary>
    public void StartDrag(DragData data, Widget feedback, Offset grabAnchor)
    {
        if (ActiveDrag is not null) EndDrag(pointer: _mousePos, cancelled: true);

        ActiveDrag = data;
        _dragOverlay = new DragFeedbackOverlay(
            feedback: feedback,
            pointer: _mousePos,
            grabAnchor: grabAnchor
        );
        PushOverlay(_dragOverlay);
        UpdateDropTarget(data: data, point: _mousePos);
        ResolveAndApplyCursor();
        _repaint.MarkAll();
    }

    /// <summary>Advance the active in-app drag to a new pointer position (no-op if not dragging).</summary>
    public void UpdateDrag(Offset pointer)
    {
        if (ActiveDrag is null) return;
        if (_dragOverlay is not null)
        {
            // Damage just the ghost's old + new regions — the overlay repositions itself, so a
            // pointer move never costs a full relayout/repaint. Drop-target highlight changes still
            // invalidate fully inside UpdateDropTarget.
            MarkPaintFor(_dragOverlay.Feedback);
            _dragOverlay.SetPointer(pointer);
            MarkPaintFor(_dragOverlay.Feedback);
        }

        UpdateDropTarget(data: ActiveDrag, point: pointer);
    }

    /// <summary>
    ///     End the active in-app drag. Drops on the target under <paramref name="pointer" /> unless
    ///     <paramref name="cancelled" />. Returns true if a target accepted the payload.
    /// </summary>
    public bool EndDrag(Offset pointer, bool cancelled = false)
    {
        if (ActiveDrag is null) return false;

        var data = ActiveDrag;
        var target = cancelled ? null : FindDropTarget(data: data, point: pointer);

        ClearDropTarget();
        if (_dragOverlay is not null)
        {
            PopOverlay(_dragOverlay);
            _dragOverlay = null;
        }

        ActiveDrag = null;
        target?.OnDrop(data: data, point: pointer);
        ResolveAndApplyCursor();
        _repaint.MarkAll();
        return target is not null;
    }

    // ── External OS drops ───────────────────────────────────────────────────────

    private void HandleExternalDropEvent(InputEvent evt)
    {
        switch (evt)
        {
            case DropBeginEvent:
                _dropFiles.Clear();
                _dropText = null;
                break;

            case DropFileEvent f:
                _dropFiles.Add(f.Path);
                _dropPoint = new Offset(x: f.X, y: f.Y);
                break;

            case DropTextEvent t:
                _dropText = t.Text;
                _dropPoint = new Offset(x: t.X, y: t.Y);
                break;

            case DropPositionEvent p:
                // Drag-over feedback while hovering. SDL doesn't reveal the payload until the drop, so
                // the probe data is empty apart from IsExternal — targets highlight on "any OS drop".
                _dropPoint = new Offset(x: p.X, y: p.Y);
                UpdateDropTarget(data: ExternalProbe, point: _dropPoint);
                break;

            case DropCompleteEvent c:
                if (c.X != 0f || c.Y != 0f) _dropPoint = new Offset(x: c.X, y: c.Y);
                var data = BuildExternalDropData();
                ClearDropTarget();
                if (data.HasFiles || data.HasText)
                {
                    FindDropTarget(data: data, point: _dropPoint)?.OnDrop(
                        data: data,
                        point: _dropPoint
                    );
                    ExternalDropped?.Invoke(data);
                }

                _dropFiles.Clear();
                _dropText = null;
                _repaint.MarkAll();
                break;
        }
    }

    private DragData BuildExternalDropData()
    {
        return new DragData {
            IsExternal = true,
            Files = _dropFiles.Count > 0 ? _dropFiles.ToArray() : [],
            Text = _dropText,
        };
    }

    // ── Shared target search + hover management ──────────────────────────────────

    private Widget? FindDropTarget(DragData data, Offset point)
    {
        var hit = HitTestAll(point);
        while (hit is not null)
        {
            if (hit.CanAcceptDrop(data)) return hit;
            hit = hit.Parent;
        }

        return null;
    }

    private void UpdateDropTarget(DragData data, Offset point)
    {
        var target = FindDropTarget(data: data, point: point);
        if (ReferenceEquals(objA: target, objB: _dropHoverTarget)) return;

        _dropHoverTarget?.OnDragLeave();
        _dropHoverTarget = target;
        _dropHoverTarget?.OnDragEnter(data);
        _repaint.MarkAll();
    }

    private void ClearDropTarget()
    {
        _dropHoverTarget?.OnDragLeave();
        _dropHoverTarget = null;
    }
}
