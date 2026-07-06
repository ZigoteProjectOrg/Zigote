using System.Diagnostics;
using Zigote.Core;
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
///     size/constraints rows, and the reflected property dump — plus the on-screen debug-draw toggles.
/// </summary>
public sealed class UiInspectorPanel(DevToolsController controller) : IDevPanel
{
    private const double RefreshMs = 250.0;
    private const float TreeRowH = 18f;
    private const float TreeViewH = 320f;
    private const int MaxTreeRows = 400;
    private const int MaxSearchResults = 100;
    private const int AutoExpandRows = 80;

    private readonly DevToolsController _controller = controller;
    private readonly HashSet<Widget> _expanded = new(ReferenceEqualityComparer.Instance);
    private readonly DevKeyValue _paint = new("Paint commands");
    private readonly DevKeyValue _widgets = new("Widgets");

    private readonly Column _crumbs = new(crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min);
    private readonly Column _props = new(crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min);
    private readonly Column _tree = new(crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min);
    private ScrollView _treeScroll = null!;

    // Selected-widget live rows (updated every Refresh, not just on reselect).
    private readonly DevBoxModel _boxModel = new();
    private readonly DevKeyValue _selSize = new("Size");
    private readonly DevKeyValue _selConstraints = new("Constraints");
    private readonly DevKeyValue _selBounds = new("Bounds");
    // NaN sentinel: never equal to a real constraints value, so the first refresh always formats.
    private Constraints _lastConstraints = new(float.NaN, float.NaN, float.NaN, float.NaN);

    // Toolbar chips that must reflect state changed outside the panel (Esc exits inspect mode).
    private DecoratedBox _inspectBox = null!;
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

    private bool _dirty = true;
    private long _last;
    private int _lastSelectionRev = -1;
    private bool _pendingReveal;
    private int _selectedRowIndex = -1;
    private Widget? _seededRoot;
    private Widget? _shownSelection;

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

