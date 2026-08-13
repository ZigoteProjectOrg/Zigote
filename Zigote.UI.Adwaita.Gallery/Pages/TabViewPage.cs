namespace AdwaitaGallery.Pages;

/// <summary>Tab View — the tab strip inline in the page, and the demo's full-size window.</summary>
public sealed class TabViewPage : ComposedWidget
{
    /// <summary>Backgrounds standing in for the demo's <c>tab-page-color-1..8</c> classes.</summary>
    private static readonly Color[] PageColors = [
        Color.Rgb(r: 153, g: 193, b: 241),
        Color.Rgb(r: 143, g: 240, b: 164),
        Color.Rgb(r: 249, g: 240, b: 107),
        Color.Rgb(r: 255, g: 190, b: 111),
        Color.Rgb(r: 246, g: 97, b: 81),
        Color.Rgb(r: 220, g: 138, b: 221),
        Color.Rgb(r: 205, g: 171, b: 143),
        Color.Rgb(r: 222, g: 221, b: 218),
    ];

    /// <summary>The pool the demo's "random themed icon" is drawn from.</summary>
    private static readonly string[] TabIcons = [
        MaterialIcons.Home,
        MaterialIcons.Star,
        MaterialIcons.Folder,
        MaterialIcons.MusicNote,
        MaterialIcons.Image,
        MaterialIcons.Map,
        MaterialIcons.Science,
        MaterialIcons.Terminal,
    ];

    private static readonly Random Rng = new();

