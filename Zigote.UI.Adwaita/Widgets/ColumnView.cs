using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>How many rows of an <see cref="AdwColumnView{T}" /> can be selected at once.</summary>
public enum AdwSelectionMode
{
    /// <summary>Rows highlight on hover and activate, but never stay selected.</summary>
    None,

    /// <summary>One row at a time.</summary>
    Single,

    /// <summary>Ctrl/⌘-click toggles, Shift-click and Shift-arrows extend, Ctrl/⌘+A selects all.</summary>
    Multiple,
}

/// <summary>Which edge of its column a cell's text sits against.</summary>
public enum AdwColumnAlign
{
    Start,

    /// <summary>For numbers: the digits line up on the right.</summary>
    End,
}

/// <summary>One column of an <see cref="AdwColumnView{T}" />: a title and how to print a row in it.</summary>
public sealed class AdwColumn<T>
{
    public AdwColumn(string title, Func<T, string> text)
    {
        Title = title;
        Text = text;
    }

    public string Title { get; }

    /// <summary>The cell text for a row. Called once per row and cached until the view is refreshed.</summary>
    public Func<T, string> Text { get; }

    /// <summary>Width in points for a fixed column; the starting width a resize begins from.</summary>
    public float Width { get; set; } = 120f;

    /// <summary>Narrowest the column gets, by resizing or by sharing out the view's width.</summary>
    public float MinWidth { get; set; } = 48f;

    /// <summary>
    ///     Take a share of the width the fixed columns leave (GtkColumnViewColumn:expand). Resizing
    ///     an expanding column by hand turns it into a fixed one at that width.
    /// </summary>
    public bool Expand { get; set; }

    /// <summary>Whether dragging the column's right edge in the header resizes it.</summary>
    public bool Resizable { get; set; } = true;

    /// <summary>
    ///     Order for sorting by this column; null makes the header inert. A table of more than
    ///     20,000 rows sorts on a worker thread, so this must be safe to call off the UI thread —
    ///     reading immutable rows is.
    /// </summary>
    public Comparison<T>? Sort { get; set; }

    public AdwColumnAlign Align { get; set; } = AdwColumnAlign.Start;

    /// <summary>Print the column as secondary text (<c>.dim-label</c>).</summary>
    public bool Dim { get; set; }

    /// <summary>A glyph before the text — an <see cref="Icons" /> constant per row, or null for none.</summary>
    public Func<T, string?>? Icon { get; set; }

    /// <summary>Sort by a key: <c>new AdwColumn&lt;File&gt;("Size", f => …).SortBy(f => f.Bytes)</c>.</summary>
    public AdwColumn<T> SortBy<TKey>(Func<T, TKey> key)
    {
        Sort = (a, b) => Comparer<TKey>.Default.Compare(x: key(a), y: key(b));
        return this;
    }
}

/// <summary>
///     GtkColumnView with libadwaita's styling — the data table of a GNOME app: a header of column
///     titles over rows of cells, each column printed from the row by a function.
///     <para>
///         Built for size: rows are painted, not built, and only those in view, so a hundred
///         thousand rows scroll as cheaply as twenty. Click a sortable title to sort (again to
///         reverse), drag a title's right edge to resize, and the header stays put while the rows
///         scroll under it. A table wider than the view scrolls sideways with its header.
///     </para>
///     <para>
///         Keys: Up/Down, Page Up/Down and Home/End move the cursor (with Shift, extend the
///         selection), Space toggles a row, Enter activates it, Ctrl/⌘+A selects all.
///     </para>
/// </summary>
public sealed class AdwColumnView<T> : ComposedWidget
{
    private const float HeaderH = 34f;
    private const float CellPadX = 12f;
    private const float IconSize = 16f;
    private const float DoubleClickSeconds = 0.4f;
    private const float ResizeGrab = 5f;

    /// <summary>Above this many rows a sort runs on a worker thread instead of stalling frames.</summary>
    private const int BackgroundSortRows = 20_000;

