namespace AdwaitaGallery.Pages;

/// <summary>Split Views — the AdwNavigationSplitView and AdwOverlaySplitView demo dialogs.</summary>
public sealed class SplitViewsPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Split Views",
            description:
            "Sidebar and content side by side — until the window is too narrow, and then not.",
            iconName: MaterialIcons.VerticalSplit
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Titled(
                    title: "Overlay Split View",
                    description:
                    "The sidebar slides over the content, behind a scrim once collapsed. Narrow the window to see it fold.",
                    child: new SizedBox(height: 300f, child: Inline())
                ),
                Demo.Group(
                    title: "Full Size",
                    description: "The same two containers at window scale, in a dialog.",
                    new AdwActionRow(
                        title: "Navigation Split View",
                        subtitle: "Two panes that become one page at the breakpoint"
                    ) {
                        ShowChevron = true,
                        OnActivated = ShowNavigationSplitView,
                    },
                    new AdwActionRow(
                        title: "Overlay Split View",
                        subtitle: "A sidebar that overlays instead of pushing"
                    ) {
                        ShowChevron = true,
                        OnActivated = ShowOverlaySplitView,
                    }
                ),
                Demo.Group(
                    title: "Which One",
                    description: null,
                    new AdwActionRow(
                        title: "Navigation",
                        subtitle: "The sidebar is where you pick what the content shows"
                    ) { IconName = MaterialIcons.VerticalSplit },
                    new AdwActionRow(
                        title: "Overlay",
                        subtitle: "The sidebar is a tool panel the content does not depend on"
                    ) { IconName = MaterialIcons.ViewSidebar }
                ),
            },
        };
    }

    /// <summary>A live overlay split view sized into the page, with its own toggle and breakpoint.</summary>
    private static Widget Inline()
    {
        var split = new AdwOverlaySplitView {
            SidebarWidth = 200f,
            AutoCollapseBelow = 420f,
        };
        split.Sidebar = new AdwStatusPage {
            IconName = MaterialIcons.ViewSidebar,
            Title = "Sidebar",
            Compact = true,
        };
        split.Content = new AdwStatusPage {
            IconName = MaterialIcons.Article,
            Title = "Content",
            Compact = true,
        };

        var bar = new AdwHeaderBar {
            Flat = true,
            Title = "Inline",
            ShowStartWindowControls = false,
            ShowEndWindowControls = false,
        };
        bar.Start.Add(
            new Tooltip(
                message: "Toggle Sidebar",
                child: Demo.IconButton(
                    icon: MaterialIcons.ViewSidebar,
                    onPressed: () => split.ShowSidebar = !split.ShowSidebar
                )
            )
        );

        return new ClipRRect(
            radius: AdwMetrics.CardRadius,
            child: new AdwToolbarView(split) { TopBars = { bar } }
        );
    }

    /// <summary>The "Navigation Split View" dialog: sidebar and content as navigation pages.</summary>
    private static void ShowNavigationSplitView()
    {
        AdwDialog? dlg = null;
        var split = new AdwNavigationSplitView {
            SidebarWidth = 220f,
            AutoCollapseBelow = 400f,
        };

        var sidebarBar = new AdwHeaderBar {
            ShowStartWindowControls = false,
            ShowEndWindowControls = false,
        };
        sidebarBar.End.Add(
            Demo.IconButton(icon: MaterialIcons.Close, onPressed: () => dlg?.Close())
        );
        // Both the "open the other pane" button and the back button only exist past the
        // breakpoint, where the two panes have become one page — IsCollapsed is the split view's
        // own answer to "am I folded", including the AutoCollapseBelow the layout decides.
        split.Sidebar = new AdwToolbarView(
            new AdwStatusPage {
                Title = "Sidebar",
                Child = new Watch(() => split.IsCollapsed.Value
                    ? new AdwButton(
                        label: "Open Content",
                        onPressed: () => split.ShowContent = true
                    ) { Pill = true }
                    : Demo.Caption("Widen or narrow the dialog to fold the panes")
                ),
            }
        ) { TopBars = { sidebarBar } };

        split.Content = new AdwToolbarView(new AdwStatusPage { Title = "Content" }) {
            TopBars = {
                new Watch(() => new AdwHeaderBar {
                        ShowBackButton = split.IsCollapsed.Value,
                        OnBack = () => split.ShowContent = false,
                        ShowStartWindowControls = false,
                        ShowEndWindowControls = false,
                    }
                ),
            },
        };

        dlg = new AdwDialog(split) {
            ContentWidth = 640f,
            ContentHeight = 480f,
        };
        dlg.Show();
    }

    /// <summary>The "Overlay Split View" dialog: a sidebar that slides over the content.</summary>
    private static void ShowOverlaySplitView()
    {
        AdwDialog? dlg = null;
        var atStart = new Signal<bool>(true);
        var split = new AdwOverlaySplitView {
            SidebarWidth = 240f,
            // The demo's `max-width: 400sp -> collapsed` breakpoint, decided by the split view's
            // own layout instead of a probe bolted onto the toolbar view.
            AutoCollapseBelow = 400f,
        };

        // ponytail: sidebar position End is faked by mirroring the panes with an RTL Directionality
        // (and an Invalidate so the split view's Row re-resolves it); a full version would need a
        // SidebarPosition property on AdwOverlaySplitView. The inner LTR scope keeps the sidebar's
        // own content unmirrored.
        var direction = new Directionality(direction: TextDirection.Ltr, child: split);

        split.Sidebar = new Directionality(
            direction: TextDirection.Ltr,
            child: new AdwStatusPage {
                Title = "Sidebar",
                // ponytail: one joined toggle group instead of two stacked grouped pill toggles —
                // AdwToggleButton has no pill style.
                Child = new AdwToggleGroup(
                    labels: ["Start", "End"],
                    active: 0,
                    onActive: i =>
                    {
                        atStart.Value = i == 0;
                        direction.Direction = i == 0 ? TextDirection.Ltr : TextDirection.Rtl;
                        split.Invalidate();
                    }
                ) { Round = true },
            }
        );
        split.Content = new AdwStatusPage { Title = "Content" };

        // The sidebar toggle sits on whichever side the sidebar is on, as in the demo.
        var header = new Watch(() =>
            {
                var bar = new AdwHeaderBar {
                    ShowStartWindowControls = false,
                    ShowEndWindowControls = false,
                };
                var toggle = new Tooltip(
                    message: "Toggle Sidebar",
                    child: Demo.IconButton(
                        icon: MaterialIcons.ViewSidebar,
                        onPressed: () => split.ShowSidebar = !split.ShowSidebar
                    )
                );
                if (atStart.Value) bar.Start.Add(toggle);
                else bar.End.Add(toggle);
                bar.End.Add(
                    Demo.IconButton(icon: MaterialIcons.Close, onPressed: () => dlg?.Close())
                );
                return bar;
            }
        );

        dlg = new AdwDialog(
            new AdwToolbarView(direction) {
                RaisedTopBar = true,
                TopBars = { header },
            }
        ) {
            ContentWidth = 640f,
            ContentHeight = 480f,
        };
        dlg.Show();
    }
}
