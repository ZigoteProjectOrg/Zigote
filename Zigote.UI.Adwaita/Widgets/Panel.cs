using Zigote.UI.Host;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

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
///     A floating utility window: a dialog-style surface (dialog background, 12px corners, the
///     popover shadow) under a flat header — a centred bold title and a circular close button, as
///     a libadwaita dialog's header bar has. Drag it by the header; resize it from any edge or
///     corner (the cursor is the affordance, there are no painted handles).
///     Use <see cref="Show" /> / <see cref="Dismiss" /> to add/remove from the overlay stack.
///     <para>Example:</para>
///     <code>
///   var panel = new AdwPanel("Properties", x: 100f, y: 80f, width: 320f, height: 400f)
///   {
///       Content   = myWidget,
///       CanClose  = true,
///       OnClose   = () => Console.WriteLine("closed"),
///   };
///   panel.Show();
/// </code>
/// </summary>
public sealed class AdwPanel : Widget
{
    /// <summary>A compact header bar: the 24px close circle plus the 7px bar padding around it.</summary>
    public const float DefaultTitleHeight = 38f;

    private const float Radius = AdwMetrics.CardRadius;
    private const float CornerGrab = 12f; // px from corner that triggers corner resize
    private const float EdgeGrab = 5f; // px from edge that triggers edge resize

    private readonly App _app;

    // Visual state
    private bool _closeHovered;

    // Screen dimensions — captured from Constraints during Measure, used for clamping
    private bool _compact;

    // Drag/resize tracking
    private ResizeDir _dragDir = ResizeDir.None;
    private Offset _dragOrigin;
    private float _hAt, _wAt, _xAt, _yAt;
    private float _screenH = 768f;
    private float _screenW = 1024f;

    // Floating position and size (absolute screen coords, user-controlled)
    private float _w, _h;

    public AdwPanel(App app, string title,
        float x, float y, float width, float height)
    {
        _app = app;
        Title = title;
        PanelX = x;
        PanelY = y;
        _w = MathF.Max(x: MinWidth, y: width);
        _h = MathF.Max(x: MinHeight, y: height);
    }

