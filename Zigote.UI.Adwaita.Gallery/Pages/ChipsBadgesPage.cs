namespace AdwaitaGallery.Pages;

/// <summary>
///     Chips and badges — the two small annotations a list or a toolbar carries: a pill you can
///     toggle to filter, and a count pinned to the corner of whatever it counts.
/// </summary>
public sealed class ChipsBadgesPage : ComposedWidget
{
    private static readonly string[] Filters =
        ["All", "Unread", "Flagged", "Attachments", "Drafts"];

    private readonly Signal<int> _filter = new(0);
    private readonly Signal<int> _unread = new(3);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new GalleryPage(
            title: "Chips & Badges",
            description: "Filter pills and the counts that sit on a corner.",
            iconName: MaterialIcons.Label
        ) {
            Children = {
                Demo.Titled(
                    title: "Chips",
                    description:
                    "A chip is a pill-shaped toggle. One of a row is selected at a time here, " +
                    "which is the filter bar every mail client has.",
                    child: Demo.Specimen(
                        new Watch(() =>
                            {
                                var wrap = new Wrap(spacing: Spacing.Sm, runSpacing: Spacing.Sm);
                                for (int i = 0; i < Filters.Length; i++)
                                {
                                    int index = i;
                                    wrap.Children.Add(
                                        new AdwChip(
                                            label: Filters[i],
                                            selected: _filter.Value == i,
                                            onPressed: () => _filter.Value = index
                                        )
                                    );
                                }

                                return wrap;
                            }
                        ),
                        new Watch(() => Demo.Value(Filters[_filter.Value])),
                        new AdwChip(label: "Disabled") { Enabled = false }
                    )
                ),
                Demo.Titled(
                    title: "Badges",
                    description:
                    "A badge counts something on the widget under it. It hides at zero, is a " +
                    "circle for one digit and stretches into a pill past that.",
                    child: Demo.Specimen(
                        new Watch(() =>
                            new Row(spacing: Spacing.Xxl, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    new AdwBadge(
                                        child: Demo.IconButton(
                                            icon: MaterialIcons.Mail,
                                            onPressed: () => { }
                                        ),
                                        count: _unread.Value
                                    ),
                                    new AdwBadge(
                                        child: Demo.IconButton(
                                            icon: MaterialIcons.Notifications,
                                            onPressed: () => { }
                                        ),
                                        count: _unread.Value * 14
                                    ),
                                    new AdwBadge(
                                        child: Demo.IconButton(
                                            icon: MaterialIcons.Error,
                                            onPressed: () => { }
                                        ),
                                        count: _unread.Value
                                    ) { Color = theme.Danger },
                                },
                            }
                        ),
                        Demo.Bar(
                            new AdwButton(label: "Clear", onPressed: () => _unread.Value = 0),
                            new AdwButton(label: "One more", onPressed: () => _unread.Value++) {
                                Style = AdwButtonStyle.Suggested,
                            }
                        )
                    )
                ),
            },
        };
    }
}
