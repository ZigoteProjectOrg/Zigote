using Zigote.Core.Native;

namespace Zigote.Core.Events;

public enum MouseButton
{
    Left,
    Right,
    Middle,
}

[Flags]
public enum Modifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Cmd = 8, // ⌘ on macOS, Super/Win elsewhere — the platform "command" modifier
}

public static class ModifiersExtensions
{
    /// <summary>
    ///     The platform "command" modifier for shortcuts (copy/paste/select-all):
    ///     <see cref="Modifiers.Cmd" />
    ///     on macOS, <see cref="Modifiers.Ctrl" /> elsewhere. Accepting either keeps shortcuts working
    ///     across platforms and matches the ⌘-labelled hints shown in menus.
    /// </summary>
    public static bool HasCommand(this Modifiers m)
    {
        return m.HasFlag(Modifiers.Ctrl) || m.HasFlag(Modifiers.Cmd);
    }
}

public abstract class InputEvent
{
    /// <summary>
    ///     SDL window id this event belongs to; 0 = unknown, treated as the main window. Hosts with
    ///     secondary OS windows route events to the right widget tree by comparing against
    ///     <c>ZigoteEngine.MainWindowId</c> / <c>NativeWindow.Id</c>.
    /// </summary>
    public uint WindowId { get; internal set; }
}

public sealed class MouseMoveEvent : InputEvent
{
    public MouseMoveEvent()
    {
    }

    public MouseMoveEvent(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; private set; }
    public float Y { get; private set; }

    /// <summary>
    ///     Motion since the previous move event, straight from the OS.
    ///     <para>
    ///         This is the only motion available while the pointer is captured
    ///         (<see cref="Engine.ZigoteEngine.RelativeMouseMode" />): the cursor is held in place, so
    ///         <see cref="X" />/<see cref="Y" /> stop changing and differencing them yields nothing. It
    ///         is also more accurate than a difference when the cursor is free, because it is not
    ///         quantized to the pixel grid or clamped at the window edge.
    ///     </para>
    /// </summary>
    public float RelativeX { get; private set; }

    public float RelativeY { get; private set; }

    // Overwrite in place so PollEventsInto can reuse a pooled instance (see EventPool) instead of
    // allocating a fresh MouseMoveEvent for every move — mouse-moves flood faster than the frame rate
    // during a drag, so this is the dominant input-rate allocation.
    internal void Reuse(float x, float y, uint windowId, float relativeX, float relativeY)
    {
        X = x;
        Y = y;
        RelativeX = relativeX;
        RelativeY = relativeY;
        WindowId = windowId;
    }
}

public sealed class MouseDownEvent(float x, float y, MouseButton button) : InputEvent
{
    public float X { get; } = x;
    public float Y { get; } = y;
    public MouseButton Button { get; } = button;
}

public sealed class MouseUpEvent(float x, float y, MouseButton button) : InputEvent
{
    public float X { get; } = x;
    public float Y { get; } = y;
    public MouseButton Button { get; } = button;
}

public sealed class ScrollEvent : InputEvent
{
    public ScrollEvent()
    {
    }

    public ScrollEvent(float x, float y, float scrollX, float scrollY)
    {
        X = x;
        Y = y;
        ScrollX = scrollX;
        ScrollY = scrollY;
    }

    public float X { get; private set; }
    public float Y { get; private set; }
    public float ScrollX { get; private set; }
    public float ScrollY { get; private set; }

    // Reused across polls like MouseMoveEvent (momentum scroll also fires faster than frame rate).
    internal void Reuse(float x, float y, float scrollX, float scrollY, uint windowId)
    {
        X = x;
        Y = y;
        ScrollX = scrollX;
        ScrollY = scrollY;
        WindowId = windowId;
    }
}

public sealed class KeyEvent(
    bool down,
    char keyChar,
    uint scancode,
    Modifiers modifiers,
    bool repeat = false)
    : InputEvent
{
    public bool Down { get; } = down;
    public char KeyChar { get; } = keyChar;
    public uint Scancode { get; } = scancode;
    public Modifiers Modifiers { get; } = modifiers;

    /// <summary>
    ///     True when this key-down is an OS auto-repeat from a held key, not the initial press. Always
    ///     false for key-up. Shortcut handlers that should fire once per press (toggles) gate on this.
    /// </summary>
    public bool Repeat { get; } = repeat;

    /// <summary>The physical (layout-independent) key, decoded from <see cref="Scancode" />.</summary>
    public KeyCode Key => (KeyCode)Scancode;
}

