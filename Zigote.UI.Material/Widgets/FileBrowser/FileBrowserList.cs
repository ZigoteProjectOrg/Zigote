using System.Globalization;
using Zigote.Core.Events;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     The directory listing of <see cref="FileBrowserDialog" />: a direct-paint, clip-virtualized
///     row list (the TreeView pattern) with hover, path-keyed multi-selection (Cmd/Ctrl toggle,
///     Shift range), double-click activation, and full keyboard control — arrows/Home/End/PageUp/
///     PageDown move the cursor, Enter activates, Backspace goes up a directory, Space toggles,
///     and typing jumps to the next name with that prefix.
/// </summary>
internal sealed class FileBrowserList : Widget
{
    public const float RowHeight = 26f;
    private const float DoubleClickSeconds = 0.4f;
    private const float TypeAheadResetSeconds = 1.0f;

    private readonly FileBrowserModel _model;
    private int _cursor = -1;
    private int _hover = -1;
    private int _lastClickIndex = -1;
    private float _lastClickTime = -10f;
    private int _pageRows = 10;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private string _typeAhead = "";
    private float _typeAheadTime = -10f;

    public FileBrowserList(FileBrowserModel model)
    {
        _model = model;
    }

    /// <summary>Double-click / Enter: open a directory or confirm a file.</summary>
    public Action<FileBrowserEntry>? OnActivate { get; set; }

    public Action? OnSelectionChanged { get; set; }

    /// <summary>Backspace: navigate to the parent directory.</summary>
    public Action? OnNavigateUp { get; set; }

    /// <summary>The hosting scroll view, for keyboard reveal-into-view.</summary>
    public ScrollView? Scroll { get; set; }

    public override bool Focusable => true;
    public override bool HandlesDirectionalKeys => true;

    /// <summary>Shared column geometry (used by the header too): name is the flexible remainder.</summary>
    internal static (float Name, float Size, float Modified) Columns(float width)
    {
        const float size = 76f;
        const float modified = 128f;
        return (MathF.Max(120f, width - size - modified), size, modified);
    }

    /// <summary>After navigation: cursor cleared, scrolled back to the top.</summary>
    public void ResetCursor()
    {
        _cursor = -1;
        _hover = -1;
        if (Scroll is { } s) s.OffsetY = 0f;
        MarkNeedsLayout();
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 560f;
        // Keep some height even when empty so the "empty folder" message has a canvas.
        var h = MathF.Max(_model.Visible.Count * RowHeight, 120f);
        _size = c.Constrain(new Size(w, h));
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
    }

    public override void Paint(PaintList paint)
    {
        paint.AddClipStart(Bounds);
        var rows = _model.Visible;
        if (rows.Count == 0)
        {
            PaintEmptyMessage(paint);
            paint.AddClipEnd();
            return;
        }

        var clip = paint.CurrentClip;
        var first = 0;
        var last = rows.Count - 1;
        if (clip is { } cl)
        {
            first = Math.Max(0, (int)((cl.Y - Bounds.Y) / RowHeight));
            last = Math.Min(last, (int)((cl.Bottom - Bounds.Y) / RowHeight));
            _pageRows = Math.Max(3, (int)(cl.Height / RowHeight) - 1);
        }

        var (nameW, sizeW, _) = Columns(_size.Width);
        var fs = _theme.FontSizeCaption;
        var now = DateTime.Now;
        for (var i = first; i <= last; i++)
        {
            var entry = rows[i];
            var rowY = Bounds.Y + i * RowHeight;
            var selected = _model.IsSelected(entry);
            var bg = selected ? _theme.Primary.WithAlpha(0.26f)
                : i == _hover ? _theme.OnSurface.WithAlpha(0.06f)
                : default;
            if (bg.A > 0f)
                paint.AddRect(
                    new Rect(
                        Bounds.X + 4f,
                        rowY + 1f,
                        _size.Width - 8f,
                        RowHeight - 2f
                    ),
                    bg,
                    Radii.Sm
                );

            var (icon, iconColor) = IconFor(entry);
            Icons.Draw(
                paint,
                icon,
                new Rect(
                    Bounds.X + 10f,
                    rowY,
                    16f,
                    RowHeight
                ),
                iconColor,
                15f
            );

            var textY = rowY + (RowHeight - fs) / 2f + fs * 0.8f;
            var fg = entry.IsHidden ? _theme.TextMuted : _theme.OnSurface;

            // Clip the name to its column so long names never bleed into Size/Modified.
            paint.AddClipStart(
                new Rect(
                    Bounds.X,
                    rowY,
                    nameW - 6f,
                    RowHeight
                )
            );
            paint.AddText(
                entry.Name,
                Bounds.X + 32f,
                textY,
                fg,
                fs
            );
            paint.AddClipEnd();

            if (!entry.IsDirectory)
                paint.AddText(
                    FormatSize(entry.Size),
                    Bounds.X + nameW,
                    textY,
                    _theme.TextSecondary,
                    fs
                );
            paint.AddText(
                FormatDate(entry.Modified, now),
                Bounds.X + nameW + sizeW,
                textY,
                _theme.TextSecondary,
                fs
            );
        }

        paint.AddClipEnd();
    }

