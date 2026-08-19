using Android.App;

namespace PathProvider;

/// <summary>Android implementation — the app sandbox, so AppName never appears in a path.</summary>
internal static class PathProviderDriver
{
    private static Android.Content.Context Ctx => Application.Context;

    public static string Data() => Ctx.FilesDir!.AbsolutePath;

    public static string Cache() => Ctx.CacheDir!.AbsolutePath;

    /// <summary>Android has no config dir of its own; a subfolder of files/ is the convention.</summary>
    public static string Config() => Path.Combine(Data(), "config");

    public static string Temp() => Path.GetTempPath();

    // External app-specific storage: visible to the user via USB, cleaned up on uninstall, no
    // permission needed. Null on the rare device without shared storage — fall back inside.
    public static string Documents() =>
        Ctx.GetExternalFilesDir(Android.OS.Environment.DirectoryDocuments)?.AbsolutePath
        ?? Path.Combine(Data(), "Documents");

    public static string Downloads() =>
        Ctx.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
        ?? Path.Combine(Data(), "Downloads");
}