public sealed class TextInputEvent(string text) : InputEvent
{
    public string Text { get; } = text;
}

/// <summary>
///     Transient IME pre-edit text. The text is displayed at the caret but is not committed to the
///     document until a subsequent <see cref="TextInputEvent" /> arrives.
/// </summary>
public sealed class TextCompositionEvent(string text, int selectionStart, int selectionLength)
    : InputEvent
{
    public string Text { get; } = text;
    public int SelectionStart { get; } = selectionStart;
    public int SelectionLength { get; } = selectionLength;
}

public sealed class ResizeEvent(uint width, uint height) : InputEvent
{
    public uint Width { get; } = width;
    public uint Height { get; } = height;
}

public sealed class QuitEvent : InputEvent;

/// <summary>
///     The OS window gained or lost keyboard focus. Hosts use it to throttle background rendering;
///     it is not routed to widgets (widget-level focus is <see cref="Zigote.Core" />-agnostic App
///     state).
/// </summary>
public sealed class WindowFocusEvent(bool focused) : InputEvent
{
    public bool Focused { get; } = focused;
}

/// <summary>
///     The user asked to close a window (titlebar ✕). <see cref="InputEvent.WindowId" /> says which.
///     The host decides: quit for the main window, destroy for a secondary one.
/// </summary>
public sealed class WindowCloseEvent : InputEvent;

/// <summary>The OS light/dark appearance reported by SDL.</summary>
public enum SystemTheme
{
    Unknown = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>The OS switched its light/dark appearance while the app was running.</summary>
public sealed class SystemThemeEvent(SystemTheme theme) : InputEvent
{
    public SystemTheme Theme { get; } = theme;
}

/// <summary>
///     The host's scroll orientation ("natural scroll" OS setting), as reported by SDL's wheel
///     direction flag. <see cref="Unknown" /> until the user first scrolls, since SDL exposes it
///     only per wheel event rather than as a queryable setting.
/// </summary>
public enum ScrollOrientation
{
    Unknown = 0,
    Normal = 1,
    Flipped = 2,
}

// ── OS → app drag-and-drop ──────────────────────────────────────────────────────
//
// An external drop of N items arrives as: DropBeginEvent, then N DropFileEvent / DropTextEvent, then
// DropCompleteEvent. DropPositionEvent fires while a drag hovers over the window (for drag-over
// feedback). The App aggregates a begin…complete run into a single OnDrop delivery; individual widgets
// don't see the raw sequence.

/// <summary>A drag payload entered the window; the item events that follow belong to this drop.</summary>
public sealed class DropBeginEvent : InputEvent;

/// <summary>One file (of possibly many) was dropped on the window. <see cref="X" />/<see cref="Y" /> are
/// window-relative, in the same logical-pixel space as pointer events.</summary>
public sealed class DropFileEvent(string path, float x, float y) : InputEvent
{
    public string Path { get; } = path;
    public float X { get; } = x;
    public float Y { get; } = y;
}

/// <summary>Plain text was dropped on the window (e.g. a selection dragged from another app).</summary>
public sealed class DropTextEvent(string text, float x, float y) : InputEvent
{
    public string Text { get; } = text;
    public float X { get; } = x;
    public float Y { get; } = y;
}

/// <summary>The pointer moved over the window while carrying a drag payload. Used to highlight the
/// drop target the pointer is currently over; no data is available until the drop completes.</summary>
public sealed class DropPositionEvent(float x, float y) : InputEvent
{
    public float X { get; } = x;
    public float Y { get; } = y;
}

/// <summary>The drag payload was released (or left the window); the accumulated item run is complete.</summary>
public sealed class DropCompleteEvent(float x, float y) : InputEvent
{
    public float X { get; } = x;
    public float Y { get; } = y;
}

/// <summary>
///     The window moved to a different monitor, or its monitor's refresh rate / content scale
///     changed. The refresh rate and HiDPI scale the app is pacing and laying out against may now be
///     stale — dragging a window from a 60 Hz panel to a 144 Hz one is the common case. Handled by
///     <c>App</c>, which re-queries both and re-paces its frame loop.
/// </summary>
public sealed class DisplayChangedEvent : InputEvent;

// ── Touchscreen ────────────────────────────────────────────────────────────────
//
// Fingers on a DIRECT touch device (a screen — trackpads stay cursor/wheel input). A contact
// lives as TouchDown, zero or more TouchMoves, then exactly one of TouchUp (lifted normally)
// or TouchCancel (OS gesture takeover, palm rejection, app backgrounded — abandon, don't
// commit, whatever the finger was doing). The engine never synthesizes mouse events from
// touches (or vice versa); routing touch into the pointer pipeline is the App's job.

/// <summary>
///     Base of the touchscreen finger events. <see cref="X" />/<see cref="Y" /> are
///     window-relative in the same logical space as mouse events. <see cref="Finger" /> is a
///     compact contact id (0–9): stable from down to up/cancel, reused by later contacts, so
///     per-pointer state keyed on it must be cleared when the contact ends.
/// </summary>
public abstract class TouchEvent : InputEvent
{
    public float X { get; private protected set; }
    public float Y { get; private protected set; }
    public int Finger { get; private protected set; }

