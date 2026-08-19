using System.Reflection;

namespace PathProvider;

/// <summary>
///     PathProvider — where an app's files belong on this platform. Everything here is a static
///     fact, so there is nothing to register with <c>PluginHost</c>: call from anywhere, any
///     thread. Paths are returned, never created — <c>Directory.CreateDirectory</c> before the
///     first write is the caller's line, and it is idempotent.
///     <para>
///         On desktop the app-private locations (<see cref="Data" />, <see cref="Cache" />,
///         <see cref="Config" />) end in <see cref="AppName" />; on mobile the OS sandbox is
///         already per-app and the name is not used.
///     </para>
/// </summary>
public static class PathProviderPlugin
{
    private static string? _appName;

    /// <summary>
    ///     The folder name for app-private directories on desktop. Defaults to the entry
    ///     assembly's name, lowercased; set it before the first path is asked for.
    /// </summary>
    public static string AppName
    {
        get => _appName ??=
            Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant() ?? "zigote-app";
        set => _appName = value;
    }

    /// <summary>The user's data that must survive: library, documents the app owns, settings-adjacent state.</summary>
    public static string Data(string? sub = null) => Sub(PathProviderDriver.Data(), sub);

    /// <summary>Disposable: deleting it costs a re-fetch or a re-scan, nothing more.</summary>
    public static string Cache(string? sub = null) => Sub(PathProviderDriver.Cache(), sub);

    /// <summary>Configuration, where the platform separates it from data (XDG does; others alias it).</summary>
    public static string Config(string? sub = null) => Sub(PathProviderDriver.Config(), sub);

    /// <summary>Gone whenever the OS pleases.</summary>
    public static string Temp() => PathProviderDriver.Temp();

    /// <summary>The user's documents folder (on mobile, the app's sandboxed one).</summary>
    public static string Documents() => PathProviderDriver.Documents();

    /// <summary>The user's downloads folder (on mobile, the app's sandboxed one).</summary>
    public static string Downloads() => PathProviderDriver.Downloads();

    private static string Sub(string root, string? sub) =>
        sub is null ? root : Path.Combine(root, sub);
}
