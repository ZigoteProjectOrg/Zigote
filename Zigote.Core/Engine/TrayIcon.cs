using System.Runtime.InteropServices;
using Zigote.Core.Native;

namespace Zigote.Core.Engine;

/// <summary>
///     One entry of a tray menu. <paramref name="Tag" /> is what comes back through the select
///     callback; a zero tag with an empty label is a separator.
/// </summary>
public readonly record struct TrayMenuItem(int Tag, string Label, bool Enabled = true)
{
    public static TrayMenuItem Separator => new(0, "", false);

    public bool IsSeparator => Tag == 0 && Label.Length == 0;
}

/// <summary>
///     A live tray icon. Disposing removes it. Both setters are cheap and idempotent enough to
///     call whenever app state moves.
/// </summary>
public interface ITrayIcon : IDisposable
{
    void SetTooltip(string tooltip);

    void SetMenu(IReadOnlyList<TrayMenuItem> items);
}

/// <summary>
///     A status-area icon, where the OS has one as an API: <c>Shell_NotifyIcon</c> on Windows and
///     <c>NSStatusItem</c> on macOS.
///     <para>
///         Linux is deliberately absent. There the tray is not an API call but a D-Bus service the
///         app has to *be* (StatusNotifierItem plus a com.canonical.dbusmenu menu), which would put
///         a D-Bus client library in the engine's dependency graph for every game that will never
///         show a tray icon. <see cref="Create" /> returns null there, and an app that wants a
///         Linux tray supplies its own <see cref="ITrayIcon" /> — Timbre does, on the connection it
///         already owns for MPRIS.
///     </para>
///     <para>
///         Callbacks arrive on whatever thread the platform delivers them on — the tray's own
///         message loop on Windows, the main thread on macOS. Marshal to your UI thread in the
///         handlers you pass in.
///     </para>
/// </summary>
public static class TrayIcon
{
    /// <summary>
    ///     Put an icon in the status area, or return null where there is nothing to put it in.
    /// </summary>
    /// <param name="tooltip">Hover text; also the accessible name of the item.</param>
    /// <param name="onSelect">A menu item was chosen, by its tag.</param>
    /// <param name="onActivate">The icon itself was clicked (Windows only; on macOS a click opens
    ///     the menu, which is the platform convention).</param>
    public static ITrayIcon? Create(string tooltip, Action<int> onSelect, Action onActivate)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new WindowsTrayIcon(tooltip, onSelect, onActivate);
            if (OperatingSystem.IsMacOS()) return new MacTrayIcon(tooltip, onSelect);
        }
        catch (Exception)
        {
            // A tray is an extra, never a reason to fail startup: a shell with no status area, a
            // missing symbol in an older libzigote, a window class that would not register.
        }

        return null;
    }
}

/// <summary>
///     macOS: an <c>NSStatusItem</c> owned by the native side (src/platform/macos_tray.m). Main
///     thread only, which is where the UI already runs.
/// </summary>
internal sealed unsafe class MacTrayIcon : ITrayIcon
{
    private static Action<int>? _onSelect;

    public MacTrayIcon(string tooltip, Action<int> onSelect)
    {
        _onSelect = onSelect;
        NativeEngine.MacTraySetHandler((nint)(delegate* unmanaged<int, void>)&Trampoline);
        NativeEngine.MacTrayShow(tooltip);
    }

    public void SetTooltip(string tooltip)
    {
        NativeEngine.MacTraySetTooltip(tooltip);
    }

    /// <summary>
    ///     The whole menu goes over as one string — <c>tag\tlabel\tenabled</c> per line, an empty
    ///     line for a separator — because it is only ever replaced wholesale, and one marshalled
    ///     string is less native surface than a builder API that would always be called the same way.
    /// </summary>
    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var spec = string.Join('\n', items.Select(i =>
            i.IsSeparator ? "" : $"{i.Tag}\t{i.Label}\t{(i.Enabled ? 1 : 0)}"));
        NativeEngine.MacTraySetMenu(spec);
    }

    public void Dispose()
    {
        _onSelect = null;
        NativeEngine.MacTrayHide();
    }

    [UnmanagedCallersOnly]
    private static void Trampoline(int tag)
    {
        _onSelect?.Invoke(tag);
    }
}

