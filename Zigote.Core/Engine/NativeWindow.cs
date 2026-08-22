using Zigote.Core.Native;
using Zigote.Core.Paint;

namespace Zigote.Core.Engine;

/// <summary>
///     A secondary UI-only OS window created via <see cref="ZigoteEngine.CreateWindow" />. It runs
///     the 2D paint path on its own wgpu surface (sharing the engine's device/queue); the 3D scene
///     and render graph stay bound to the main window. Per frame the host submits a paint list and
///     calls <see cref="Render" />; input arrives through the engine's normal poll loop tagged with
///     this window's <see cref="Id" />. Dispose to close the window.
/// </summary>
public sealed unsafe class NativeWindow : IDisposable
{
    private readonly ZigoteEngine _engine;
    private PaintList.PinCallback? _submitCb;
    private PaintList.PinCallback? _submitOverlayCb;
    private ulong _window;

    internal NativeWindow(ZigoteEngine engine, ulong window, uint id)
    {
        _engine = engine;
        _window = window;
        Id = id;
        RefreshSize();
    }

    /// <summary>SDL window id — matches <see cref="Events.InputEvent.WindowId" /> for routing.</summary>
    public uint Id { get; }

    /// <summary>False after the window is disposed (or the engine shut down underneath it).</summary>
    public bool IsAlive => _window != 0;

    /// <summary>Current surface width in physical pixels.</summary>
    public uint PixelWidth { get; private set; }

    /// <summary>Current surface height in physical pixels.</summary>
    public uint PixelHeight { get; private set; }

    /// <summary>HiDPI scale factor of the display the window is on.</summary>
    public float Scale { get; private set; } = 1f;

    /// <summary>Window width in logical pixels.</summary>
    public float LogicalWidth => Scale > 0 ? PixelWidth / Scale : PixelWidth;

    /// <summary>Window height in logical pixels.</summary>
    public float LogicalHeight => Scale > 0 ? PixelHeight / Scale : PixelHeight;

    public void Dispose()
    {
        if (_window == 0) return;
        NativeEngine.WindowDestroy(windowHandle: _window);
        _window = 0;
    }

    /// <summary>Re-read the pixel size + display scale (call after a resize event for this window).</summary>
    public void RefreshSize()
    {
        if (_window == 0) return;
        NativeEngine.WindowPixelSize(
            windowHandle: _window,
            outW: out uint w,
            outH: out uint h
        );
        PixelWidth = w;
        PixelHeight = h;
        Scale = NativeEngine.WindowScale(windowHandle: _window);
        if (Scale <= 0f) Scale = 1f;
    }

    /// <summary>Submit this window's paint commands for the next <see cref="Render" />.</summary>
    public void SubmitPaint(PaintList paint)
    {
        if (_window == 0) return;
        // Secondary windows go through the SAME frame API as the main window now, selected by
        // the window handle — they used to have their own submit/render trio.
        _submitCb ??= (ptr, count) =>
            _ = NativeEngine.SubmitPaintCommands(
                window: _window,
                stream: ptr,
                len: count
            );
        paint.PinAndCall(_submitCb);
    }

    /// <summary>Submit this window's overlay-layer paint commands (popups, tooltips).</summary>
    public void SubmitOverlay(PaintList paint)
    {
        if (_window == 0) return;
        _submitOverlayCb ??= (ptr, count) =>
            _ = NativeEngine.SubmitOverlayCommands(
                window: _window,
                stream: ptr,
                len: count
            );
        paint.PinAndCall(_submitOverlayCb);
    }

    /// <summary>Render the submitted paint lists and present this window's surface.</summary>
    public void Render()
    {
        if (_window == 0) return;
        // FrameBegin carries the scale this window rasterises at; FrameEnd draws and presents.
        NativeEngine.FrameBegin(
            window: _window,
            sceneW: 0,
            sceneH: 0,
            scale: Scale,
            deltaTime: 0f
        );
        NativeEngine.FrameEnd(window: _window);
    }

    /// <summary>Raise the window above others and give it input focus.</summary>
    public void Raise()
    {
        if (_window == 0) return;
        NativeEngine.WindowRaise(windowHandle: _window);
    }

    /// <summary>Screen position of the window's top-left corner (logical desktop coordinates).</summary>
    public (int X, int Y) GetPosition()
    {
        if (_window == 0) return (0, 0);
        NativeEngine.WindowPosition(
            windowHandle: _window,
            outX: out int x,
            outY: out int y
        );
        return (x, y);
    }

    /// <summary>Move the window to an absolute screen position (logical desktop coordinates).</summary>
    public void SetPosition(int x, int y)
    {
        if (_window == 0) return;
        NativeEngine.WindowSetPosition(
            windowHandle: _window,
            x: x,
            y: y
        );
    }

    public void SetTitle(string title)
    {
        if (_window == 0) return;
        byte[] titleBytes = ZigoteEngine.Utf8Z(title);
        fixed (byte* tp = titleBytes)
            NativeEngine.WindowSetTitle(windowHandle: _window, title: tp);
    }

    /// <summary>Enable SDL3 text-input mode for this window (text-field focus).</summary>
    public void StartTextInput()
    {
        if (_window == 0) return;
        NativeEngine.WindowStartTextInput(windowHandle: _window);
    }

    /// <summary>Disable SDL3 text-input mode for this window.</summary>
    public void StopTextInput()
    {
        if (_window == 0) return;
        NativeEngine.WindowStopTextInput(windowHandle: _window);
    }

    /// <summary>Position the platform IME candidate window next to the active caret.</summary>
    public void SetTextInputArea(Rect area, int cursor = 0)
    {
        if (_window == 0) return;
        NativeEngine.WindowSetTextInputArea(
            windowHandle: _window,
            x: (int)MathF.Round(area.X),
            y: (int)MathF.Round(area.Y),
            w: Math.Max(val1: 1, val2: (int)MathF.Round(area.Width)),
            h: Math.Max(val1: 1, val2: (int)MathF.Round(area.Height)),
            cursor: cursor
        );
    }
}