    private readonly List<AdwColumn<T>> _columns;
    private readonly HashSet<int> _selected = []; // item indices — they survive a re-sort
    private int _anchor = -1; // view position Shift extends from
    private Body? _body;
    private string?[][] _cells = [];
    private float[][] _cellWidths = []; // measured text widths, 0 = not measured yet
    private string?[][] _fitted = []; // ellipsized texts for cells that overflow their column
    private float[] _fittedFor = []; // the width each column's _fitted entries were cut for
    private int _cursor = -1; // view position
    private Header? _header;
    private IReadOnlyList<T> _items;
    private int[] _order = []; // view position → item index
    private ScrollView? _scroll;
    private bool _sortAscending = true;
    private int _sortVersion;
    private int _sortColumn = -1;
    private ThemeData _theme = ThemeData.Dark;
    private float _viewW;
    private float[] _widths = [];

    public AdwColumnView(IReadOnlyList<T> items, params AdwColumn<T>[] columns)
    {
        _items = items;
        _columns = [..columns];
        Reset();
    }

    /// <summary>The rows. Assigning a new list clears the selection and re-applies the sort.</summary>
    public IReadOnlyList<T> Items
    {
        get => _items;
        set
        {
            _items = value;
            _selected.Clear();
            _cursor = _anchor = -1;
            Reset();
            OnSelectionChanged?.Invoke();
        }
    }

    public IReadOnlyList<AdwColumn<T>> Columns => _columns;

    public AdwSelectionMode SelectionMode { get; set; } = AdwSelectionMode.Single;

    /// <summary>Hairlines between rows (GtkColumnView:show-row-separators).</summary>
    public bool ShowRowSeparators { get; set; }

    /// <summary>
    ///     Dense rows (<c>.data-table</c>) — for tables read in bulk, like a log or an inspector.
    /// </summary>
    public bool Compact { get; set; }

    /// <summary>Accessible name for the table.</summary>
    public string? SemanticsLabel { get; set; }

    /// <summary>Double-click, or Enter on the cursor row.</summary>
    public Action<T>? OnActivated { get; set; }

    public Action? OnSelectionChanged { get; set; }

    /// <summary>Right-click: the row under the pointer (null on empty space) and the point.</summary>
    public Action<T?, Offset>? OnContextMenu { get; set; }

    /// <summary>The selected rows, in view order.</summary>
    public IEnumerable<T> SelectedItems
    {
        get
        {
            foreach (int item in _order)
            {
                if (_selected.Contains(item)) yield return _items[item];
            }
        }
    }

    public int SelectedCount => _selected.Count;

    /// <summary>Index of the column the rows are sorted by, or -1 for the items' own order.</summary>
    public int SortColumn => _sortColumn;

    public bool SortAscending => _sortAscending;

    private float RowH
    {
        get
        {
            float h = Compact ? 28f : 36f;
            return AdwTouchMetrics.IsCompact ? MathF.Max(x: h, y: AdwTouchMetrics.MinTarget) : h;
        }
    }

    /// <summary>
    ///     Re-read the rows after changing them in place: cell texts are re-printed and the sort
    ///     re-applied. The selection is kept.
    /// </summary>
    public void Refresh()
    {
        Reset();
        _body?.MarkNeedsLayout();
    }

    /// <summary>Sort by column <paramref name="column" /> (-1 restores the items' own order).</summary>
    public void SortBy(int column, bool ascending = true)
    {
        _sortColumn = column >= 0 && column < _columns.Count && _columns[column].Sort is not null ? column : -1;
        _sortAscending = ascending;
        ApplySort();
        _header?.MarkNeedsPaint();
        _body?.MarkNeedsPaint();
    }

    /// <summary>Select exactly the rows at these item indices.</summary>
    public void Select(IEnumerable<int> itemIndices)
    {
        _selected.Clear();
        foreach (int i in itemIndices)
        {
            if ((uint)i < (uint)_items.Count) _selected.Add(i);
        }

        _body?.MarkNeedsPaint();
        OnSelectionChanged?.Invoke();
    }

    private void Reset()
    {
        int n = _columns.Count;
        _cells = new string?[n][];
        _cellWidths = new float[n][];
        _fitted = new string?[n][];
        _fittedFor = new float[n];
        for (int c = 0; c < n; c++)
        {
            _cells[c] = new string?[_items.Count];
            _cellWidths[c] = new float[_items.Count];
            _fitted[c] = new string?[_items.Count];
        }
        _order = new int[_items.Count];
        for (int i = 0; i < _order.Length; i++) _order[i] = i;
        ApplySort();
    }

