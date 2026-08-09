using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Gives its child a flex factor inside a Row or Column. Non-flex children are measured first;
///     the remaining space is split among flex children proportionally by <see cref="Flex" />.
///     <see cref="Fit" /> controls whether the child must fill its share (<see cref="FlexFit.Tight" />
///     )
///     or may be smaller (<see cref="FlexFit.Loose" />).
/// </summary>
public class Flexible : Widget
{
    // Single-element child array reused across GetChildren() calls. Flexible/Expanded/Spacer are
    // pervasive in Row/Column trees; returning a fresh `[Child]` each call allocated a Widget[] on
    // every tree walk (attach/detach, focus, semantics, reconcile). Kept in sync by the Child setter.
    private readonly Widget[] _childArray = new Widget[1];
    private Widget _child = null!;
    private Size _size;

    public Flexible(Widget child, int flex = 1, FlexFit fit = FlexFit.Loose)
    {
        Child = child;
        Flex = flex;
        Fit = fit;
    }

    public Widget Child
    {
        get => _child;
        set
        {
            _child = value;
            _childArray[0] = value;
        }
    }

    public int Flex { get; set; } = 1;
    public FlexFit Fit { get; set; } = FlexFit.Loose;

    public override Size Measure(Constraints c)
    {
        _size = Child.Measure(c);
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
        Child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _childArray;
    }
}

/// <summary>A flex child that fills its entire share of the main axis (<see cref="FlexFit.Tight" />).</summary>
public class Expanded(Widget child, int flex = 1) : Flexible(child, flex, FlexFit.Tight);

/// <summary>A zero-size flexible gap (an <see cref="Expanded" /> with a transparent child).</summary>
public class Spacer(int flex = 1) : Expanded(new SizedBox(), flex);

// ── Shared flex layout logic ───────────────────────────────────────────────────

internal static class FlexLayout
{
    /// <summary>
    ///     Shared measure for Row/Column.
    ///     axis=0 → horizontal (Row); axis=1 → vertical (Column).
    /// </summary>
    // Signatures take the concrete List<Widget> (all callers — Row/Column — hold one) rather than
    // IReadOnlyList<Widget>: the JIT devirtualizes and inlines the sealed List indexer/Count, so the
    // three measure passes + layout/paint/hit-test loops avoid an interface call per child per pass.
    internal static Size Measure(
        List<Widget> children,
        Constraints c,
        int axis,
        MainAxisAlignment mainAlign,
        CrossAxisAlignment crossAlign,
        MainAxisSize mainSize,
        float spacing,
        ref ChildMetrics[] metrics)
    {
        // Reuse the caller's buffer across frames; only grow when needed. Clear the live range so
        // slots for flex children that pass 2 may skip (unbounded main axis) read as zeroed, exactly
        // as a freshly-allocated array would. ChildMetrics is a struct, so this stays allocation-free.
        if (metrics.Length < children.Count)
            metrics = new ChildMetrics[children.Count];
        else
            Array.Clear(metrics, 0, children.Count);

        var maxCross = 0f;
        // Seed with the fixed inter-child gaps so the per-child available space, the flex
        // distribution, and the MainAxisSize.Min total all see only what spacing leaves over.
        var totalFixed = children.Count > 1 ? spacing * (children.Count - 1) : 0f;
        var totalFlex = 0;

        var mainMax = axis == 0 ? c.MaxWidth : c.MaxHeight;
        var crossMax = axis == 0 ? c.MaxHeight : c.MaxWidth;

        // Pass 1 — measure non-flex children
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is Flexible exp)
            {
                totalFlex += exp.Flex;
                continue;
            }

            // Remaining main-axis space for this child. If a prior child reported an infinite extent
            // (e.g. a fill widget mistakenly placed in an unbounded scroll), ∞ − ∞ is NaN — treat that
            // as still-unbounded so a stray infinity can't poison this child's constraints.
            var avail = mainMax - totalFixed;
            if (float.IsNaN(avail)) avail = float.PositiveInfinity;

            var childC = axis == 0
                ? new Constraints(maxWidth: avail, maxHeight: crossMax)
                : new Constraints(maxWidth: crossMax, maxHeight: avail);

            // Stretch pins the child to the full cross extent — but only when that extent is finite.
            // Inside a ScrollView the cross max is +∞; stretching to it forces an infinite child size
            // that poisons layout/paint with NaN. When unbounded, fall through to the loose constraint
            // above (min 0) so the child measures to its natural cross size instead of crashing.
            if (crossAlign == CrossAxisAlignment.Stretch && float.IsFinite(crossMax))
                childC = axis == 0
                    ? new Constraints(maxWidth: avail, minHeight: crossMax, maxHeight: crossMax)
                    : new Constraints(crossMax, crossMax, maxHeight: avail);

