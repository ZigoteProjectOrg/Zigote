using System.Runtime.InteropServices;

namespace Zigote.Core.Native;

/// <summary>
///     P/Invoke bindings for the native macOS menu bar (implemented in
///     Zigote.Engine/src/platform/macos_menu.m). These are hand-written rather than
///     generated because they are exported from an Objective-C source, not Zig.
///     Only valid to call on macOS.
/// </summary>
internal static partial class NativeEngine
{
    [LibraryImport(Lib, EntryPoint = "zigote_macmenu_set_handler")]
    internal static partial void MacMenuSetHandler(nint callback);

    [LibraryImport(
        Lib,
        EntryPoint = "zigote_macmenu_reset",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial void MacMenuReset(string appName);

    [LibraryImport(
        Lib,
        EntryPoint = "zigote_macmenu_add_menu",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial nint MacMenuAddMenu(string title);

    [LibraryImport(
        Lib,
        EntryPoint = "zigote_macmenu_add_submenu",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial nint MacMenuAddSubmenu(nint parent, string title);

    [LibraryImport(
        Lib,
        EntryPoint = "zigote_macmenu_add_item",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial void MacMenuAddItem(nint parent, string title, int tag, string key,
        uint modMask,
        int enabled, string sfSymbol, int checkedState);

    [LibraryImport(Lib, EntryPoint = "zigote_macmenu_set_menu_role")]
    internal static partial void MacMenuSetMenuRole(nint menu, int role);

    [LibraryImport(Lib, EntryPoint = "zigote_macmenu_add_separator")]
    internal static partial void MacMenuAddSeparator(nint parent);

    [LibraryImport(Lib, EntryPoint = "zigote_macmenu_commit")]
    internal static partial void MacMenuCommit();

    [LibraryImport(Lib, EntryPoint = "zigote_macmenu_show_standard_about")]
    internal static partial void MacMenuShowStandardAbout();

    /// <summary>
    ///     Begin a native macOS drag-OUT session (implemented in
    ///     Zigote.Engine/src/platform/macos_drag.m). Hand-written for the same reason as the menu
    ///     bindings — the symbol comes from an Objective-C source, and it exists only in the macOS
    ///     build, so callers must guard on <see cref="OperatingSystem.IsMacOS" />. Returns 1 if a
    ///     dragging session started, 0 otherwise. <paramref name="filesNl" /> is newline-separated
    ///     absolute paths; either argument may be empty.
    /// </summary>
    [LibraryImport(
        Lib,
        EntryPoint = "zigote_macdrag_begin",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial int MacDragBegin(string text, string filesNl);
}