namespace Zigote.UI.Material;

/// <summary>
///     Shows the child at the selected index. Alias over
///     <see cref="TabView" />; pair it with a <see cref="TabBar" /> and keep
///     <see cref="TabView.SelectedIndex" /> in sync (there is no shared <c>TabController</c>, so wire
///     the bar's <c>onTap</c> to update this view's index).
/// </summary>
public sealed class TabBarView : TabView
{
    public TabBarView(List<Widget>? children = null, int initialIndex = 0)
    {
        if (children is not null) Children.AddRange(children);
        SelectedIndex = initialIndex;
    }
}