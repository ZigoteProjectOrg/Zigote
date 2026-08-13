using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Lays its children out in runs along the main axis, wrapping to a new run when the next child
///     would overflow. Ideal for tag clouds, chip rows and responsive toolbars.
/// </summary>
public class Wrap : MultiChildWidget
{
    private float[] _mains = [];
    private Offset[] _offsets = [];
    private Size _size;

    /// <summary>
    ///     Named-argument constructor: <c>new Wrap(spacing: 8, runSpacing: 8, children: [...])</c>.
    ///     All arguments optional. If <paramref name="spacing" />/<paramref name="runSpacing" /> are
    ///     omitted the widget keeps its small default gap (pass <c>0</c> explicitly for no gap).
    /// </summary>
    public Wrap(
        IEnumerable<Widget>? children = null,
        Axis direction = Axis.Horizontal,
        double? spacing = null,
        double? runSpacing = null,
        Key? key = null) : base(children)
    {
        Direction = direction;
        if (spacing is { } s) Spacing = (float)s;
        if (runSpacing is { } r) RunSpacing = (float)r;
        if (key is not null) Key = key;
    }

    public Axis Direction { get; set; } = Axis.Horizontal;

    /// <summary>
    ///     Horizontal flow within a run. <c>null</c> (the default) follows the ambient
    ///     <see cref="Directionality" />; under <see cref="TextDirection.Rtl" /> each horizontal run
    ///     fills right-to-left. Vertical wraps are unaffected.
    /// </summary>
    public TextDirection? LayoutDirection { get; set; }

    /// <summary>Gap between children within a run.</summary>
    public float Spacing { get; set; } = UI.Theme.Spacing.Sm;

    /// <summary>Gap between runs (cross-axis).</summary>
    public float RunSpacing { get; set; } = UI.Theme.Spacing.Sm;

    public override Size Measure(Constraints c)
    {
        bool horizontal = Direction == Axis.Horizontal;
        float maxMain = horizontal ? c.MaxWidth : c.MaxHeight;
        if (!float.IsFinite(maxMain)) maxMain = float.MaxValue;

        bool rtl = horizontal &&
                   (LayoutDirection ?? Directionality.Of(BuildContext.Current)) ==
                   TextDirection.Rtl;

        // Grow-only scratch buffers — never reallocate when the child count is stable (steady-state
        // 0-alloc, matching the Column/Toolbar pattern). Only the first Children.Count slots are used.
        if (_offsets.Length < Children.Count) _offsets = new Offset[Children.Count];
        if (rtl && _mains.Length < Children.Count) _mains = new float[Children.Count];

        float mainCursor = 0f, crossCursor = 0f, runCross = 0f, lineMainExtent = 0f;
        var childC = new Constraints(
            minWidth: 0,
            maxWidth: horizontal ? c.MaxWidth : float.PositiveInfinity,
            minHeight: 0,
            maxHeight: horizontal ? float.PositiveInfinity : c.MaxHeight
        );

        for (int i = 0; i < Children.Count; i++)
        {
            var sz = Children[i].Measure(childC);
            float childMain = horizontal ? sz.Width : sz.Height;
            float childCross = horizontal ? sz.Height : sz.Width;

            // Wrap to a new run when this child would overflow the current one.
            if (mainCursor > 0f && mainCursor + childMain > maxMain)
            {
                crossCursor += runCross + RunSpacing;
                mainCursor = 0f;
                runCross = 0f;
            }

            _offsets[i] = horizontal
                ? new Offset(x: mainCursor, y: crossCursor)
                : new Offset(x: crossCursor, y: mainCursor);
            if (rtl) _mains[i] = childMain;

            mainCursor += childMain + Spacing;
            runCross = MathF.Max(x: runCross, y: childCross);
            lineMainExtent = MathF.Max(x: lineMainExtent, y: mainCursor - Spacing);
        }

        float totalCross = crossCursor + runCross;
        _size = c.Constrain(
            horizontal
                ? new Size(width: lineMainExtent, height: totalCross)
                : new Size(width: totalCross, height: lineMainExtent)
        );

        // RTL: mirror each child's x against the measured width so runs fill right-to-left. Done as
        // a post-pass because the width is only known once every run has been placed.
        if (rtl)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                _offsets[i] = new Offset(
                    x: _size.Width - _offsets[i].X - _mains[i],
                    y: _offsets[i].Y
                );
            }
        }

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

        // The same guard FlexLayout.Layout carries: the child list can grow between the Measure
        // that sized _offsets and this Layout — a reconcile, a re-entrant relayout from a native
        // live-resize, or a subtree swapped in by an ancestor that then skipped re-measuring. Lay
        // out the measured prefix instead of indexing past the table, and ask for another layout so
        // the rest arrives next frame rather than staying invisible.
        int count = Math.Min(val1: Children.Count, val2: _offsets.Length);
        for (int i = 0; i < count; i++)
        {
            Children[i].Layout(
                new Offset(x: origin.X + _offsets[i].X, y: origin.Y + _offsets[i].Y)
            );
        }

        if (count < Children.Count) MarkNeedsLayout();
    }

    public override void Paint(PaintList paint)
    {
        foreach (var child in Children) child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return null;
    }
}
