namespace PathProvider;

/// <summary>
///     Desktop implementation. Linux follows XDG (env override, spec fallback, both plus
///     <see cref="PathProviderPlugin.AppName" />); Windows uses the roaming/local AppData pair;
///     macOS the ~/Library trio. Downloads has no <see cref="Environment.SpecialFolder" />, so
///     Linux reads <c>user-dirs.dirs</c> and everyone else gets ~/Downloads, which is the
///     platform default anyway.
/// </summary>
internal static class PathProviderDriver
{
    private static string App => PathProviderPlugin.AppName;
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Data() =>
        OperatingSystem.IsLinux()
            ? Under(Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Path.Combine(".local", "share"), App)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Application Support", App)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), App);

    public static string Cache() =>
        OperatingSystem.IsLinux()
            ? Under(Environment.GetEnvironmentVariable("XDG_CACHE_HOME"), ".cache", App)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Caches", App)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App);

    public static string Config() =>
        OperatingSystem.IsLinux()
            ? Under(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), ".config", App)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Preferences", App)
                // Windows has no data/config split; both live in roaming AppData.
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), App);

    public static string Temp() => Path.GetTempPath();

    public static string Documents() =>
        OperatingSystem.IsLinux()
            ? UserDir("XDG_DOCUMENTS_DIR") ?? Path.Combine(Home, "Documents")
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string Downloads() =>
        OperatingSystem.IsLinux()
            ? UserDir("XDG_DOWNLOAD_DIR") ?? Path.Combine(Home, "Downloads")
            : Path.Combine(Home, "Downloads");

    /// <summary>XDG base-dir rule: the env var wins when set, else home + the spec's relative path.</summary>
    internal static string Under(string? xdgValue, string fallbackRelative, string appName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = string.IsNullOrEmpty(xdgValue) ? Path.Combine(home, fallbackRelative) : xdgValue;
        return Path.Combine(root, appName);
    }

    /// <summary>A localized user dir ("Downloads" is "Загрузки" somewhere) from user-dirs.dirs.</summary>
    private static string? UserDir(string key)
    {
        try
        {
            var file = Path.Combine(
                Under(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), ".config", ""),
                "user-dirs.dirs");
            return File.Exists(file) ? ParseUserDir(File.ReadLines(file), key, Home) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The value for <paramref name="key" />, unquoted, $HOME expanded; null when absent.</summary>
    internal static string? ParseUserDir(IEnumerable<string> lines, string key, string home)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + "=", StringComparison.Ordinal)) continue;
            var value = trimmed[(key.Length + 1)..].Trim('"')
                .Replace("$HOME", home, StringComparison.Ordinal);
            return value.Length == 0 ? null : value;
        }

        return null;
    }
}
