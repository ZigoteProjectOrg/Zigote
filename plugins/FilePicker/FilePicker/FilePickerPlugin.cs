namespace FilePicker;

/// <summary>
///     FilePicker — one shared API over the platform's own pickers. Desktop routes through
///     <c>Zigote.Core.Engine.FileDialog</c> (native dialogs, in-app fallback and UI-thread rules
///     included — call from the UI thread, await anywhere); Android routes through the Storage
///     Access Framework. Nothing to register with <c>PluginHost</c>: every call is on demand.
///     <para>
///         Results: null (or an empty array) means the user cancelled. On desktop results are
///         filesystem paths; on Android file results are <c>content://</c> URI strings — read
///         them through a ContentResolver, not File IO — while a picked folder comes back as a
///         real path where one exists (primary storage) and null where it does not (SD card,
///         cloud provider).
///     </para>
/// </summary>
public static class FilePickerPlugin
{
    /// <summary>Pick one existing file. Null = cancelled.</summary>
    /// <param name="filters">Name + extension patterns ("png", ".png", "*.png" all work); desktop only —
    ///     the Android system picker filters by MIME and shows everything.</param>
    public static Task<string?> OpenFileAsync(
        string? title = null, (string Name, string[] Patterns)[]? filters = null)
        => FilePickerDriver.OpenFileAsync(title, filters);

    /// <summary>Pick one or more existing files. Empty = cancelled.</summary>
    /// <param name="filters"><inheritdoc cref="OpenFileAsync" path="/param[@name='filters']" /></param>
    public static Task<string[]> OpenFilesAsync(
        string? title = null, (string Name, string[] Patterns)[]? filters = null)
        => FilePickerDriver.OpenFilesAsync(title, filters);

    /// <summary>Pick an existing folder. Null = cancelled (or, on Android, a folder with no real path).</summary>
    public static Task<string?> PickFolderAsync(string? title = null)
        => FilePickerDriver.PickFolderAsync(title);

    /// <summary>Pick a save destination; the dialog owns the overwrite prompt. Null = cancelled.</summary>
    /// <param name="filters"><inheritdoc cref="OpenFileAsync" path="/param[@name='filters']" /></param>
    public static Task<string?> SaveFileAsync(
        string? title = null, string? suggestedName = null,
        (string Name, string[] Patterns)[]? filters = null)
        => FilePickerDriver.SaveFileAsync(title, suggestedName, filters);
}
