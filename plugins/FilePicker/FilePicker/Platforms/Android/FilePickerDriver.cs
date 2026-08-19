using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;

namespace FilePicker;

/// <summary>
///     Android implementation — the Storage Access Framework, reached through a throwaway
///     transparent activity (the pattern Timbre proved out): pickers deliver their answers to an
///     Activity, and the app's only activity belongs to SDL, so a purpose-built one is started,
///     reports back and finishes.
///     <para>
///         One picker at a time: a second call while one is up is answered as cancelled
///         immediately — the system shows one picker anyway, and queueing would hand the user a
///         surprise dialog minutes later.
///     </para>
/// </summary>
internal static class FilePickerDriver
{
    /// <summary>The call awaiting the current picker, or null. Swapped atomically.</summary>
    private static TaskCompletionSource<string[]>? _pending;

    public static Task<string?> OpenFileAsync(
        string? title, (string Name, string[] Patterns)[]? filters)
        => FirstOrNull(Launch(PickerActivity.ModeOpen, many: false, suggestedName: null));

    public static Task<string[]> OpenFilesAsync(
        string? title, (string Name, string[] Patterns)[]? filters)
        => Launch(PickerActivity.ModeOpen, many: true, suggestedName: null);

    public static Task<string?> PickFolderAsync(string? title)
        => FirstOrNull(Launch(PickerActivity.ModeFolder, many: false, suggestedName: null));

    public static Task<string?> SaveFileAsync(
        string? title, string? suggestedName, (string Name, string[] Patterns)[]? filters)
        => FirstOrNull(Launch(PickerActivity.ModeSave, many: false, suggestedName: suggestedName));

    private static async Task<string?> FirstOrNull(Task<string[]> picking)
    {
        string[] results = await picking;
        return results.Length > 0 ? results[0] : null;
    }

    private static Task<string[]> Launch(string mode, bool many, string? suggestedName)
    {
        var tcs = new TaskCompletionSource<string[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pending, tcs, null) is not null)
            return Task.FromResult(Array.Empty<string>());

        try
        {
            var context = Application.Context;
            var intent = new Intent(context, typeof(PickerActivity))
                .PutExtra(PickerActivity.ModeExtra, mode)
                .PutExtra(PickerActivity.ManyExtra, many)
                .PutExtra(PickerActivity.NameExtra, suggestedName)
                // Started from outside an activity context, so it needs its own task.
                .AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception)
        {
            _pending = null;
            return Task.FromResult(Array.Empty<string>());
        }

        return tcs.Task;
    }

    /// <summary>The activity's answer; an empty array is a cancel.</summary>
    internal static void Complete(string[] results)
        => Interlocked.Exchange(ref _pending, null)?.TrySetResult(results);

    /// <summary>
    ///     Turn a Storage Access Framework tree URI into a path ordinary file APIs can walk.
    ///     <para>
    ///         SAF hands back an opaque document tree, not a directory. For the case that
    ///         actually matters (a folder on the device's own storage) the tree id is
    ///         <c>primary:Some/Path</c>, which maps onto the external storage root. An SD card or
    ///         a cloud provider has no such path and returns null — supporting those means
    ///         reading through the SAF stream API, which is the caller's much bigger decision.
    ///     </para>
    /// </summary>
    internal static string? TreeUriToPath(global::Android.Net.Uri uri)
    {
        string? documentId = DocumentsContract.GetTreeDocumentId(uri);
        if (string.IsNullOrEmpty(documentId)) return null;

        string[] parts = documentId.Split(':', 2);
        if (parts.Length != 2 ||
            !string.Equals(parts[0], "primary", StringComparison.OrdinalIgnoreCase))
            return null;

        string? root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrEmpty(root)) return null;
        return parts[1].Length == 0 ? root : Path.Combine(root, parts[1]);
    }
}

