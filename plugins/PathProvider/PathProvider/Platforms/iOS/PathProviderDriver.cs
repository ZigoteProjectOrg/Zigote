namespace PathProvider;

/// <summary>iOS implementation — everything under the app sandbox; pure BCL, no bindings needed.</summary>
internal static class PathProviderDriver
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Data() => Path.Combine(Home, "Library", "Application Support");

    public static string Cache() => Path.Combine(Home, "Library", "Caches");

    public static string Config() => Path.Combine(Home, "Library", "Preferences");

    public static string Temp() => Path.GetTempPath();

    /// <summary>The sandbox Documents folder — what iTunes/Files may expose when the app opts in.</summary>
    public static string Documents() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string Downloads() => Path.Combine(Documents(), "Downloads");
}
