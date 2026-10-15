using Zigote.UI.TextShaping;
using System.Collections.Immutable;
using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.Core.Threading;
using Zigote.Editor.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Editor.Panels;

/// <summary>
///     Project panel: a live, expandable folder tree of the open project directory.
///     Folders expand/collapse on click; files can be selected and dragged into the
///     viewport (same drop pipeline as before). A search box flattens to matching files.
/// </summary>
public sealed class AssetBrowserPanel : Widget, IDisposable
{
    private const float RowH = 22f;
    private const float HeaderH = 4f; // top padding; the search box lives above the scroller
    private const float Indent = 14f;
    private const float DragThreshold = 5f;

    /// <summary>
    ///     How long a burst of keystrokes or filesystem events is allowed to coalesce before the tree
    ///     is walked. Editors save whole directories at once, so watcher events arrive in floods —
    ///     without this each one costs its own walk.
    /// </summary>
    private static readonly TimeSpan ScanDebounce = TimeSpan.FromMilliseconds(120);

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Latest _scan;
    private readonly EditorState _state;
    private readonly FileSystemWatcher? _watcher;
    private readonly Background _work;

    /// <summary>
    ///     Raised when the selected row should be scrolled into view — <c>(top, height)</c> in the
    ///     panel's own content space. The shell wires it to the wrapping scroller.
    /// </summary>
    public Action<float, float>? OnRevealRequested;

    private volatile bool _dirty = true;
    private Offset _dragStart;
    private string _filter = "";
    private int _hoverIndex = -1;
    private bool _isDragging;
    private long _lastClickMs;
    private string? _lastClickPath;
    private int _pressIndex = -1;

    /// <summary>
    ///     The visible tree, produced whole on a worker and swapped in on the UI thread. Immutable
    ///     rather than a mutating list: it crosses a thread boundary, and the walk that produced it is
    ///     still allowed to be superseded on its way back.
    /// </summary>
    private ImmutableArray<Entry> _rows = [];

    private string? _selectedPath;
    private Size _size;
    private ThemeData _theme;

