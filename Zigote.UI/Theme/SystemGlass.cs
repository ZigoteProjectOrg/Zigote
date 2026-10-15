using System.Runtime.InteropServices;
using System.Text;

namespace Zigote.UI.Theme;

/// <summary>
///     The OS's own Liquid Glass preferences, so glass in a Zigote app matches the system's:
///     <list type="bullet">
///         <item>
///             <see cref="Tinted" /> — the Appearance → Liquid Glass "Clear ↔ Tinted" control
///             (macOS 26.1+, stored as <c>NSGlassTintAmount</c>). Tinted glass is more opaque and
///             higher-contrast; the regular panes follow it, clear panes over media do not.
///         </item>
///         <item>
///             <see cref="ReduceTransparency" /> — Accessibility → Display → Reduce transparency
///             (macOS and iOS). Every pane goes near-opaque frosted, the way system glass does.
///         </item>
///     </list>
///     <see cref="ZigoteApp" /> calls <see cref="Refresh" /> about once a second and repaints when
///     anything changed. Both are settable (tests, an in-app override); the next
///     <see cref="Refresh" /> on a supported OS puts the system's value back.
/// </summary>
public static class SystemGlass
{
    /// <summary>0 = Clear (Apple's default), 1 = fully Tinted.</summary>
    public static float Tinted { get; set; }

    public static bool ReduceTransparency { get; set; }

    /// <summary>Re-read the OS settings. True when either value changed.</summary>
    public static bool Refresh()
    {
        float tinted = Tinted;
        bool reduce = ReduceTransparency;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                tinted = ReadGlobalFloat("NSGlassTintAmount") ?? 0f;
                reduce = MacReduceTransparency();
            }
            else if (OperatingSystem.IsIOS())
            {
                // ponytail: iOS exposes no public read of its Clear/Tinted switch; reduce
                // transparency is public. Read the mac key too in case the global domain carries it.
                tinted = ReadGlobalFloat("NSGlassTintAmount") ?? 0f;
                reduce = UIAccessibilityIsReduceTransparencyEnabled();
            }
            else
            {
                return false;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        tinted = Math.Clamp(value: tinted, min: 0f, max: 1f);
        if (tinted == Tinted && reduce == ReduceTransparency) return false;
        Tinted = tinted;
        ReduceTransparency = reduce;
        return true;
    }

    // ── CoreFoundation preferences ─────────────────────────────────────────────

    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string UIKit = "/System/Library/Frameworks/UIKit.framework/UIKit";
    private const uint Utf8 = 0x08000100;
    private const int CFNumberDouble = 13;

    /// <summary>A global-domain default as a float — AppKit writes this one as a string.</summary>
    private static float? ReadGlobalFloat(string key)
    {
        nint app = CFStringCreateWithCString(alloc: 0, s: "kCFPreferencesAnyApplication", encoding: Utf8);
        nint k = CFStringCreateWithCString(alloc: 0, s: key, encoding: Utf8);
        try
        {
            // Drops the process's cached copy, so a change in System Settings is seen.
            CFPreferencesAppSynchronize(app);
            nint v = CFPreferencesCopyAppValue(key: k, app: app);
            if (v == 0) return null;
            try
            {
                if (CFGetTypeID(v) == CFNumberGetTypeID())
                    return CFNumberGetValue(number: v, type: CFNumberDouble, value: out double d) ? (float)d : null;
                if (CFGetTypeID(v) != CFStringGetTypeID()) return null;
                var buf = new byte[64];
                if (!CFStringGetCString(s: v, buffer: buf, size: buf.Length, encoding: Utf8)) return null;
                string text = Encoding.UTF8.GetString(buf).TrimEnd('\0');
                return float.TryParse(s: text, style: System.Globalization.NumberStyles.Float,
                    provider: System.Globalization.CultureInfo.InvariantCulture, result: out float f)
                    ? f
                    : null;
            }
            finally
            {
                CFRelease(v);
            }
        }
        finally
        {
            CFRelease(k);
            CFRelease(app);
        }
    }

    /// <summary>
    ///     <c>NSWorkspace.sharedWorkspace.accessibilityDisplayShouldReduceTransparency</c> — the
    ///     universalaccess defaults domain is not reliably readable outside System Settings.
    /// </summary>
    private static bool MacReduceTransparency()
    {
        nint workspace = objc_msgSend(
            receiver: objc_getClass("NSWorkspace"),
            selector: sel_registerName("sharedWorkspace")
        );
        return workspace != 0 && objc_msgSend_bool(
            receiver: workspace,
            selector: sel_registerName("accessibilityDisplayShouldReduceTransparency")
        ) != 0;
    }

    [DllImport(CF)] private static extern nint CFStringCreateWithCString(nint alloc, string s, uint encoding);
    [DllImport(CF)] private static extern nint CFPreferencesCopyAppValue(nint key, nint app);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CFPreferencesAppSynchronize(nint app);
    [DllImport(CF)] private static extern nuint CFGetTypeID(nint cf);
    [DllImport(CF)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CF)] private static extern nuint CFStringGetTypeID();
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CFNumberGetValue(nint number, int type, out double value);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CFStringGetCString(nint s, byte[] buffer, nint size, uint encoding);
    [DllImport(CF)] private static extern void CFRelease(nint cf);

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector);
    // BOOL is bool on arm64 and signed char on x86_64 — one byte either way.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte objc_msgSend_bool(nint receiver, nint selector);

    [DllImport(UIKit)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool UIAccessibilityIsReduceTransparencyEnabled();
}
