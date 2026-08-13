namespace AdwaitaGallery.Pages;

/// <summary>
///     Status Pages — the empty, error and welcome states, switched live so the three read as one
///     component rather than three screenshots.
/// </summary>
public sealed class StatusPagesPage : ComposedWidget
{
    private static readonly string[] Kinds = ["Empty", "No Results", "Error", "Welcome"];

    private readonly Signal<int> _kind = new(0);

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            title: "Status Pages",
            description:
            "What a view shows when it has nothing to show: an icon, a title, a line, one action.",
            iconName: MaterialIcons.Info
        ) {
            ClampWidth = 680f,
            Children = {
                new Align(
                    alignment: Alignment.Center,
                    child: new AdwToggleGroup(
                        labels: Kinds,
                        active: 0,
                        onActive: i => _kind.Value = i
                    )
                ) { HeightFactor = 1f },
                new SizedBox(
                    height: 320f,
                    child: Demo.Stage(child: new Watch(() => Page(host)), padding: Spacing.Md)
                ),
                Demo.Group(
                    title: "Rules of Thumb",
                    description: null,
                    new AdwActionRow(
                        title: "Name the state, not the failure",
                        subtitle: "“No Documents Yet”, not “Query returned 0 rows”"
                    ),
                    new AdwActionRow(
                        title: "Offer the one action that fixes it",
                        subtitle: "A pill button under the description"
                    ),
                    new AdwActionRow(
                        title: "Compact mode for small spaces",
                        subtitle: "The sidebar's no-results state is the same widget"
                    )
                ),
            },
        };
    }

    private Widget Page(GalleryHost host)
    {
        return _kind.Value switch {
            1 => new AdwStatusPage {
                IconName = MaterialIcons.SearchOff,
                Title = "No Results Found",
                Description = "Try a different search term",
            },
            2 => new AdwStatusPage {
                IconName = MaterialIcons.ErrorOutline,
                Title = "Could Not Connect",
                Description = "Check the network and try again",
                Child = new AdwButton(label: "Retry", onPressed: () => host.Toast("Retrying…")) {
                    Style = AdwButtonStyle.Suggested,
                    Pill = true,
                },
            },
            3 => new AdwStatusPage {
                IconName = MaterialIcons.WavingHand,
                Title = "Welcome to Adwaita Demo",
                Description = "A tour of the widget set, one page at a time",
                Child = new AdwButton(
                    label: "Start the Tour",
                    onPressed: () => host.Open("Boxed Lists")
                ) {
                    Style = AdwButtonStyle.Suggested,
                    Pill = true,
                },
            },
            _ => new AdwStatusPage {
                IconName = MaterialIcons.Inbox,
                Title = "No Documents Yet",
                Description = "Documents you create will appear here",
                Child =
                    new AdwButton(label: "New Document", onPressed: () => host.Toast("Created")) {
                        Style = AdwButtonStyle.Suggested,
                        Pill = true,
                    },
            },
        };
    }
}