/// <summary>
///     The invisible activity that exists only to receive an answer. Started, asks its one
///     question, hands the result to <see cref="FilePickerDriver.Complete" /> and finishes — the
///     user sees the system picker and nothing else, because the theme is fully transparent and
///     no content view is ever set.
/// </summary>
[Activity(
    Name = "dev.zigote.plugins.filepicker.PickerActivity",
    Exported = false,
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    // Excluded from recents: it is machinery, not a place the user can go back to.
    ExcludeFromRecents = true
    // Deliberately NOT NoHistory. It reads as the right flag for a throwaway activity, and it
    // silently breaks the picker: Android finishes a no-history activity the moment it stops,
    // which is precisely what happens when the SAF picker comes to the front — so the activity
    // is gone before it can be given the result, OnActivityResult never runs, and the chosen
    // file vanishes with no error anywhere. This finishes itself explicitly instead.
)]
public sealed class PickerActivity : Activity
{
    internal const string ModeExtra = "zigote.filepicker.mode";
    internal const string ManyExtra = "zigote.filepicker.many";
    internal const string NameExtra = "zigote.filepicker.name";

    internal const string ModeOpen = "open";
    internal const string ModeFolder = "folder";
    internal const string ModeSave = "save";

    private const int RequestOpen = 100;
    private const int RequestFolder = 101;
    private const int RequestSave = 102;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        switch (Intent?.GetStringExtra(ModeExtra))
        {
            case ModeOpen:
                var open = new Intent(Intent.ActionOpenDocument)
                    .AddCategory(Intent.CategoryOpenable)
                    .SetType("*/*");
                if (Intent.GetBooleanExtra(ManyExtra, false))
                    open.PutExtra(Intent.ExtraAllowMultiple, true);
                StartActivityForResult(open, RequestOpen);
                break;

            case ModeFolder:
                // OPEN_DOCUMENT_TREE is the only way to be given a folder on modern Android: an
                // app cannot browse the filesystem itself, so the picker belongs to the system
                // and the grant is per-folder.
                var folder = new Intent(Intent.ActionOpenDocumentTree)
                    .AddFlags(ActivityFlags.GrantReadUriPermission |
                              ActivityFlags.GrantPersistableUriPermission);
                StartActivityForResult(folder, RequestFolder);
                break;

            case ModeSave:
                var save = new Intent(Intent.ActionCreateDocument)
                    .AddCategory(Intent.CategoryOpenable)
                    .SetType("*/*");
                if (Intent.GetStringExtra(NameExtra) is { Length: > 0 } name)
                    save.PutExtra(Intent.ExtraTitle, name);
                StartActivityForResult(save, RequestSave);
                break;

            default:
                Done([]);
                break;
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (resultCode != Result.Ok || data is null)
        {
            Done([]);
            return;
        }

        switch (requestCode)
        {
            case RequestOpen:
                Done(OpenedUris(data));
                return;

            case RequestFolder:
                if (data.Data is not { } tree)
                {
                    Done([]);
                    return;
                }

                // Persist the grant, or it is gone at the next launch and the app points at a
                // folder it may no longer open.
                try
                {
                    ContentResolver?.TakePersistableUriPermission(
                        tree, ActivityFlags.GrantReadUriPermission);
                }
                catch (Java.Lang.SecurityException)
                {
                    // Some providers refuse to make a grant persistable. The folder still works
                    // for this session, which is better than refusing it outright.
                }

                Done(FilePickerDriver.TreeUriToPath(tree) is { } path ? [path] : []);
                return;

            case RequestSave:
                Done(data.Data is { } created ? [created.ToString()!] : []);
                return;
        }
    }

    /// <summary>Multi-select answers arrive as ClipData; a single pick as Data.</summary>
    private static string[] OpenedUris(Intent data)
    {
        if (data.ClipData is { } clip)
        {
            var uris = new List<string>(clip.ItemCount);
            for (int i = 0; i < clip.ItemCount; i++)
                if (clip.GetItemAt(i)?.Uri?.ToString() is { } uri)
                    uris.Add(uri);
            return uris.ToArray();
        }

        return data.Data?.ToString() is { } single ? [single] : [];
    }

    private void Done(string[] results)
    {
        FilePickerDriver.Complete(results);
        Finish();
    }
}
