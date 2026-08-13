using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Widgets.Navigation;

/// <summary>
///     A flat back button that pops the nearest <see cref="Navigator" /> when tapped. Suitable as an
///     <c>AppBar.Leading</c>. Renders nothing when the navigator has nothing to pop.
/// </summary>
public sealed class BackButton : ComposedWidget
{
    /// <summary>Button label. Defaults to a chevron + "Back".</summary>
    public string Label { get; init; } = "‹ Back";

    protected override Widget Build(BuildContext context)
    {
        var nav = Navigator.MaybeOf(context);
        if (nav is null || !nav.CanPop)
            return new SizedBox(width: 0f, height: 0f);

        return new Button(label: Label, onPressed: () => nav.MaybePop()) {
            Style = ButtonStyle.Flat,
        };
    }
}
