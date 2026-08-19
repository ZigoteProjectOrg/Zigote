using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;

namespace WebView;

/// <summary>
///     WebView — the widget half. Two presentation modes, chosen by the platform backend:
///     an overlay native view positioned over the engine surface (Windows, X11, Android, iOS —
///     nothing Zigote draws can appear on top of it), or an engine texture the page renders
///     into (Linux-Wayland — composites like any widget, input forwarded into the page).
/// </summary>
public sealed class WebView : ComposedWidget
{
    private readonly WebViewController _controller;
    private readonly NativeViewHost _host = new();
    private WebTextureSurface? _surface;

    public WebView(WebViewController controller) => _controller = controller;

    /// <summary>Painted until the browser view first draws (and where there is no backend).</summary>
    public Color Background
    {
        get => _host.Background;
        set
        {
            _host.Background = value;
            if (_surface is { } s) s.Background = value;
        }
    }

    protected override void OnMount()
    {
        if (Zigote.UI.Host.App.Active is { } app)
            _host.View = _controller.EnsureAttached(app.Engine.GetNativeParent());
        if (_controller.TextureBackend is { } texture)
            _surface = new WebTextureSurface(texture) { Background = _host.Background };
    }

    protected override void OnUnmount()
    {
        // Hide only — the controller owns the view's lifetime, so a re-mount (tab switch back)
        // shows the same page again. NativeViewHost's own unmount does the hiding.
        _host.View = null;
    }

    protected override Widget Build(BuildContext context) => (Widget?)_surface ?? _host;
}

/// <summary>
///     The texture-mode surface: paints the backend's latest frame and feeds its own input
///     events back into the page. The page surface follows this widget's laid-out size.
/// </summary>
internal sealed class WebTextureSurface : Widget
{
    private readonly ITextureWebViewBackend _backend;
    private Size _size;
    private (float W, float H, float Scale) _pushed;
    private Offset _lastPointer;

    public WebTextureSurface(ITextureWebViewBackend backend) => _backend = backend;

    public Color Background { get; set; }

    public override bool Focusable => true;

    protected override void OnMount()
    {
        _backend.FrameArrived += MarkNeedsPaint;
        _backend.SetDisplayed(true);
    }

    /// <summary>
    ///     Unmounted is a background tab: the page keeps running (the controller owns its
    ///     lifetime, and a tab must keep its timers, sockets and JS alive), but nobody can see it,
    ///     so it stops paying for the frame conversion and the texture upload.
    /// </summary>
    protected override void OnUnmount()
    {
        _backend.FrameArrived -= MarkNeedsPaint;
        _backend.SetDisplayed(false);
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(
            width: float.IsInfinity(c.MaxWidth) ? 640 : c.MaxWidth,
            height: float.IsInfinity(c.MaxHeight) ? 480 : c.MaxHeight
        ));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);
        float scale = Scale();
        if (_pushed == (_size.Width, _size.Height, scale)) return;
        _pushed = (_size.Width, _size.Height, scale);
        _backend.SetSurfaceSize(logicalWidth: _size.Width, logicalHeight: _size.Height, scale: scale);
    }

    public override void Paint(PaintList paint)
    {
        ulong texture = _backend.AcquireTexture();
        if (texture == 0)
        {
            if (Background.A > 0) paint.AddRect(bounds: Bounds, color: Background);
            return;
        }

        var (tw, th) = _backend.TextureSize;
        paint.AddImage(
            bounds: Bounds,
            pixelWidth: (int)tw,
            pixelHeight: (int)th,
            pixels: null,
            cacheKey: texture
        );
    }

    // Input: window space → page surface space (widget-local × scale).

    public override void OnPointerDown(Offset point)
    {
        _lastPointer = point;
        var (x, y) = Local(point);
        _backend.PointerDown(x, y);
    }

    public override void OnPointerUp(Offset point)
    {
        var (x, y) = Local(point);
        _backend.PointerUp(x, y);
    }

    public override void OnPointerMove(Offset point)
    {
        _lastPointer = point;
        var (x, y) = Local(point);
        _backend.PointerMove(x, y);
    }

    public override void OnScroll(float dx, float dy)
    {
        var (x, y) = Local(_lastPointer);
        _backend.Scroll(dx: dx, dy: dy, x: x, y: y);
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        _backend.Key(ch: keyChar, scancode: scancode, down: down, mods: mods);
    }

    public override void OnTextInput(string text) => _backend.Text(text);

    protected override void OnFocusChanged(bool focused) => _backend.SetPageFocus(focused);

    private (float X, float Y) Local(Offset point)
    {
        float scale = Scale();
        return ((point.X - Bounds.X) * scale, (point.Y - Bounds.Y) * scale);
    }

    private static float Scale() => Zigote.UI.Host.App.Active?.Engine.Scale ?? 1f;
}
