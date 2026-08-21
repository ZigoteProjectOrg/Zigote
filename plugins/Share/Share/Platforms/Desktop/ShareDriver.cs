using Zigote.Core.Engine;

namespace Share;

/// <summary>
///     Desktop implementation — there is no share sheet worth reaching here. Windows keeps one
///     behind WinRT's DataTransferManager (COM interop plus an HWND), macOS behind
///     NSSharingServicePicker (an ObjC bridge this package does not have), and Linux has none at
///     all. So desktop does the one useful thing that works on all three: it puts the payload on
///     the clipboard and answers <see cref="ShareStatus.Unavailable" /> — the app can then say
///     "copied" instead of pretending a sheet appeared.
///     <para>
///         ponytail: clipboard fallback, no native sheet. Wire DataTransferManager /
///         NSSharingServicePicker when a desktop app actually asks for the OS sheet.
///     </para>
/// </summary>
internal static class ShareDriver
{
    public static Task<ShareStatus> ShareAsync(string? text, string? subject, string[] paths)
    {
        ZigoteEngine.Instance?.SetClipboard(Compose(text, subject, paths));
        return Task.FromResult(ShareStatus.Unavailable);
    }

    /// <summary>Subject first, then the message, then one file path per line.</summary>
    internal static string Compose(string? text, string? subject, string[] paths)
    {
        var lines = new List<string>(paths.Length + 2);
        if (!string.IsNullOrWhiteSpace(subject)) lines.Add(subject);
        if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
        lines.AddRange(paths);
        return string.Join('\n', lines);
    }
}
