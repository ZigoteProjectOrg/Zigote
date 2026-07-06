using Zigote.UI.Host;

namespace Zigote.UI.Material;

internal enum ResizeDir
{
    None,
    TitleDrag,
    N,
    Ne,
    E,
    Se,
    S,
    Sw,
    W,
    Nw,
}

/// <summary>
///     A floating panel overlay with a draggable title bar and 8-direction resize handles.
///     Use <see cref="Show" /> / <see cref="Dismiss" /> to add/remove from the overlay stack.
///     <para>Example:</para>
///     <code>
///   var panel = new Panel(theme, "Properties", x: 100f, y: 80f, width: 320f, height: 400f)
///   {
///       Content   = myWidget,
///       CanClose  = true,
///       OnClose   = () => Console.WriteLine("closed"),
///   };
///   panel.Show();
/// </code>
/// </summary>
public sealed class Panel : Widget
{
    public const float DefaultTitleHeight = 28f;
    private const float CornerGrab = 12f; // px from corner that triggers corner resize
    private const float EdgeGrab = 5f; // px from edge that triggers edge resize

    private readonly App _app;
    private readonly ThemeData _theme;

    // Visual state
    private bool _closeHovered;

    // Drag/resize tracking
    private ResizeDir _dragDir = ResizeDir.None;
    private Offset _dragOrigin;
    private float _hAt, _wAt, _xAt, _yAt;

    // Screen dimensions — captured from Constraints during Measure, used for clamping
    private float _screenH = 768f;
    private float _screenW = 1024f;

    // Floating position and size (absolute screen coords, user-controlled)
    private float _w, _h;

    public Panel(App app, ThemeData theme, string title,
        float x, float y, float width, float height)
    {
        _app = app;
        _theme = theme;
        Title = title;
        PanelX = x;
        PanelY = y;
        _w = MathF.Max(MinWidth, width);
        _h = MathF.Max(MinHeight, height);
    }

