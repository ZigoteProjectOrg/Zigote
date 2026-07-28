namespace Zigote.UI.Material;

public enum FileSortColumn
{
    Name,
    Size,
    Modified,
}

/// <summary>One row of a directory listing. Size is -1 for directories.</summary>
public readonly record struct FileBrowserEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    bool IsHidden);

/// <summary>
///     The pure state behind <see cref="FileBrowserDialog" />: one directory's entries plus the
///     view pipeline (hidden/extension/search filters → sort → <see cref="Visible" />),
///     back/forward/up navigation history, and path-keyed multi-selection. File-system access is
///     confined to <see cref="Load" />; everything else operates on the entry list, so the whole
///     view pipeline is unit-testable through <see cref="SetEntries" />.
/// </summary>
public sealed class FileBrowserModel
{
    // Linux is the only case-sensitive-by-convention platform we ship on.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private readonly List<string> _back = [];
    private readonly List<FileBrowserEntry> _entries = [];
    private readonly List<string> _forward = [];
    private readonly HashSet<string> _selected = new(PathComparer);
    private readonly List<FileBrowserEntry> _visible = [];
    private int _anchor = -1;

    public string CurrentDirectory { get; private set; } = "";

    /// <summary>The filtered + sorted rows the view renders.</summary>
    public IReadOnlyList<FileBrowserEntry> Visible => _visible;

    public FileSortColumn SortColumn { get; private set; } = FileSortColumn.Name;
    public bool SortAscending { get; private set; } = true;

    public bool ShowHidden { get; set; }

    /// <summary>Folder-pick mode: list directories only.</summary>
    public bool DirectoriesOnly { get; set; }

    public bool AllowMultiSelect { get; set; }

    /// <summary>Case-insensitive substring filter on names (the search box).</summary>
    public string SearchText { get; set; } = "";

    /// <summary>
    ///     Extensions (without dots) the active filter admits; null or containing "*" = all
    ///     files. Directories always pass.
    /// </summary>
    public string[]? ExtensionFilter { get; set; }

    /// <summary>When set, navigation is clamped to this subtree (project-scoped asset pickers).</summary>
    public string? LockRoot { get; init; }

    /// <summary>Human-readable reason the current directory could not be listed, or null.</summary>
    public string? LastError { get; private set; }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp =>
        Path.GetDirectoryName(CurrentDirectory) is { Length: > 0 } parent && IsWithinRoot(parent);

    public IReadOnlyCollection<string> SelectedPaths => _selected;

