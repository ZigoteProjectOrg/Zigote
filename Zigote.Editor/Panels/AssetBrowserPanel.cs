using System.Collections.Immutable;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Core.Threading;
using Zigote.Editor.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels;

/// <summary>
///     Project panel: a live, expandable folder tree of the open project directory.
///     Folders expand/collapse on click; files can be selected and dragged into the
///     viewport (same drop pipeline as before). A search box flattens to matching files.
/// </summary>
public sealed class AssetBrowserPanel : Widget, IDisposable
{
    private const float RowH = 22f;
    private const float HeaderH = AdwMetrics.CompactControlHeight + 8f;
    private const float Indent = 14f;
    private const float DragThreshold = 5f;

    /// <summary>
    ///     How long a burst of keystrokes or filesystem events is allowed to coalesce before the tree
    ///     is walked. Editors save whole directories at once, so watcher events arrive in floods —
    ///     without this each one costs its own walk.
    /// </summary>
    private static readonly TimeSpan ScanDebounce = TimeSpan.FromMilliseconds(120);

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly AdwSearchEntry _searchField;
    private readonly EditorState _state;
    private readonly FileSystemWatcher? _watcher;
    private readonly Background _work;
    private readonly Latest _scan;

    /// <summary>
    ///     The visible tree, produced whole on a worker and swapped in on the UI thread. Immutable
    ///     rather than a mutating list: it crosses a thread boundary, and the walk that produced it is
    ///     still allowed to be superseded on its way back.
    /// </summary>
    private ImmutableArray<Entry> _rows = [];

    private volatile bool _dirty = true;
    private Offset _dragStart;
    private string _filter = "";
    private int _hoverIndex = -1;
    private bool _isDragging;
    private long _lastClickMs;
    private string? _lastClickPath;
    private int _pressIndex = -1;
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

        _searchField = new AdwSearchEntry {
            Placeholder = "Search files",
            Compact = true,
            OnChanged = f =>
            {
                _filter = f;
                _dirty = true;
            },
        };

