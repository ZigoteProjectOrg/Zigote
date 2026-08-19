using Zigote.Core.Engine;

namespace Tray;

/// <summary>
///     One call that puts an icon in the status area on whatever desktop this is, or answers
///     null where there is nowhere to put one. Windows and macOS ride the engine's
///     <see cref="TrayIcon" /> (Shell_NotifyIcon / NSStatusItem); Linux is the
///     <see cref="StatusNotifierItem" /> D-Bus service in this package.
///     <para>
///         <b>Threading:</b> callbacks arrive on a D-Bus thread (Linux) or the tray's own
///         message loop (Windows) — post to your UI thread in the handlers you pass in.
///     </para>
///     <para>
///         Everything degrades to null, never an exception: a desktop with no status area —
///         plain GNOME without the AppIndicator extension is the common one — is a normal
///         desktop, not an error.
///     </para>
/// </summary>
public static class ZigoteTray
{
    /// <summary>
    ///     Create the platform's tray icon. Null = no status area here; the app should carry on
    ///     without one.
    /// </summary>
    /// <param name="appId">
    ///     The desktop entry / theme icon name (e.g. <c>dev.zigote.MyApp</c>) — on Linux the
    ///     shell looks the icon up in the hicolor theme under this name.
    /// </param>
    /// <param name="title">The item's title, shown in tooltips and accessibility surfaces.</param>
    /// <param name="tooltip">Initial hover text; update later with <see cref="ITrayIcon.SetTooltip" />.</param>
    /// <param name="onSelect">A menu item was chosen, by its <see cref="TrayMenuItem.Tag" />.</param>
    /// <param name="onActivate">The icon itself was clicked (Windows and Linux; macOS opens the menu).</param>
    public static async Task<ITrayIcon?> CreateAsync(string appId, string title, string tooltip,
        Action<int> onSelect, Action onActivate)
    {
        if (TrayIcon.Create(tooltip, onSelect, onActivate) is { } icon) return icon;
        if (!OperatingSystem.IsLinux()) return null;

        var sni = new StatusNotifierItem(appId, title, tooltip, onSelect, onActivate);
        await sni.StartAsync();
        if (sni.Running) return sni;
        // Matches TrayIcon.Create's contract: null means "no tray here". The reason lives in
        // sni.LastError, but a failed registration holds nothing worth keeping alive.
        sni.Dispose();
        return null;
    }
}