    /// <summary>Contact pressure 0..1; 1 on hardware that doesn't report pressure.</summary>
    public float Pressure { get; private protected set; }
}

/// <summary>A finger touched the screen.</summary>
public sealed class TouchDownEvent : TouchEvent
{
    public TouchDownEvent(float x, float y, int finger, float pressure)
    {
        X = x;
        Y = y;
        Finger = finger;
        Pressure = pressure;
    }
}

/// <summary>A touching finger moved.</summary>
public sealed class TouchMoveEvent : TouchEvent
{
    public TouchMoveEvent()
    {
    }

    public TouchMoveEvent(float x, float y, int finger, float pressure)
    {
        X = x;
        Y = y;
        Finger = finger;
        Pressure = pressure;
    }

    // Overwrite in place so PollEventsInto can reuse a pooled instance (see EventPool): a
    // dragging finger floods moves faster than the frame rate, exactly like mouse moves.
    internal void Reuse(float x, float y, int finger, float pressure, uint windowId)
    {
        X = x;
        Y = y;
        Finger = finger;
        Pressure = pressure;
        WindowId = windowId;
    }
}

/// <summary>A finger lifted off the screen normally.</summary>
public sealed class TouchUpEvent : TouchEvent
{
    public TouchUpEvent(float x, float y, int finger, float pressure)
    {
        X = x;
        Y = y;
        Finger = finger;
        Pressure = pressure;
    }
}

/// <summary>
///     The contact ended abnormally — the OS took the gesture (system edge swipe, palm
///     rejection) or the app is being backgrounded. Handlers must abandon the interaction
///     (no tap, no click, no drop) rather than treat this as an up at the last position.
/// </summary>
public sealed class TouchCancelEvent : TouchEvent
{
    public TouchCancelEvent(float x, float y, int finger, float pressure)
    {
        X = x;
        Y = y;
        Finger = finger;
        Pressure = pressure;
    }
}

// ── Mobile app lifecycle ───────────────────────────────────────────────────────

/// <summary>
///     The OS is about to suspend the app (SDL will_enter_background). Delivered BEFORE the
///     suspension: the host must stop rendering/presenting before the next frame — on iOS,
///     GPU work while backgrounded is a watchdog kill — and should flush persistent state,
///     since no further code may run once suspended. Never fires on desktop.
/// </summary>
public sealed class AppBackgroundEvent : InputEvent;

/// <summary>The app returned to the foreground (SDL did_enter_foreground); rendering may resume.</summary>
public sealed class AppForegroundEvent : InputEvent;

/// <summary>The OS is low on memory and asks the app to drop caches it can rebuild.</summary>
public sealed class LowMemoryEvent : InputEvent;

/// <summary>
///     The mobile on-screen keyboard appeared or disappeared. The platform backends already
///     keep the focused text area visible (the view pans against the
///     <c>SetTextInputArea</c> rect the text widgets report), so this is for layout/scroll
///     polish — e.g. insetting content that the keyboard would cover. Never fires on desktop.
/// </summary>
public sealed class ScreenKeyboardEvent(bool shown) : InputEvent
{
    public bool Shown { get; } = shown;
}

/// <summary>
///     Converts raw <see cref="ZgEvent" /> structs into typed <see cref="InputEvent" />
///     instances.
/// </summary>
internal static class EventDecoder
{
    /// <param name="textBase">
    ///     Base of the poll text buffer (<c>zigote_poll_text_ptr</c>) for the just-polled batch; text
    ///     events slice their UTF-8 payload out of it. Passed as <see cref="nint" /> so the caller may
    ///     be an iterator (which cannot hold a raw pointer across <c>yield</c>).
    /// </param>
    public static unsafe InputEvent? Decode(in ZgEvent e, nint textBase)
    {
        var evt = DecodeKind(e, textBase);
        if (evt is not null) evt.WindowId = e.WindowId;
        return evt;
    }

