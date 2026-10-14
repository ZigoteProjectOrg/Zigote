using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace Wakelock;

/// <summary>
///     Desktop implementation — each OS's own "the user is watching, don't blank the screen":
///     an IOKit power assertion on macOS, a power request on Windows, the freedesktop screensaver
///     inhibit on Linux. All three are owned by this process, so a crash releases them too.
/// </summary>
internal static class WakelockDriver
{
    private const string Reason = "Keeping the screen awake";

    private static uint _macAssertion;
    private static IntPtr _windowsRequest;
    private static DBusConnection? _linuxBus;

    public static bool Supported =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
        || (OperatingSystem.IsLinux() && DBusAddress.Session is not null);

    /// <summary>Called on transitions only — <see cref="WakelockPlugin.Set" /> holds the lock.</summary>
    public static bool Set(bool on)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return on ? MacHold() : MacRelease();
            if (OperatingSystem.IsWindows()) return on ? WindowsHold() : WindowsRelease();
            if (OperatingSystem.IsLinux()) return on ? LinuxHold() : LinuxRelease();
        }
        catch (Exception)
        {
            // A missing framework, an old Windows, no screensaver service: not held, say so.
        }

        return false;
    }

    // ── macOS ───────────────────────────────────────────────────────────────

    private static bool MacHold()
    {
        IntPtr type = CfString("PreventUserIdleDisplaySleep");
        IntPtr name = CfString(Reason);
        try
        {
            return IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out _macAssertion) == 0;
        }
        finally
        {
            CFRelease(type);
            CFRelease(name);
        }
    }

    private static bool MacRelease() => IOPMAssertionRelease(_macAssertion) == 0;

    private static IntPtr CfString(string text)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return CFStringCreateWithCString(IntPtr.Zero, utf8, CfStringEncodingUtf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private const uint AssertionLevelOn = 255;
    private const uint CfStringEncodingUtf8 = 0x08000100;
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint id);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, IntPtr cString, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    // ── Windows ─────────────────────────────────────────────────────────────

    // A power request rather than SetThreadExecutionState: the latter is per-thread and lapses
    // when the calling thread ends, and Enable can be called from any thread.
    private static bool WindowsHold()
    {
        IntPtr text = Marshal.StringToHGlobalUni(Reason);
        try
        {
            var context = new ReasonContext { Version = 0, Flags = PowerRequestContextSimpleString, Reason = text };
            IntPtr request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == new IntPtr(-1)) return false;
            if (!PowerSetRequest(request, PowerRequestDisplayRequired))
            {
                CloseHandle(request);
                return false;
            }

            _windowsRequest = request;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    // Closing the handle clears every request made through it.
    private static bool WindowsRelease()
    {
        bool closed = CloseHandle(_windowsRequest);
        _windowsRequest = IntPtr.Zero;
        return closed;
    }

    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestDisplayRequired = 0;

    // REASON_CONTEXT with the simple-string arm of its union; Flags tells the OS which arm to read.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // ── Linux ───────────────────────────────────────────────────────────────

    // GNOME, KDE and the other org.freedesktop.ScreenSaver services tie an inhibit to the caller's
    // bus name and drop it when that name leaves the bus. So the connection *is* the hold: open
    // one to inhibit, dispose it to release — no cookie to lose, and a crash cannot leak it.
    private static bool LinuxHold()
    {
        if (DBusAddress.Session is not { } address) return false;
        var connection = new DBusConnection(address);
        try
        {
            // Blocking is safe: Tmds completes on the thread pool, never on the caller's context.
            if (!Task.Run(() => InhibitAsync(connection)).Wait(TimeSpan.FromSeconds(2)))
                throw new TimeoutException();
            _linuxBus = connection;
            return true;
        }
        catch (Exception)
        {
            connection.Dispose();
            return false;
        }
    }

    private static bool LinuxRelease()
    {
        _linuxBus?.Dispose();
        _linuxBus = null;
        return true;
    }

    private static async Task InhibitAsync(DBusConnection connection)
    {
        await connection.ConnectAsync();
        MessageBuffer request;
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver",
                "Inhibit", "ss");
            writer.WriteString(AppDomain.CurrentDomain.FriendlyName); // application_name
            writer.WriteString(Reason); // reason_for_inhibit
            request = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        // The cookie is only needed for UnInhibit, which disposing the connection replaces.
        await connection.CallMethodAsync(request, static (Message message, object? _) => message.GetBodyReader().ReadUInt32(), null);
    }
}
