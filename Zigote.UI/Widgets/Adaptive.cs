using Zigote.UI.Widgets.Layout;

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
public sealed class AdaptiveBuilder : StatelessWidget
{
    private readonly Func<BuildContext, WindowSizeClass, Widget> _builder;

    public AdaptiveBuilder(Func<BuildContext, WindowSizeClass, Widget> builder)
    {
        _builder = builder;
    }

    protected override Widget Build(BuildContext context)
    {
        return new LayoutBuilder((ctx, c) => _builder(ctx, WindowSize.ClassFor(c.MaxWidth)));
    }
}
