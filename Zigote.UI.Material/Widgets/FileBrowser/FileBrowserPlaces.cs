namespace Zigote.UI.Material;

/// <summary>
///     The sidebar shortcuts for <see cref="FileBrowserDialog" />: the standard user folders plus
///     mounted volumes, resolved per OS. Everything is best-effort — entries that don't exist or
///     can't be probed are simply omitted.
/// </summary>
public static class FileBrowserPlaces
{
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
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (full.Length == 0) full = path;
                if (!Directory.Exists(path) || !seen.Add(full)) return;
                places.Add(new Place(Label: label, Path: path, Icon: icon));
            }
            catch
            {
                // Unprobeable path — skip.
            }
        }

        if (pinnedPath is not null)
        {
            Add(
                label: pinnedLabel ??
                       Path.GetFileName(Path.TrimEndingDirectorySeparator(pinnedPath)),
                path: pinnedPath,
                icon: Icons.Folder
            );
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(label: "Home", path: home, icon: Icons.Home);
        Add(
            label: "Desktop",
            path: Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            icon: Icons.Computer
        );
        Add(
            label: "Documents",
            path: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            icon: Icons.Description
        );
        Add(
            label: "Downloads",
            path: Path.Combine(path1: home, path2: "Downloads"),
            icon: Icons.Download
        );

        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    string letter = drive.Name.TrimEnd('\\');
                    string label = string.IsNullOrEmpty(drive.VolumeLabel)
                        ? letter
                        : $"{drive.VolumeLabel} ({letter})";
                    Add(label: label, path: drive.RootDirectory.FullName, icon: Icons.Storage);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                Add(label: "Macintosh HD", path: "/", icon: Icons.Storage);
                if (Directory.Exists("/Volumes"))
                {
                    foreach (string volume in Directory.GetDirectories("/Volumes"))
                    {
                        string name = Path.GetFileName(volume);
                        if (name.StartsWith('.')) continue;
                        // "/Volumes/Macintosh HD" is a link back to "/" — the dedupe in Add can't
                        // see through it, so resolve links explicitly.
                        try
                        {
                            if (Directory.ResolveLinkTarget(
                                    linkPath: volume,
                                    returnFinalTarget: true
                                )?.FullName == "/")
                                continue;
                        }
                        catch
                        {
                            // Unresolvable link — list it anyway.
                        }

                        Add(label: name, path: volume, icon: Icons.Storage);
                    }
                }
            }
            else
            {
                Add(label: "File System", path: "/", icon: Icons.Storage);
                string user = Environment.UserName;
                foreach (string root in (string[])[$"/media/{user}", $"/run/media/{user}", "/mnt"])
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string mount in Directory.GetDirectories(root))
                        Add(label: Path.GetFileName(mount), path: mount, icon: Icons.Storage);
                }
            }
        }
        catch
        {
            // Volume probing is decorative — a failure must never block the dialog.
        }

        return places;
    }

    public readonly record struct Place(string Label, string Path, string Icon);
}
