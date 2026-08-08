using System.Diagnostics;
using System.Runtime.CompilerServices;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     2D / UI inspector, a widget-inspector-style panel: a select-widget mode (click a widget
///     on-screen to select it in the tree), a searchable live widget tree with inline content details
///     (hovering a row highlights the widget on-screen; selecting reveals + scrolls to it), a clickable
///     ancestor breadcrumb, a mini layout explorer (the widget's box drawn inside its parent's), live
///     size/constraints rows, and the property view — an expandable tree that drills into nested values
///     (Style → Background), or the same walk as copyable JSON — plus the on-screen debug-draw toggles.
/// </summary>
public sealed class UiInspectorPanel(DevToolsController controller) : IDevPanel
{
    private const double RefreshMs = 250.0;
    private const float TreeRowH = 15f;
    private const float TreeViewH = 340f;
    private const int MaxSearchResults = 500;
    private const int AutoExpandRows = 80;
    private const int MaxNodes = 20000;
    private const float ChevronSize = 11f;

    // Property tree: how far a drill-down may go, how many rows it may cost, and the JSON depth.
    private const int MaxPropDepth = 8;
    private const int MaxPropRows = 400;
    private const float PropIndent = 10f;
    private const int JsonDepth = 4;

    private readonly DevToolsController _controller = controller;
    private readonly HashSet<Widget> _expanded = new(ReferenceEqualityComparer.Instance);
    private readonly DevKeyValue _paint = new("Paint commands");
    private readonly DevKeyValue _widgets = new("Widgets");

    private readonly Column _crumbs = new(
        crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min
    );

    private readonly Column _props = new(
        crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min
    );

    // The tree is a flat row model + a virtualized list: only the rows inside the viewport are built,
    // so a 20k-node tree costs what a 20-node one does. See docs/devtools-widget-tree.md.
    private readonly List<TreeNode> _rows = [];
    private readonly DevNote _treeNote = new("");
    private ListView _treeList = null!;
    private long _rowsHash;
    private int _publishedKey;

    // Selected-widget live rows (updated every Refresh, not just on reselect).
    private readonly DevBoxModel _boxModel = new();
    private readonly DevKeyValue _selSize = new("Size");
    private readonly DevKeyValue _selConstraints = new("Constraints");

    private readonly DevKeyValue _selBounds = new("Bounds");
    private readonly DevKeyValue _selInParent = new("In parent");
    private readonly DevKeyValue _selTree = new("Tree");
    private readonly DevKeyValue _selDirty = new("Dirty");
    private readonly DevKeyValue _selCounts = new("Counts");

    // NaN sentinel: never equal to a real constraints value, so the first refresh always formats.
    private Constraints _lastConstraints = new(
        float.NaN,
        float.NaN,
        float.NaN,
        float.NaN
    );

    // Toolbar chips that must reflect state changed outside the panel (Esc exits inspect mode).
    private DecoratedBox _inspectBox = null!;
    private IconGlyph _inspectIcon = null!;
    private Label _inspectLabel = null!;
    private ThemeData _theme = ThemeData.Dark;

    private readonly DevSearchField _search = new() { Placeholder = "search widgets" };
    private string _filter = "";

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tWidgets = new();
    private readonly CachedText _tPaint = new();
    private readonly CachedText _tSelSize = new();
    private readonly CachedText _tSelBounds = new();
    private readonly CachedText _tSelInParent = new();
    private readonly CachedText _tSelTree = new();
    private readonly CachedText _tSelDirty = new();
    private readonly CachedText _tSelCounts = new();

    private bool _dirty = true;
    private long _last;
    private int _lastSelectionRev = -1;
    private bool _pendingReveal;
    private int _selectedRowIndex = -1;
    private Widget? _seededRoot;
    private Widget? _shownSelection;

    // Property tree state: which member paths ("/Style/Shadow") are open, and the view mode.
    private readonly HashSet<string> _propOpen = new(StringComparer.Ordinal);
    private bool _jsonMode;
    private bool _propsDirty = true;

    public string Title => "UI Inspector";
    public DevCategory Category => DevCategory.Ui2D;

    public Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        _search.OnChanged = f =>
        {
            _filter = f;
            _dirty = true;
        };

        _inspectLabel = new Label("Select widget", DevKit.CaptionSize) { MaxLines = 1 };
        _inspectIcon = new IconGlyph(Icons.Pivot, AdwMetrics.IconSize);
        _inspectBox = new DecoratedBox {
            Radius = 4f,
            Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Sm, 3f),
                new Row(spacing: Spacing.Xs, crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        _inspectIcon,
                        _inspectLabel,
                    },
                }
            ),
        };
        var inspect = new Pressable {
            Child = _inspectBox,
            FocusRadius = 4f,
            SemanticsLabel = "Select widget mode",
            OnPressed = () =>
            {
                _controller.InspectMode = !_controller.InspectMode;
                if (!_controller.InspectMode) _controller.HoverHighlight = null;
                SyncInspectChip();
            },
        };

        _treeList = new ListView(itemExtent: TreeRowH);
        _treeList.SetBuilder(_rows.Count, BuildRow, true);

        SyncInspectChip();
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                // A Wrap, not a Row: four chips do not fit a 320px dock on one line.
                new Wrap {
                    Spacing = 4f,
                    RunSpacing = 4f,
                    Children = {
                        inspect,
                        ToolChip("Expand all", ExpandAll),
                        ToolChip("Collapse", CollapseAll),
                        // The selection outlines the widget on-screen and survives closing the panel —
                        // there has to be a way back to "nothing selected".
                        ToolChip(
                            "Deselect",
                            () =>
                            {
                                _controller.SelectWidget(null);
                                _controller.HoverHighlight = null;
                                _dirty = true;
                            }
                        ),
                    },
                },
                new SizedBox(height: Spacing.Xs),
                _search,
                new DevSectionHeader("On-screen debug"),
                new DevToggle(
                    "Repaint rainbow",
                    _controller.ShowRepaintRainbow,
                    v => _controller.ShowRepaintRainbow = v
                ),
                new DevToggle(
                    "Layout bounds",
                    _controller.ShowLayoutBounds,
                    v => _controller.ShowLayoutBounds = v
                ),
                new DevToggle(
                    "Overflow",
                    _controller.ShowOverflow,
                    v => _controller.ShowOverflow = v
                ),
                new DevSectionHeader("Stats"),
                _widgets,
                _paint,
                new DevSectionHeader("Widget tree"),
                new SizedBox(
                    height: TreeViewH,
                    child: new DecoratedBox {
                        Radius = 4f,
                        Fill = _theme.PanelSunken.WithAlpha(0.5f),
                        Child = _treeList,
                    }
                ),
                _treeNote,
                new DevSectionHeader("Selected"),
                _crumbs,
                _boxModel,
                _selSize,
                _selConstraints,
                _selBounds,
                _selInParent,
                _selTree,
                _selDirty,
                _selCounts,
                _props,
            },
        };
    }

    private Pressable ToolChip(string label, Action onTap)
    {
        var box = new DecoratedBox {
            Radius = 4f,
            Fill = _theme.Fill2,
            Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Sm, 3f),
                new Label(label, DevKit.CaptionSize) { MaxLines = 1 }
            ),
        };
        var p = new Pressable {
            Child = box,
            FocusRadius = 4f,
            OnPressed = onTap,
        };
        p.OnStateChanged = () => box.Fill = p.Hovered ? _theme.ControlHover : _theme.Fill2;
        return p;
    }

    private void SyncInspectChip()
    {
        var on = _controller.InspectMode;
        _inspectBox.Fill = on ? _theme.Primary : _theme.Fill2;
        _inspectLabel.Color = _inspectIcon.Color = on ? _theme.OnPrimary : _theme.OnSurface;
    }

    private void ExpandAll()
    {
        var root = _controller.App.Root;
        if (root is null) return;
        var stack = new Stack<Widget>();
        stack.Push(root);
        var guard = 0;
        while (stack.Count > 0 && guard++ < 5000)
        {
            var w = stack.Pop();
            _expanded.Add(w);
            foreach (var c in WidgetDebug.Children(w)) stack.Push(c);
        }

        _dirty = true;
    }

    private void CollapseAll()
    {
        _expanded.Clear();
        if (_controller.App.Root is { } root) _expanded.Add(root);
        _dirty = true;
    }

    public void Refresh(float dt)
    {
        var t = App.Active?.Theme ?? ThemeData.Dark;
        _theme = t;
        var root = _controller.App.Root;
        _widgets.Value = root is not null ? _tWidgets.Update($"{WidgetDebug.Count(root)}") : "0";
        _widgets.ValueColor = t.Hint;
        _paint.Value = _tPaint.Update(
            $"{DebugStats.UiPaintCommands} + {DebugStats.OverlayPaintCommands} overlay"
        );
        _paint.ValueColor = t.Hint;

        SyncInspectChip();
        SeedExpansion(root);

        if (_lastSelectionRev != _controller.SelectionRevision)
        {
            _lastSelectionRev = _controller.SelectionRevision;
            RevealSelection();
        }

        RefreshSelectedLive(t);

        var now = Stopwatch.GetTimestamp();
        var due = (now - _last) * 1000.0 / Stopwatch.Frequency >= RefreshMs;
        if (!_dirty && !due) return;
        _last = now;
        _dirty = false;

        RebuildTree(root, t);
        RebuildProps(t);

        if (_pendingReveal && _selectedRowIndex >= 0)
        {
            _treeList.EnsureVisible(_selectedRowIndex, 32f);
            _pendingReveal = false;
        }
    }

    /// <summary>First sight of a root: auto-expand top-down until ~a screenful of rows is visible.</summary>
    private void SeedExpansion(Widget? root)
    {
        if (root is null || ReferenceEquals(root, _seededRoot)) return;
        _seededRoot = root;
        _expanded.Clear();
        var queue = new Queue<Widget>();
        queue.Enqueue(root);
        var visible = 1;
        while (queue.Count > 0 && visible < AutoExpandRows)
        {
            var w = queue.Dequeue();
            _expanded.Add(w);
            foreach (var c in WidgetDebug.Children(w))
            {
                queue.Enqueue(c);
                visible++;
            }
        }

        _dirty = true;
    }

    /// <summary>Expand every ancestor of the selection and queue a scroll-to-row after the rebuild.</summary>
    private void RevealSelection()
    {
        if (_controller.SelectedWidget is { } sel)
        {
            foreach (var node in WidgetDebug.PathTo(sel)) _expanded.Add(node);
            _pendingReveal = true;
        }

        _dirty = true;
    }

    // ── Widget tree ──

    /// <summary>One visible tree row: the widget, its depth, and whether it is expandable / expanded.</summary>
    private readonly record struct TreeNode(Widget W, int Depth, bool HasKids, bool Open);

    /// <summary>
    ///     Flatten the visible tree into <see cref="_rows" /> and hand the count to the virtualized
    ///     list. Rows themselves are built on demand by <see cref="BuildRow" />, so this walk stays a
    ///     cheap struct-list fill even for a tree with thousands of nodes. When the flattened structure
    ///     is unchanged (the common case on the 250 ms tick) the list is left alone entirely.
    /// </summary>
    private void RebuildTree(Widget? root, ThemeData t)
    {
        _ = t;
        _rows.Clear();
        _rowsHash = 17;
        _selectedRowIndex = -1;

        if (root is null)
        {
            _treeNote.Text = "No root widget.";
        }
        else if (_filter.Length > 0)
        {
            FlattenMatches(root);
        }
        else
        {
            Flatten(root, 0);
            _treeNote.Text = $"{_rows.Count} rows visible.";
        }

        // Selection changes the painted row, so it takes part in the publish key.
        var key = HashCode.Combine(
            _rows.Count,
            _rowsHash,
            _controller.SelectedWidget is { } s ? RuntimeHelpers.GetHashCode(s) : 0
        );
        if (key == _publishedKey) return;
        _publishedKey = key;
        _treeList.SetBuilder(_rows.Count, BuildRow, true);
    }

    private void AddRow(Widget w, int depth, bool hasKids, bool open)
    {
        if (ReferenceEquals(_controller.SelectedWidget, w)) _selectedRowIndex = _rows.Count;
        _rows.Add(
            new TreeNode(
                w,
                depth,
                hasKids,
                open
            )
        );
        _rowsHash = _rowsHash * 31 + RuntimeHelpers.GetHashCode(w) + depth * 7 + (open ? 3 : 0);
    }

    private void Flatten(Widget w, int depth)
    {
        if (_rows.Count >= MaxNodes) return;

        var hasKids = false;
        foreach (var _ in WidgetDebug.Children(w))
        {
            hasKids = true;
            break;
        }

        var open = hasKids && _expanded.Contains(w);
        AddRow(
            w,
            depth,
            hasKids,
            open
        );
        if (!open) return;
        foreach (var c in WidgetDebug.Children(w)) Flatten(c, depth + 1);
    }

    private void FlattenMatches(Widget root)
    {
        var stack = new Stack<Widget>();
        var seen = new HashSet<Widget>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        var matches = 0;
        var scanned = 0;
        while (stack.Count > 0 && scanned < MaxNodes)
        {
            var w = stack.Pop();
            if (!seen.Add(w)) continue;
            scanned++;

            if (w.GetType().Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                WidgetDebug.Describe(w)?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ==
                true)
            {
                matches++;
                // Search hits keep their real depth so the rainbow guides still place them in the tree.
                if (matches <= MaxSearchResults)
                    AddRow(
                        w,
                        WidgetDebug.PathTo(w).Count - 1,
                        false,
                        false
                    );
            }

            foreach (var c in WidgetDebug.Children(w)) stack.Push(c);
        }

        _treeNote.Text = matches switch {
            0 => "No matches.",
            > MaxSearchResults => $"{matches} matches (showing {MaxSearchResults}).",
            _ => $"{matches} match{(matches == 1 ? "" : "es")}.",
        };
    }

    private Widget BuildRow(int i)
    {
        var n = _rows[i];
        var t = _theme;
        var w = n.W;
        var selected = ReferenceEquals(_controller.SelectedWidget, w);

        var bg = new DecoratedBox {
            Radius = 3f,
            Fill = selected ? t.Primary.WithAlpha(0.22f) : Color.Transparent,
        };
        var label = new Label(
            w.GetType().Name,
            DevKit.CaptionSize,
            selected ? t.Primary : t.OnSurface
        ) {
            MaxLines = 1,
            Overflow = TextOverflow.Ellipsis,
        };
        Widget chevron = n.HasKids
            ? new IconGlyph(
                n.Open ? Icons.ChevronDown : Icons.ChevronRight,
                ChevronSize,
                DevKit.DepthColor(n.Depth)
            )
            : new SizedBox(ChevronSize);

        var content = new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
            Children = {
                new DevTreeGuides(n.Depth, TreeRowH),
                ChevronButton(chevron, w, n.HasKids),
                new Flexible(label, fit: FlexFit.Loose),
            },
        };
        var detail = WidgetDebug.Describe(w);
        if (detail is not null)
        {
            content.Children.Add(new SizedBox(4f));
            content.Children.Add(
                new Flexible(
                    new Label(detail, DevKit.CaptionSize - 0.5f, t.Hint) {
                        MaxLines = 1,
                        Overflow = TextOverflow.Ellipsis,
                    },
                    fit: FlexFit.Loose
                )
            );
        }

        bg.Child = new Padding(EdgeInsets.Only(2f, right: 4f), content);

        var row = new Pressable {
            Child = bg,
            OnPressed = () =>
            {
                _controller.SelectWidget(ReferenceEquals(_controller.SelectedWidget, w) ? null : w);
                _dirty = true;
            },
        };
        // Hovering a row previews the widget on-screen (the overlay paints HoverHighlight).
        row.OnStateChanged = () =>
        {
            bg.Fill = ReferenceEquals(_controller.SelectedWidget, w)
                ? t.Primary.WithAlpha(0.22f)
                : row.Hovered
                    ? t.ControlHover.WithAlpha(0.4f)
                    : Color.Transparent;
            if (row.Hovered) _controller.HoverHighlight = w;
            else if (ReferenceEquals(_controller.HoverHighlight, w))
                _controller.HoverHighlight = null;
        };

        return new SizedBox(height: TreeRowH, child: row);
    }

    private SizedBox ChevronButton(Widget chevron, Widget w, bool hasKids)
    {
        if (!hasKids) return new SizedBox(ChevronSize, child: chevron);
        return new SizedBox(
            ChevronSize,
            child: new Pressable {
                Child = chevron,
                OnPressed = () =>
                {
                    if (!_expanded.Add(w)) _expanded.Remove(w);
                    _dirty = true;
                },
            }
        );
    }

    // ── Selected widget ──

    private void RefreshSelectedLive(ThemeData t)
    {
        var sel = _controller.SelectedWidget;
        _boxModel.Target = sel;
        if (sel is null)
        {
            _selSize.Value = _selConstraints.Value = _selBounds.Value =
                _selInParent.Value = _selTree.Value = _selDirty.Value = _selCounts.Value = "—";
            _lastConstraints = new Constraints(
                float.NaN,
                float.NaN,
                float.NaN,
                float.NaN
            );
            return;
        }

        var b = sel.Bounds;
        _selSize.Value =
            _tSelSize.Update($"{sel.MeasuredSize.Width:0.#}×{sel.MeasuredSize.Height:0.#}");
        _selBounds.Value =
            _tSelBounds.Update($"{b.X:0.#},{b.Y:0.#} · {b.Width:0.#}×{b.Height:0.#}");
        var c = sel.DebugLastConstraints;
        if (!c.Equals(_lastConstraints))
        {
            _lastConstraints = c;
            _selConstraints.Value = WidgetDebug.FormatConstraints(c);
        }

        // Where the box sits in its parent and how much of it it eats — the numbers behind the diagram.
        var parent = sel.Parent;
        while (parent is not null && parent.Bounds is not { Width: > 0f, Height: > 0f })
            parent = parent.Parent;
        _selInParent.Value = parent is { } p
            ? _tSelInParent.Update(
                $"+{b.X - p.Bounds.X:0.#},{b.Y - p.Bounds.Y:0.#} · {b.Width / p.Bounds.Width * 100f:0}%×{b.Height / p.Bounds.Height * 100f:0}%"
            )
            : "—";

        var kids = 0;
        foreach (var _ in WidgetDebug.Children(sel)) kids++;
        _selTree.Value = _tSelTree.Update(
            $"depth {WidgetDebug.PathTo(sel).Count - 1} · {kids} child{(kids == 1 ? "" : "ren")}"
        );
        _selDirty.Value = _tSelDirty.Update(
            $"B:{(sel.NeedsBuild ? 1 : 0)} L:{(sel.NeedsLayout ? 1 : 0)} P:{(sel.NeedsPaint ? 1 : 0)}"
        );
        _selCounts.Value = _tSelCounts.Update(
            $"M:{sel.MeasureCount} L:{sel.LayoutCount} P:{sel.PaintCount} R:{sel.RebuildCount}"
        );

        _selSize.ValueColor = _selConstraints.ValueColor = _selBounds.ValueColor =
            _selInParent.ValueColor = _selTree.ValueColor = _selDirty.ValueColor =
                _selCounts.ValueColor = t.Hint;
    }

    private void RebuildProps(ThemeData t)
    {
        var sel = _controller.SelectedWidget;
        if (!_propsDirty && ReferenceEquals(sel, _shownSelection) && sel is not null) return;
        _shownSelection = sel;
        _propsDirty = false;

        var crumbs = new List<Widget>();
        var rows = new List<Widget>();
        if (sel is null)
        {
            _propOpen.Clear();
            rows.Add(
                new DevNote(
                    "Select a widget in the tree — or use Select widget and click one on-screen."
                )
            );
        }
        else
        {
            crumbs.Add(BuildCrumbs(sel, t));
            rows.Add(new DevSectionHeader("Properties"));
            rows.Add(
                new Wrap {
                    Spacing = 4f,
                    RunSpacing = 4f,
                    Children = {
                        ModeChip(
                            "Tree",
                            !_jsonMode,
                            () => SetJsonMode(false),
                            t
                        ),
                        ModeChip(
                            "JSON",
                            _jsonMode,
                            () => SetJsonMode(true),
                            t
                        ),
                        ToolChip("Copy JSON", () => App.Active?.Engine?.SetClipboard(Json(sel))),
                    },
                }
            );
            rows.Add(new SizedBox(height: Spacing.Xs));

            if (_jsonMode)
            {
                var json = Json(sel);
                rows.Add(
                    new SelectableText(json) {
                        FontFamily = "code",
                        FontSize = DevKit.CaptionSize,
                        Color = t.OnSurface,
                    }
                );
            }
            else
            {
                var budget = MaxPropRows;
                AddProps(
                    rows,
                    sel,
                    "",
                    0,
                    ref budget,
                    t
                );
                if (budget <= 0)
                    rows.Add(new DevNote($"…more than {MaxPropRows} rows, collapse some."));
            }
        }

        _crumbs.SetChildren(crumbs);
        _props.SetChildren(rows);
    }

    private void SetJsonMode(bool json)
    {
        _jsonMode = json;
        _propsDirty = true;
        _dirty = true;
    }

    /// <summary>JSON of the selection minus the live header rows, at the tree's own depth budget.</summary>
    private static string Json(Widget sel)
    {
        return WidgetDebug.ToJson(sel, JsonDepth);
    }

    /// <summary>Depth-first expansion of the property tree, bounded by a shared row budget.</summary>
    private void AddProps(
        List<Widget> rows,
        object o,
        string path,
        int depth,
        ref int budget,
        ThemeData t
    )
    {
        foreach (var m in WidgetDebug.Members(o))
        {
            if (budget-- <= 0) return;
            // The live rows above and the breadcrumb already carry these, freshly.
            if (depth == 0 && m.Name is "Bounds" or "Type" or "Dirty" or "Counts") continue;

            var childPath = path + "/" + m.Name;
            var open = m.Expandable && _propOpen.Contains(childPath);
            rows.Add(
                PropRow(
                    m,
                    depth,
                    open,
                    childPath,
                    t
                )
            );
            if (open && m.Raw is not null && depth + 1 < MaxPropDepth)
                AddProps(
                    rows,
                    m.Raw,
                    childPath,
                    depth + 1,
                    ref budget,
                    t
                );
        }
    }

    private Widget PropRow(
        WidgetDebug.DebugMember m,
        int depth,
        bool open,
        string path,
        ThemeData t
    )
    {
        Widget chevron = m.Expandable
            ? new IconGlyph(
                open ? Icons.ChevronDown : Icons.ChevronRight,
                ChevronSize,
                DevKit.DepthColor(depth)
            )
            : new SizedBox(ChevronSize);

        var row = new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
            Children = {
                new SizedBox(depth * PropIndent),
                chevron,
                new SizedBox(Spacing.Xs),
                new Label(m.Name, AdwTypography.Caption, t.TextSecondary) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                },
                new SizedBox(Spacing.Sm),
                new Expanded(
                    new Label(
                        m.Value,
                        AdwTypography.Monospace,
                        m.Raw is null ? t.TextSecondary : t.OnSurface
                    ) {
                        MaxLines = 1,
                        Overflow = TextOverflow.Ellipsis,
                        Align = TextAlign.Right,
                    }
                ),
            },
        };

        var content = new SizedBox(
            height: DevKit.Row,
            child: new Padding(EdgeInsets.Symmetric(DevKit.RowInset), row)
        );
        if (!m.Expandable) return content;

        return new Pressable {
            Child = content,
            FocusRadius = 4f,
            OnPressed = () =>
            {
                if (!_propOpen.Add(path)) _propOpen.Remove(path);
                _propsDirty = true;
                _dirty = true;
            },
        };
    }

    private Pressable ModeChip(string label, bool active, Action onTap, ThemeData t)
    {
        return new Pressable {
            FocusRadius = 4f,
            OnPressed = onTap,
            Child = new DecoratedBox {
                Radius = 4f,
                Fill = active ? t.Primary : t.Fill2,
                Child = new Padding(
                    EdgeInsets.Symmetric(Spacing.Sm, 3f),
                    new Label(
                        label,
                        DevKit.CaptionSize,
                        active ? t.OnPrimary : t.OnSurface
                    ) { MaxLines = 1 }
                ),
            },
        };
    }

    /// <summary>Clickable root→selection ancestor chain, breadcrumb style.</summary>
    private Widget BuildCrumbs(Widget sel, ThemeData t)
    {
        var path = WidgetDebug.PathTo(sel);
        var wrap = new Wrap {
            Spacing = 2f,
            RunSpacing = 2f,
        };
        var start = Math.Max(0, path.Count - 8);
        if (start > 0)
            wrap.Children.Add(new IconGlyph(Icons.MoreHoriz, 14f, t.TextSecondary));

        for (var i = start; i < path.Count; i++)
        {
            var node = path[i];
            var last = i == path.Count - 1;
            var box = new DecoratedBox {
                Radius = 3f,
                Fill = last ? t.Primary.WithAlpha(0.22f) : t.Fill2,
                Child = new Padding(
                    EdgeInsets.Symmetric(Spacing.Xs, 1.5f),
                    new Label(
                        node.GetType().Name,
                        DevKit.CaptionSize - 1f,
                        last ? t.Primary : t.OnSurface
                    ) { MaxLines = 1 }
                ),
            };
            wrap.Children.Add(
                last
                    ? box
                    : new Pressable {
                        Child = box,
                        OnPressed = () => _controller.SelectWidget(node),
                    }
            );
            if (!last)
                wrap.Children.Add(new IconGlyph(Icons.ChevronRight, 14f, t.TextSecondary));
        }

        return new Padding(EdgeInsets.Only(bottom: Spacing.Xs), wrap);
    }
}