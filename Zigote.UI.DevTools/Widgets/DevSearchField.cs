using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;

using Zigote.UI.Host;
namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     A compact filter/search input for devtools panels (the inspector tree search). Focusable,
///     monospace, with a clear ✕ affordance; fires <see cref="OnChanged" /> on every edit. Esc clears
///     the text first, then blurs (<see cref="IKeyboardTrap" /> keeps it from closing the panel).
/// </summary>
public sealed class DevSearchField : RenderWidget, ITextInputClient, IKeyboardTrap
{
    private const float Height = 24f;

    private Size _size;
    private string _text = "";
    private ThemeData _theme = ThemeData.Dark;

    public string Placeholder { get; set; } = "search";
    public Action<string>? OnChanged { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnChanged?.Invoke(_text);
            MarkNeedsPaint();
        }
    }

    public override bool Focusable => true;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
        _size = new Size(w, Height);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
    }

    public override void Paint(PaintList paint)
    {
        var focused = Owner?.FocusedWidget == this;
        paint.AddRect(Bounds, _theme.PanelSunken, 5f);
        paint.AddBorder(Bounds, focused ? _theme.Primary.WithAlpha(0.7f) : _theme.Separator, 5f, 1f);

        var textY = Bounds.Y + Height * 0.7f;
        paint.AddText("⌕", Bounds.X + 7f, textY, _theme.Hint, DevKit.CaptionSize + 1f);

        var tx = Bounds.X + 22f;
        if (_text.Length == 0)
        {
            if (!focused)
                paint.AddText(Placeholder, tx, textY, _theme.Hint.WithAlpha(0.7f), DevKit.CaptionSize);
        }
        else
        {
            paint.AddText(_text, tx, textY, _theme.OnSurface, DevKit.CaptionSize, fontFamily: "code");
            // Clear affordance.
            paint.AddText("✕", Bounds.Right - 16f, textY, _theme.Hint, DevKit.CaptionSize);
        }

        if (focused && BlinkOn())
        {
            var caretX = tx + TextMeasure.Width(_text, DevKit.CaptionSize, fontFamily: "code") + 1f;
            paint.AddRect(new Rect(caretX, Bounds.Y + 5f, 1.5f, Height - 10f), _theme.Primary);
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

    public override MouseCursor? GetCursor(Offset point)
    {
        return point.X < Bounds.Right - 20f || _text.Length == 0 ? MouseCursor.Text : null;
    }

    public override void OnPointerDown(Offset point)
    {
        if (_text.Length > 0 && point.X >= Bounds.Right - 20f)
        {
            Text = "";
            return;
        }

        Owner?.RequestFocus(this);
    }

    public override void OnTextInput(string text)
    {
        var next = _text;
        foreach (var ch in text)
            if (!char.IsControl(ch))
                next += ch;
        Text = next;
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Backspace:
                if (_text.Length > 0) Text = _text[..^1];
                break;
            case KeyCode.Escape:
                if (_text.Length > 0) Text = "";
                else Owner?.RequestFocus(null);
                break;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(_text, Owner?.FocusedWidget == this);
    }
}
