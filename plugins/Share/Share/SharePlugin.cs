namespace Share;

/// <summary>What the platform's share sheet answered.</summary>
public enum ShareStatus
{
    /// <summary>The content went somewhere — or, where the platform will not say, it was handed over.</summary>
    Success,

    /// <summary>The user closed the sheet without picking anything.</summary>
    Dismissed,

    /// <summary>Nothing here shares: no sheet on this platform, nothing to share, or the files are gone.</summary>
    Unavailable
}

/// <summary>
///     Share — hand text, a link or files to the platform's own share sheet, the
///     <c>share_plus</c> slot from the plugin roadmap. Static, nothing to register with
///     <c>PluginHost</c>: every call is on demand.
///     <para>
///         Never throws: a device with no share sheet answers <see cref="ShareStatus.Unavailable" />,
///         which is a normal answer for a Linux desktop, not a crash.
///     </para>
/// </summary>
public static class SharePlugin
{
    /// <summary>Share text — a message, or a URL, or both ("Look at this https://…").</summary>
    /// <param name="subject">Mail-shaped targets use it as the subject line; the rest ignore it.</param>
    public static Task<ShareStatus> ShareTextAsync(string text, string? subject = null)
        => string.IsNullOrWhiteSpace(text)
            ? Task.FromResult(ShareStatus.Unavailable)
            : ShareDriver.ShareAsync(text, subject, []);

    /// <summary>
    ///     Share files, optionally with a message. Paths that do not exist are dropped; if none
    ///     survive and there is no text, the answer is <see cref="ShareStatus.Unavailable" />.
    /// </summary>
    public static Task<ShareStatus> ShareFilesAsync(
        IReadOnlyList<string> paths, string? text = null, string? subject = null)
    {
        string[] existing = Existing(paths);
        if (existing.Length == 0)
            return string.IsNullOrWhiteSpace(text)
                ? Task.FromResult(ShareStatus.Unavailable)
                : ShareDriver.ShareAsync(text!, subject, []);
        return ShareDriver.ShareAsync(text, subject, existing);
    }

    /// <summary>Absolute paths of the files that are actually there, in the order given.</summary>
    internal static string[] Existing(IReadOnlyList<string> paths)
    {
        var kept = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full)) kept.Add(full);
            }
            catch (Exception)
            {
                // A path the OS rejects outright is a path with no file behind it.
            }
        }

        return kept.ToArray();
    }
}