    private void ApplySort()
    {
        if (_order.Length != _items.Count) return;
        int version = ++_sortVersion; // any sort still running on a worker is now stale
        if (_sortColumn < 0 || _columns[_sortColumn].Sort is not { } compare)
        {
            for (int i = 0; i < _order.Length; i++) _order[i] = i;
            return;
        }

        int dir = _sortAscending ? 1 : -1;
        var items = _items;
        if (_order.Length < BackgroundSortRows || Owner is not { } app)
        {
            SortOrder(order: _order, items: items, compare: compare, dir: dir);
            return;
        }

        // n·log n comparisons over a hundred thousand rows take tens of milliseconds — several
        // frames. Sort a copy on a worker and swap it in when it lands; until then the old order
        // stays up. The tie-break makes the result independent of the order it starts from.
        int[] order = (int[])_order.Clone();
        Task.Run(() =>
            {
                SortOrder(order: order, items: items, compare: compare, dir: dir);
                app.Post(() =>
                    {
                        if (version != _sortVersion || !ReferenceEquals(objA: items, objB: _items)) return;
                        _order = order;
                        _body?.MarkNeedsPaint();
                    }
                );
            }
        );
    }

    /// <summary>
    ///     Stable: ties fall back to the items' own order, so reversing a sort reverses only what
    ///     differs and the result does not depend on the order the sort started from.
    /// </summary>
    private static void SortOrder(int[] order, IReadOnlyList<T> items, Comparison<T> compare, int dir) =>
        Array.Sort(
            array: order,
            comparison: (a, b) =>
            {
                int r = compare(x: items[a], y: items[b]) * dir;
                return r != 0 ? r : a.CompareTo(b);
            }
        );

    private string Cell(int column, int item) =>
        _cells[column][item] ??= _columns[column].Text(_items[item]);

    /// <summary>
    ///     The cell's text cut to <paramref name="room" /> with an ellipsis, and its width. Every
    ///     result is cached — the measure per cell, the cut per cell and column width — so a steady
    ///     scroll measures nothing and a column resize re-cuts only the rows on screen.
    /// </summary>
    private (string Text, float Width) Fit(int column, int item, float room, float fs)
    {
        string text = Cell(column: column, item: item);
        ref float measured = ref _cellWidths[column][item];
        if (measured == 0f && text.Length > 0) measured = TextMeasure.Width(text: text, fontSize: fs);
        if (measured <= room) return (text, measured);

        if (_fittedFor[column] != room)
        {
            Array.Clear(_fitted[column]);
            _fittedFor[column] = room;
        }

        string cut = _fitted[column][item] ??= Ellipsize(text: text, room: room, fs: fs);
        return (cut, room); // a cut text fills its room, to within the ellipsis
    }

    internal static string Ellipsize(string text, float room, float fs)
    {
        float ellipsis = TextMeasure.Width(text: "…", fontSize: fs);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (TextMeasure.Width(text: text.AsSpan(start: 0, length: mid), fontSize: fs) + ellipsis <= room) lo = mid;
            else hi = mid - 1;
        }

