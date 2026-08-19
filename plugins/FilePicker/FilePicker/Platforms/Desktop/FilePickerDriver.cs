using Zigote.Core.Engine;

namespace FilePicker;

/// <summary>
///     Desktop implementation — a straight delegation to <see cref="FileDialog" />, which already
///     owns the native backends (portal/zenity, IFileDialog, NSOpenPanel), the in-app fallback and
///     the one-dialog-at-a-time queue. This file only maps the plugin's dependency-free filter
///     tuples onto <see cref="FileDialogFilter" />.
/// </summary>
internal static class FilePickerDriver
{
    public static Task<string?> OpenFileAsync(
        string? title, (string Name, string[] Patterns)[]? filters)
        => FileDialog.OpenFileAsync(title: title, filters: Map(filters));

    public static Task<string[]> OpenFilesAsync(
        string? title, (string Name, string[] Patterns)[]? filters)
        => FileDialog.OpenFilesAsync(title: title, filters: Map(filters));

    public static Task<string?> PickFolderAsync(string? title)
        => FileDialog.PickFolderAsync(title: title);

    public static Task<string?> SaveFileAsync(
        string? title, string? suggestedName, (string Name, string[] Patterns)[]? filters)
        => FileDialog.SaveFileAsync(title: title, suggestedName: suggestedName, filters: Map(filters));

    internal static FileDialogFilter[]? Map((string Name, string[] Patterns)[]? filters)
        => filters?.Select(f => new FileDialogFilter(f.Name, f.Patterns)).ToArray();
}