            var sz = children[i].Measure(childC);
            metrics[i] = new ChildMetrics(sz, 0f);
            totalFixed += axis == 0 ? sz.Width : sz.Height;
            maxCross = MathF.Max(maxCross, axis == 0 ? sz.Height : sz.Width);
        }

        // Pass 2 — measure flex children
        var remaining = MathF.Max(0f, mainMax - totalFixed);
        if (totalFlex > 0 && float.IsFinite(mainMax))
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is not Flexible exp) continue;

                var share = remaining * exp.Flex / totalFlex;
                // Tight fit fills the whole share; loose fit may be smaller (min main = 0).
                var mainMin = exp.Fit == FlexFit.Tight ? share : 0f;
                // Stretch pins the cross-min only when the cross axis is bounded; an unbounded
                // (scrolling) cross axis measures the flex child loose instead of forcing ∞ (→ NaN).
                var stretch = crossAlign == CrossAxisAlignment.Stretch && float.IsFinite(crossMax);
                var crossMin = stretch ? crossMax : 0f;

                var childC = axis == 0
                    ? new Constraints(
                        mainMin,
                        share,
                        crossMin,
                        crossMax
                    )
                    : new Constraints(
                        crossMin,
                        crossMax,
                        mainMin,
                        share
                    );

                var sz = exp.Measure(childC);
                metrics[i] = new ChildMetrics(sz, 0f);
                totalFixed += axis == 0 ? sz.Width : sz.Height;
                maxCross = MathF.Max(maxCross, axis == 0 ? sz.Height : sz.Width);
            }

        // Pass 3 — compute cross-axis offsets for each child
        for (var i = 0; i < children.Count; i++)
        {
            var childCross = axis == 0 ? metrics[i].Size.Height : metrics[i].Size.Width;
            var crossOff = crossAlign switch {
                CrossAxisAlignment.Center => (maxCross - childCross) / 2f,
                CrossAxisAlignment.End => maxCross - childCross,
                _ => 0f,
            };
            if (!float.IsFinite(crossOff))
                crossOff = 0f; // ∞ − ∞ from an infinite child → no offset
            metrics[i] = new ChildMetrics(metrics[i].Size, crossOff);
        }

        var mainTotal = mainSize == MainAxisSize.Min ? totalFixed :
            float.IsFinite(mainMax) ? mainMax : totalFixed;
        var ownMain = axis == 0 ? mainTotal : maxCross;
        var ownCross = axis == 0 ? maxCross : mainTotal;
        return c.Constrain(new Size(ownMain, ownCross));
    }

    internal static void Layout(
        List<Widget> children,
        ChildMetrics[] metrics,
        Rect bounds,
        int axis,
        MainAxisAlignment mainAlign,
        float spacing = 0f,
        bool rtl = false)
    {
        // Normally metrics is sized to children.Count by the immediately-preceding Measure. Guard the
        // window anyway: if the child list grew since that Measure (a tree mutation between measure and
        // layout — reconciliation, or a re-entrant relayout from a native live-resize / drop callback),
        // lay out only the measured prefix this frame instead of indexing past the array and crashing.
        // The mutation that added children marks NeedsLayout, so the next frame re-measures and corrects.
        var count = Math.Min(children.Count, metrics.Length);

        var totalChildMain = 0f;
        for (var i = 0; i < count; i++)
            totalChildMain += axis == 0 ? metrics[i].Size.Width : metrics[i].Size.Height;
        // Fixed gaps count as occupied main-axis space: alignment distributes only what remains.
        if (count > 1) totalChildMain += spacing * (count - 1);

        var ownMain = axis == 0 ? bounds.Width : bounds.Height;
        var extra = ownMain - totalChildMain;
        if (!float.IsFinite(extra))
            extra = 0f; // ∞ − ∞ from an infinite child → distribute no slack
        extra = MathF.Max(0f, extra);
        var n = count;

        var startOffset = mainAlign switch {
            MainAxisAlignment.Center => extra / 2f,
            MainAxisAlignment.End => extra,
            MainAxisAlignment.SpaceAround => n > 0 ? extra / n / 2f : 0f,
            _ => 0f,
        };
        var gap = mainAlign switch {
            MainAxisAlignment.SpaceBetween => n > 1 ? extra / (n - 1) : 0f,
            MainAxisAlignment.SpaceAround => n > 0 ? extra / n : 0f,
            MainAxisAlignment.SpaceEvenly => n > 0 ? extra / (n + 1) : 0f,
            _ => 0f,
        };
        if (mainAlign == MainAxisAlignment.SpaceEvenly) startOffset = gap;

        // RTL mirrors the whole main axis on a horizontal flex: child i's leading edge measured from
        // the RIGHT edge instead of the left, so both the child order and the alignment slack flip
        // (MainAxisAlignment.Start hugs the right edge). Only meaningful with a finite own extent.
        var mirror = rtl && axis == 0 && float.IsFinite(ownMain);

        var cursor = startOffset;

        for (var i = 0; i < count; i++)
        {
            var mainSz = axis == 0 ? metrics[i].Size.Width : metrics[i].Size.Height;
            var crossOff = metrics[i].CrossOffset;

            var mainPos = mirror
                ? bounds.X + ownMain - cursor - mainSz
                : (axis == 0 ? bounds.X : bounds.Y) + cursor;

            var origin = axis == 0
                ? new Offset(mainPos, bounds.Y + crossOff)
                : new Offset(bounds.X + crossOff, mainPos);

            children[i].Layout(origin);
            cursor += mainSz + gap + spacing;
        }
    }

    internal static void Paint(List<Widget> children, PaintList paint)
    {
        // Index, not foreach: a foreach over the list would still be fine here (concrete List, non-
        // boxing), but the plain index keeps the loop trivially inlinable and allocation-free.
        for (var i = 0; i < children.Count; i++) children[i].Paint(paint);
    }

    internal static Widget? HitTest(List<Widget> children, Rect bounds, Offset point)
    {
        if (!bounds.Contains(point.X, point.Y)) return null;
        // Reverse so top-painted (last) child wins
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var hit = children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return null;
    }

    internal readonly struct ChildMetrics(Size size, float crossOffset)
    {
        public readonly Size Size = size;
        public readonly float CrossOffset = crossOffset; // offset on the cross axis for alignment
    }
}