    private void PaintEmptyMessage(PaintList paint)
    {
        var message = _model.LastError ??
                      (_model.SearchText.Length > 0 ? "No matching items" : "Empty folder");
        var fs = _theme.FontSizeCaption;
        paint.AddText(
            message,
            Bounds.X + 16f,
            Bounds.Y + 28f,
            _model.LastError is null ? _theme.TextMuted : _theme.Warning,
            fs
        );
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

    private int RowIndexAt(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return -1;
        var idx = (int)((point.Y - Bounds.Y) / RowHeight);
        return idx >= 0 && idx < _model.Visible.Count ? idx : -1;
    }

    public override void OnPointerMove(Offset point)
    {
        var idx = RowIndexAt(point);
        if (idx == _hover) return;
        _hover = idx;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (_hover == -1) return;
        _hover = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);
        var idx = RowIndexAt(point);
        if (idx < 0)
        {
            _model.ClearSelection();
            _cursor = -1;
            OnSelectionChanged?.Invoke();
            MarkNeedsPaint();
            return;
        }

        var mods = App.Active?.CurrentModifiers ?? Modifiers.None;
        var toggle = mods.HasCommand();
        var range = (mods & Modifiers.Shift) != 0;

        var time = App.Active?.Time ?? 0f;
        var isDoubleClick = !toggle && !range && idx == _lastClickIndex &&
                            time - _lastClickTime < DoubleClickSeconds;
        _lastClickIndex = idx;
        _lastClickTime = time;

        _model.SelectIndex(idx, toggle, range);
        _cursor = idx;
        OnSelectionChanged?.Invoke();
        MarkNeedsPaint();

        if (isDoubleClick) OnActivate?.Invoke(_model.Visible[idx]);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        var extend = (mods & Modifiers.Shift) != 0;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Up:
                MoveCursor(-1, extend);
                break;
            case KeyCode.Down:
                MoveCursor(1, extend);
                break;
            case KeyCode.Home:
                MoveCursorTo(0, extend);
                break;
            case KeyCode.End:
                MoveCursorTo(_model.Visible.Count - 1, extend);
                break;
            case KeyCode.PageUp:
                MoveCursor(-_pageRows, extend);
                break;
            case KeyCode.PageDown:
                MoveCursor(_pageRows, extend);
                break;
            case KeyCode.Enter or KeyCode.KpEnter:
                if (_cursor >= 0 && _cursor < _model.Visible.Count)
                    OnActivate?.Invoke(_model.Visible[_cursor]);
                break;
            case KeyCode.Backspace:
                OnNavigateUp?.Invoke();
                break;
            case KeyCode.Space:
                if (_model.AllowMultiSelect && _cursor >= 0)
                {
                    _model.SelectIndex(_cursor, true);
                    OnSelectionChanged?.Invoke();
                    MarkNeedsPaint();
                }

                break;
        }
    }

    /// <summary>Type-ahead: printable characters jump to the next matching name.</summary>
    public override void OnTextInput(string text)
    {
        if (string.IsNullOrEmpty(text) || _model.Visible.Count == 0) return;
        var time = App.Active?.Time ?? 0f;
        if (time - _typeAheadTime > TypeAheadResetSeconds) _typeAhead = "";
        _typeAheadTime = time;
        _typeAhead += text;

        // A growing prefix should keep matching the current row; a fresh single letter scans on.
        var from = _typeAhead.Length > text.Length ? _cursor - 1 : _cursor;
        var idx = _model.TypeAheadIndex(_typeAhead, from);
        if (idx >= 0) MoveCursorTo(idx, false);
    }

    private void MoveCursor(int delta, bool extend)
    {
        if (_model.Visible.Count == 0) return;
        var target = _cursor < 0 ? (delta > 0 ? 0 : _model.Visible.Count - 1)
            : Math.Clamp(_cursor + delta, 0, _model.Visible.Count - 1);
        MoveCursorTo(target, extend);
    }

    private void MoveCursorTo(int index, bool extend)
    {
        if ((uint)index >= (uint)_model.Visible.Count) return;
        _cursor = index;
        _model.SelectIndex(index, range: extend);
        Scroll?.EnsureVisible(index * RowHeight, RowHeight);
        OnSelectionChanged?.Invoke();
        MarkNeedsPaint();
    }

    // ── Formatting / icons ────────────────────────────────────────────────────

    private (string Glyph, Color Color) IconFor(in FileBrowserEntry entry)
    {
        if (entry.IsDirectory) return (Icons.Folder, _theme.Info);
        var ext = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
        return ext switch {
            "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" or "tga" or "hdr" or "ktx2" =>
                (Icons.Image, _theme.Success),
            "wav" or "mp3" or "ogg" or "flac" => (Icons.Audio, _theme.Warning),
            "mp4" or "mov" or "avi" or "mkv" or "webm" => (Icons.Movie, _theme.Warning),
            "cs" or "fs" or "zig" or "js" or "ts" or "json" or "xml" or "yaml" or "yml"
                or "toml" or "wgsl" or "glsl" or "hlsl" or "sh" => (Icons.Code, _theme.TextSecondary),
            "md" or "txt" or "log" => (Icons.Description, _theme.TextSecondary),
            _ => (Icons.File, _theme.TextMuted),
        };
    }

    internal static string FormatSize(long bytes)
    {
        return bytes switch {
            < 0 => "",
            < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
            < 1024 * 1024 =>
                (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
            < 1024L * 1024 * 1024 =>
                (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
            _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) +
                 " GB",
        };
    }

    internal static string FormatDate(DateTime modified, DateTime now)
    {
        if (modified.Date == now.Date)
            return "Today " + modified.ToString("HH:mm", CultureInfo.InvariantCulture);
        return modified.ToString(
            modified.Year == now.Year ? "MMM d HH:mm" : "MMM d, yyyy",
            CultureInfo.InvariantCulture
        );
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            _model.Visible.Count,
            _model.SelectedPaths.Count,
            _cursor,
            _hover
        );
    }
}

