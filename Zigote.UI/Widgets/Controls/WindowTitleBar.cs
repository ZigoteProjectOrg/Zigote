using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     In-app titlebar strip for chrome-styled OS windows (see <see cref="WindowChrome" /> —
///     normally injected app-wide by <see cref="App.ApplyWindowChrome" /> above every window's
///     root). MacUnified: an inset for the native traffic lights plus the centered title — the
///     whole strip is the drag region. AdwaitaCsd: GNOME-style minimize/maximize/close circle
///     buttons on the right (drawn in-app, wired to the native window through
///     <c>zigote_window_chrome_*</c>), drag region excluding them. The drag rects are re-declared
///     from <see cref="Layout" />, so window resizes keep them correct automatically.
/// </summary>
public sealed class WindowTitleBar : Widget
{
    public const float BarHeight = 34f;
    private const float TrafficLightInset = 78f; // native close/min/zoom cluster + margin
    private const float ButtonDiameter = 24f;
    private const float ButtonGap = 8f;
    private const float RightMargin = 10f;

    private int _hover = -1; // 0 minimize · 1 maximize · 2 close
    private Rect _lastDragRect;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public string Title { get; set; } = "";
    public WindowChromeStyle Style { get; set; } = WindowChromeStyle.System;

    /// <summary>Adwaita: also offer minimize/maximize (off = close only, the GNOME dialog look).</summary>
    public bool ShowMinMax { get; set; } = true;

    /// <summary>The window this bar decorates — drag rects and button actions target it.</summary>
    public App? ForWindow { get; set; }

    /// <summary>Close-button action (Adwaita). The host decides what closing means.</summary>
    public Action? OnClose { get; set; }

    private bool HasButtons => Style == WindowChromeStyle.AdwaitaCsd;
    private int ButtonCount => HasButtons ? ShowMinMax ? 3 : 1 : 0;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(float.IsFinite(c.MaxWidth) ? c.MaxWidth : 400f, BarHeight));
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
        PushDragRegion();
    }

    /// <summary>Declare the draggable strip: the bar minus the traffic-light inset (MacUnified)
    ///     or minus the button cluster (Adwaita). Skipped while unchanged.</summary>
    private void PushDragRegion()
    {
        if (ForWindow is not { } win || Style == WindowChromeStyle.System) return;
        var left = Style == WindowChromeStyle.MacUnified ? TrafficLightInset : 0f;
        var right = ButtonCount * (ButtonDiameter + ButtonGap) + RightMargin;
        var rect = new Rect(
            Bounds.X + left,
            Bounds.Y,
            MathF.Max(0f, _size.Width - left - right),
            BarHeight
        );
        if (rect.Equals(_lastDragRect)) return;
        _lastDragRect = rect;
        win.Engine.WindowChromeDragRects(
            win.WindowId,
            [rect.X, rect.Y, rect.Width, rect.Height]
        );
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, _theme.TitleBar);
        paint.AddRect(
            new Rect(
                Bounds.X,
                Bounds.Bottom - 1f,
                _size.Width,
                1f
            ),
            _theme.Separator
        );

        if (Title.Length > 0)
        {
            var fs = _theme.FontSizeCaption + 1f;
            var textW = (App.Active ?? ForWindow)?.Engine
                .MeasureText(Title, fs, weight: FontWeight.Bold).Width ?? Title.Length * fs * 0.55f;
            var textY = Bounds.Y + (BarHeight - fs) / 2f + fs * 0.8f;
            paint.AddText(
                Title,
                Bounds.X + (_size.Width - textW) / 2f,
                textY,
                _theme.TextSecondary,
                fs,
                fontWeight: FontWeight.Bold
            );
        }

        for (var i = 0; i < ButtonCount; i++) PaintButton(paint, i);
    }

    private void PaintButton(PaintList paint, int index)
    {
        var rect = ButtonRect(index);
        var kind = KindOf(index);
        if (index == _hover)
            paint.AddRect(rect, _theme.ControlHover, ButtonDiameter / 2f);
        else
            paint.AddRect(rect, _theme.Control, ButtonDiameter / 2f);

        var fg = _theme.OnSurface;
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        switch (kind)
        {
            case 2: // close — ✕ glyph
                Icons.Draw(
                    paint,
                    Icons.Close,
                    rect,
                    fg,
                    13f
                );
                break;
            case 1: // maximize — small square outline
                paint.AddBorder(
                    new Rect(
                        cx - 4f,
                        cy - 4f,
                        8f,
                        8f
                    ),
                    fg,
                    1f,
                    1.4f
                );
                break;
            default: // minimize — low horizontal bar (the Adwaita glyph)
                paint.AddRect(
                    new Rect(
                        cx - 4.5f,
                        cy + 2.5f,
                        9f,
                        1.6f
                    ),
                    fg
                );
                break;
        }
    }

    /// <summary>Visual order left→right: minimize, maximize, close (GNOME default).</summary>
    private int KindOf(int index)
    {
        return ShowMinMax ? index : 2;
    }

    private Rect ButtonRect(int index)
    {
        var fromRight = ButtonCount - 1 - index;
        var x = Bounds.Right - RightMargin - ButtonDiameter -
                fromRight * (ButtonDiameter + ButtonGap);
        return new Rect(
            x,
            Bounds.Y + (BarHeight - ButtonDiameter) / 2f,
            ButtonDiameter,
            ButtonDiameter
        );
    }

    /// <summary>Anywhere in the bar except the window buttons drags the window.</summary>
    internal bool IsDragPoint(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) && ButtonAt(point) < 0;
    }

    private int ButtonAt(Offset point)
    {
        for (var i = 0; i < ButtonCount; i++)
        {
            var r = ButtonRect(i);
            if (r.Contains(point.X, point.Y)) return i;
        }

        return -1;
    }

    public override void OnPointerMove(Offset point)
    {
        var hit = ButtonAt(point);
        if (hit == _hover) return;
        _hover = hit;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (_hover == -1) return;
        _hover = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        var hit = ButtonAt(point);
        if (hit < 0) return;
        switch (KindOf(hit))
        {
            case 2:
                OnClose?.Invoke();
                break;
            case 1:
                if (ForWindow is { } win) win.Engine.WindowChromeToggleMaximize(win.WindowId);
                break;
            default:
                if (ForWindow is { } w) w.Engine.WindowChromeMinimize(w.WindowId);
                break;
        }
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        return ButtonAt(point) >= 0 ? MouseCursor.Pointer : null;
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(_hover, Title, (int)Style);
    }
}
