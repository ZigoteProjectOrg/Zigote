using Zigote.Core.State;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwPreferencesDialog — a ~640×560 dialog with a flat header bar (title + circular close
///     button) over one or more <see cref="AdwPreferencesPage" />s. With multiple pages a joined
///     toggle of page titles under the header switches between them.
/// </summary>
public sealed class AdwPreferencesDialog : AdwDialog
{
    private readonly Signal<int> _page = new(0);

    public AdwPreferencesDialog()
    {
        ContentWidth = 640f;
        ContentHeight = 560f;
        Child = new Content(this);
    }

    /// <summary>The <see cref="AdwPreferencesPage" />s. Populate before showing.</summary>
    public List<Widget> Pages { get; init; } = [];

    // init: the content sheet is built in the constructor, so a later assignment never shows.
    public string Title { get; init; } = "Preferences";

    private sealed class Content(AdwPreferencesDialog owner) : ComposedWidget
    {
        protected override Widget Build(BuildContext context)
        {
            var header = new AdwHeaderBar {
                Flat = true,
                Title = owner.Title,
            };
            header.End.Add(
                new AdwButton(onPressed: owner.Close) {
                    IconName = MaterialIcons.Close,
                    Style = AdwButtonStyle.Flat,
                    Circular = true,
                }
            );

            var col = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = { header },
            };

            if (owner.Pages.Count > 1)
                col.Children.Add(
                    new Padding(
                        EdgeInsets.Only(bottom: Spacing.Sm),
                        // heightFactor 1: hug the switcher's height, fill (and center on) the width.
                        new Center(new Watch(Switcher), heightFactor: 1.0)
                    )
                );

            col.Children.Add(
                new Expanded(
                    new Watch(() => owner.Pages.Count == 0
                        ? new SizedBox()
                        : owner.Pages[Math.Clamp(owner._page.Value, 0, owner.Pages.Count - 1)]
                    )
                )
            );

            return col;
        }

        // The page toggle IS an AdwToggleGroup — this used to be a hand-rolled copy of one, which
        // meant it neither showed the pages' icons nor faded on hover like every other switcher.
        private Widget Switcher()
        {
            return new AdwToggleGroup(
                [
                    .. owner.Pages.Select((page, i) => page is AdwPreferencesPage p
                        ? new AdwToggle(p.Title, p.IconName)
                        : new AdwToggle($"Page {i + 1}")
                    ),
                ],
                owner._page.Value,
                i => owner._page.Value = i
            );
        }
    }
}