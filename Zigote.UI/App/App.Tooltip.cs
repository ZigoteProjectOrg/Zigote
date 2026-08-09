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
                _tooltipOverlay = new TooltipBubble(text, _mousePos, Theme);
                PushOverlay(_tooltipOverlay);
            }
        }
        else
        {
            _tooltipOverlay.Position = _mousePos;
        }
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
