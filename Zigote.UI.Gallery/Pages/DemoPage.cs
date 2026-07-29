using Zigote.Core;
using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Navigation;

namespace Gallery;

/// <summary>
///     The detail shell every demo renders in: app bar with a <see cref="BackButton" /> (pops the
///     nearest navigator; the app's <c>OnPopPage</c> routes the pop back through the
///     <see cref="NavigationStore" />) over the demo's scrollable content. The title translates
///     (and the back chevron flips) with the active locale.
/// </summary>
internal sealed class DemoPage : StatelessWidget
{
    private readonly DemoInfo _demo;

    public DemoPage(DemoInfo demo)
    {
        _demo = demo;
    }

    protected override Widget Build(BuildContext context)
    {
        var l = GalleryL10n.Of(context);

        // The soft keyboard hides the bottom strip of the window; pad the scroll content by it so
        // a focused field can still be scrolled clear of it. Zero everywhere without an IME.
        var keyboard = MediaQuery.Of(context).ViewInsets.Bottom;

        return new Scaffold(
            new AppBar(
                new Text(_demo.Title(l)),
                // The AppBar leading slot is icon-sized (48 px) and physically on the left in
                // either direction, so a left-pointing chevron is always correct here.
                new BackButton { Label = "‹" },
                centerTitle: true
            ),
            new SingleChildScrollView {
                Child = new Padding(
                    EdgeInsets.FromLtrb(16, 16, 16, 16 + keyboard),
                    _demo.BuildPage()
                ),
            }
        );
    }
}