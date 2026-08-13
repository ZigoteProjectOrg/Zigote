using Zigote.UI.Host;
using Zigote.UI.Widgets.Controls;

namespace Zigote.UI.Widgets.Menu;

/// <summary>
///     Standard menu roles the OS decorates: <see cref="Window" /> becomes the app's windows menu
///     (macOS appends Minimize/Zoom and the live window list), <see cref="Help" /> becomes the help
///     menu (macOS adds the search field). Ignored by the in-window <see cref="MenuBar" />.
/// </summary>
public enum AppMenuRole
{
    None,
    Window,
    Help,
}

/// <summary>
///     A top-level application menu (e.g. "File", "Edit") and its items. Items reuse
///     <see cref="ContextMenuItem" /> (separators, nested submenus, icons, checkmarks, enabled
///     state) — so the same model drives the in-window <see cref="MenuBar" /> and a native backend
///     such as macOS <c>NSMenu</c>.
/// </summary>
public sealed record AppMenu(
    string Title,
    IReadOnlyList<ContextMenuItem> Items,
    AppMenuRole Role = AppMenuRole.None);

/// <summary>
///     Seam for an OS-native menu bar. The default has no backend, so callers fall back
///     to the in-window <see cref="MenuBar" />. A platform (e.g. macOS) can assign
///     <see cref="Backend" /> to route the same <see cref="AppMenu" /> model to an
///     <c>NSMenu</c> via FFI, with no change to call sites.
/// </summary>
public interface INativeMenuBar
{
    /// <summary>Install the menus into the OS-native menu bar. Returns true if the OS now owns it.</summary>
    bool TryInstall(IReadOnlyList<AppMenu> menus);

    /// <summary>
    ///     Drop app menus from the native bar, leaving the platform's minimal bar (macOS:
    ///     the bare app menu). Used when a host switches to the in-window bar.
    /// </summary>
    void Uninstall();
}

public static class NativeMenuBar
{
    /// <summary>The active native backend, or null when none is available (the common case today).</summary>
    public static INativeMenuBar? Backend { get; set; }

    /// <summary>
    ///     App-specific about screen, invoked by the native app menu's "About …" item (macOS).
    ///     Unset, the item falls back to the platform's standard about panel. Hosts without a
    ///     native bar wire the same action into their own Help menu.
    /// </summary>
    public static Action? AboutRequested { get; set; }

    /// <summary>
    ///     Host preference: false forces the in-window <see cref="MenuBar" /> even when a native
    ///     backend exists (<see cref="TryInstall" /> then declines). Flip it back and reinstall to
    ///     return to the native bar.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    ///     Try to hand the menus to the OS. Returns true if a native bar took ownership
    ///     (so no in-window bar should be shown); false to fall back to <see cref="MenuBar" />.
    /// </summary>
    public static bool TryInstall(IReadOnlyList<AppMenu> menus)
    {
        bool native = Enabled && Backend?.TryInstall(menus) == true;
        // A native bar dispatches its own key equivalents; every bar we draw ourselves needs the
        // shortcuts bound as app accelerators, or they are painted text that does nothing.
        MenuAccelerators.Install(app: App.Active, menus: native ? [] : menus);
        return native;
    }

    /// <summary>Reset the native bar to the platform's minimal state (no-op without a backend).</summary>
    public static void Uninstall() => Backend?.Uninstall();
}
