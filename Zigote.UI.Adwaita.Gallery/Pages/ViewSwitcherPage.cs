namespace AdwaitaGallery.Pages;

/// <summary>View Switcher — the stack inline in the page, and the AdwViewSwitcher demo dialog.</summary>
public sealed class ViewSwitcherPage : ComposedWidget
{
    /// <summary>The demo's <c>max-width: 550sp</c> breakpoint.</summary>
    private const float NarrowWidth = 550f;

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "View Switcher",
            description:
            "One stack, switched from the header while there is room and from a bottom bar when there is not.",
            iconName: MaterialIcons.SwitchLeft
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Titled(
                    title: "Inline",
                    description:
                    "The same stack with the inline switcher — the compact form for a page.",
                    child: new SizedBox(height: 320f, child: Inline())
                ),
                Demo.Titled(
                    title: "Sidebar",
                    description:
                    "AdwViewSwitcherSidebar drives the same stack from a list — what a wide window " +
                    "uses where a narrow one would use the switcher above. Its 1.10 prefix slot " +
                    "holds the search entry.",
                    child: new SizedBox(height: 320f, child: Sidebar())
                ),
                Demo.Group(
                    title: "Full Size",
                    description:
                    "At window scale the switcher moves between the header bar and a bottom bar at 550 px.",
                    new AdwActionRow(
                        title: "Run the Demo",
                        subtitle: "AdwViewSwitcher + AdwViewSwitcherBar"
                    ) {
                        ShowChevron = true,
                        OnActivated = ShowDemo,
                    }
                ),
            },
        };
    }

    /// <summary>The same stack, switched from a sidebar with a search entry in its prefix.</summary>
    private static Widget Sidebar()
    {
        var stack = Stack();
        var sidebar = new AdwViewSwitcherSidebar(stack);
        var search = new AdwSearchEntry {
            Placeholder = "Filter views",
            Compact = true,
        };
        search.OnChanged = q => sidebar.Filter = q;
        sidebar.Prefix = new Padding(padding: EdgeInsets.All(Spacing.Sm), child: search);

        return new ClipRRect(
            radius: AdwMetrics.CardRadius,
            child: new DecoratedBox {
                Radius = AdwMetrics.CardRadius,
                BorderColor = ThemeProvider.Of(BuildContext.Current).Border,
                Child = new Row {
                    Children = {
                        new SizedBox(width: 220f, child: sidebar),
                        new SizedBox(width: 1f, child: new AdwSeparator(true)),
                        new Expanded(stack),
                    },
                },
            }
        );
    }

    /// <summary>The stack with an inline switcher above it, sized into the page.</summary>
    private static Widget Inline()
    {
        var stack = Stack();
        return new ClipRRect(
            radius: AdwMetrics.CardRadius,
            child: new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    new Padding(
                        padding: EdgeInsets.All(Spacing.Sm),
                        child: new Center { Child = new AdwInlineViewSwitcher(stack) }
                    ),
                    new Expanded(stack),
                },
            }
        );
    }

    /// <summary>The demo's four pages — the same stack inline and in the dialog.</summary>
    private static AdwViewStack Stack()
    {
        return new AdwViewStack(
            new AdwViewStackPage(
                name: "page1",
                title: "World",
                child: Page(
                    icon: MaterialIcons.Public,
                    title: "World",
                    description: "View the time in cities around the world"
                ),
                iconName: MaterialIcons.Public
            ),
            new AdwViewStackPage(
                name: "page2",
                title: "Alarm",
                child: Page(
                    icon: MaterialIcons.Alarm,
                    title: "Alarm",
                    description: "Set customizable alarms to go off on specific days"
                ),
                iconName: MaterialIcons.Alarm
            ),
            new AdwViewStackPage(
                name: "page3",
                title: "Stopwatch",
                child: Page(
                    icon: MaterialIcons.Timer,
                    title: "Stopwatch",
                    description: "Use the stopwatch to time how long something takes"
                ),
                iconName: MaterialIcons.Timer,
                badge: 3
            ),
            new AdwViewStackPage(
                name: "page4",
                title: "Timer",
                child: Page(
                    icon: MaterialIcons.HourglassEmpty,
                    title: "Timer",
                    description: "Set a countdown in seconds, minutes or hours"
                ),
                iconName: MaterialIcons.HourglassEmpty
            )
        );
    }

    private static void ShowDemo()
    {
        var stack = Stack();
        var narrow = new Signal<bool>(false);

        // The demo's breakpoint, read by a zero-height probe rather than by wrapping the dialog: a
        // LayoutBuilder around the whole thing returns a new column every run, so the retained
        // stack — and the page the user is on — is detached and re-attached on every resize frame.
        var breakpoint = new LayoutBuilder((_, c) =>
            {
                narrow.Value = c.MaxWidth < NarrowWidth;
                return SizedBox.Shrink();
            }
        );

        AdwDialog? dlg = null;
        // Only the two switcher sites answer to the breakpoint: wide keeps the switcher in the
        // header bar, narrow drops it there and reveals the bottom AdwViewSwitcherBar instead.
        var header = new Watch(() => new AdwHeaderBar {
                Title = "AdwViewSwitcher Demo",
                TitleWidget = narrow.Value ? null : new AdwViewSwitcher(stack),
                End = { Demo.IconButton(icon: MaterialIcons.Close, onPressed: () => dlg?.Close()) },
            }
        );
        var switcherBar = new Watch(() => narrow.Value
            ? new AdwViewSwitcherBar(stack)
            : SizedBox.Shrink()
        );

        dlg = new AdwDialog(
            new AdwToolbarView(stack) {
                TopBars = {
                    breakpoint,
                    header,
                },
                BottomBars = { switcherBar },
            }
        ) {
            ContentWidth = 640f,
            ContentHeight = 480f,
        };
        dlg.Show();
    }

    private static Widget Page(string icon, string title, string description)
    {
        return new AdwStatusPage {
            IconName = icon,
            Title = title,
            Description = description,
        };
    }
}