        var root = RootPath;
        if (root != null && Directory.Exists(root))
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
                    _state.QueueAssetRenamed(e.OldFullPath, e.FullPath);
                    _state.NotifyAssetsChanged();
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Project] FileSystemWatcher failed: {ex.Message}");
            }
    }

    private string? RootPath => _state.ProjectDir ??
                                (Directory.Exists(_state.AssetRoot) ? _state.AssetRoot : null);

    public void Dispose()
    {
        _scan.Dispose();
        _work.Dispose();
        _watcher?.Dispose();
    }

    private static bool Skip(string name)
    {
        return name.StartsWith('.') || name is "obj" or "bin";
    }

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
        var root = RootPath;
        var filter = _filter;
        // Snapshotted: the worker must not read a set the UI thread expands under it.
        var expanded = new HashSet<string>(_expanded, StringComparer.Ordinal);

        _scan.Run(
            token => Scan(
                root,
                filter,
                expanded,
                token
            ),
            rows =>
            {
                _rows = rows;
                App.Active?.RequestLayout(); // the tree changed size; nothing else marks it
            },
            ScanDebounce
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
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                             .Where(p => Path.GetFileName(p).Contains(
                                     filter,
                                     StringComparison.OrdinalIgnoreCase
                                 )
                             )
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .Take(500))
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(
                        new Entry(
                            f,
                            Path.GetRelativePath(root, f).Replace('\\', '/'),
                            false,
                            0
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
            rows,
            root,
            0,
            expanded,
            token
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
            dirs = Directory.GetDirectories(dir).Where(d => !Skip(Path.GetFileName(d)))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            files = Directory.GetFiles(dir).Where(f => !Skip(Path.GetFileName(f)))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return;
        }

        foreach (var d in dirs)
        {
            rows.Add(
                new Entry(
                    d,
                    Path.GetFileName(d),
                    true,
                    depth
                )
            );
            if (expanded.Contains(d))
                WalkDir(
                    rows,
                    d,
                    depth + 1,
                    expanded,
                    token
                );
        }

        foreach (var f in files)
            rows.Add(
                new Entry(
                    f,
                    Path.GetFileName(f),
                    false,
                    depth
                )
            );
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

        _searchField.Measure(
            new Constraints(maxWidth: c.MaxWidth - 12f, maxHeight: AdwMetrics.CompactControlHeight)
        );
        var h = HeaderH + _rows.Length * RowH + 6f;
        _size = c.Constrain(new Size(c.MaxWidth, MathF.Max(h, c.MinHeight)));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _searchField.Layout(new Offset(origin.X + 6f, origin.Y + 4f));
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_searchField];
    }

    // ── Paint ───────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        _searchField.Paint(paint);

        var fs = _theme.FontSizeCaption;
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            var y = Bounds.Y + HeaderH + i * RowH;
            var rowRect = new Rect(
                Bounds.X,
                y,
                Bounds.Width,
                RowH
            );

            var selected = _selectedPath == row.FullPath;
            var pill = new Rect(
                rowRect.X + 3f,
                rowRect.Y + 1f,
                rowRect.Width - 6f,
                rowRect.Height - 2f
            );
            if (selected) paint.AddRect(pill, _theme.SelectionTint, 5f);
            else if (AdwStyle.RowFill(_theme, _hoverIndex == i, false) is { A: > 0f } wash)
                paint.AddRect(pill, wash, 5f);

            var x = Bounds.X + 6f + row.Depth * Indent;
            var ty = y + (RowH - fs) / 2f + fs * 0.8f;

            if (row.IsDir)
            {
                var open = _expanded.Contains(row.FullPath);
                Icons.Draw(
                    paint,
                    open ? Icons.ChevronDown : Icons.ChevronRight,
                    new Rect(
                        x,
                        y,
                        13f,
                        RowH
                    ),
                    _theme.TextMuted,
                    13f
                );
                Icons.Draw(
                    paint,
                    open ? Icons.FolderOpen : Icons.Folder,
                    new Rect(
                        x + 14f,
                        y,
                        16f,
                        RowH
                    ),
                    _theme.Warning.WithAlpha(0.85f),
                    14f
                );
                paint.AddText(
                    row.Name,
                    x + 33f,
                    ty,
                    _theme.OnSurface,
                    fs
                );
            }
            else
            {
                Icons.Draw(
                    paint,
                    FileGlyph(row.Name),
                    new Rect(
                        x + 14f,
                        y,
                        16f,
                        RowH
                    ),
                    IconColor(row.Name),
                    14f
                );
                paint.AddText(
                    row.Name,
                    x + 33f,
                    ty,
                    _theme.OnSurface,
                    fs
                );
            }
        }
    }

    // ── Interaction ─────────────────────────────────────────────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        var h = _searchField.HitTest(point);
        if (h is not null) return h;
        return this; // own the row area for clicks/drag
    }

    private int RowAt(Offset point)
    {
        if (point.Y < Bounds.Y + HeaderH) return -1;
        var i = (int)((point.Y - (Bounds.Y + HeaderH)) / RowH);
        return i >= 0 && i < _rows.Length ? i : -1;
    }

    public override void OnPointerEnter()
    {
    }

    public override void OnPointerExit()
    {
        _hoverIndex = -1;
    }

    public override void OnPointerMove(Offset point)
    {
        _hoverIndex = RowAt(point);
        if (_pressIndex >= 0 && !_isDragging &&
            (MathF.Abs(point.X - _dragStart.X) > DragThreshold ||
             MathF.Abs(point.Y - _dragStart.Y) > DragThreshold) &&
            !_rows[_pressIndex].IsDir)
            _isDragging = true;
    }

    public override void OnPointerDown(Offset point)
    {
        _pressIndex = RowAt(point);
        _dragStart = point;
        _isDragging = false;
    }

    public override void OnPointerUp(Offset point)
    {
        if (_pressIndex < 0) return;
        var row = _rows[_pressIndex];

        if (row.IsDir)
        {
            if (!_expanded.Remove(row.FullPath)) _expanded.Add(row.FullPath);
            _dirty = true;
        }
        else if (_isDragging)
        {
            // Dropped somewhere (e.g. the viewport) — same pipeline as the old asset grid.
            _state.NotifyAssetDropped(row.FullPath, point);
        }
        else
        {
            var now = Environment.TickCount64;
            var isDouble = row.FullPath == _lastClickPath && now - _lastClickMs < 400;
            _selectedPath = row.FullPath;
            _state.NotifyAssetSelected(row.FullPath);
            if (isDouble && IsOpenable(row.FullPath))
                _state.NotifyOpenFile(row.FullPath);
            _lastClickPath = row.FullPath;
            _lastClickMs = now;
            MarkNeedsPaint();
        }

        _pressIndex = -1;
        _isDragging = false;
    }

    /// <summary>Text/code files the code editor can open on double-click.</summary>
    private static bool IsOpenable(string path)
    {
        return Ext(
            path,
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

    private static string Icon(string name)
    {
        return name switch {
            _ when Ext(
                name,
                ".glb",
                ".gltf",
                ".fbx",
                ".obj"
            ) => "[3D]",
            _ when Ext(
                name,
                ".png",
                ".jpg",
                ".jpeg",
                ".webp",
                ".gif"
            ) => "[TX]",
            _ when Ext(name, ".cs", ".lua") => "[CS]",
            _ when Ext(
                name,
                ".wav",
                ".ogg",
                ".mp3"
            ) => "[AU]",
            _ when Ext(name, ".scene") => "[SC]",
            _ => "[··]",
        };
    }

    private static Color IconColor(string name)
    {
        return name switch {
            _ when Ext(
                name,
                ".glb",
                ".gltf",
                ".fbx",
                ".obj"
            ) => new Color(0.4f, 0.75f, 1f),
            _ when Ext(
                name,
                ".png",
                ".jpg",
                ".jpeg",
                ".webp",
                ".gif"
            ) => new Color(0.4f, 0.9f, 0.5f),
            _ when Ext(name, ".cs", ".lua") => new Color(0.75f, 0.45f, 1f),
            _ when Ext(
                name,
                ".wav",
                ".ogg",
                ".mp3"
            ) => new Color(1f, 0.88f, 0.3f),
            _ when Ext(name, ".scene") => new Color(1f, 0.6f, 0.3f),
            _ => new Color(0.6f, 0.6f, 0.6f),
        };
    }

    private static string FileGlyph(string name)
    {
        return name switch {
            _ when Ext(
                name,
                ".glb",
                ".gltf",
                ".fbx",
                ".obj"
            ) => Icons.Cube,
            _ when Ext(
                name,
                ".png",
                ".jpg",
                ".jpeg",
                ".webp",
                ".gif"
            ) => Icons.Image,
            _ when Ext(name, ".cs", ".lua") => Icons.Code,
            _ when Ext(
                name,
                ".wav",
                ".ogg",
                ".mp3"
            ) => Icons.Audio,
            _ when Ext(name, ".scene") => Icons.Layers,
            _ => Icons.File,
        };
    }

    private static bool Ext(string name, params string[] exts)
    {
        return exts.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    // ── Tree model ──────────────────────────────────────────────────────────────

    private readonly record struct Entry(string FullPath, string Name, bool IsDir, int Depth);
}