        return lo == 0 ? "…" : string.Concat(str0: text.AsSpan(start: 0, length: lo).TrimEnd(), str1: "…");
    }

    // ── Column geometry ───────────────────────────────────────────────────────

    /// <summary>Resolve column widths for the view width: fixed ones as set, the rest share.</summary>
    private float ResolveWidths(float viewW)
    {
        if (_widths.Length != _columns.Count) _widths = new float[_columns.Count];
        float fixedW = 0f;
        int expanding = 0;
        foreach (var col in _columns)
        {
            if (col.Expand) expanding++;
            else fixedW += MathF.Max(x: col.Width, y: col.MinWidth);
        }

        float share = expanding > 0 ? MathF.Max(x: 0f, y: viewW - fixedW) / expanding : 0f;
        float total = 0f;
        for (int c = 0; c < _columns.Count; c++)
        {
            var col = _columns[c];
            _widths[c] = MathF.Round(MathF.Max(x: col.MinWidth, y: col.Expand ? share : col.Width));
            total += _widths[c];
        }

        return MathF.Max(x: total, y: viewW);
    }

    private float ColumnX(int column)
    {
        float x = 0f;
        for (int c = 0; c < column; c++) x += _widths[c];
        return x;
    }

    // ── Tree ──────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        _header = new Header(this);
        _body = new Body(this);
        _scroll = new ScrollView(_body) {
            ScrollHorizontal = true,
            // The header follows the rows sideways.
            OnScrolled = (_, _) => _header?.MarkNeedsPaint(),
        };
        return new DecoratedBox {
            // A column view is a `.view`.
            Fill = AdwPalette.For(_theme).ViewBg,
            Child = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    _header,
                    new Expanded(_scroll),
                },
            },
        };
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void SelectAt(int position, bool toggle, bool range)
    {
        if ((uint)position >= (uint)_order.Length) return;
        _cursor = position;
        if (SelectionMode == AdwSelectionMode.None)
        {
            _body?.MarkNeedsPaint();
            return;
        }

        int item = _order[position];
        if (SelectionMode == AdwSelectionMode.Single || (!toggle && !range))
        {
            _selected.Clear();
            _selected.Add(item);
            _anchor = position;
        }
        else if (range)
        {
            if (_anchor < 0) _anchor = position;
            if (!toggle) _selected.Clear();
            int lo = Math.Min(val1: _anchor, val2: position), hi = Math.Max(val1: _anchor, val2: position);
            for (int p = lo; p <= hi; p++) _selected.Add(_order[p]);
        }
        else
        {
            if (!_selected.Remove(item)) _selected.Add(item);
            _anchor = position;
        }

        _body?.MarkNeedsPaint();
        OnSelectionChanged?.Invoke();
    }

    private void MoveCursor(int position, bool extend)
    {
        if (_order.Length == 0) return;
        position = Math.Clamp(value: position, min: 0, max: _order.Length - 1);
        SelectAt(position: position, toggle: false, range: extend && SelectionMode == AdwSelectionMode.Multiple);
        _scroll?.EnsureVisible(top: position * RowH, height: RowH);
    }

    /// <summary>The painted rows, as tall as all of them; the scroll view clips them.</summary>
    private sealed class Body(AdwColumnView<T> owner) : Widget
    {
        private int _hover = -1;
        private int _lastClick = -1;
        private float _lastClickTime = -10f;
        private int _pageRows = 10;
        private Size _size;

        public override bool Focusable => true;
        public override bool HandlesDirectionalKeys => true;

        public override Size Measure(Constraints c)
        {
            float w = owner.ResolveWidths(owner._viewW);
            _size = new Size(width: w, height: MathF.Max(x: owner._order.Length * owner.RowH, y: owner.RowH));
            return _size;
        }

        public override void Layout(Offset origin) =>
            Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);

        public override void Paint(PaintList paint)
        {
            var theme = owner._theme;
            var p = AdwPalette.For(theme);
            float rowH = owner.RowH;
            int count = owner._order.Length;
            if (count == 0) return;

            int first = 0, last = count - 1;
            if (paint.CurrentClip is { } clip)
            {
                first = Math.Max(val1: 0, val2: (int)((clip.Y - Bounds.Y) / rowH));
                last = Math.Min(val1: last, val2: (int)((clip.Bottom - Bounds.Y) / rowH));
                _pageRows = Math.Max(val1: 3, val2: (int)(clip.Height / rowH) - 1);
            }

            float fs = AdwTypography.Body.Size;
            for (int pos = first; pos <= last; pos++)
            {
                int item = owner._order[pos];
                float y = Bounds.Y + (pos * rowH);
                var row = new Rect(x: Bounds.X, y: y, width: _size.Width, height: rowH);
                bool selected = owner.SelectionMode != AdwSelectionMode.None && owner._selected.Contains(item);
                var bg = AdwStyle.ViewRowFill(theme: theme, hovered: pos == _hover, pressed: false, selected: selected);
                if (bg.A > 0f) paint.AddRect(bounds: row, color: bg);
                if (owner.ShowRowSeparators && pos > 0)
                    paint.AddRect(bounds: new Rect(x: row.X, y: y, width: row.Width, height: 1f), color: theme.Separator);
                if (pos == owner._cursor && Focused)
                    paint.AddFocusRing(bounds: row, radius: 0f, theme: theme);

                float x = Bounds.X;
                float baseline = y + ((rowH - fs) / 2f) + (fs * 0.8f);
                for (int c = 0; c < owner._columns.Count; c++)
                {
                    var col = owner._columns[c];
                    float w = owner._widths[c];
                    var color = col.Dim ? p.DimLabel : p.ViewFg;
                    float textX = x + CellPadX;
                    paint.AddClipStart(new Rect(x: x, y: y, width: MathF.Max(x: 0f, y: w - 4f), height: rowH));
                    if (col.Icon?.Invoke(owner._items[item]) is { } glyph)
                    {
                        Icons.Draw(paint: paint, glyph: glyph, box: new Rect(x: textX, y: y, width: IconSize, height: rowH),
                            color: color, size: IconSize);
                        textX += IconSize + 8f;
                    }

                    float room = MathF.Max(x: 0f, y: x + w - CellPadX - textX);
                    (string text, float textW) = owner.Fit(column: c, item: item, room: room, fs: fs);
                    if (col.Align == AdwColumnAlign.End) textX = x + w - CellPadX - textW;
                    paint.AddText(text: text, baselineX: textX, baselineY: baseline, color: color, fontSize: fs);
                    paint.AddClipEnd();
                    x += w;
                }
            }
        }

        private int PositionAt(Offset point)
        {
            if (!Bounds.Contains(px: point.X, py: point.Y)) return -1;
            int pos = (int)((point.Y - Bounds.Y) / owner.RowH);
            return pos < owner._order.Length ? pos : -1;
        }

        public override void OnPointerMove(Offset point)
        {
            int pos = PositionAt(point);
            if (pos == _hover) return;
            _hover = pos;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            if (_hover < 0) return;
            _hover = -1;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset point)
        {
            var app = App.Active;
            app?.RequestFocus(this);
            int pos = PositionAt(point);
            if (pos < 0) return;

            var mods = app?.CurrentModifiers ?? Modifiers.None;
            bool toggle = mods.HasCommand(), range = (mods & Modifiers.Shift) != 0;
            float time = app?.Time ?? 0f;
            bool doubleClick = !toggle && !range && pos == _lastClick && time - _lastClickTime < DoubleClickSeconds;
            _lastClick = pos;
            _lastClickTime = time;

            owner.SelectAt(position: pos, toggle: toggle, range: range);
            if (doubleClick) owner.OnActivated?.Invoke(owner._items[owner._order[pos]]);
        }

        public override void OnRightClick(Offset point)
        {
            App.Active?.RequestFocus(this);
            int pos = PositionAt(point);
            if (pos < 0)
            {
                owner.OnContextMenu?.Invoke(arg1: default, arg2: point);
                return;
            }

            // Right-clicking inside the selection keeps it; elsewhere it moves to that row first.
            if (!owner._selected.Contains(owner._order[pos])) owner.SelectAt(position: pos, toggle: false, range: false);
            owner.OnContextMenu?.Invoke(arg1: owner._items[owner._order[pos]], arg2: point);
        }

        public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
        {
            if (!down) return;
            bool shift = (mods & Modifiers.Shift) != 0;
            int cursor = owner._cursor;
            switch ((KeyCode)scancode)
            {
                case KeyCode.Up:
                    owner.MoveCursor(position: cursor < 0 ? owner._order.Length - 1 : cursor - 1, extend: shift);
                    break;
                case KeyCode.Down:
                    owner.MoveCursor(position: cursor + 1, extend: shift);
                    break;
                case KeyCode.PageUp:
                    owner.MoveCursor(position: cursor - _pageRows, extend: shift);
                    break;
                case KeyCode.PageDown:
                    owner.MoveCursor(position: cursor + _pageRows, extend: shift);
                    break;
                case KeyCode.Home:
                    owner.MoveCursor(position: 0, extend: shift);
                    break;
                case KeyCode.End:
                    owner.MoveCursor(position: owner._order.Length - 1, extend: shift);
                    break;
                case KeyCode.Space when cursor >= 0:
                    owner.SelectAt(position: cursor, toggle: owner.SelectionMode == AdwSelectionMode.Multiple, range: false);
                    break;
                case KeyCode.Enter or KeyCode.KpEnter when cursor >= 0 && cursor < owner._order.Length:
                    owner.OnActivated?.Invoke(owner._items[owner._order[cursor]]);
                    break;
                case KeyCode.A when mods.HasCommand() && owner.SelectionMode == AdwSelectionMode.Multiple:
                    owner.Select(Enumerable.Range(start: 0, count: owner._items.Count));
                    break;
            }
        }

        public override void DescribeSemantics(SemanticsConfiguration config)
        {
            config.Role = SemanticsRole.List;
            config.Label = owner.SemanticsLabel;
            config.Value = $"{owner._order.Length} rows, {owner._selected.Count} selected";
            config.Actions = SemanticsAction.Focus;
            config.AddFlag(flag: SemanticsFlags.Focusable, on: true).AddFlag(flag: SemanticsFlags.Focused, on: Focused);
        }

        public override int DebugStateHash() =>
            HashCode.Combine(value1: owner._order.Length, value2: owner._selected.Count, value3: owner._cursor, value4: _hover);
    }

    /// <summary>The column titles: sort on click, resize by dragging a title's right edge.</summary>
    private sealed class Header(AdwColumnView<T> owner) : Widget
    {
        private int _dragColumn = -1;
        private float _dragStartWidth;
        private float _dragStartX;
        private int _hover = -1;
        private int _pressed = -1;
        private int _resizeHover = -1;
        private Size _size;
        private string?[] _titleCut = []; // ellipsized titles, cut for _titleRoom
        private float[] _titleRoom = [];
        private float[] _titleWidth = [];

        private float ScrollX => owner._scroll?.OffsetX ?? 0f;

        public override Size Measure(Constraints c)
        {
            float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 480f;
            // The body measures after this, so it learns the view width from here.
            owner._viewW = w;
            owner.ResolveWidths(w);
            _size = new Size(width: w, height: HeaderH);
            return _size;
        }

        public override void Layout(Offset origin) =>
            Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);

        public override void Paint(PaintList paint)
        {
            var theme = owner._theme;
            var p = AdwPalette.For(theme);
            float fs = AdwTypography.CaptionHeading.Size;
            float baseline = Bounds.Y + ((HeaderH - fs) / 2f) + (fs * 0.8f);

            paint.AddClipStart(Bounds);
            float x = Bounds.X - ScrollX;
            for (int c = 0; c < owner._columns.Count; c++)
            {
                var col = owner._columns[c];
                float w = owner._widths[c];
                var cell = new Rect(x: x, y: Bounds.Y, width: w, height: HeaderH);
                if (col.Sort is not null && (_hover == c || _pressed == c) && _dragColumn < 0)
                    paint.AddRect(bounds: cell, color: _pressed == c ? p.ViewActiveFill : p.ViewHoverFill);

                bool sorted = owner._sortColumn == c;
                var color = sorted ? p.ViewFg : p.DimLabel;
                float arrowW = sorted ? 16f : 0f;
                (string title, float titleW) = Title(column: c, room: MathF.Max(x: 0f, y: w - (CellPadX * 2f) - arrowW), fs: fs);
                float tx = col.Align == AdwColumnAlign.End ? x + w - CellPadX - titleW - arrowW : x + CellPadX;
                paint.AddClipStart(new Rect(x: x, y: Bounds.Y, width: MathF.Max(x: 0f, y: w - 4f), height: HeaderH));
                paint.AddText(text: title, baselineX: tx, baselineY: baseline, color: color, fontSize: fs,
                    fontWeight: FontWeight.Bold);
                if (sorted)
                {
                    Icons.Draw(paint: paint, glyph: owner._sortAscending ? Icons.DropUp : Icons.DropDown,
                        box: new Rect(x: tx + titleW, y: Bounds.Y, width: arrowW, height: HeaderH), color: color, size: 16f);
                }

                paint.AddClipEnd();

                // The resize handle shows only where it can be grabbed, and while it is held.
                if (col.Resizable && (_resizeHover == c || _dragColumn == c))
                {
                    paint.AddRect(bounds: new Rect(x: x + w - 1f, y: Bounds.Y + 6f, width: 1f, height: HeaderH - 12f),
                        color: _dragColumn == c ? p.Accent : theme.Separator);
                }

                x += w;
            }

            paint.AddClipEnd();
            paint.AddRect(bounds: new Rect(x: Bounds.X, y: Bounds.Bottom - 1f, width: Bounds.Width, height: 1f),
                color: theme.Separator);
        }

        /// <summary>The title, ellipsized when its column is too narrow; recut only when the room changes.</summary>
        private (string Text, float Width) Title(int column, float room, float fs)
        {
            if (_titleCut.Length != owner._columns.Count)
            {
                _titleCut = new string?[owner._columns.Count];
                _titleRoom = new float[owner._columns.Count];
                _titleWidth = new float[owner._columns.Count];
            }

            string title = owner._columns[column].Title;
            if (_titleRoom[column] != room || _titleCut[column] is null)
            {
                _titleRoom[column] = room;
                // Bold is wider than the cells' regular weight; measure it as drawn.
                float full = TextMeasure.Width(text: title, fontSize: fs, weight: FontWeight.Bold);
                string cut = full <= room ? title : Ellipsize(text: title, room: room * 0.94f, fs: fs);
                _titleCut[column] = cut;
                _titleWidth[column] = ReferenceEquals(objA: cut, objB: title)
                    ? full
                    : TextMeasure.Width(text: cut, fontSize: fs, weight: FontWeight.Bold);
            }

            return (_titleCut[column]!, _titleWidth[column]);
        }

        /// <summary>Column whose right edge is within grabbing distance of the point, or -1.</summary>
        private int ResizeEdgeAt(Offset point)
        {
            float x = Bounds.X - ScrollX;
            for (int c = 0; c < owner._columns.Count; c++)
            {
                x += owner._widths[c];
                if (owner._columns[c].Resizable && MathF.Abs(point.X - x) <= ResizeGrab) return c;
            }

            return -1;
        }

        private int ColumnAt(Offset point)
        {
            float x = Bounds.X - ScrollX;
            for (int c = 0; c < owner._columns.Count; c++)
            {
                if (point.X >= x && point.X < x + owner._widths[c]) return c;
                x += owner._widths[c];
            }

            return -1;
        }

        public override MouseCursor? GetCursor(Offset point)
        {
            if (_dragColumn >= 0 || ResizeEdgeAt(point) >= 0) return MouseCursor.ResizeEW;
            int c = ColumnAt(point);
            return c >= 0 && owner._columns[c].Sort is not null ? MouseCursor.Pointer : null;
        }

        public override void OnPointerMove(Offset point)
        {
            if (_dragColumn >= 0)
            {
                var col = owner._columns[_dragColumn];
                col.Width = MathF.Max(x: col.MinWidth, y: MathF.Round(_dragStartWidth + point.X - _dragStartX));
                MarkNeedsLayout();
                owner._body?.MarkNeedsLayout();
                return;
            }

            int resize = ResizeEdgeAt(point);
            int hover = resize >= 0 ? -1 : ColumnAt(point);
            if (resize == _resizeHover && hover == _hover) return;
            _resizeHover = resize;
            _hover = hover;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            if (_dragColumn >= 0) return;
            _hover = _resizeHover = -1;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset point)
        {
            int edge = ResizeEdgeAt(point);
            if (edge >= 0)
            {
                var col = owner._columns[edge];
                // Grabbing an expanding column pins it at the width it has on screen.
                col.Width = owner._widths[edge];
                col.Expand = false;
                _dragColumn = edge;
                _dragStartX = point.X;
                _dragStartWidth = col.Width;
                MarkNeedsPaint();
                return;
            }

            int c = ColumnAt(point);
            _pressed = c >= 0 && owner._columns[c].Sort is not null ? c : -1;
            MarkNeedsPaint();
        }

        public override void OnPointerUp(Offset point)
        {
            if (_dragColumn >= 0)
            {
                _dragColumn = -1;
                MarkNeedsPaint();
                return;
            }

            int pressed = _pressed;
            _pressed = -1;
            MarkNeedsPaint();
            if (pressed < 0 || ColumnAt(point) != pressed) return;
            bool ascending = owner._sortColumn != pressed || !owner._sortAscending;
            owner.SortBy(column: pressed, ascending: ascending);
        }

        public override void OnPointerCancel()
        {
            _dragColumn = _pressed = -1;
            MarkNeedsPaint();
        }

        public override bool CanTouchDrag(bool vertical) => _dragColumn >= 0;
    }
}
