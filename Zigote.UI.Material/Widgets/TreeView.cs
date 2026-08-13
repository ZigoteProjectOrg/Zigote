using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     Generic, data-driven tree view. Nodes are described entirely by delegates so the widget works
///     for
///     any data type: <paramref name="childrenOf" /> yields a node's children,
///     <paramref name="labelOf" />
///     its display text, and the optional <see cref="IconOf" /> a MaterialIcons glyph.
///     Rows are expandable via a disclosure chevron, indented per depth, hover/selection highlighted,
///     and
///     report clicks through <paramref name="onSelect" />. The visible rows are re-flattened each
///     layout;
///     content is clipped to <see cref="Widget.Bounds" />. Expanding a node fades + slides its freshly
///     revealed descendant rows into place.
/// </summary>
public sealed class TreeView<T> : Widget where T : notnull
{
    private readonly Func<T, IReadOnlyList<T>> _childrenOf;

    // Expanded-by-default: track the COLLAPSED set so freshly-seen nodes start expanded.
    private readonly HashSet<T> _collapsed;

    private readonly AnimationController _expand;
    private readonly List<Row> _flat = [];
    private readonly Func<T, string> _labelOf;
    private readonly Action<T>? _onSelect;
    private readonly IReadOnlyList<T> _roots;
    private bool _animActive;
    private T _animNode = default!;
    private bool _compact;
    private int _hoverIndex = -1;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public TreeView(
        IReadOnlyList<T> roots,
        Func<T, IReadOnlyList<T>> childrenOf,
        Func<T, string> labelOf,
        Action<T>? onSelect = null,
        IEqualityComparer<T>? comparer = null)
    {
        _roots = roots;
        _childrenOf = childrenOf;
        _labelOf = labelOf;
        _onSelect = onSelect;
        _collapsed = new HashSet<T>(comparer ?? EqualityComparer<T>.Default);
        Comparer = comparer ?? EqualityComparer<T>.Default;
        _expand = new AnimationController(Motion.Standard, this) { Curve = Curves.EaseOut };
        _expand.OnTick += MarkNeedsPaint;
        _expand.OnCompleted += () =>
        {
            _animActive = false;
            MarkNeedsPaint();
        };
    }


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        _expand.AttachTicker(this);
    }


    /// <summary>
    ///     Equality comparer used to track expand/collapse and selection. Default: reference/default
    ///     equality.
    /// </summary>
    public IEqualityComparer<T> Comparer { get; }

    /// <summary>Currently selected node (null = none).</summary>
    public T? Selected { get; set; }

    /// <summary>Optional MaterialIcons glyph per node, drawn before the label.</summary>
    public Func<T, string>? IconOf { get; set; }

    /// <summary>Per-row height in logical pixels.</summary>
    public float RowHeight { get; set; } = 24f;

    /// <summary>Indentation added per depth level.</summary>
    public float IndentPerLevel { get; set; } = 16f;

    // Effective metrics. 24pt rows and a chevron inside them are a pointer rhythm; on a phone the
    // rows grow to a finger target and the per-level indent tightens so deep nodes keep their label.
    private float RowH => _compact ? MathF.Max(RowHeight, TouchMetrics.MinTarget) : RowHeight;

    private float Indent => _compact ? MathF.Min(IndentPerLevel, 12f) : IndentPerLevel;

    public bool IsExpanded(T node)
    {
        return !_collapsed.Contains(node);
    }

    public void SetExpanded(T node, bool expanded)
    {
        var changed = expanded ? _collapsed.Remove(node) : _collapsed.Add(node);
        if (!changed) return;
        if (expanded) PlayExpand(node);
        MarkNeedsLayout();
    }

    public void ToggleExpanded(T node)
    {
        var nowExpanded = _collapsed.Remove(node);
        if (!nowExpanded) _collapsed.Add(node);
        if (nowExpanded) PlayExpand(node);
        MarkNeedsLayout();
    }

    // Fade + slide the freshly revealed descendant rows in when a node expands. Collapsing is instant
    // (the rows are gone from the flattened list, so there is nothing left to animate out).
    private void PlayExpand(T node)
    {
        _animNode = node;
        _animActive = true;
        _expand.Dismiss();
        _expand.Forward();
    }

    // The [start, end) flattened-row range of the animating node's descendants, or (0, 0) when idle.
    private (int Start, int End) AnimatingRange()
    {
        if (!_animActive) return (0, 0);
        var ni = -1;
        for (var i = 0; i < _flat.Count; i++)
            if (Comparer.Equals(_flat[i].Node, _animNode))
            {
                ni = i;
                break;
            }

        if (ni < 0) return (0, 0);
        var depth = _flat[ni].Depth;
        var end = ni + 1;
        while (end < _flat.Count && _flat[end].Depth > depth) end++;
        return (ni + 1, end);
    }

    private void Flatten()
    {
        _flat.Clear();
        foreach (var r in _roots) FlattenNode(r, 0);
    }

    private void FlattenNode(T node, int depth)
    {
        var children = _childrenOf(node);
        var hasChildren = children.Count > 0;
        var expanded = IsExpanded(node);
        _flat.Add(
            new Row(
                node,
                depth,
                hasChildren,
                expanded
            )
        );
        if (hasChildren && expanded)
            foreach (var c in children)
                FlattenNode(c, depth + 1);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
        Flatten();
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        var h = _flat.Count * RowH;
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

    /// <summary>
    ///     The inclusive row window intersecting <paramref name="clip" /> (the active paint clip, already
    ///     intersected with our bounds). Falls back to the full range when there is no clip.
    /// </summary>
    private (int First, int Last) VisibleRange(Rect? clip)
    {
        if (_flat.Count == 0 || RowH <= 0f) return (0, -1);
        if (clip is not { } c) return (0, _flat.Count - 1);
        var first = Math.Max(0, (int)((c.Y - Bounds.Y) / RowH));
        var last = Math.Min(_flat.Count - 1, (int)((c.Bottom - Bounds.Y) / RowH));
        return (first, last);
    }

    public override void Paint(PaintList paint)
    {
        if (_flat.Count == 0 || RowH <= 0f) return;
        paint.AddClipStart(Bounds);

        // Virtualize: iterate only the rows inside the active clip window (the ancestor scroll viewport
        // intersected with our bounds) instead of walking every flattened row.
        var (first, last) = VisibleRange(paint.CurrentClip);
        var (animStart, animEnd) = AnimatingRange();
        var animT = _expand.Value;
        var fs = _theme.FontSizeCaption;
        for (var i = first; i <= last; i++)
        {
            var row = _flat[i];
            var rowY = Bounds.Y + i * RowH;

            // Freshly revealed descendant rows fade + slide into place while the node expands.
            var animating = _animActive && i >= animStart && i < animEnd;
            if (animating)
            {
                paint.PushAlpha(Math.Clamp(animT, 0f, 1f));
                paint.PushTranslate(0f, -(1f - animT) * 6f);
            }

            var isSelected = Selected is not null && Comparer.Equals(Selected, row.Node);
            var isHover = i == _hoverIndex;

            var bg = isSelected ? _theme.Primary.WithAlpha(0.22f)
                : isHover ? _theme.OnSurface.WithAlpha(0.06f)
                : new Color(
                    0,
                    0,
                    0,
                    0
                );
            if (bg.A > 0f)
                paint.AddRect(
                    new Rect(
                        Bounds.X + 4f,
                        rowY + 1f,
                        _size.Width - 8f,
                        RowH - 2f
                    ),
                    bg,
                    Radii.Sm
                );

            var indent = 8f + row.Depth * Indent;

            // Disclosure chevron — only when the node has children.
            if (row.HasChildren)
            {
                var glyph = row.Expanded ? Icons.ChevronDown : Icons.ChevronRight;
                Icons.Draw(
                    paint,
                    glyph,
                    new Rect(
                        Bounds.X + indent,
                        rowY,
                        14f,
                        RowH
                    ),
                    _theme.TextMuted,
                    14f
                );
            }

            var contentX = Bounds.X + indent + 16f;

            // Optional leading icon.
            if (IconOf is not null)
            {
                var glyph = IconOf(row.Node);
                if (!string.IsNullOrEmpty(glyph))
                {
                    Icons.Draw(
                        paint,
                        glyph,
                        new Rect(
                            contentX,
                            rowY,
                            16f,
                            RowH
                        ),
                        _theme.OnSurface,
                        15f
                    );
                    contentX += 20f;
                }
            }

            var fg = isSelected ? _theme.OnSurface : _theme.OnSurface.WithAlpha(0.9f);
            var textY = rowY + (RowH - fs) / 2f + fs * 0.8f;
            paint.AddText(
                _labelOf(row.Node),
                contentX,
                textY,
                fg,
                fs
            );

            if (animating)
            {
                paint.PopTranslate();
                paint.PopAlpha();
            }
        }

        paint.AddClipEnd();
    }

    private int RowIndexAt(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return -1;
        var idx = (int)((point.Y - Bounds.Y) / RowH);
        return idx >= 0 && idx < _flat.Count ? idx : -1;
    }

    public override void OnPointerMove(Offset point)
    {
        var idx = RowIndexAt(point);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            MarkNeedsPaint();
        }
    }

    public override void OnPointerExit()
    {
        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);
        var idx = RowIndexAt(point);
        if (idx < 0) return;
        var row = _flat[idx];

        // Click on the disclosure chevron toggles expansion.
        var indent = 8f + row.Depth * Indent;
        var chevronLeft = Bounds.X + indent;
        var chevronW = _compact ? 24f : 14f;
        if (row.HasChildren && point.X >= chevronLeft && point.X <= chevronLeft + chevronW)
        {
            ToggleExpanded(row.Node);
            return;
        }

        Selected = row.Node;
        _onSelect?.Invoke(row.Node);
        MarkNeedsPaint();
    }

    public override int DebugStateHash()
    {
        var sel = Selected is null ? 0 : Comparer.GetHashCode(Selected);
        return HashCode.Combine(
            _flat.Count,
            _hoverIndex,
            sel,
            _collapsed.Count
        );
    }

    private readonly struct Row(T node, int depth, bool hasChildren, bool expanded)
    {
        public readonly T Node = node;
        public readonly int Depth = depth;
        public readonly bool HasChildren = hasChildren;
        public readonly bool Expanded = expanded;
    }
}