    private static unsafe InputEvent? DecodeKind(in ZgEvent e, nint textBase)
    {
        return (EventKind)e.Kind switch {
            EventKind.MouseMove => new MouseMoveEvent(e.X, e.Y),
            EventKind.MouseDown => new MouseDownEvent(e.X, e.Y, DecodeButton(e.Button)),
            EventKind.MouseUp => new MouseUpEvent(e.X, e.Y, DecodeButton(e.Button)),
            EventKind.Scroll => new ScrollEvent(
                e.X,
                e.Y,
                e.ScrollX,
                e.ScrollY
            ),
            // For key events the (mouse-unused) Button byte carries the SDL auto-repeat flag.
            EventKind.KeyDown => new KeyEvent(
                true,
                (char)e.KeyChar,
                e.KeyScancode,
                DecodeMods(e.Modifiers),
                e.Button != 0
            ),
            EventKind.KeyUp => new KeyEvent(
                false,
                (char)e.KeyChar,
                e.KeyScancode,
                DecodeMods(e.Modifiers),
                e.Button != 0
            ),
            EventKind.TextInput => new TextInputEvent(e.GetTextInput((byte*)textBase)),
            EventKind.TextEditing => new TextCompositionEvent(
                e.GetTextInput((byte*)textBase),
                (int)e.CompositionStart,
                (int)e.CompositionLength
            ),
            EventKind.Resize => new ResizeEvent(e.ResizeW, e.ResizeH),
            // The (mouse-unused) Button byte carries gained (1) / lost (0).
            EventKind.WindowFocus => new WindowFocusEvent(e.Button != 0),
            EventKind.WindowClose => new WindowCloseEvent(),
            // The (mouse-unused) Button byte carries the theme value (0 unknown / 1 light / 2 dark).
            EventKind.SystemTheme => new SystemThemeEvent((SystemTheme)e.Button),
            EventKind.DropBegin => new DropBeginEvent(),
            EventKind.DropFile => new DropFileEvent(e.GetTextInput((byte*)textBase), e.X, e.Y),
            EventKind.DropText => new DropTextEvent(e.GetTextInput((byte*)textBase), e.X, e.Y),
            EventKind.DropPosition => new DropPositionEvent(e.X, e.Y),
            EventKind.DropComplete => new DropCompleteEvent(e.X, e.Y),
            EventKind.TouchDown => new TouchDownEvent(
                e.X,
                e.Y,
                (int)e.TouchFinger,
                e.TouchPressure
            ),
            EventKind.TouchMove => new TouchMoveEvent(
                e.X,
                e.Y,
                (int)e.TouchFinger,
                e.TouchPressure
            ),
            EventKind.TouchUp => new TouchUpEvent(
                e.X,
                e.Y,
                (int)e.TouchFinger,
                e.TouchPressure
            ),
            EventKind.TouchCancel => new TouchCancelEvent(
                e.X,
                e.Y,
                (int)e.TouchFinger,
                e.TouchPressure
            ),
            EventKind.AppBackground => new AppBackgroundEvent(),
            EventKind.AppForeground => new AppForegroundEvent(),
            EventKind.LowMemory => new LowMemoryEvent(),
            EventKind.ScreenKeyboardShown => new ScreenKeyboardEvent(true),
            EventKind.ScreenKeyboardHidden => new ScreenKeyboardEvent(false),
            EventKind.DisplayChanged => new DisplayChangedEvent(),
            EventKind.Quit => new QuitEvent(),
            _ => null,
        };
    }

    private static MouseButton DecodeButton(byte b)
    {
        return b switch {
            1 => MouseButton.Right,
            2 => MouseButton.Middle,
            _ => MouseButton.Left,
        };
    }

    private static Modifiers DecodeMods(byte m)
    {
        var mods = (ModifierKeys)m;
        return ((mods & ModifierKeys.Shift) != 0 ? Modifiers.Shift : Modifiers.None) |
               ((mods & ModifierKeys.Ctrl) != 0 ? Modifiers.Ctrl : Modifiers.None) |
               ((mods & ModifierKeys.Alt) != 0 ? Modifiers.Alt : Modifiers.None) |
               ((mods & ModifierKeys.Cmd) != 0 ? Modifiers.Cmd : Modifiers.None);
    }
}