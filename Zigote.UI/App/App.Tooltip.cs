using Zigote.UI.Widgets.Controls;

namespace Zigote.UI.Host;

public partial class App
{
    // ── Tooltip ───────────────────────────────────────────────────────────────

    private void AdvanceTooltip(float dt)
    {
        string? text = null;
        var w = _hoveredWidget;
        while (w != null)
        {
            if (w.TooltipText != null)
            {
                text = w.TooltipText;
                break;
            }

            w = w.Parent;
        }

        if (text is null)
        {
            HideTooltip();
            return;
        }

        // The timer accrues only while no bubble is shown (it gates the per-frame overlay mark in
        // Frame); once up, the bubble just tracks the pointer — it repositions on the next relayout,
        // and those frames are already marked by whatever caused the relayout.
        if (_tooltipOverlay is null)
        {
            _tooltipTimer += dt;
            if (_tooltipTimer > 0.7f)
            {
                _tooltipOverlay = new TooltipBubble(text: text, position: _mousePos, theme: Theme);
                PushOverlay(_tooltipOverlay);
            }
        }
        else
            _tooltipOverlay.Position = _mousePos;
    }

    private void HideTooltip()
    {
        // The timer may be accruing with no bubble shown yet — always clear it so the per-frame
        // overlay mark in Frame stops.
        _tooltipTimer = 0f;
        if (_tooltipOverlay is null) return;
        PopOverlay(_tooltipOverlay);
        _tooltipOverlay = null;
    }
}
