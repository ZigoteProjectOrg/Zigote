namespace AppSettings;

/// <summary>
///     Desktop implementation — desktops have no per-app settings page to open. GNOME, Windows
///     and macOS all keep app permissions in places that differ per OS version and per
///     packaging (Flatpak vs not), so guessing a URI here would send users somewhere wrong.
///     <para>
///         ponytail: answers false everywhere. Add a deep link per desktop when an app has a
///         desktop permission story that needs one.
///     </para>
/// </summary>
internal static class AppSettingsDriver
{
    public static Task<bool> OpenAsync(SettingsPage page) => Task.FromResult(false);
}