    public AssetBrowserPanel(EditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;
        // Held, not orphaned: the scope registers itself with its parent and only leaves that list
        // when disposed, so keeping just the Latest would strand one entry per panel rebuild.
        _work = state.Background.Child("assets");
        _scan = _work.Latest();

        // Start with the asset folder open: it is where everything the user wants lives.
        if (state.ProjectDir is { } projectDir)
        {
            _expanded.Add(
                Path.GetFullPath(path: state.AssetRoot, basePath: projectDir).TrimEnd('/', '\\')
            );
        }

        string? root = RootPath;
        if (root != null && Directory.Exists(root))
        {
            try
            {
                _watcher = new FileSystemWatcher(root) {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                FileSystemEventHandler h = (_, _) =>
                {
                    _dirty = true;
                    _state.NotifyAssetsChanged();
                };
                _watcher.Created += h;
                _watcher.Deleted += h;
                _watcher.Renamed += (_, e) =>
                {
                    _dirty = true;
                    // Heal the registry + open-scene references on the main thread (renames from
                    // Finder/IDE included — the editor is no longer the only safe way to move assets).
                    _state.QueueAssetRenamed(oldFullPath: e.OldFullPath, newFullPath: e.FullPath);
                    _state.NotifyAssetsChanged();
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Project] FileSystemWatcher failed: {ex.Message}");
            }
        }
    }

    /// <summary>File-name filter; non-empty flattens the tree to the matching files.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value;
            _dirty = true;
            MarkNeedsLayout();
        }
    }

    // Holds keyboard focus (taken on click) so the arrow keys walk the tree.
    public override bool Focusable => true;

    // Arrow keys walk the tree; without this the app spends them on directional focus traversal.
    public override bool HandlesDirectionalKeys => true;

    private string? RootPath => _state.ProjectDir ??
                                (Directory.Exists(_state.AssetRoot) ? _state.AssetRoot : null);

    private static string RevealLabel => OperatingSystem.IsMacOS() ? "Reveal in Finder" :
        OperatingSystem.IsWindows() ? "Show in Explorer" : "Show in File Manager";

    public void Dispose()
    {
        _scan.Dispose();
        _work.Dispose();
        _watcher?.Dispose();
    }

    private static bool Skip(string name) => name.StartsWith('.') || name is "obj" or "bin";

    /// <summary>
    ///     Walk the project on a worker and swap the finished tree in.
    ///     <para>
    ///         This used to happen inside <see cref="Measure" />: a filtered search enumerated every
    ///         file under the project root, recursively, during layout — on <b>every keystroke</b>, and
    ///         again on every filesystem event. On a project with a real <c>Assets/</c> tree that is a
    ///         frozen editor per character typed. Debounced and latest-wins, so a burst of either costs
    ///         one walk and only the newest one is shown.
    ///     </para>
    /// </summary>
    private void RequestRebuild()
    {
        string? root = RootPath;
        string filter = _filter;
        // Snapshotted: the worker must not read a set the UI thread expands under it.
        var expanded = new HashSet<string>(collection: _expanded, comparer: StringComparer.Ordinal);

        _scan.Run(
            work: token => Scan(
                root: root,
                filter: filter,
                expanded: expanded,
                token: token
            ),
            onUi: rows =>
            {
                _rows = rows;
                App.Active?.RequestLayout(); // the tree changed size; nothing else marks it
            },
            delay: ScanDebounce
        );
    }

    /// <summary>
    ///     The whole panel content as a pure function of (root, filter, expanded folders). No widget,
    ///     no engine, no field of this panel — which is what makes it safe to run off the UI thread.
    /// </summary>
    private static ImmutableArray<Entry> Scan(string? root, string filter,
        HashSet<string> expanded, CancellationToken token)
    {
        if (root == null || !Directory.Exists(root)) return [];
        var rows = ImmutableArray.CreateBuilder<Entry>();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            // Flat filtered view of matching files.
            try
            {
                foreach (string f in Directory.EnumerateFiles(
                                 path: root,
                                 searchPattern: "*",
                                 searchOption: SearchOption.AllDirectories
                             )
                             .Where(p => Path.GetFileName(p).Contains(
                                     value: filter,
                                     comparisonType: StringComparison.OrdinalIgnoreCase
                                 )
                             )
                             .OrderBy(
                                 keySelector: p => p,
                                 comparer: StringComparer.OrdinalIgnoreCase
                             )
                             .Take(500))
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(
                        new Entry(
                            FullPath: f,
                            Name: Path.GetRelativePath(relativeTo: root, path: f)
                                .Replace(oldChar: '\\', newChar: '/'),
                            IsDir: false,
                            Depth: 0
                        )
                    );
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                /* directory vanished mid-scan */
            }

            return rows.ToImmutable();
        }

        WalkDir(
            rows: rows,
            dir: root,
            depth: 0,
            expanded: expanded,
            token: token
        );
        return rows.ToImmutable();
    }

    private static void WalkDir(ImmutableArray<Entry>.Builder rows, string dir, int depth,
        HashSet<string> expanded, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        List<string> dirs, files;
        try
        {
            dirs = [
                .. Directory.GetDirectories(dir).Where(d => !Skip(Path.GetFileName(d)))
                    .OrderBy(
                        keySelector: Path.GetFileName,
                        comparer: StringComparer.OrdinalIgnoreCase
                    ),
            ];
            files = [
                .. Directory.GetFiles(dir).Where(f => !Skip(Path.GetFileName(f)))
                    .OrderBy(
                        keySelector: Path.GetFileName,
                        comparer: StringComparer.OrdinalIgnoreCase
                    ),
            ];
        }
        catch
        {
            return;
        }

        foreach (string d in dirs)
        {
            rows.Add(
                new Entry(
                    FullPath: d,
                    Name: Path.GetFileName(d),
                    IsDir: true,
                    Depth: depth
                )
            );
            if (expanded.Contains(d))
            {
                WalkDir(
                    rows: rows,
                    dir: d,
                    depth: depth + 1,
                    expanded: expanded,
                    token: token
                );
            }
        }

        foreach (string f in files)
        {
            rows.Add(
                new Entry(
                    FullPath: f,
                    Name: Path.GetFileName(f),
                    IsDir: false,
                    Depth: depth
                )
            );
        }
    }

    // ── Layout ──────────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        if (_dirty)
        {
            _dirty = false;
            RequestRebuild();
        }

        // One spare row keeps room for the "no matches" line.
        float h = HeaderH + (Math.Max(val1: 1, val2: _rows.Length) * RowH) + 6f;
        _size = c.Constrain(new Size(width: c.MaxWidth, height: MathF.Max(x: h, y: c.MinHeight)));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    // ── Paint ───────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        float fs = _theme.FontSizeCaption;
        if (_rows.Length == 0 && !string.IsNullOrWhiteSpace(_filter))
        {
            paint.AddText(
                text: "No files match",
                baselineX: Bounds.X + 10f,
                baselineY: TextBaseline.Centered(Bounds.Y + HeaderH, RowH, fs),
                color: _theme.TextMuted,
                fontSize: fs
            );
        }

        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            float y = Bounds.Y + HeaderH + (i * RowH);
            var rowRect = new Rect(
                x: Bounds.X,
                y: y,
                width: Bounds.Width,
                height: RowH
            );

            bool selected = _selectedPath == row.FullPath;
            var pill = new Rect(
                x: rowRect.X + 3f,
                y: rowRect.Y + 1f,
                width: rowRect.Width - 6f,
                height: rowRect.Height - 2f
            );
            if (selected) paint.AddRect(bounds: pill, color: _theme.SelectionTint, radius: 5f);
            else if (AdwStyle.RowFill(theme: _theme, hovered: _hoverIndex == i, pressed: false) is
                     { A: > 0f } wash)
                paint.AddRect(bounds: pill, color: wash, radius: 5f);

            float x = Bounds.X + 6f + (row.Depth * Indent);
            float ty = TextBaseline.Centered(y, RowH, fs);

            if (row.IsDir)
            {
                bool open = _expanded.Contains(row.FullPath);
                Icons.Draw(
                    paint: paint,
                    glyph: open ? Icons.ChevronDown : Icons.ChevronRight,
                    box: new Rect(
                        x: x,
                        y: y,
                        width: 13f,
                        height: RowH
                    ),
                    color: _theme.TextMuted,
                    size: 13f
                );
                Icons.Draw(
                    paint: paint,
                    glyph: open ? Icons.FolderOpen : Icons.Folder,
                    box: new Rect(
                        x: x + 14f,
                        y: y,
                        width: 16f,
                        height: RowH
                    ),
                    color: _theme.Warning.WithAlpha(0.85f),
                    size: 14f
                );
                paint.AddText(
                    text: row.Name,
                    baselineX: x + 33f,
                    baselineY: ty,
                    color: _theme.OnSurface,
                    fontSize: fs
                );
            }
            else
            {
                Icons.Draw(
                    paint: paint,
                    glyph: FileGlyph(row.Name),
                    box: new Rect(
                        x: x + 14f,
                        y: y,
                        width: 16f,
                        height: RowH
                    ),
                    color: IconColor(row.Name),
                    size: 14f
                );
                paint.AddText(
                    text: row.Name,
                    baselineX: x + 33f,
                    baselineY: ty,
                    color: _theme.OnSurface,
                    fontSize: fs
                );
            }
        }
    }

    // ── Interaction ─────────────────────────────────────────────────────────────

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? this : null; // own the row area for clicks/drag

    private int RowAt(Offset point)
    {
        if (point.Y < Bounds.Y + HeaderH) return -1;
        int i = (int)((point.Y - (Bounds.Y + HeaderH)) / RowH);
        return i >= 0 && i < _rows.Length ? i : -1;
    }

    private void SetHover(int index)
    {
        if (_hoverIndex == index) return;
        _hoverIndex = index;
        MarkNeedsPaint();
    }

    public override void OnPointerExit() => SetHover(-1);

    public override void OnPointerMove(Offset point)
    {
        SetHover(RowAt(point));
        if (_pressIndex >= 0 && _pressIndex < _rows.Length && !_isDragging &&
            (MathF.Abs(point.X - _dragStart.X) > DragThreshold ||
             MathF.Abs(point.Y - _dragStart.Y) > DragThreshold) &&
            !_rows[_pressIndex].IsDir)
            _isDragging = true;
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);
        _pressIndex = RowAt(point);
        _dragStart = point;
        _isDragging = false;
    }

    public override void OnPointerUp(Offset point)
    {
        // The tree can be swapped by a finished scan between press and release.
        if (_pressIndex < 0 || _pressIndex >= _rows.Length)
        {
            _pressIndex = -1;
            return;
        }

        var row = _rows[_pressIndex];

        if (row.IsDir)
        {
            Select(row);
            ToggleFolder(row.FullPath);
        }
        else if (_isDragging)
        {
            // Dropped somewhere (e.g. the viewport) — same pipeline as the old asset grid.
            _state.NotifyAssetDropped(path: row.FullPath, screenPos: point);
        }
        else
        {
            long now = Environment.TickCount64;
            bool isDouble = row.FullPath == _lastClickPath && now - _lastClickMs < 400;
            Select(row);
            if (isDouble) Open(row);
            _lastClickPath = row.FullPath;
            _lastClickMs = now;
        }

        _pressIndex = -1;
        _isDragging = false;
    }

    private void ToggleFolder(string path)
    {
        if (!_expanded.Remove(path)) _expanded.Add(path);
        _dirty = true;
        MarkNeedsLayout();
    }

    private void Select(Entry row)
    {
        _selectedPath = row.FullPath;
        if (!row.IsDir) _state.NotifyAssetSelected(row.FullPath);
        MarkNeedsPaint();
    }

    private void Open(Entry row)
    {
        if (row.IsDir) ToggleFolder(row.FullPath);
        else if (IsOpenable(row.FullPath)) _state.NotifyOpenFile(row.FullPath);
    }

    // ── Keyboard (panel focused) ────────────────────────────────────────────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || _rows.Length == 0) return;
        int cur = -1;
        for (int i = 0; i < _rows.Length && cur < 0; i++)
        {
            if (_rows[i].FullPath == _selectedPath)
                cur = i;
        }

        switch ((KeyCode)scancode)
        {
            case KeyCode.Down:
                MoveTo(cur + 1);
                break;
            case KeyCode.Up:
                MoveTo(cur < 0 ? _rows.Length - 1 : cur - 1);
                break;
            case KeyCode.Right when cur >= 0 && _rows[cur].IsDir:
                if (!_expanded.Contains(_rows[cur].FullPath)) ToggleFolder(_rows[cur].FullPath);
                else MoveTo(cur + 1);
                break;
            case KeyCode.Left when cur >= 0:
                if (_rows[cur].IsDir && _expanded.Contains(_rows[cur].FullPath))
                    ToggleFolder(_rows[cur].FullPath);
                else
                {
                    // Step out to the enclosing folder: the nearest shallower row above.
                    for (int i = cur - 1; i >= 0; i--)
                    {
                        if (_rows[i].Depth >= _rows[cur].Depth) continue;
                        MoveTo(i);
                        break;
                    }
                }

                break;
            case KeyCode.Enter when cur >= 0:
                Open(_rows[cur]);
                break;
        }
    }

    private void MoveTo(int index)
    {
        index = Math.Clamp(value: index, min: 0, max: _rows.Length - 1);
        Select(_rows[index]);
        OnRevealRequested?.Invoke(arg1: HeaderH + (index * RowH), arg2: RowH);
    }

    // ── Context menu ────────────────────────────────────────────────────────────

    public override void OnRightClick(Offset point)
    {
        App.Active?.RequestFocus(this);
        int index = RowAt(point);
        string? root = RootPath;
        if (root is null) return;
        Entry? row = index >= 0 ? _rows[index] : null;
        if (row is { } hit) Select(hit);

        // New folders go inside the clicked folder, beside the clicked file, or at the root.
        string folder = row is null ? root :
            row.Value.IsDir ? row.Value.FullPath :
            Path.GetDirectoryName(row.Value.FullPath) ?? root;
        string target = row?.FullPath ?? root;
        List<ContextMenuItem> items = [];
        if (row is { IsDir: false } file && IsOpenable(file.FullPath))
            items.Add(new ContextMenuItem(Label: "Open", OnSelect: () => Open(file)));
        items.Add(new ContextMenuItem(Label: RevealLabel, OnSelect: () => Reveal(target)));
        items.Add(
            new ContextMenuItem(
                Label: "Copy Path",
                OnSelect: () => ZigoteEngine.Instance?.SetClipboard(target)
            )
        );
        items.Add(
            new ContextMenuItem(
                Label: "Copy Relative Path",
                OnSelect: () => ZigoteEngine.Instance?.SetClipboard(
                    Path.GetRelativePath(relativeTo: root, path: target)
                        .Replace(oldChar: '\\', newChar: '/')
                )
            )
        );
        items.Add(new ContextMenuItem(Label: "", OnSelect: null, Separator: true));
        items.Add(
            new ContextMenuItem(
                Label: "New Folder…",
                OnSelect: () => PromptName(
                    title: "New Folder",
                    initial: "New Folder",
                    action: "Create",
                    onAccept: name =>
                    {
                        Directory.CreateDirectory(Path.Combine(path1: folder, path2: name));
                        _expanded.Add(folder);
                    }
                )
            )
        );
        if (row is { IsDir: false } renamable)
        {
            items.Add(
                new ContextMenuItem(
                    Label: "Rename…",
                    OnSelect: () => PromptName(
                        title: "Rename Asset",
                        initial: Path.GetFileName(renamable.FullPath),
                        action: "Rename",
                        onAccept: name =>
                        {
                            // Through the state so the registry and open scene follow the file.
                            string to = Path.Combine(path1: folder, path2: name);
                            if (_state.RenameAsset(
                                    oldRelativePath: Path.GetRelativePath(
                                        relativeTo: root,
                                        path: renamable.FullPath
                                    ),
                                    newRelativePath: Path.GetRelativePath(
                                        relativeTo: root,
                                        path: to
                                    )
                                ))
                                _selectedPath = to;
                        }
                    )
                )
            );
        }

        new ContextMenu([.. items]).ShowAt(point);
    }

    private static void Reveal(string path)
    {
        try
        {
            var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") {
                    ArgumentList = {
                        "-R",
                        path,
                    },
                } :
                OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer") {
                    ArgumentList = { "/select," + path },
                } :
                // No portable "select this file" on Linux — open the folder that holds it.
                new ProcessStartInfo("xdg-open") {
                    ArgumentList = { Directory.Exists(path) ? path : Path.GetDirectoryName(path)! },
                };
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            EditorLog.Add(severity: LogSeverity.Warning, message: $"Reveal failed: {e.Message}");
        }
    }

    /// <summary>Ask for a single file or folder name; rejects empty names and path separators.</summary>
    private void PromptName(string title, string initial, string action, Action<string> onAccept)
    {
        var entry = new AdwEntry { Text = initial };
        var dlg = new AdwAlertDialog(title) {
            ExtraChild = entry,
            DefaultResponse = "ok",
            CloseResponse = "cancel",
        };
        dlg.AddResponse(id: "cancel", label: "Cancel");
        dlg.AddResponse(id: "ok", label: action, appearance: AdwResponseAppearance.Suggested);
        dlg.OnResponse = id =>
        {
            string name = entry.Text.Trim();
            if (id != "ok" || name.Length == 0 || name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return;
            try
            {
                onAccept(name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                EditorLog.Add(
                    severity: LogSeverity.Warning,
                    message: $"{title} failed: {e.Message}"
                );
            }

            _dirty = true;
            MarkNeedsLayout();
        };
        dlg.Show();
    }

    /// <summary>Text/code files the code editor can open on double-click.</summary>
    private static bool IsOpenable(string path)
    {
        return Ext(
            name: path,
            ".cs",
            ".wgsl",
            ".zig",
            ".lua",
            ".json",
            ".scene",
            ".txt",
            ".md",
            ".glsl",
            ".hlsl",
            ".toml",
            ".csproj"
        );
    }

    // ── Icons ───────────────────────────────────────────────────────────────────

    private static Color IconColor(string name)
    {
        return name switch {
            _ when Ext(
                name: name,
                ".glb",
                ".gltf",
                ".fbx",
                ".obj"
            ) => new Color(r: 0.4f, g: 0.75f, b: 1f),
            _ when Ext(
                name: name,
                ".png",
                ".jpg",
                ".jpeg",
                ".webp",
                ".gif"
            ) => new Color(r: 0.4f, g: 0.9f, b: 0.5f),
            _ when Ext(name: name, ".cs", ".lua") => new Color(r: 0.75f, g: 0.45f, b: 1f),
            _ when Ext(
                name: name,
                ".wav",
                ".ogg",
                ".mp3"
            ) => new Color(r: 1f, g: 0.88f, b: 0.3f),
            _ when Ext(name: name, ".scene") => new Color(r: 1f, g: 0.6f, b: 0.3f),
            _ => new Color(r: 0.6f, g: 0.6f, b: 0.6f),
        };
    }

    private static string FileGlyph(string name)
    {
        return name switch {
            _ when Ext(
                name: name,
                ".glb",
                ".gltf",
                ".fbx",
                ".obj"
            ) => Icons.Cube,
            _ when Ext(
                name: name,
                ".png",
                ".jpg",
                ".jpeg",
                ".webp",
                ".gif"
            ) => Icons.Image,
            _ when Ext(name: name, ".cs", ".lua") => Icons.Code,
            _ when Ext(
                name: name,
                ".wav",
                ".ogg",
                ".mp3"
            ) => Icons.Audio,
            _ when Ext(name: name, ".scene") => Icons.Layers,
            _ => Icons.File,
        };
    }

    private static bool Ext(string name, params string[] exts) => exts.Any(e =>
        name.EndsWith(value: e, comparisonType: StringComparison.OrdinalIgnoreCase)
    );

    // ── Tree model ──────────────────────────────────────────────────────────────

    private readonly record struct Entry(string FullPath, string Name, bool IsDir, int Depth);
}
