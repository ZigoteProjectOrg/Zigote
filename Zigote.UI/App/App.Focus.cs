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
    // ── Focus ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Focus a widget. A composite control that is not focusable itself (an entry that delegates
    ///     its text editing to an inner field, a row wrapping one) hands the focus to its first
    ///     focusable descendant — "focus this control" is what every caller means, and focusing the
    ///     wrapper instead would silently take the caret nowhere. Unlike Tab traversal this does not
    ///     require a laid-out rect, so it works on the frame a control is first mounted.
    /// </summary>
    public void RequestFocus(Widget? widget)
    {
        if (widget is { Focusable: false }) widget = FirstFocusableIn(widget) ?? widget;
        if (FocusedWidget == widget) return;
        FocusedWidget?.Focused = false;
        if (FocusedWidget is ITextInputClient) StopHostTextInput();

        FocusedWidget = widget;

        FocusedWidget?.Focused = true;
        if (FocusedWidget is ITextInputClient) StartHostTextInput();

        _semanticsDirty = true;
        if (SemanticsBridge != null)
            SemanticsBridge.FocusChanged(
                widget is null
                    ? null
                    : SemanticsRoot?.Flatten()
                        .FirstOrDefault(n => ReferenceEquals(n.Source, widget))
            );
    }

    public void ClearFocus()
    {
        RequestFocus(null);
    }

    /// <summary>Depth-first search for the first focusable under <paramref name="widget" />.</summary>
    private static Widget? FirstFocusableIn(Widget widget)
    {
        foreach (var child in widget.GetVisibleChildren())
        {
            if (child.Focusable) return child;
            if (FirstFocusableIn(child) is { } deeper) return deeper;
        }

        return null;
    }
}
