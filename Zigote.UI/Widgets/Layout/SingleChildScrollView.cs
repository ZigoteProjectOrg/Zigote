using Zigote.Core;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A scroll view with a single child. Thin alias over
///     <see cref="ScrollView" />: <paramref name="scrollDirection" /> selects the axis and
///     <paramref name="padding" /> wraps the child. (<paramref name="reverse" /> is accepted but
///     not applied.)
/// </summary>
public sealed class SingleChildScrollView : ScrollView
{
    public SingleChildScrollView(
        Widget? child = null,
        Axis scrollDirection = Axis.Vertical,
        EdgeInsets? padding = null,
        bool reverse = false)
        : base(padding is { } p && child is not null ? new Padding(p, child) : child)
    {
        ScrollVertical = scrollDirection == Axis.Vertical;
        ScrollHorizontal = scrollDirection == Axis.Horizontal;
        _ = reverse;
    }
}