    /// <summary>Convenience constructor using <see cref="App.Active" />.</summary>
    public Panel(ThemeData theme, string title,
        float x, float y, float width, float height)
        : this(
            App.Active!,
            theme,
            title,
            x,
            y,
            width,
            height
        )
    {
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    public string Title { get; set; }
    public Widget? Content { get; set; }
    public bool CanClose { get; set; } = true;
    public Action? OnClose { get; set; }
    public float TitleHeight { get; set; } = DefaultTitleHeight;

    /// <summary>Current floating X position (screen coords).</summary>
    public float PanelX { get; set; }

    /// <summary>Current floating Y position (screen coords).</summary>
    public float PanelY { get; set; }

    /// <summary>Current panel width. Clamped to [<see cref="MinWidth" />, <see cref="MaxWidth" />].</summary>
    public float PanelWidth
    {
        get => _w;
        set => _w = Math.Clamp(value, MinWidth, MaxWidth);
    }

    /// <summary>Current panel height. Clamped to [<see cref="MinHeight" />, <see cref="MaxHeight" />].</summary>
    public float PanelHeight
    {
        get => _h;
        set => _h = Math.Clamp(value, MinHeight, MaxHeight);
    }

    public float MinWidth { get; set; } = 120f;
    public float MinHeight { get; set; } = DefaultTitleHeight + 16f;
    public float MaxWidth { get; set; } = 4096f;
    public float MaxHeight { get; set; } = 4096f;

    // ── Derived geometry ──────────────────────────────────────────────────────

    private Rect PanelRect => new(
        PanelX,
        PanelY,
        _w,
        _h
    );

    private Rect TitleBarRect => new(
        PanelX,
        PanelY,
        _w,
        TitleHeight
    );

    private Rect ContentRect =>
        new(
            PanelX,
            PanelY + TitleHeight,
            _w,
            MathF.Max(0f, _h - TitleHeight)
        );

    private Rect CloseButtonRect =>
        new(
            PanelX + _w - TitleHeight + 4f,
            PanelY + 4f,
            TitleHeight - 8f,
            TitleHeight - 8f
        );

    // ── Overlay helpers ───────────────────────────────────────────────────────

    /// <summary>Push this panel onto the overlay stack so it appears on top of the UI.</summary>
    public void Show()
    {
        _app.PushOverlay(this);
    }

    /// <summary>Remove this panel from the overlay stack.</summary>
    public void Dismiss()
    {
        _app.PopOverlay(this);
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        if (float.IsFinite(c.MaxWidth)) _screenW = c.MaxWidth;
        if (float.IsFinite(c.MaxHeight)) _screenH = c.MaxHeight;

        Content?.Measure(Constraints.Tight(_w, MathF.Max(0f, _h - TitleHeight)));

        // Full-screen size — we are a non-blocking overlay (HitTest limits capture to panel rect)
        return new Size(_screenW, _screenH);
    }

    public override void Layout(Offset _)
    {
        Bounds = new Rect(
            0,
            0,
            _screenW,
            _screenH
        );
        Content?.Layout(new Offset(PanelX, PanelY + TitleHeight));
    }

    public override void Paint(PaintList paint)
    {
        var pr = PanelRect;

        // Drop shadow
        paint.AddShadow(
            pr,
            new Color(
                0f,
                0f,
                0f,
                0.4f
            ),
            6f,
            14f,
            4f
        );

        // Body background
        paint.AddRect(pr, _theme.Surface, 4f);

        // Title bar background (slightly lighter / accent)
        paint.AddRect(TitleBarRect, _theme.SurfaceAlt, 4f);

        // Grip dots (visual drag indicator, centred on left side of title bar)
        PaintGripDots(paint);

        // Title text
        var fs = _theme.FontSizeBody;
        var titleX = PanelX + 22f; // leave room for grip dots
        var titleMaxW = _w - 22f - (CanClose ? TitleHeight : 0f);
        paint.AddText(
            Title,
            titleX,
            PanelY + TitleHeight * 0.8f,
            _theme.OnSurface,
            fs
        );
        _ = titleMaxW; // measured for reference; clipping is handled by the renderer

        // Close button
        if (CanClose)
        {
            var cr = CloseButtonRect;
            if (_closeHovered)
                paint.AddRect(cr, _theme.Error.WithAlpha(0.85f), 3f);
            paint.AddText(
                "×",
                cr.X + (cr.Width - fs * 0.6f) / 2f,
                cr.Y + cr.Height * 0.75f,
                _closeHovered ? _theme.OnPrimary : _theme.OnSurface.WithAlpha(0.45f),
                fs - 2f
            );
        }

        // Content area (clipped)
        if (_h > TitleHeight)
        {
            paint.AddClipStart(ContentRect);
            Content?.Paint(paint);
            paint.AddClipEnd();
        }

        // Outer border
        paint.AddBorder(pr, _theme.Primary.WithAlpha(0.18f), 4f);

        // Resize corner indicators
        PaintResizeCorners(paint);
    }

    private void PaintGripDots(PaintList paint)
    {
        var dotColor = _theme.OnSurface.WithAlpha(0.2f);
        const float dotR = 1.5f;
        const float dotGap = 4f;
        var gx = PanelX + 8f;
        var gy = PanelY + TitleHeight / 2f - dotGap;
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 2; col++)
            paint.AddRect(
                new Rect(
                    gx + col * dotGap - dotR,
                    gy + row * dotGap - dotR,
                    dotR * 2f,
                    dotR * 2f
                ),
                dotColor,
                dotR
            );
    }

    private void PaintResizeCorners(PaintList paint)
    {
        const float sz = 8f;
        var c = _theme.OnSurface.WithAlpha(0.12f);
        // Draw a small L-shaped indicator at SE and SW corners
        var seX = PanelX + _w - sz;
        var seY = PanelY + _h - sz;
        paint.AddRect(
            new Rect(
                seX,
                seY + sz - 2f,
                sz,
                2f
            ),
            c
        );
        paint.AddRect(
            new Rect(
                seX + sz - 2f,
                seY,
                2f,
                sz
            ),
            c
        );

        var swX = PanelX;
        var swY = PanelY + _h - sz;
        paint.AddRect(
            new Rect(
                swX,
                swY + sz - 2f,
                sz,
                2f
            ),
            c
        );
        paint.AddRect(
            new Rect(
                swX,
                swY,
                2f,
                sz
            ),
            c
        );
    }

    public override Widget? HitTest(Offset point)
    {
        // Non-blocking overlay: only capture within the visual panel rect
        if (!PanelRect.Contains(point.X, point.Y)) return null;

        var dir = HitDir(point);
        if (dir != ResizeDir.None)
            return this; // resize edge or corner

        if (TitleBarRect.Contains(point.X, point.Y))
            return this; // title bar drag or close button

        // Delegate to content
        return Content?.HitTest(point) ?? this;
    }

    public override void OnPointerDown(Offset point)
    {
        _dragOrigin = point;
        _xAt = PanelX;
        _yAt = PanelY;
        _wAt = _w;
        _hAt = _h;

        var dir = HitDir(point);
        if (dir != ResizeDir.None)
        {
            _dragDir = dir;
            return;
        }

        if (TitleBarRect.Contains(point.X, point.Y))
        {
            // Close button takes priority over dragging
            if (CanClose && CloseButtonRect.Contains(point.X, point.Y))
            {
                Dismiss();
                OnClose?.Invoke();
                return;
            }

            _dragDir = ResizeDir.TitleDrag;
        }
    }

