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
    private float RightReserve => (ButtonCount * (ButtonDiameter + ButtonGap)) + RightMargin;

    private float LeftInset => Style == WindowChromeStyle.MacUnified ? TrafficLightInset : 0f;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(
            new Size(width: float.IsFinite(c.MaxWidth) ? c.MaxWidth : 400f, height: BarHeight)
        );

        _leadingW = 0f;
        if (Leading is { } lead)
        {
            // Unbounded width so content-sized children report their intrinsic width (a bar that
            // fills whatever it is given would otherwise claim the entire strip); clamped after.
            float room = MathF.Max(x: 0f, y: _size.Width - LeftInset - RightReserve);
            _leadingW = MathF.Min(
                x: room,
                y: lead.Measure(
                    new Constraints(
                        minWidth: 0f,
                        maxWidth: float.PositiveInfinity,
                        minHeight: BarHeight,
                        maxHeight: BarHeight
                    )
                ).Width
            );
        }

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
        Leading?.Layout(new Offset(x: origin.X + LeftInset, y: origin.Y));
        PushDragRegion();
    }

    /// <summary>
    ///     Declare the draggable strip: the bar minus the traffic-light inset (MacUnified)
    ///     or minus the button cluster (Adwaita). Skipped while unchanged.
    /// </summary>
    private void PushDragRegion()
    {
        if (ForWindow is not { } win || Style == WindowChromeStyle.System) return;
        float left = LeftInset + _leadingW;
        float right = RightReserve;
        var rect = new Rect(
            x: Bounds.X + left,
            y: Bounds.Y,
            width: MathF.Max(x: 0f, y: _size.Width - left - right),
            height: BarHeight
        );
        if (rect.Equals(_lastDragRect)) return;
        _lastDragRect = rect;
        win.Engine.WindowChromeDragRects(
            windowId: win.WindowId,
            quads: [rect.X, rect.Y, rect.Width, rect.Height]
        );
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: _theme.TitleBar);
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.X,
                y: Bounds.Bottom - 1f,
                width: _size.Width,
                height: 1f
            ),
            color: _theme.Separator
        );

        Leading?.Paint(paint);

        if (Title.Length > 0)
        {
            float fs = _theme.FontSizeCaption + 1f;
            float textW = (App.Active ?? ForWindow)?.Engine
                          .MeasureText(text: Title, fontSize: fs, weight: FontWeight.Bold).Width ??
                          Title.Length * fs * 0.55f;
            float textY = Bounds.Y + ((BarHeight - fs) / 2f) + (fs * 0.8f);
            // Centered on the bar, then pushed clear of the leading widget / window buttons if the
            // window is too narrow for a true centre — the GNOME headerbar behaviour. Dropped
            // entirely when even the shifted title would collide (a title is not worth clipping).
            float free0 = Bounds.X + LeftInset + _leadingW;
            float free1 = Bounds.Right - RightReserve;
            // Guard before the clamp: once the gap is narrower than the title, free1 - textW falls
            // below free0 and Math.Clamp throws on an inverted range (it did, mid window-resize).
            if (free1 - free0 >= textW)
            {
                paint.AddText(
                    text: Title,
                    baselineX: Math.Clamp(
                        value: Bounds.X + ((_size.Width - textW) / 2f),
                        min: free0,
                        max: free1 - textW
                    ),
                    baselineY: textY,
                    color: _theme.TextSecondary,
                    fontSize: fs,
                    fontWeight: FontWeight.Bold
                );
            }
        }

        for (int i = 0; i < ButtonCount; i++) PaintButton(paint: paint, index: i);
    }

    private void PaintButton(PaintList paint, int index)
    {
        var rect = ButtonRect(index);
        int kind = KindOf(index);
        if (index == _hover)
            paint.AddRect(bounds: rect, color: _theme.ControlHover, radius: ButtonDiameter / 2f);
        else
            paint.AddRect(bounds: rect, color: _theme.Control, radius: ButtonDiameter / 2f);

        var fg = _theme.OnSurface;
        float cx = rect.X + (rect.Width / 2f);
        float cy = rect.Y + (rect.Height / 2f);
        switch (kind)
        {
            case 2: // close — ✕ glyph
                Icons.Draw(
                    paint: paint,
                    glyph: Icons.Close,
                    box: rect,
                    color: fg,
                    size: 13f
                );
                break;
            case 1: // maximize — small square outline
                paint.AddBorder(
                    bounds: new Rect(
                        x: cx - 4f,
                        y: cy - 4f,
                        width: 8f,
                        height: 8f
                    ),
                    color: fg,
                    radius: 1f,
                    width: 1.4f
                );
                break;
            default: // minimize — low horizontal bar (the Adwaita glyph)
                paint.AddRect(
                    bounds: new Rect(
                        x: cx - 4.5f,
                        y: cy + 2.5f,
                        width: 9f,
                        height: 1.6f
                    ),
                    color: fg
                );
                break;
        }
    }

    /// <summary>Visual order left→right: minimize, maximize, close (GNOME default).</summary>
    private int KindOf(int index) => ShowMinMax ? index : 2;

    private Rect ButtonRect(int index)
    {
        int fromRight = ButtonCount - 1 - index;
        float x = Bounds.Right - RightMargin - ButtonDiameter -
                  (fromRight * (ButtonDiameter + ButtonGap));
        return new Rect(
            x: x,
            y: Bounds.Y + ((BarHeight - ButtonDiameter) / 2f),
            width: ButtonDiameter,
            height: ButtonDiameter
        );
    }

    /// <summary>
    ///     Anywhere in the bar except the window buttons and the leading widget drags the
    ///     window.
    /// </summary>
    internal bool IsDragPoint(Offset point)
    {
        return Bounds.Contains(px: point.X, py: point.Y) && ButtonAt(point) < 0 &&
               Leading?.HitTest(point) is null;
    }

    /// <summary>
    ///     The leading widget claims its own points; the rest of the bar stays the drag/button
    ///     surface (so the base <see cref="Widget.HitTest" /> result — this bar — is kept).
    /// </summary>
    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Leading?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Leading);

    private int ButtonAt(Offset point)
    {
        for (int i = 0; i < ButtonCount; i++)
        {
            var r = ButtonRect(i);
            if (r.Contains(px: point.X, py: point.Y)) return i;
        }

        return -1;
    }

    public override void OnPointerMove(Offset point)
    {
        int hit = ButtonAt(point);
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
        int hit = ButtonAt(point);
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

    public override MouseCursor? GetCursor(Offset point) =>
        ButtonAt(point) >= 0 ? MouseCursor.Pointer : null;

    public override int DebugStateHash() => HashCode.Combine(
        value1: _hover,
        value2: Title,
        value3: (int)Style
    );
}
