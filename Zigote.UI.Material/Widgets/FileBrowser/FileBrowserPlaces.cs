namespace Zigote.UI.Material;

/// <summary>
///     The sidebar shortcuts for <see cref="FileBrowserDialog" />: the standard user folders plus
///     mounted volumes, resolved per OS. Everything is best-effort — entries that don't exist or
///     can't be probed are simply omitted.
/// </summary>
public static class FileBrowserPlaces
{
    public readonly record struct Place(string Label, string Path, string Icon);

    /// <summary>
    ///     Build the places list. <paramref name="pinnedLabel" />/<paramref name="pinnedPath" />
    ///     pin a caller-specific location on top (e.g. the current project).
    /// </summary>
    public static List<Place> Build(string? pinnedLabel = null, string? pinnedPath = null)
    {
        var places = new List<Place>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string label, string? path, string icon)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (full.Length == 0) full = path;
                if (!Directory.Exists(path) || !seen.Add(full)) return;
                places.Add(new Place(label, path, icon));
            }
            catch
            {
                // Unprobeable path — skip.
            }
        }

        if (pinnedPath is not null)
            Add(
                pinnedLabel ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(pinnedPath)),
                pinnedPath,
                Icons.Folder
            );

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Home", home, Icons.Home);
        Add(
            "Desktop",
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Icons.Computer
        );
        Add(
            "Documents",
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Icons.Description
        );
        Add("Downloads", Path.Combine(home, "Downloads"), Icons.Download);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    var letter = drive.Name.TrimEnd('\\');
                    var label = string.IsNullOrEmpty(drive.VolumeLabel)
                        ? letter
                        : $"{drive.VolumeLabel} ({letter})";
                    Add(label, drive.RootDirectory.FullName, Icons.Storage);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                Add("Macintosh HD", "/", Icons.Storage);
                if (Directory.Exists("/Volumes"))
                    foreach (var volume in Directory.GetDirectories("/Volumes"))
                    {
                        var name = Path.GetFileName(volume);
                        if (name.StartsWith('.')) continue;
                        // "/Volumes/Macintosh HD" is a link back to "/" — the dedupe in Add can't
                        // see through it, so resolve links explicitly.
                        try
                        {
                            if (Directory.ResolveLinkTarget(volume, true)?.FullName == "/")
                                continue;
                        }
                        catch
                        {
                            // Unresolvable link — list it anyway.
                        }

                        Add(name, volume, Icons.Storage);
                    }
            }
            else
            {
                Add("File System", "/", Icons.Storage);
                var user = Environment.UserName;
                foreach (var root in (string[])[$"/media/{user}", $"/run/media/{user}", "/mnt"])
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var mount in Directory.GetDirectories(root))
                        Add(Path.GetFileName(mount), mount, Icons.Storage);
                }
            }
        }
        catch
        {
            // Volume probing is decorative — a failure must never block the dialog.
        }

        return places;
    }
}