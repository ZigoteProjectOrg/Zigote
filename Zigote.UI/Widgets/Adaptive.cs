using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets;

/// <summary>
///     Coarse width bucket an adaptive layout designs against (Material 3 window size classes).
///     Derive it from real layout width via <see cref="AdaptiveBuilder" /> (preferred — reacts
///     to live resizes and to the panel the widget actually sits in) or from the whole window
///     via <see cref="MediaQueryData.SizeClass" />.
/// </summary>
public enum WindowSizeClass
{
    /// <summary>&lt; 600 logical px: phones, and phone-sized panes.</summary>
    Compact,

    /// <summary>600–839 logical px: tablets portrait, large phones landscape, narrow windows.</summary>
    Medium,

    /// <summary>≥ 840 logical px: desktop windows, tablets landscape.</summary>
    Expanded,
}

public static class WindowSize
{
    /// <summary>Upper bound (exclusive) of <see cref="WindowSizeClass.Compact" />.</summary>
    public const float CompactMax = 600f;

    /// <summary>Upper bound (exclusive) of <see cref="WindowSizeClass.Medium" />.</summary>
    public const float MediumMax = 840f;

    /// <summary>
    ///     The size class for a layout width. Unbounded width (∞ — e.g. inside a horizontal
    ///     scroller) classifies as <see cref="WindowSizeClass.Expanded" />: the content is not
    ///     space-constrained there.
    /// </summary>
    public static WindowSizeClass ClassFor(float width)
    {
        if (float.IsNaN(width)) return WindowSizeClass.Compact;
        if (width < CompactMax) return WindowSizeClass.Compact;
        return width < MediumMax ? WindowSizeClass.Medium : WindowSizeClass.Expanded;
    }
}

/// <summary>
///     Builds its subtree from the <see cref="WindowSizeClass" /> of the width it is actually
///     given — a <see cref="LayoutBuilder" /> specialization, so the builder re-runs whenever
///     the incoming constraints change (window resize, rotation, split-view drag) and one page
///     can serve phone, tablet and desktop:
///     <code>
/// new AdaptiveBuilder((ctx, size) => size == WindowSizeClass.Compact
///     ? new Column(...)   // stacked
///     : new Row(...))     // side-by-side
/// </code>
/// </summary>
public sealed class AdaptiveBuilder : Widget
{
    private readonly Func<BuildContext, WindowSizeClass, Widget> _builder;
    private readonly Transitions.AnimatedSwitcher _switcher;
    private WindowSizeClass? _lastClass;
    private Size _size;

    /// <param name="builder">Builds the subtree for a size class; re-invoked only when the class changes.</param>
    /// <param name="transitionDuration">
    ///     Cross-fade length (seconds) when the size class changes; 0 swaps instantly.
    /// </param>
    public AdaptiveBuilder(Func<BuildContext, WindowSizeClass, Widget> builder,
        float transitionDuration = 0.2f)
    {
        _builder = builder;
        _switcher = new Transitions.AnimatedSwitcher(duration: transitionDuration);
    }

    // Unlike a raw LayoutBuilder (which rebuilds whenever the exact constraints change — i.e. every
    // frame of a window-resize drag), rebuild ONLY when the size CLASS bucket changes. Within a
    // bucket the same subtree is re-measured in place, so a live resize doesn't reconstruct (and
    // silently discard) the whole page tree per frame — the discarded copies were both the resize
    // CPU spike and a native leak for any un-Disposed Image textures inside them.
    //
    // Class changes route through an AnimatedSwitcher, so the breakpoint swap cross-fades (and
    // size-eases) instead of snapping. Retained instances shared between the two subtrees survive
    // the overlap: Widget.Detach skips children the incoming subtree has already re-parented.
    public override Size Measure(Constraints c)
    {
        var cls = WindowSize.ClassFor(c.MaxWidth);
        if (_lastClass != cls)
        {
            _switcher.Child = _builder(BuildContext.Current, cls);
            _lastClass = cls;
        }

        _size = _switcher.Measure(c);
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
        _switcher.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _switcher.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _switcher.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_switcher);
    }
}