    /// <summary>Convenience constructor using <see cref="App.Active" />.</summary>
    public AdwPanel(string title,
        float x, float y, float width, float height)
        : this(
            app: App.Active!,
            title: title,
            x: x,
            y: y,
            width: width,
            height: height
        ) { }

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
        set => _w = Math.Clamp(value: value, min: MinWidth, max: MaxWidth);
    }

    /// <summary>Current panel height. Clamped to [<see cref="MinHeight" />, <see cref="MaxHeight" />].</summary>
    public float PanelHeight
    {
        get => _h;
        set => _h = Math.Clamp(value: value, min: MinHeight, max: MaxHeight);
    }

    public float MinWidth { get; set; } = 120f;
    public float MinHeight { get; set; } = DefaultTitleHeight + 16f;
    public float MaxWidth { get; set; } = 4096f;
    public float MaxHeight { get; set; } = 4096f;

    // ── Derived geometry ──────────────────────────────────────────────────────

    /// <summary>
    ///     Effective title-bar height. It carries the drag strip and the close button, so on a phone —
    ///     where the panel is full-screen and the title bar is the only chrome left — it grows to a
    ///     finger target.
    /// </summary>
    private float TitleH =>
        _compact ? MathF.Max(x: TitleHeight, y: AdwTouchMetrics.MinTarget) : TitleHeight;

    private Rect PanelRect => new(
        x: PanelX,
        y: PanelY,
        width: _w,
        height: _h
    );

    private Rect TitleBarRect => new(
        x: PanelX,
        y: PanelY,
        width: _w,
        height: TitleH
    );

    private Rect ContentRect =>
        new(
            x: PanelX,
            y: PanelY + TitleH,
            width: _w,
            height: MathF.Max(x: 0f, y: _h - TitleH)
        );

    private Rect CloseButtonRect =>
        new(
            x: PanelX + _w - AdwMetrics.HeaderBarPaddingX - AdwMetrics.FrameButtonCircle,
            y: PanelY + ((TitleH - AdwMetrics.FrameButtonCircle) / 2f),
            width: AdwMetrics.FrameButtonCircle,
            height: AdwMetrics.FrameButtonCircle
        );

    // ── Overlay helpers ───────────────────────────────────────────────────────

    /// <summary>Push this panel onto the overlay stack so it appears on top of the UI.</summary>
    public void Show() => _app.PushOverlay(this);

    /// <summary>Remove this panel from the overlay stack.</summary>
    public void Dismiss() => _app.PopOverlay(this);

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        if (float.IsFinite(c.MaxWidth)) _screenW = c.MaxWidth;
        if (float.IsFinite(c.MaxHeight)) _screenH = c.MaxHeight;

        // A free-floating, 8-way-resizable window is a desktop metaphor with no phone equivalent
        // (and its 5pt resize edges are ungrabbable anyway). Present full-screen instead; HitDir
        // then reports no resize zones, leaving the title bar and close button as the only chrome.
        _compact = AdwTouchMetrics.IsCompact;
        if (_compact)
        {
            PanelX = 0f;
            PanelY = 0f;
            _w = _screenW;
            _h = _screenH;
        }

        Content?.Measure(Constraints.Tight(width: _w, height: MathF.Max(x: 0f, y: _h - TitleH)));

        // Full-screen size — we are a non-blocking overlay (HitTest limits capture to panel rect)
        return new Size(width: _screenW, height: _screenH);
    }

    public override void Layout(Offset _)
    {
        Bounds = new Rect(
            x: 0,
            y: 0,
            width: _screenW,
            height: _screenH
        );
        Content?.Layout(new Offset(x: PanelX, y: PanelY + TitleH));
    }

    public override void Paint(PaintList paint)
    {
        // Resolved per paint, not captured: the panel outlives a light/dark or accent switch.
        var theme = _app.Theme;
        var p = AdwPalette.For(theme);
        var pr = PanelRect;

        paint.AddElevation(bounds: pr, radius: Radius, style: AdwMetrics.PopoverShadow);
        paint.AddRect(bounds: pr, color: p.DialogBg, radius: Radius);

        // `headerbar.flat` — no fill of its own; a centred `.heading` title.
        float fs = AdwTypography.Heading.Size;
        float titleW = TextMeasure.Width(text: Title, fontSize: fs, weight: FontWeight.Bold);
        paint.AddText(
            text: Title,
            baselineX: PanelX + ((_w - titleW) / 2f),
            baselineY: TextBaseline.Centered(PanelY, TitleH, fs),
            color: p.DialogFg,
            fontSize: fs,
            fontWeight: FontWeight.Bold
        );

        // `windowcontrols > button > image` — the 24px circle the close glyph sits on.
        if (CanClose)
        {
            var cr = CloseButtonRect;
            paint.AddRect(
                bounds: cr,
                color: _closeHovered ? p.ButtonFillHover : p.ButtonFill,
                radius: cr.Width / 2f
            );
            Icons.Draw(
                paint: paint,
                glyph: MaterialIcons.Close,
                box: cr,
                color: p.DialogFg,
                size: AdwMetrics.IconSize
            );
        }

        if (_h > TitleH)
        {
            paint.AddClipStart(ContentRect);
            Content?.Paint(paint);
            paint.AddClipEnd();
        }

        // The 1px outline that keeps a floating surface defined against a same-coloured window.
        paint.AddBorder(bounds: pr, color: p.PopoverShade, radius: Radius);
    }

    public override Widget? HitTest(Offset point)
    {
        // Non-blocking overlay: only capture within the visual panel rect
        if (!PanelRect.Contains(px: point.X, py: point.Y)) return null;

        var dir = HitDir(point);
        if (dir != ResizeDir.None)
            return this; // resize edge or corner

        if (TitleBarRect.Contains(px: point.X, py: point.Y))
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

        if (TitleBarRect.Contains(px: point.X, py: point.Y))
        {
            // Close button takes priority over dragging
            if (CanClose && CloseButtonRect.Contains(px: point.X, py: point.Y))
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
        float dx = point.X - _dragOrigin.X;
        float dy = point.Y - _dragOrigin.Y;

        switch (_dragDir)
        {
            case ResizeDir.None:
                _closeHovered = CanClose && CloseButtonRect.Contains(px: point.X, py: point.Y);
                break;

            case ResizeDir.TitleDrag:
                PanelX = Math.Clamp(value: _xAt + dx, min: 0f, max: _screenW - _w);
                PanelY = Math.Clamp(value: _yAt + dy, min: 0f, max: _screenH - TitleH);
                // Relayout so the inner Content tracks the panel this frame: Content is only
                // (re)positioned in Layout, and a captured mouse-move otherwise just repaints —
                // leaving the content at its stale absolute Bounds while the chrome moves.
                MarkNeedsLayout();
                break;

            default:
                ApplyResize(dir: _dragDir, dx: dx, dy: dy);
                MarkNeedsLayout(); // resize must reflow + reposition Content too
                break;
        }
    }

    public override void OnPointerUp(Offset _) => _dragDir = ResizeDir.None;

    /// <summary>The press was taken over (pinch, app background): stop moving/resizing.</summary>
    public override void OnPointerCancel() => _dragDir = ResizeDir.None;

    /// <summary>
    ///     Moving or resizing the panel runs in both axes, so a grabbed title bar or edge owns the
    ///     gesture; a press that grabbed neither still leaves it to whatever scrolls behind.
    /// </summary>
    public override bool CanTouchDrag(bool vertical) => _dragDir != ResizeDir.None;

    public override void OnPointerExit() => _closeHovered = false;

    public override IEnumerable<Widget> GetChildren() => Content is not null ? [Content] : [];

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: (int)PanelX,
            value2: (int)PanelY,
            value3: (int)_w,
            value4: (int)_h,
            value5: Content?.DebugStateHash() ?? 0
        );
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private ResizeDir HitDir(Offset p)
    {
        if (_compact) return ResizeDir.None; // full-screen: nothing to resize

        // Corner zones (larger grab area for usability)
        bool inL = p.X < PanelX + CornerGrab;
        bool inR = p.X > PanelX + _w - CornerGrab;
        bool inT = p.Y < PanelY + CornerGrab;
        bool inB = p.Y > PanelY + _h - CornerGrab;

        // Narrow edge zones (must be very close to the edge)
        bool onL = p.X < PanelX + EdgeGrab;
        bool onR = p.X > PanelX + _w - EdgeGrab;
        bool onT = p.Y < PanelY + EdgeGrab;
        bool onB = p.Y > PanelY + _h - EdgeGrab;

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
        float Cw(float w) => Math.Clamp(value: w, min: MinWidth, max: MaxWidth);

        float Ch(float h) => Math.Clamp(value: h, min: MinHeight, max: MaxHeight);

        switch (dir)
        {
            case ResizeDir.E:
                _w = Cw(_wAt + dx);
                break;

            case ResizeDir.W:
            {
                float nw = Cw(_wAt - dx);
                PanelX = _xAt + (_wAt - nw);
                _w = nw;
                break;
            }

            case ResizeDir.S:
                _h = Ch(_hAt + dy);
                break;

            case ResizeDir.N:
            {
                float nh = Ch(_hAt - dy);
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
                float nw = Cw(_wAt - dx);
                PanelX = _xAt + (_wAt - nw);
                _w = nw;
                _h = Ch(_hAt + dy);
                break;
            }

            case ResizeDir.Ne:
            {
                _w = Cw(_wAt + dx);
                float nh = Ch(_hAt - dy);
                PanelY = _yAt + (_hAt - nh);
                _h = nh;
                break;
            }

            case ResizeDir.Nw:
            {
                float nw = Cw(_wAt - dx);
                PanelX = _xAt + (_wAt - nw);
                _w = nw;
                float nh = Ch(_hAt - dy);
                PanelY = _yAt + (_hAt - nh);
                _h = nh;
                break;
            }
        }

        // Keep panel within screen bounds
        PanelX = Math.Clamp(value: PanelX, min: 0f, max: _screenW - _w);
        PanelY = Math.Clamp(value: PanelY, min: 0f, max: _screenH - TitleH);
    }
}
