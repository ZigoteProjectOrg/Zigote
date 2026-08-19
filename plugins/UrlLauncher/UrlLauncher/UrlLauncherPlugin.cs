namespace UrlLauncher;

/// <summary>
///     UrlLauncher — hand a URL to whatever handles it: the browser, the mail client, another
///     app's deep link. Static, nothing to register with <c>PluginHost</c>.
/// </summary>
public static class UrlLauncherPlugin
{
    /// <summary>Fire and forget. The common call — a click on a link never awaits a browser.</summary>
    public static void Open(string url) => _ = TryOpenAsync(url);

    /// <summary>
    ///     False when the URL is blank or nothing on this device handles it. No exception ever
    ///     escapes: a desktop with no http handler is a strange desktop, not a crash.
    /// </summary>
    public static Task<bool> TryOpenAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult(false);
        return UrlLauncherDriver.OpenAsync(url);
    }
}