/// <summary>
///     Windows: <c>Shell_NotifyIcon</c> against a message-only window, which lives on its own
///     thread with its own message loop rather than borrowing the engine's — the tray's popup menu
///     runs a modal loop for as long as it is open, and doing that on the render thread would
///     freeze the app while the menu is up.
/// </summary>
internal sealed class WindowsTrayIcon : ITrayIcon
{
    private const uint WM_TRAY = 0x0400 + 1; // WM_APP + 1
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_CLOSE = 0x0010;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04;

    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    private readonly Action<int> _onSelect;
    private readonly Action _onActivate;
    private readonly WndProcDelegate _wndProc; // rooted: the OS holds a raw pointer to it
    private readonly uint _taskbarCreated;
    private readonly Thread _thread;

    private nint _hwnd;
    private string _tooltip;

    /// <summary>Read from the message-loop thread, written from the UI thread.</summary>
    private volatile IReadOnlyList<TrayMenuItem> _items = [];

    public WindowsTrayIcon(string tooltip, Action<int> onSelect, Action onActivate)
    {
        _onSelect = onSelect;
        _onActivate = onActivate;
        _tooltip = tooltip;
        _wndProc = WndProc;
        // Explorer re-broadcasts this after it restarts, and every icon that does not listen for it
        // is simply gone until the app is restarted.
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        // Not disposed: the pump thread sets it, and a wait that timed out would otherwise dispose
        // the event out from under a Set() that is still coming — an unhandled exception on a
        // thread nobody is watching.
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Pump(ready)) { IsBackground = true, Name = "tray" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        if (_hwnd == 0) throw new InvalidOperationException("tray window did not come up");
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (_hwnd != 0) Shell_NotifyIconW(NIM_MODIFY, Data(NIF_TIP));
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        _items = items;
    }

    public void Dispose()
    {
        if (_hwnd == 0) return;
        // Posted, not called: the window belongs to the pump thread, and only that thread may
        // destroy it. The pump removes the icon on WM_DESTROY and falls out of its loop.
        PostMessageW(_hwnd, WM_CLOSE, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private void Pump(ManualResetEventSlim ready)
    {
        var instance = GetModuleHandleW(null);
        var className = "ZigoteTray+" + Environment.ProcessId;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            ready.Set();
            return;
        }

        // HWND_MESSAGE (-3): a window that exists only to receive messages — never shown, never in
        // the taskbar, not something alt-tab can land on.
        _hwnd = CreateWindowExW(0, className, className, 0, 0, 0, 0, 0, -3, 0, instance, 0);
        if (_hwnd != 0) Shell_NotifyIconW(NIM_ADD, Data(NIF_MESSAGE | NIF_ICON | NIF_TIP));
        ready.Set();
        if (_hwnd == 0) return;

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated)
        {
            Shell_NotifyIconW(NIM_ADD, Data(NIF_MESSAGE | NIF_ICON | NIF_TIP));
            return 0;
        }

        switch (msg)
        {
            case WM_TRAY:
                switch ((uint)lParam)
                {
                    case WM_LBUTTONUP:
                        _onActivate();
                        break;
                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU:
                        ShowMenu(hwnd);
                        break;
                }

                return 0;

            case WM_DESTROY:
                Shell_NotifyIconW(NIM_DELETE, Data(0));
                _hwnd = 0;
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            foreach (var item in _items)
                AppendMenuW(
                    menu,
                    item.IsSeparator ? MF_SEPARATOR : MF_STRING | (item.Enabled ? 0 : MF_GRAYED),
                    (nuint)item.Tag,
                    item.IsSeparator ? null : item.Label
                );

            GetCursorPos(out var point);
            // Documented dance: without the foreground window the menu never closes when the user
            // clicks away, and without the trailing post the *next* menu sometimes fails to open.
            SetForegroundWindow(hwnd);
            var chosen = TrackPopupMenu(
                menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.X, point.Y, 0, hwnd, 0);
            PostMessageW(hwnd, 0x0000, 0, 0);
            if (chosen > 0) _onSelect(chosen);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NOTIFYICONDATAW Data(uint flags)
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            // The app's own icon, straight out of its executable's resources (the SDK stamps it
            // there from ApplicationIcon), so there is no icon file to find at runtime and no way
            // for the tray to disagree with the taskbar.
            hIcon = AppIcon(),
            // szTip is 128 chars *including* the terminator, and ByValTStr silently truncating is
            // fine — a tooltip is not data.
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip
        };
    }

    private static nint AppIcon()
    {
        var exe = Environment.ProcessPath;
        var icon = exe is null ? 0 : ExtractIconW(GetModuleHandleW(null), exe, 0);
        // ExtractIcon returns 1 for "no icons in that file", which is not a handle.
        return icon is 0 or 1 ? LoadIconW(0, 32512 /* IDI_APPLICATION */) : icon;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance,
        nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved,
        nint hwnd, nint rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIconW(nint instance, string exeFile, uint index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