/// <summary>
///     The non-scrolling column header above <see cref="FileBrowserList" /> — Name / Size /
///     Modified with a sort direction arrow; clicking a column sorts by it (again to flip).
/// </summary>
internal sealed class FileBrowserHeader : Widget
{
    public const float Height = 24f;
    private readonly FileBrowserModel _model;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public FileBrowserHeader(FileBrowserModel model)
    {
        _model = model;
    }

    public Action<FileSortColumn>? OnSort { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 560f;
        _size = c.Constrain(new Size(w, Height));
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
    }

    public override void Paint(PaintList paint)
    {
        var (nameW, sizeW, _) = FileBrowserList.Columns(_size.Width);
        var fs = _theme.FontSizeCaption - 1f;
        var textY = Bounds.Y + (Height - fs) / 2f + fs * 0.8f;

        DrawLabel(paint, "Name", FileSortColumn.Name, Bounds.X + 32f, textY, fs);
        DrawLabel(paint, "Size", FileSortColumn.Size, Bounds.X + nameW, textY, fs);
        DrawLabel(paint, "Modified", FileSortColumn.Modified, Bounds.X + nameW + sizeW, textY, fs);
        paint.AddRect(
            new Rect(
                Bounds.X,
                Bounds.Bottom - 1f,
                _size.Width,
                1f
            ),
            _theme.Separator
        );
    }

    private void DrawLabel(PaintList paint, string label, FileSortColumn column, float x,
        float textY, float fs)
    {
        var active = _model.SortColumn == column;
        paint.AddText(
            label,
            x,
            textY,
            active ? _theme.OnSurface : _theme.TextMuted,
            fs
        );
        if (active)
            Icons.Draw(
                paint,
                _model.SortAscending ? Icons.DropUp : Icons.DropDown,
                new Rect(
                    x + label.Length * fs * 0.62f + 2f,
                    Bounds.Y,
                    12f,
                    Height
                ),
                _theme.TextSecondary,
                12f
            );
    }

    public override void OnPointerDown(Offset point)
    {
        var (nameW, sizeW, _) = FileBrowserList.Columns(_size.Width);
        var x = point.X - Bounds.X;
        var column = x < nameW ? FileSortColumn.Name
            : x < nameW + sizeW ? FileSortColumn.Size
            : FileSortColumn.Modified;
        OnSort?.Invoke(column);
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        return MouseCursor.Pointer;
    }
}