    /// <summary>Tab numbering is global and keeps incrementing, exactly like the GNOME demo.</summary>
    private static int _nextTab = 1;

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Tab View",
            description:
            "A modern tab widget: a strip of pinnable, closable tabs over one content area.",
            iconName: MaterialIcons.Tab
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Titled(
                    title: "Inline",
                    description: "The same view sized into the page — each tab's entry renames it.",
                    child: new SizedBox(height: 300f, child: Inline())
                ),
                Demo.Group(
                    title: "Full Size",
                    description:
                    "The GNOME demo's own window, with the tab menu, the overview and new windows.",
                    new AdwActionRow(title: "Run the Demo", subtitle: "AdwTabView + AdwTabBar") {
                        ShowChevron = true,
                        OnActivated = () => ShowDemoWindow(),
                    }
                ),
            },
        };
    }

    /// <summary>Three tabs under their strip, sized into the page instead of a window.</summary>
    private static Widget Inline()
    {
        var view = new AdwTabView();
        var revision = new Signal<int>(0);
        for (int i = 0; i < 3; i++) view.Append(NewTab(revision));

        return new ClipRRect(
            radius: AdwMetrics.CardRadius,
            child: new AdwToolbarView(view) {
                TopBars = {
                    new Watch(() =>
                        {
                            _ = revision.Value;
                            return new AdwTabBar(view);
                        }
                    ),
                },
            }
        );
    }

    // ── The demo window ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Opens the demo "window". <paramref name="adopted" /> is the page moved here by the tab
    ///     menu's "Move to New Window"; without it the view is prepopulated with three tabs.
    ///     <para>
    ///         ponytail: a dialog stands in for a real second window (Zigote has one window), and
    ///         the demo's Ctrl+T / Ctrl+N / Ctrl+W accelerators are dropped — they would need a
    ///         window-level key binding API.
    ///     </para>
    /// </summary>
    private static void ShowDemoWindow(AdwTabPage? adopted = null)
    {
        var view = new AdwTabView();

        // Bumped whenever a tab's title, icon or pinned state changes: AdwTabView only rebuilds the
        // strip on add/remove/select, so the bar and the tab menu ride on this signal instead.
        var revision = new Signal<int>(0);

        AdwDialog? dlg = null;
        view.OnClosed = _ =>
        {
            // Closing the last tab closes the window, like the demo.
            if (view.Pages.Count == 0) dlg?.Close();
        };

        if (adopted is not null)
            view.Append(adopted);
        else
        {
            for (int i = 0; i < 3; i++)
                view.Append(NewTab(revision));
        }

        dlg = new AdwDialog(
            new AdwToolbarView(view) {
                RaisedTopBar = true,
                TopBars = {
                    new AdwHeaderBar {
                        Title = "Tab View Demo",
                        Start = {
                            new Tooltip(
                                message: "New Window",
                                child: Demo.IconButton(
                                    icon: MaterialIcons.Window,
                                    onPressed: () => ShowDemoWindow()
                                )
                            ),
                        },
                        End = {
                            new Watch(() => TabMenu(view: view, revision: revision)),
                            new Tooltip(
                                message: "View Open Tabs",
                                child: new AdwTabButton(
                                    view: view,
                                    onPressed: () => ShowOverview(view: view, revision: revision)
                                )
                            ),
                            new Tooltip(
                                message: "New Tab",
                                child: Demo.IconButton(
                                    icon: MaterialIcons.Add,
                                    onPressed: () => view.Append(NewTab(revision))
                                )
                            ),
                            new Tooltip(
                                message: "Close",
                                child: Demo.IconButton(
                                    icon: MaterialIcons.Close,
                                    onPressed: () => dlg?.Close()
                                )
                            ),
                        },
                    },
                    new Watch(() =>
                        {
                            _ = revision.Value;
                            return new AdwTabBar(view);
                        }
                    ),
                },
            }
        ) {
            ContentWidth = 800f,
            ContentHeight = 600f,
        };
        dlg.Show();
    }

    // ── Tabs ────────────────────────────────────────────────────────────────────

    private static AdwTabPage NewTab(Signal<int> revision, string? title = null,
        string? icon = null)
    {
        var page = new AdwTabPage(
            title: title ?? $"Tab {_nextTab++}",
            child: new SizedBox(),
            iconName: icon ?? TabIcons[Rng.Next(TabIcons.Length)]
        );
        page.Child = TabContent(page: page, revision: revision);
        return page;
    }

    /// <summary>The demo's page: a centered entry bound both ways to the tab title.</summary>
    private static Widget TabContent(AdwTabPage page, Signal<int> revision)
    {
        // The colour is derived from the title rather than stored, so a duplicated tab — which
        // copies the title — comes out in the same colour as its original.
        var color = PageColors[(int)((uint)page.Title.GetHashCode() % PageColors.Length)];
        return new Container {
            Background = color,
            Child = new Center {
                Child = new AdwEntry {
                    Text = page.Title,
                    Width = 220f,
                    OnChanged = text =>
                    {
                        page.Title = text;
                        revision.Value++;
                    },
                },
            },
        };
    }

    /// <summary>
    ///     The demo's tab context menu, acting on the selected tab.
    ///     <para>
    ///         ponytail: AdwTabBar has no right-click hook, so the menu lives in the header bar;
    ///         the "Loading" / "Needs Attention" / "Indicator" section is dropped — AdwTabPage
    ///         carries no such state and AdwTabBar draws neither spinner nor indicator.
    ///     </para>
    /// </summary>
    private static Widget TabMenu(AdwTabView view, Signal<int> revision)
    {
        _ = revision.Value; // rebuild on rename / pin
        int index = view.SelectedIndex; // ... and when the selection moves
        if (view.Pages.Count == 0) return new SizedBox();

        var page = view.Pages[index];
        var previous = index > 0 ? view.Pages[index - 1] : null;
        bool canCloseBefore = !page.Pinned && previous is not null && !previous.Pinned;
        bool canCloseAfter = index < view.Pages.Count - 1;
        bool hasIcon = page.IconName is not null;

        return new AdwMenuButton(MaterialIcons.MoreVert) {
            Sections = {
                new List<AdwMenuItem> {
                    new(
                        label: "Move to New Window",
                        onActivated: () =>
                        {
                            view.Close(page);
                            ShowDemoWindow(page);
                        }
                    ) {
                        Enabled = !page.Pinned && view.Pages.Count > 1,
                    },
                    new(
                        label: "Duplicate",
                        onActivated: () => view.Append(
                            NewTab(revision: revision, title: page.Title, icon: page.IconName)
                        )
                    ),
                },
                new List<AdwMenuItem> {
                    new(
                        label: "Pin Tab",
                        onActivated: () => SetPinned(page: page, pinned: true, revision: revision)
                    ) {
                        Enabled = !page.Pinned,
                    },
                    new(
                        label: "Unpin Tab",
                        onActivated: () => SetPinned(page: page, pinned: false, revision: revision)
                    ) {
                        Enabled = page.Pinned,
                    },
                },
                new List<AdwMenuItem> {
                    // Re-enabling picks a fresh icon rather than restoring the last one.
                    new(
                        label: "Icon",
                        onActivated: () => SetIcon(
                            page: page,
                            icon: hasIcon ? null : TabIcons[Rng.Next(TabIcons.Length)],
                            revision: revision
                        )
                    ) {
                        Role = AdwMenuItemRole.Check,
                        Checked = hasIcon,
                    },
                    new(
                        label: "Refresh Icon",
                        onActivated: () => SetIcon(
                            page: page,
                            icon: TabIcons[Rng.Next(TabIcons.Length)],
                            revision: revision
                        )
                    ) {
                        Enabled = hasIcon,
                    },
                },
                new List<AdwMenuItem> {
                    new(
                        label: "Close Other Tabs",
                        onActivated: () => CloseRange(
                            view: view,
                            page: page,
                            before: true,
                            after: true
                        )
                    ) {
                        Enabled = canCloseBefore || canCloseAfter,
                    },
                    new(
                        label: "Close Tabs to the Left",
                        onActivated: () => CloseRange(
                            view: view,
                            page: page,
                            before: true,
                            after: false
                        )
                    ) {
                        Enabled = canCloseBefore,
                    },
                    new(
                        label: "Close Tabs to the Right",
                        onActivated: () => CloseRange(
                            view: view,
                            page: page,
                            before: false,
                            after: true
                        )
                    ) {
                        Enabled = canCloseAfter,
                    },
                },
                new List<AdwMenuItem> {
                    new(label: "Close", onActivated: () => view.Close(page)) {
                        Enabled = !page.Pinned,
                    },
                },
            },
        };
    }

    private static void SetPinned(AdwTabPage page, bool pinned, Signal<int> revision)
    {
        page.Pinned = pinned;
        revision.Value++;
    }

    private static void SetIcon(AdwTabPage page, string? icon, Signal<int> revision)
    {
        page.IconName = icon;
        revision.Value++;
    }

    /// <summary>Closes the unpinned tabs before and/or after <paramref name="page" />.</summary>
    private static void CloseRange(AdwTabView view, AdwTabPage page, bool before, bool after)
    {
        int index = view.Pages.IndexOf(page);
        // Collect first: closing shifts the positions of everything after the removed tab.
        var doomed = new List<AdwTabPage>();
        for (int i = 0; i < view.Pages.Count; i++)
        {
            if (i == index || view.Pages[i].Pinned) continue;
            if (i < index ? before : after) doomed.Add(view.Pages[i]);
        }

        foreach (var other in doomed) view.Close(other);
    }

    // ── Overview ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The real <see cref="AdwTabOverview" />. This used to be a hand-rolled grid of buttons
    ///     with a note that the kit had no overview widget; it has one now, so the gallery shows
    ///     that instead of a stand-in.
    /// </summary>
    private static void ShowOverview(AdwTabView view, Signal<int> revision)
    {
        new AdwTabOverview(view) {
            Title = "Open Tabs",
            OnCreateTab = () => view.Append(NewTab(revision)),
        }.Show();
    }
}