        _inspectLabel = new Label("⌖ Select widget", DevKit.CaptionSize) { MaxLines = 1 };
        _inspectBox = new DecoratedBox {
            Radius = 4f,
            Child = new Padding(EdgeInsets.Symmetric(Spacing.Sm, 3f), _inspectLabel),
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

        _treeScroll = new ScrollView(_tree) { ScrollVertical = true };

        SyncInspectChip();
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min) {
            Children = {
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        inspect,
                        new Spacer(),
                        ToolChip("Expand all", ExpandAll),
                        new SizedBox(width: 4f),
                        ToolChip("Collapse", CollapseAll),
                    },
                },
                new SizedBox(height: Spacing.Xs),
                _search,
                new DevSectionHeader("On-screen debug"),
                new DevToggle("Repaint rainbow", _controller.ShowRepaintRainbow,
                    v => _controller.ShowRepaintRainbow = v),
                new DevToggle("Layout bounds", _controller.ShowLayoutBounds,
                    v => _controller.ShowLayoutBounds = v),
                new DevToggle("Overflow", _controller.ShowOverflow, v => _controller.ShowOverflow = v),
                new DevSectionHeader("Stats"),
                _widgets, _paint,
                new DevSectionHeader("Widget tree"),
                new SizedBox(height: TreeViewH, child: new DecoratedBox {
                    Radius = 4f,
                    Fill = _theme.PanelSunken.WithAlpha(0.5f),
                    Child = _treeScroll,
                }),
                new DevSectionHeader("Selected"),
                _crumbs,
                _boxModel,
                _selSize, _selConstraints, _selBounds,
                _props,
            },
        };
    }

    private Pressable ToolChip(string label, Action onTap)
    {
        var box = new DecoratedBox {
            Radius = 4f,
            Fill = _theme.Fill2,
            Child = new Padding(EdgeInsets.Symmetric(Spacing.Sm, 3f),
                new Label(label, DevKit.CaptionSize) { MaxLines = 1 }),
        };
        var p = new Pressable { Child = box, FocusRadius = 4f, OnPressed = onTap };
        p.OnStateChanged = () => box.Fill = p.Hovered ? _theme.ControlHover : _theme.Fill2;
        return p;
    }

    private void SyncInspectChip()
    {
        var on = _controller.InspectMode;
        _inspectBox.Fill = on ? _theme.Primary : _theme.Fill2;
        _inspectLabel.Color = on ? _theme.OnPrimary : _theme.OnSurface;
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
            $"{DebugStats.UiPaintCommands} + {DebugStats.OverlayPaintCommands} overlay");
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
            _treeScroll.EnsureVisible(_selectedRowIndex * TreeRowH, TreeRowH, 32f);
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

    private void RebuildTree(Widget? root, ThemeData t)
    {
        var rows = new List<Widget>();
        _selectedRowIndex = -1;

        if (root is null)
        {
            rows.Add(new DevNote("No root widget."));
        }
        else if (_filter.Length > 0)
        {
            BuildSearchRows(root, rows, t);
        }
        else
        {
            AddNode(root, 0, rows, t);
            if (rows.Count >= MaxTreeRows)
                rows.Add(new DevNote($"… truncated at {MaxTreeRows} rows — collapse or search."));
        }

        _tree.SetChildren(rows);
    }

    private void BuildSearchRows(Widget root, List<Widget> rows, ThemeData t)
    {
        var stack = new Stack<Widget>();
        var seen = new HashSet<Widget>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        var matches = 0;
        var scanned = 0;
        while (stack.Count > 0 && scanned < 5000)
        {
            var w = stack.Pop();
            if (!seen.Add(w)) continue;
            scanned++;

            var detail = WidgetDebug.Describe(w);
            if (w.GetType().Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                detail?.Contains(_filter, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (matches++ < MaxSearchResults) rows.Add(TreeRow(w, 0, false, false, detail, t));
            }

            foreach (var c in WidgetDebug.Children(w)) stack.Push(c);
        }

        rows.Add(new DevNote(matches switch {
            0 => "No matches.",
            > MaxSearchResults => $"{matches} matches (showing {MaxSearchResults}).",
            _ => $"{matches} match{(matches == 1 ? "" : "es")}.",
        }));
    }

    private void AddNode(Widget w, int depth, List<Widget> rows, ThemeData t)
    {
        if (rows.Count >= MaxTreeRows) return;

        var hasKids = false;
        foreach (var _ in WidgetDebug.Children(w))
        {
            hasKids = true;
            break;
        }

        var open = hasKids && _expanded.Contains(w);
        if (ReferenceEquals(_controller.SelectedWidget, w)) _selectedRowIndex = rows.Count;
        rows.Add(TreeRow(w, depth, hasKids, open, WidgetDebug.Describe(w), t));

        if (!open) return;
        foreach (var c in WidgetDebug.Children(w))
            AddNode(c, depth + 1, rows, t);
    }

    private Widget TreeRow(Widget w, int depth, bool hasKids, bool open, string? detail, ThemeData t)
    {
        var selected = ReferenceEquals(_controller.SelectedWidget, w);

        var bg = new DecoratedBox {
            Radius = 3f,
            Fill = selected ? t.Primary.WithAlpha(0.22f) : Color.Transparent,
        };
        var label = new Label(w.GetType().Name, DevKit.CaptionSize,
            selected ? t.Primary : t.OnSurface) { MaxLines = 1, Overflow = TextOverflow.Ellipsis };
        var chevron = new Label(hasKids ? open ? "▾" : "▸" : " ", DevKit.CaptionSize, t.Hint)
            { MaxLines = 1 };

        var content = new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
            Children = {
                ChevronButton(chevron, w, hasKids),
                new SizedBox(width: 2f),
                new Flexible(label, fit: FlexFit.Loose),
            },
        };
        if (detail is not null)
        {
            content.Children.Add(new SizedBox(width: Spacing.Xs));
            content.Children.Add(new Flexible(
                new Label(detail, DevKit.CaptionSize - 0.5f, t.Hint)
                    { MaxLines = 1, Overflow = TextOverflow.Ellipsis },
                fit: FlexFit.Loose));
        }

        bg.Child = new Padding(
            EdgeInsets.Only(left: 4f + Math.Min(depth, 20) * 10f, right: 4f), content);

        var row = new Pressable {
            Child = bg,
            OnPressed = () =>
            {
                _controller.SelectWidget(
                    ReferenceEquals(_controller.SelectedWidget, w) ? null : w);
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

    private SizedBox ChevronButton(Label chevron, Widget w, bool hasKids)
    {
        if (!hasKids) return new SizedBox(width: 12f, child: chevron);
        return new SizedBox(width: 12f, child: new Pressable {
            Child = chevron,
            OnPressed = () =>
            {
                if (!_expanded.Add(w)) _expanded.Remove(w);
                _dirty = true;
            },
        });
    }

    // ── Selected widget ──

    private void RefreshSelectedLive(ThemeData t)
    {
        var sel = _controller.SelectedWidget;
        _boxModel.Target = sel;
        if (sel is null)
        {
            _selSize.Value = _selConstraints.Value = _selBounds.Value = "—";
            _lastConstraints = new Constraints(float.NaN, float.NaN, float.NaN, float.NaN);
            return;
        }

        var b = sel.Bounds;
        _selSize.Value = _tSelSize.Update($"{sel.MeasuredSize.Width:0.#}×{sel.MeasuredSize.Height:0.#}");
        _selBounds.Value = _tSelBounds.Update($"{b.X:0.#},{b.Y:0.#} · {b.Width:0.#}×{b.Height:0.#}");
        var c = sel.DebugLastConstraints;
        if (!c.Equals(_lastConstraints))
        {
            _lastConstraints = c;
            _selConstraints.Value = WidgetDebug.FormatConstraints(c);
        }

        _selSize.ValueColor = _selConstraints.ValueColor = _selBounds.ValueColor = t.Hint;
    }

    private void RebuildProps(ThemeData t)
    {
        var sel = _controller.SelectedWidget;
        if (ReferenceEquals(sel, _shownSelection) && sel is not null) return;
        _shownSelection = sel;

        var crumbs = new List<Widget>();
        var rows = new List<Widget>();
        if (sel is null)
        {
            rows.Add(new DevNote("Select a widget in the tree — or use ⌖ Select widget and click one on-screen."));
        }
        else
        {
            crumbs.Add(BuildCrumbs(sel, t));
            foreach (var (name, value) in WidgetDebug.Properties(sel))
                if (name is not ("Bounds" or "Type")) // live rows + breadcrumb cover these
                    rows.Add(new DevKeyValue(name, value));
        }

        _crumbs.SetChildren(crumbs);
        _props.SetChildren(rows);
    }

    /// <summary>Clickable root→selection ancestor chain, breadcrumb style.</summary>
    private Widget BuildCrumbs(Widget sel, ThemeData t)
    {
        var path = WidgetDebug.PathTo(sel);
        var wrap = new Wrap { Spacing = 2f, RunSpacing = 2f };
        var start = Math.Max(0, path.Count - 8);
        if (start > 0)
            wrap.Children.Add(new Label("…", DevKit.CaptionSize - 1f, t.Hint) { MaxLines = 1 });

        for (var i = start; i < path.Count; i++)
        {
            var node = path[i];
            var last = i == path.Count - 1;
            var box = new DecoratedBox {
                Radius = 3f,
                Fill = last ? t.Primary.WithAlpha(0.22f) : t.Fill2,
                Child = new Padding(EdgeInsets.Symmetric(Spacing.Xs, 1.5f),
                    new Label(node.GetType().Name, DevKit.CaptionSize - 1f,
                        last ? t.Primary : t.OnSurface) { MaxLines = 1 }),
            };
            wrap.Children.Add(last
                ? box
                : new Pressable {
                    Child = box,
                    OnPressed = () => _controller.SelectWidget(node),
                });
            if (!last)
                wrap.Children.Add(new Label("›", DevKit.CaptionSize - 1f, t.Hint) { MaxLines = 1 });
        }

        return new Padding(EdgeInsets.Only(bottom: Spacing.Xs), wrap);
    }
}
