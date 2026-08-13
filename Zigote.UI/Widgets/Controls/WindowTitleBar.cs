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
    private float _leadingW;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public string Title { get; set; } = "";
    public WindowChromeStyle Style { get; set; } = WindowChromeStyle.System;

    /// <summary>
    ///     Optional widget hosted at the left of the bar — the GNOME headerbar pattern, where the
    ///     app menu shares the titlebar row instead of costing a second strip below it. It is
    ///     excluded from the drag region, so its own controls stay clickable.
    /// </summary>
    public Widget? Leading { get; set; }

    /// <summary>Adwaita: also offer minimize/maximize (off = close only, the GNOME dialog look).</summary>
    public bool ShowMinMax { get; set; } = true;

    /// <summary>The window this bar decorates — drag rects and button actions target it.</summary>
    public App? ForWindow { get; set; }

    /// <summary>Close-button action (Adwaita). The host decides what closing means.</summary>
    public Action? OnClose { get; set; }

    private bool HasButtons => Style == WindowChromeStyle.AdwaitaCsd;
    private int ButtonCount => HasButtons ? ShowMinMax ? 3 : 1 : 0;

    /// <summary>Width the window buttons claim on the right — off-limits to drags and the title.</summary>
    private float RightReserve => ButtonCount * (ButtonDiameter + ButtonGap) + RightMargin;

    private float LeftInset => Style == WindowChromeStyle.MacUnified ? TrafficLightInset : 0f;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(float.IsFinite(c.MaxWidth) ? c.MaxWidth : 400f, BarHeight));

        _leadingW = 0f;
        if (Leading is { } lead)
        {
            // Unbounded width so content-sized children report their intrinsic width (a bar that
            // fills whatever it is given would otherwise claim the entire strip); clamped after.
            var room = MathF.Max(0f, _size.Width - LeftInset - RightReserve);
            _leadingW = MathF.Min(
                room,
                lead.Measure(
                    new Constraints(
                        0f,
                        float.PositiveInfinity,
                        BarHeight,
                        BarHeight
                    )
                ).Width
            );
        }

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
        Leading?.Layout(new Offset(origin.X + LeftInset, origin.Y));
        PushDragRegion();
    }

    /// <summary>Declare the draggable strip: the bar minus the traffic-light inset (MacUnified)
    ///     or minus the button cluster (Adwaita). Skipped while unchanged.</summary>
    private void PushDragRegion()
    {
        if (ForWindow is not { } win || Style == WindowChromeStyle.System) return;
        var left = LeftInset + _leadingW;
        var right = RightReserve;
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

        Leading?.Paint(paint);

        if (Title.Length > 0)
        {
            var fs = _theme.FontSizeCaption + 1f;
            var textW = (App.Active ?? ForWindow)?.Engine
                .MeasureText(Title, fs, weight: FontWeight.Bold).Width ?? Title.Length * fs * 0.55f;
            var textY = Bounds.Y + (BarHeight - fs) / 2f + fs * 0.8f;
            // Centered on the bar, then pushed clear of the leading widget / window buttons if the
            // window is too narrow for a true centre — the GNOME headerbar behaviour. Dropped
            // entirely when even the shifted title would collide (a title is not worth clipping).
            var free0 = Bounds.X + LeftInset + _leadingW;
            var free1 = Bounds.Right - RightReserve;
            // Guard before the clamp: once the gap is narrower than the title, free1 - textW falls
            // below free0 and Math.Clamp throws on an inverted range (it did, mid window-resize).
            if (free1 - free0 >= textW)
                paint.AddText(
                    Title,
                    Math.Clamp(Bounds.X + (_size.Width - textW) / 2f, free0, free1 - textW),
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

    /// <summary>Anywhere in the bar except the window buttons and the leading widget drags the
    ///     window.</summary>
    internal bool IsDragPoint(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) && ButtonAt(point) < 0 &&
               Leading?.HitTest(point) is null;
    }

    /// <summary>The leading widget claims its own points; the rest of the bar stays the drag/button
    ///     surface (so the base <see cref="Widget.HitTest" /> result — this bar — is kept).</summary>
    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Leading?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Leading);
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