    /// <summary>Enter a directory, recording history (no-op outside <see cref="LockRoot" />).</summary>
    public void NavigateTo(string directory)
    {
        var full = SafeFullPath(directory);
        if (!IsWithinRoot(full)) return;
        if (CurrentDirectory.Length > 0 && !PathComparer.Equals(CurrentDirectory, full))
        {
            _back.Add(CurrentDirectory);
            _forward.Clear();
        }

        Load(full);
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentDirectory);
        Load(target);
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentDirectory);
        Load(target);
    }

    public void GoUp()
    {
        if (!CanGoUp) return;
        NavigateTo(Path.GetDirectoryName(CurrentDirectory)!);
    }

    /// <summary>Re-list the current directory (after a New Folder, external change, …).</summary>
    public void Refresh()
    {
        if (CurrentDirectory.Length > 0) Load(CurrentDirectory);
    }

    /// <summary>Toggle direction when the column is already active, else sort ascending by it.</summary>
    public void SortBy(FileSortColumn column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }

        ApplyView();
    }

    /// <summary>Rebuild <see cref="Visible" /> from the raw entries (call after mutating any
    ///     filter property). Selection survives when the paths remain visible.</summary>
    public void ApplyView()
    {
        _visible.Clear();
        foreach (var e in _entries)
        {
            if (!ShowHidden && e.IsHidden) continue;
            if (DirectoriesOnly && !e.IsDirectory) continue;
            if (!e.IsDirectory && !MatchesExtensionFilter(e.Name)) continue;
            if (SearchText.Length > 0 &&
                !e.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) continue;
            _visible.Add(e);
        }

        _visible.Sort(Compare);

        if (_selected.Count > 0)
        {
            var visiblePaths = new HashSet<string>(_visible.Select(v => v.FullPath), PathComparer);
            _selected.RemoveWhere(p => !visiblePaths.Contains(p));
        }

        if (_anchor >= _visible.Count) _anchor = -1;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    public bool IsSelected(in FileBrowserEntry entry)
    {
        return _selected.Contains(entry.FullPath);
    }

    /// <summary>Entries of <see cref="Visible" /> currently selected, in view order.</summary>
    public List<FileBrowserEntry> SelectedEntries()
    {
        return _visible.Where(e => _selected.Contains(e.FullPath)).ToList();
    }

    public void ClearSelection()
    {
        _selected.Clear();
        _anchor = -1;
    }

    /// <summary>
    ///     Click/keyboard selection: plain replaces, <paramref name="toggle" /> (Cmd/Ctrl)
    ///     toggles, <paramref name="range" /> (Shift) selects anchor→index. Toggle/range only
    ///     apply with <see cref="AllowMultiSelect" />.
    /// </summary>
    public void SelectIndex(int index, bool toggle = false, bool range = false)
    {
        if ((uint)index >= (uint)_visible.Count) return;
        var path = _visible[index].FullPath;

        if (range && AllowMultiSelect && _anchor >= 0 && _anchor < _visible.Count)
        {
            _selected.Clear();
            var lo = Math.Min(_anchor, index);
            var hi = Math.Max(_anchor, index);
            for (var i = lo; i <= hi; i++) _selected.Add(_visible[i].FullPath);
            return; // the anchor stays put so a further shift-click re-ranges from it
        }

        if (toggle && AllowMultiSelect)
        {
            if (!_selected.Remove(path)) _selected.Add(path);
            _anchor = index;
            return;
        }

        _selected.Clear();
        _selected.Add(path);
        _anchor = index;
    }

    /// <summary>
    ///     Type-ahead: index of the next visible name starting with <paramref name="prefix" />,
    ///     scanning forward from <paramref name="from" /> + 1 and wrapping; -1 when none match.
    /// </summary>
    public int TypeAheadIndex(string prefix, int from)
    {
        if (prefix.Length == 0 || _visible.Count == 0) return -1;
        for (var step = 1; step <= _visible.Count; step++)
        {
            var i = ((from + step) % _visible.Count + _visible.Count) % _visible.Count;
            if (_visible[i].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    /// <summary>List <paramref name="directory" /> from disk. Failures land in
    ///     <see cref="LastError" /> with an empty listing rather than throwing.</summary>
    public void Load(string directory)
    {
        LastError = null;
        var list = new List<FileBrowserEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                try
                {
                    var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    var hidden = info.Name.StartsWith('.') ||
                                 (info.Attributes & FileAttributes.Hidden) != 0;
                    list.Add(
                        new FileBrowserEntry(
                            info.Name,
                            info.FullName,
                            isDir,
                            isDir ? -1L : (info as FileInfo)?.Length ?? 0L,
                            info.LastWriteTime,
                            hidden
                        )
                    );
                }
                catch
                {
                    // A single unreadable entry (dangling symlink, permission hole) must not
                    // take down the whole listing.
                }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        SetEntries(directory, list);
    }

    /// <summary>Replace the raw listing (the seam <see cref="Load" /> and tests feed).</summary>
    public void SetEntries(string directory, IEnumerable<FileBrowserEntry> entries)
    {
        CurrentDirectory = SafeFullPath(directory);
        _entries.Clear();
        _entries.AddRange(entries);
        ClearSelection();
        ApplyView();
    }

    private bool MatchesExtensionFilter(string name)
    {
        if (ExtensionFilter is not { Length: > 0 } exts) return true;
        var ext = Path.GetExtension(name).TrimStart('.');
        foreach (var e in exts)
        {
            if (e == "*") return true;
            if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private int Compare(FileBrowserEntry a, FileBrowserEntry b)
    {
        // Folders group before files regardless of column or direction (every OS browser does).
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        var c = SortColumn switch {
            FileSortColumn.Size => a.Size.CompareTo(b.Size),
            FileSortColumn.Modified => a.Modified.CompareTo(b.Modified),
            _ => 0,
        };
        if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return SortAscending ? c : -c;
    }

    private bool IsWithinRoot(string directory)
    {
        if (LockRoot is null) return true;
        var root = Path.TrimEndingDirectorySeparator(SafeFullPath(LockRoot));
        var dir = Path.TrimEndingDirectorySeparator(SafeFullPath(directory));
        if (!dir.StartsWith(root, PathComparison)) return false;
        return dir.Length == root.Length || dir[root.Length] is '/' or '\\';
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
