using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.Core.Diagnostics;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     A single-line console command field: a focusable, monospace text input that runs
///     <see cref="DebugCommands" />. It keeps Tab (auto-complete), Escape (blur), Up/Down (history) and
///     the arrows for itself via <see cref="IKeyboardTrap" />, and receives typed characters via
///     <see cref="ITextInputClient" />. Command output goes to <see cref="DebugLog" />, which the console
///     panel's log tail renders — this widget only owns the input line.
/// </summary>
public sealed class DevConsoleField : Widget, ITextInputClient, IKeyboardTrap
{
    private const string Placeholder = "type a command — try 'help'";

    /// <summary>Adwaita entry height, raised to a finger-sized target on a phone.</summary>
    private float _height = AdwMetrics.EntryHeight;

    private int _historyIdx = -1;
    private string _input = "";
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    /// <summary>Fired after a command line is submitted (so the panel can scroll its tail to newest).</summary>
    public Action? OnSubmitted { get; set; }

    public override bool Focusable => true;

    public override bool HandlesDirectionalKeys => true;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _height = DevKit.Compact ? ControlMetrics.MinTouchTarget : AdwMetrics.EntryHeight;
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
        _size = new Size(w, _height);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        var focused = Owner?.FocusedWidget == this;
        var p = AdwPalette.For(_theme);
        // Adwaita entry: view background, 1px border, and a 2px accent focus ring when focused.
        paint.AddRect(Bounds, p.ViewBg, AdwMetrics.ControlRadius);
        paint.AddBorder(
            Bounds,
            focused ? _theme.Accent : p.Border,
            AdwMetrics.ControlRadius,
            focused ? _theme.FocusRingWidth : 1f
        );

        var textY = Bounds.Y + _height * 0.62f;
        Icons.DrawAt(
            paint,
            Icons.ChevronRight,
            Bounds.X + 8f,
            textY,
            _theme.Accent,
            AdwMetrics.IconSize
        );

        var tx = Bounds.X + 8f + AdwMetrics.IconSize + Spacing.Xs;
        if (_input.Length == 0 && !focused)
        {
            paint.AddText(
                Placeholder,
                tx,
                textY,
                _theme.Hint.WithAlpha(0.7f),
                DevKit.CaptionSize
            );
        }
        else
        {
            paint.AddText(
                _input,
                tx,
                textY,
                _theme.OnSurface,
                DevKit.CaptionSize,
                fontFamily: "code"
            );
            if (focused && BlinkOn())
            {
                var caretX = tx + TextMeasure.Width(
                    _input,
                    DevKit.CaptionSize,
                    fontFamily: "code"
                ) + 1f;
                paint.AddRect(
                    new Rect(
                        caretX,
                        Bounds.Y + _height * 0.25f,
                        1.5f,
                        _height * 0.5f
                    ),
                    _theme.Primary
                );
            }
        }
    }

    private static bool BlinkOn()
    {
        var time = App.Active?.Time ?? 0f;
        return (int)(time * 2f) % 2 == 0;
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override void OnPointerDown(Offset point)
    {
        Owner?.RequestFocus(this);
    }

    public override void OnTextInput(string text)
    {
        foreach (var ch in text)
            if (!char.IsControl(ch))
                _input += ch;
        MarkNeedsPaint();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Backspace:
                if (_input.Length > 0) _input = _input[..^1];
                break;
            case KeyCode.Enter or KeyCode.KpEnter:
                Submit();
                break;
            case KeyCode.Escape:
                Owner?.RequestFocus(null);
                break;
            case KeyCode.Tab:
                Complete();
                break;
            case KeyCode.Up:
                Recall(1);
                break;
            case KeyCode.Down:
                Recall(-1);
                break;
            default:
                return;
        }

        MarkNeedsPaint();
    }

    private void Submit()
    {
        var line = _input.Trim();
        if (line.Length == 0) return;
        DebugCommands.Execute(line);
        _input = "";
        _historyIdx = -1;
        OnSubmitted?.Invoke();
    }

    private void Complete()
    {
        var prefix = _input.TrimStart();
        if (prefix.Contains(' ')) return; // only complete the command word
        var matches = DebugCommands.Complete(prefix);
        if (matches.Count == 1) _input = matches[0] + " ";
        else if (matches.Count > 1) _input = LongestCommonPrefix(matches);
    }

    private void Recall(int dir)
    {
        var history = DebugCommands.History;
        if (history.Count == 0) return;
        _historyIdx = Math.Clamp(_historyIdx + dir, -1, history.Count - 1);
        _input = _historyIdx < 0 ? "" : history[history.Count - 1 - _historyIdx];
    }

    private static string LongestCommonPrefix(List<string> items)
    {
        var p = items[0];
        foreach (var s in items)
        {
            var n = Math.Min(p.Length, s.Length);
            var i = 0;
            while (i < n && p[i] == s[i]) i++;
            p = p[..i];
        }

        return p;
    }
}
