using Android.App;
using Android.Content;
using Android.Database;
using Android.Provider;
using Android.Webkit;
using AndroidUri = Android.Net.Uri;

namespace Share;

/// <summary>
///     Android implementation — <c>ACTION_SEND</c> / <c>ACTION_SEND_MULTIPLE</c> through the
///     system chooser. Files cannot travel as paths (a <c>file://</c> URI throws
///     FileUriExposedException since API 24), so each one is copied into the app's cache and
///     handed over as a <c>content://</c> URI served by <see cref="ShareFileProvider" />.
///     <para>
///         ponytail: the answer is always <see cref="ShareStatus.Success" /> once the chooser
///         starts — learning which app was picked (or that the user backed out) needs a
///         BroadcastReceiver plus an IntentSender. Add it when an app needs to react to the
///         choice rather than to the share.
///     </para>
/// </summary>
internal static class ShareDriver
{
    public static Task<ShareStatus> ShareAsync(string? text, string? subject, string[] paths)
    {
        try
        {
            var context = Application.Context;
            AndroidUri[] uris = paths.Select(p => ShareFileProvider.Publish(context, p))
                .Where(u => u is not null).Select(u => u!).ToArray();

            var intent = new Intent(uris.Length > 1 ? Intent.ActionSendMultiple : Intent.ActionSend);
            intent.SetType(MimeType(paths, uris.Length));
            if (!string.IsNullOrWhiteSpace(text)) intent.PutExtra(Intent.ExtraText, text);
            if (!string.IsNullOrWhiteSpace(subject)) intent.PutExtra(Intent.ExtraSubject, subject);

            if (uris.Length == 1) intent.PutExtra(Intent.ExtraStream, uris[0]);
            else if (uris.Length > 1)
                intent.PutParcelableArrayListExtra(
                    Intent.ExtraStream, uris.Cast<Android.OS.IParcelable>().ToList());

            if (uris.Length > 0)
            {
                // ClipData is what carries the read grant to the app the user picks — the extras
                // alone are not enough for the chooser to pass permission along.
                var clip = ClipData.NewRawUri((string?)null, uris[0]);
                for (int i = 1; i < uris.Length; i++) clip?.AddItem(new ClipData.Item(uris[i]));
                intent.ClipData = clip;
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            }

            var chooser = Intent.CreateChooser(intent, (string?)null)!
                // Started from outside an activity context, so it needs its own task.
                .AddFlags(ActivityFlags.NewTask);
            context.StartActivity(chooser);
            return Task.FromResult(ShareStatus.Success);
        }
        catch (Exception)
        {
            return Task.FromResult(ShareStatus.Unavailable);
        }
    }

    /// <summary>One file keeps its own type; a mixed batch degrades to the widest match.</summary>
    private static string MimeType(string[] paths, int count)
    {
        if (count == 0) return "text/plain";
        string[] types = paths.Select(TypeOf).Distinct().ToArray();
        if (types.Length == 1) return types[0];
        string[] families = types.Select(t => t.Split('/')[0]).Distinct().ToArray();
        return families.Length == 1 ? families[0] + "/*" : "*/*";
    }

    internal static string TypeOf(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension)
               ?? "application/octet-stream";
    }
}

/// <summary>
///     The provider that hands shared files to the app the user picks. It serves exactly one
///     directory — <c>cacheDir/zigote-share</c>, which only <see cref="Publish" /> writes to — so
///     a receiving app cannot walk out of it into the rest of the app's storage.
/// </summary>
[ContentProvider(new[] { "${applicationId}.zigote.share" }, Exported = false, GrantUriPermissions = true)]
internal sealed class ShareFileProvider : ContentProvider
{
    private static string Authority(Context context) => context.PackageName + ".zigote.share";

    private static DirectoryInfo Root(Context context)
        => Directory.CreateDirectory(Path.Combine(context.CacheDir!.AbsolutePath, "zigote-share"));

    /// <summary>Copy a file into the served directory and return the URI for it; null if it cannot be copied.</summary>
    public static AndroidUri? Publish(Context context, string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            File.Copy(path, Path.Combine(Root(context).FullName, name), overwrite: true);
            return AndroidUri.Parse($"content://{Authority(context)}/{AndroidUri.Encode(name)}");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The file a URI names, or null when it escapes the served directory.</summary>
    private FileInfo? Resolve(AndroidUri uri)
    {
        string root = Root(Context!).FullName;
        string full = Path.GetFullPath(Path.Combine(root, uri.Path?.TrimStart('/') ?? ""));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? new FileInfo(full)
            : null;
    }

    public override bool OnCreate() => true;

    public override Android.OS.ParcelFileDescriptor? OpenFile(AndroidUri uri, string mode)
    {
        if (Resolve(uri) is not { Exists: true } file) return null;
        return Android.OS.ParcelFileDescriptor.Open(
            new Java.IO.File(file.FullName), Android.OS.ParcelFileMode.ReadOnly);
    }

    public override string? GetType(AndroidUri uri) => ShareDriver.TypeOf(uri.Path ?? "");

    /// <summary>Name and size — what a chooser preview and a mail attachment ask for.</summary>
    public override ICursor? Query(
        AndroidUri uri, string[]? projection, string? selection, string[]? selectionArgs,
        string? sortOrder)
    {
        if (Resolve(uri) is not { Exists: true } file) return null;
        string[] columns = projection ?? [IOpenableColumns.DisplayName, IOpenableColumns.Size];
        var values = new Java.Lang.Object[columns.Length];
        for (int i = 0; i < columns.Length; i++)
            values[i] = columns[i] == IOpenableColumns.DisplayName ? new Java.Lang.String(file.Name)
                : columns[i] == IOpenableColumns.Size ? Java.Lang.Long.ValueOf(file.Length)
                : new Java.Lang.String();

        var cursor = new MatrixCursor(columns);
        cursor.AddRow(values);
        return cursor;
    }

    // Read-only: nothing outside this app may write here.
    public override AndroidUri? Insert(AndroidUri uri, ContentValues? values) => null;
    public override int Delete(AndroidUri uri, string? selection, string[]? selectionArgs) => 0;

    public override int Update(
        AndroidUri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
}