    public override void OnPointerMove(Offset point)
    {
        var dx = point.X - _dragOrigin.X;
        var dy = point.Y - _dragOrigin.Y;

        switch (_dragDir)
        {
            case ResizeDir.None:
                _closeHovered = CanClose && CloseButtonRect.Contains(point.X, point.Y);
                break;

            case ResizeDir.TitleDrag:
                PanelX = Math.Clamp(_xAt + dx, 0f, _screenW - _w);
                PanelY = Math.Clamp(_yAt + dy, 0f, _screenH - TitleHeight);
                // Relayout so the inner Content tracks the panel this frame: Content is only
                // (re)positioned in Layout, and a captured mouse-move otherwise just repaints —
                // leaving the content at its stale absolute Bounds while the chrome moves.
                MarkNeedsLayout();
                break;

            default:
                ApplyResize(_dragDir, dx, dy);
                MarkNeedsLayout(); // resize must reflow + reposition Content too
                break;
        }
    }

    public override void OnPointerUp(Offset _)
    {
        _dragDir = ResizeDir.None;
    }

    public override void OnPointerExit()
    {
        _closeHovered = false;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return Content is not null ? [Content] : [];
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            (int)PanelX,
            (int)PanelY,
            (int)_w,
            (int)_h,
            Content?.DebugStateHash() ?? 0
        );
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private ResizeDir HitDir(Offset p)
    {
        // Corner zones (larger grab area for usability)
        var inL = p.X < PanelX + CornerGrab;
        var inR = p.X > PanelX + _w - CornerGrab;
        var inT = p.Y < PanelY + CornerGrab;
        var inB = p.Y > PanelY + _h - CornerGrab;

        // Narrow edge zones (must be very close to the edge)
        var onL = p.X < PanelX + EdgeGrab;
        var onR = p.X > PanelX + _w - EdgeGrab;
        var onT = p.Y < PanelY + EdgeGrab;
        var onB = p.Y > PanelY + _h - EdgeGrab;

        // Corners take priority
        if (inT && inL) return ResizeDir.Nw;
        if (inT && inR) return ResizeDir.Ne;
        if (inB && inL) return ResizeDir.Sw;
        if (inB && inR) return ResizeDir.Se;

        // Edges
        if (onT) return ResizeDir.N;
        if (onB) return ResizeDir.S;
        if (onL) return ResizeDir.W;
        if (onR) return ResizeDir.E;

        return ResizeDir.None;
    }

    private void ApplyResize(ResizeDir dir, float dx, float dy)
    {
        float Cw(float w)
        {
            return Math.Clamp(w, MinWidth, MaxWidth);
        }

        float Ch(float h)
        {
            return Math.Clamp(h, MinHeight, MaxHeight);
        }

        switch (dir)
        {
            case ResizeDir.E:
                _w = Cw(_wAt + dx);
                break;

            case ResizeDir.W:
            {
                var nw = Cw(_wAt - dx);
                PanelX = _xAt + (_wAt - nw);
                _w = nw;
                break;
            }

            case ResizeDir.S:
                _h = Ch(_hAt + dy);
                break;

            case ResizeDir.N:
            {
                var nh = Ch(_hAt - dy);
                PanelY = _yAt + (_hAt - nh);
                _h = nh;
                break;
            }

            case ResizeDir.Se:
                _w = Cw(_wAt + dx);
                _h = Ch(_hAt + dy);
                break;

            case ResizeDir.Sw:
            {
                var nw = Cw(_wAt - dx);
                PanelX = _xAt + (_wAt - nw);
                _w = nw;
                _h = Ch(_hAt + dy);
                break;
            }

            case ResizeDir.Ne:
            {
                _w = Cw(_wAt + dx);
                var nh = Ch(_hAt - dy);
                PanelY = _yAt + (_hAt - nh);
                _h = nh;
                break;
            }

            case ResizeDir.Nw:
            {
                var nw = Cw(_wAt - dx);
                PanelX = _xAt + (_wAt - nw);
                _w = nw;
                var nh = Ch(_hAt - dy);
                PanelY = _yAt + (_hAt - nh);
                _h = nh;
                break;
            }
        }

        // Keep panel within screen bounds
        PanelX = Math.Clamp(PanelX, 0f, _screenW - _w);
        PanelY = Math.Clamp(PanelY, 0f, _screenH - TitleHeight);
    }
}