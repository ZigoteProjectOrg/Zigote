namespace AppSettings;

/// <summary>Which settings page to land on.</summary>
public enum SettingsPage
{
    /// <summary>This app's own page — permissions, storage, notifications all hang off it.</summary>
    App,

    /// <summary>This app's notification settings.</summary>
    Notifications,

    /// <summary>The system location page — where the user turns location services back on.</summary>
    Location
}

/// <summary>
///     AppSettings — send the user to the OS settings page for this app, the <c>app_settings</c>
///     slot from the plugin roadmap. The other half of a permanently denied permission: the app
///     cannot ask again, so it offers a button that opens Settings instead.
///     <para>
///         Static, nothing to register with <c>PluginHost</c>. False means there is no such page
///         here — every desktop, and pages a given OS does not expose.
///     </para>
/// </summary>
public static class AppSettingsPlugin
{
    /// <summary>Open the settings page. Never throws; false means nothing opened.</summary>
    public static Task<bool> OpenAsync(SettingsPage page = SettingsPage.App)
        => AppSettingsDriver.OpenAsync(page);
}
