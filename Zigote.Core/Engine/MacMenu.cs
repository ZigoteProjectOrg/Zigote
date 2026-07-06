using System.Runtime.InteropServices;
using Zigote.Core.Native;

namespace Zigote.Core.Engine;

/// <summary>
///     Public, managed entry point to the native macOS menu bar (NSMenu). Builds the
///     application's global menu from the top down and routes item clicks back through
///     a single tag-based callback. No-op-unsafe to call off macOS — guard with
///     <see cref="IsSupported" />.
/// </summary>
public static unsafe class MacMenu
{
    /// <summary>
    ///     Reserved tag the native app menu's About item dispatches with (regular item tags start
    ///     at 1). Must match ZIG_MENU_ABOUT_TAG in macos_menu.m.
    /// </summary>
    public const int AboutTag = -1;

    private static Action<int>? _onSelect;

    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Register the click handler. The callback receives the selected item's tag.</summary>
    public static void SetHandler(Action<int> onSelect)
    {
        _onSelect = onSelect;
        NativeEngine.MacMenuSetHandler((nint)(delegate* unmanaged<int, void>)&Trampoline);
    }

    [UnmanagedCallersOnly]
    private static void Trampoline(int tag)
    {
        _onSelect?.Invoke(tag);
    }

    /// <summary>Start a new menu bar with the standard application menu (About/Hide/Quit).</summary>
    public static void Reset(string appName)
    {
        NativeEngine.MacMenuReset(appName);
    }

    /// <summary>Append a top-level menu; returns an opaque handle for adding items.</summary>
    public static nint AddMenu(string title)
    {
        return NativeEngine.MacMenuAddMenu(title);
    }

    /// <summary>Append a submenu under <paramref name="parent" />; returns the child handle.</summary>
    public static nint AddSubmenu(nint parent, string title)
    {
        return NativeEngine.MacMenuAddSubmenu(parent, title);
    }

    /// <param name="sfSymbol">Optional SF Symbol name shown as the item image (macOS 11+).</param>
    /// <param name="checked">Leading checkmark state.</param>
    public static void AddItem(nint parent, string title, int tag, string key, uint modMask,
        bool enabled, string? sfSymbol = null, bool @checked = false)
    {
        NativeEngine.MacMenuAddItem(
            parent,
            title,
            tag,
            key,
            modMask,
            enabled ? 1 : 0,
            sfSymbol ?? "",
            @checked ? 1 : 0
        );
    }

    /// <summary>Mark a menu as a standard AppKit role: 1 = windows menu, 2 = help menu.</summary>
    public static void SetMenuRole(nint menu, int role)
    {
        NativeEngine.MacMenuSetMenuRole(menu, role);
    }

    public static void AddSeparator(nint parent)
    {
        NativeEngine.MacMenuAddSeparator(parent);
    }

    /// <summary>Install the assembled menu as the application's main menu.</summary>
    public static void Commit()
    {
        NativeEngine.MacMenuCommit();
    }

    /// <summary>Show the standard Cocoa about panel (the About fallback when the app has no
    ///     custom about screen).</summary>
    public static void ShowStandardAbout()
    {
        NativeEngine.MacMenuShowStandardAbout();
    }
}