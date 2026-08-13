using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     A single-line console command field: a focusable, monospace text input that runs
///     <see cref="DebugCommands" />. It keeps Tab (auto-complete), Escape (blur), Up/Down (history)
///     and
///     the arrows for itself via <see cref="IKeyboardTrap" />, and receives typed characters via
///     <see cref="ITextInputClient" />. Command output goes to <see cref="DebugLog" />, which the
///     console
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
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
        _size = new Size(width: w, height: _height);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        bool focused = Owner?.FocusedWidget == this;
        var p = AdwPalette.For(_theme);
        // Adwaita entry: view background, 1px border, and a 2px accent focus ring when focused.
        paint.AddRect(bounds: Bounds, color: p.ViewBg, radius: AdwMetrics.ControlRadius);
        paint.AddBorder(
            bounds: Bounds,
            color: focused ? _theme.Accent : p.Border,
            radius: AdwMetrics.ControlRadius,
            width: focused ? _theme.FocusRingWidth : 1f
        );

        float textY = Bounds.Y + (_height * 0.62f);
        Icons.DrawAt(
            paint: paint,
            glyph: Icons.ChevronRight,
            x: Bounds.X + 8f,
            baselineY: textY,
            color: _theme.Accent,
            size: AdwMetrics.IconSize
        );

        float tx = Bounds.X + 8f + AdwMetrics.IconSize + Spacing.Xs;
        if (_input.Length == 0 && !focused)
        {
            paint.AddText(
                text: Placeholder,
                baselineX: tx,
                baselineY: textY,
                color: _theme.Hint.WithAlpha(0.7f),
                fontSize: DevKit.CaptionSize
            );
        }
        else
        {
            paint.AddText(
                text: _input,
                baselineX: tx,
                baselineY: textY,
                color: _theme.OnSurface,
                fontSize: DevKit.CaptionSize,
                fontFamily: "code"
            );
            if (focused && BlinkOn())
            {
                float caretX = tx + TextMeasure.Width(
                    text: _input,
                    fontSize: DevKit.CaptionSize,
                    fontFamily: "code"
                ) + 1f;
                paint.AddRect(
                    bounds: new Rect(
                        x: caretX,
                        y: Bounds.Y + (_height * 0.25f),
                        width: 1.5f,
                        height: _height * 0.5f
                    ),
                    color: _theme.Primary
                );
            }
        }
    }

    private static bool BlinkOn()
    {
        float time = App.Active?.Time ?? 0f;
        return (int)(time * 2f) % 2 == 0;
    }

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? this : null;

    public override void OnPointerDown(Offset point) => Owner?.RequestFocus(this);

    public override void OnTextInput(string text)
    {
        foreach (char ch in text)
        {
            if (!char.IsControl(ch))
                _input += ch;
        }

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
        string line = _input.Trim();
        if (line.Length == 0) return;
        DebugCommands.Execute(line);
        _input = "";
        _historyIdx = -1;
        OnSubmitted?.Invoke();
    }

    private void Complete()
    {
        string prefix = _input.TrimStart();
        if (prefix.Contains(' ')) return; // only complete the command word
        var matches = DebugCommands.Complete(prefix);
        if (matches.Count == 1) _input = matches[0] + " ";
        else if (matches.Count > 1) _input = LongestCommonPrefix(matches);
    }

    private void Recall(int dir)
    {
        var history = DebugCommands.History;
        if (history.Count == 0) return;
        _historyIdx = Math.Clamp(value: _historyIdx + dir, min: -1, max: history.Count - 1);
        _input = _historyIdx < 0 ? "" : history[history.Count - 1 - _historyIdx];
    }

    private static string LongestCommonPrefix(List<string> items)
    {
        string p = items[0];
        foreach (string s in items)
        {
            int n = Math.Min(val1: p.Length, val2: s.Length);
            int i = 0;
            while (i < n && p[i] == s[i]) i++;
            p = p[..i];
        }

        return p;
    }
}
