using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets;

/// <summary>
///     The widget side of a platform child view: something the OS draws (a webview, a native
///     control) parented into this window and positioned over the engine's surface.
///     Implementations translate the window-space rectangle into whatever their platform's
///     child-positioning call is — MoveWindow, setFrame, XMoveResizeWindow, View.layout.
/// </summary>
public interface INativeView : IDisposable
{
    /// <summary>
    ///     The view's rectangle moved or resized. <paramref name="windowRect" /> is in window
    ///     space, logical pixels; <paramref name="scale" /> converts to physical pixels where the
    ///     platform positions children in those (X11, Android).
    /// </summary>
    void SetBounds(Rect windowRect, float scale);

    /// <summary>Show or hide the native view (the host widget left or re-entered the tree).</summary>
    void SetVisible(bool visible);
}

/// <summary>
///     NativeViewHost — reserves layout space for a platform child view and keeps the view's
///     bounds in sync with its own, every time layout moves it (a resize, a scroll, a tab
///     switch). The native view composites OVER the engine's surface: nothing Zigote draws can
///     appear on top of it — put popups beside it, not above it.
///     <para>
///         The host owns position and visibility only; the view's lifetime belongs to whoever
///         created it (a WebViewController, a plugin) — unmounting hides the view, it does not
///         dispose it, so a re-mounted host (tab switch back) shows the same view again.
///     </para>
/// </summary>
public sealed class NativeViewHost : Widget
{
    private Size _size;
    private INativeView? _view;
    private Rect _lastRect;
    private float _lastScale;
    private bool _shown;

    /// <summary>The platform view this host positions. Swapping hides the old one.</summary>
    public INativeView? View
    {
        get => _view;
        set
        {
            if (ReferenceEquals(_view, value)) return;
            if (_shown) _view?.SetVisible(false);
            _view = value;
            _shown = false;
            _lastRect = default;
            MarkNeedsPaint();
        }
    }

    /// <summary>Measured size under unbounded constraints; bounded axes fill what they are given.</summary>
    public Size PreferredSize { get; set; } = new(width: 640, height: 480);

    /// <summary>Painted under the native view — visible until the view first draws, and wherever
    ///     the view is hidden.</summary>
    public Color Background { get; set; } = Color.Transparent;

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(
            width: float.IsInfinity(c.MaxWidth) ? PreferredSize.Width : c.MaxWidth,
            height: float.IsInfinity(c.MaxHeight) ? PreferredSize.Height : c.MaxHeight
        ));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);
    }

    public override void Paint(PaintList paint)
    {
        if (Background.A > 0) paint.AddRect(bounds: Bounds, color: Background);
        Sync();
    }

    protected override void OnUnmount()
    {
        if (_shown) _view?.SetVisible(false);
        _shown = false;
    }

    /// <summary>
    ///     Push the current bounds to the native view when they moved. Called from Paint: layout
    ///     changes (resize, scroll) damage this widget's region, so a moved host always repaints —
    ///     and an unmoved one costs a rectangle comparison.
    /// </summary>
    private void Sync()
    {
        if (_view is not { } view) return;

        float scale = Host.App.Active?.Engine.Scale ?? 1f;
        if (!_shown)
        {
            _shown = true;
            view.SetVisible(true);
        }

        if (Bounds == _lastRect && scale == _lastScale) return;
        _lastRect = Bounds;
        _lastScale = scale;
        view.SetBounds(windowRect: Bounds, scale: scale);
    }
}
