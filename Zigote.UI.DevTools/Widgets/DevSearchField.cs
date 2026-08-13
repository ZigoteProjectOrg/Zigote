using Zigote.Core;
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
///     A compact filter/search input for devtools panels (the inspector tree search). Focusable,
///     monospace, with a clear affordance; fires <see cref="OnChanged" /> on every edit. Esc clears
///     the text first, then blurs (<see cref="IKeyboardTrap" /> keeps it from closing the panel).
/// </summary>
public sealed class DevSearchField : Widget, ITextInputClient, IKeyboardTrap
{
    private float _height = AdwMetrics.EntryHeight;

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
        paint.AddRect(bounds: Bounds, color: p.ViewBg, radius: AdwMetrics.ControlRadius);
        paint.AddBorder(
            bounds: Bounds,
            color: focused ? _theme.Accent : p.Border,
            radius: AdwMetrics.ControlRadius,
            width: focused ? _theme.FocusRingWidth : 1f
        );

        float textY = Bounds.Y + (_height * 0.64f);
        Icons.DrawAt(
            paint: paint,
            glyph: Icons.Search,
            x: Bounds.X + 8f,
            baselineY: textY,
            color: p.DimLabel,
            size: AdwMetrics.IconSize
        );

        float tx = Bounds.X + 8f + AdwMetrics.IconSize + Spacing.Sm;
        if (_text.Length == 0)
        {
            if (!focused)
            {
                paint.AddText(
                    text: Placeholder,
                    baselineX: tx,
                    baselineY: textY,
                    color: _theme.Hint.WithAlpha(0.7f),
                    fontSize: DevKit.CaptionSize
                );
            }
        }
        else
        {
            paint.AddText(
                text: _text,
                baselineX: tx,
                baselineY: textY,
                color: _theme.OnSurface,
                fontSize: DevKit.CaptionSize,
                fontFamily: "code"
            );
            // Clear affordance.
            Icons.DrawAt(
                paint: paint,
                glyph: Icons.Close,
                x: Bounds.Right - AdwMetrics.IconSize - 8f,
                baselineY: textY,
                color: p.DimLabel,
                size: AdwMetrics.IconSize
            );
        }

        if (focused && BlinkOn())
        {
            float caretX = tx + TextMeasure.Width(
                text: _text,
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

    private static bool BlinkOn()
    {
        float time = App.Active?.Time ?? 0f;
        return (int)(time * 2f) % 2 == 0;
    }

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? this : null;

    public override MouseCursor? GetCursor(Offset point)
    {
        return point.X < Bounds.Right - AdwMetrics.IconSize - 12f || _text.Length == 0
            ? MouseCursor.Text
            : null;
    }

    public override void OnPointerDown(Offset point)
    {
        if (_text.Length > 0 && point.X >= Bounds.Right - AdwMetrics.IconSize - 12f)
        {
            Text = "";
            return;
        }

        Owner?.RequestFocus(this);
    }

    public override void OnTextInput(string text)
    {
        string next = _text;
        foreach (char ch in text)
        {
            if (!char.IsControl(ch))
                next += ch;
        }

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

    public override int DebugStateHash() => HashCode.Combine(
        value1: _text,
        value2: Owner?.FocusedWidget == this
    );
}
