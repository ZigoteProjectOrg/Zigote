using AdwaitaGallery.Pages;

namespace AdwaitaGallery;

/// <summary>One sidebar item: its title, the line under it in search, its icon and the page.</summary>
internal sealed record GalleryEntry(
    string Title,
    string Subtitle,
    string IconName,
    Func<Widget> Build);

/// <summary>A sidebar section — a heading (null for the unheaded first one) over its entries.</summary>
internal sealed record GallerySection(string? Title, GalleryEntry[] Entries);

/// <summary>
///     The gallery's table of contents. Everything else — the sidebar, the search index, the header
///     titles, the welcome page's shortcuts — is derived from it, so adding a page is one entry.
/// </summary>
internal static class GalleryRegistry
{
    public static readonly GallerySection[] Sections = [
        new(
            Title: null,
            Entries: [
                new GalleryEntry(
                    Title: "Welcome",
                    Subtitle: "What this gallery is",
                    IconName: MaterialIcons.WavingHand,
                    Build: () => new WelcomePage()
                ),
            ]
        ),
        new(
            Title: "Navigation",
            Entries: [
                new GalleryEntry(
                    Title: "Navigation View",
                    Subtitle: "A page stack with a header that follows it",
                    IconName: MaterialIcons.Layers,
                    Build: () => new NavigationViewPage()
                ),
                new GalleryEntry(
                    Title: "Split Views",
                    Subtitle: "Sidebar layouts that fold at narrow widths",
                    IconName: MaterialIcons.VerticalSplit,
                    Build: () => new SplitViewsPage()
                ),
                new GalleryEntry(
                    Title: "Paned",
                    Subtitle: "Two panes and a draggable handle",
                    IconName: MaterialIcons.Splitscreen,
                    Build: () => new PanedPage()
                ),
                new GalleryEntry(
                    Title: "Breakpoints",
                    Subtitle: "Containers that fold on their own size",
                    IconName: MaterialIcons.Rule,
                    Build: () => new BreakpointsPage()
                ),
                new GalleryEntry(
                    Title: "View Switcher",
                    Subtitle: "One stack, a header switcher and a bottom bar",
                    IconName: MaterialIcons.SwitchLeft,
                    Build: () => new ViewSwitcherPage()
                ),
                new GalleryEntry(
                    Title: "Tab View",
                    Subtitle: "Pinned and closable tabs with a tab menu",
                    IconName: MaterialIcons.Tab,
                    Build: () => new TabViewPage()
                ),
                new GalleryEntry(
                    Title: "Bottom Sheet",
                    Subtitle: "A sheet you drag up over the content",
                    IconName: MaterialIcons.VerticalAlignBottom,
                    Build: () => new BottomSheetsPage()
                ),
                new GalleryEntry(
                    Title: "Carousel",
                    Subtitle: "Swipeable pages with dot and line indicators",
                    IconName: MaterialIcons.ViewCarousel,
                    Build: () => new CarouselPage()
                ),
                new GalleryEntry(
                    Title: "Image Grid",
                    Subtitle: "A lazy, virtualized grid that pages as it scrolls",
                    IconName: MaterialIcons.GridView,
                    Build: () => new ImageGridPage()
                ),
            ]
        ),
        new(
            Title: "Controls",
            Entries: [
                new GalleryEntry(
                    Title: "Buttons",
                    Subtitle: "Every style, size and shape",
                    IconName: MaterialIcons.SmartButton,
                    Build: () => new ButtonsPage()
                ),
                new GalleryEntry(
                    Title: "Toggles",
                    Subtitle: "Linked groups, toggle buttons and switches",
                    IconName: MaterialIcons.ToggleOn,
                    Build: () => new TogglesPage()
                ),
                new GalleryEntry(
                    Title: "Checks & Radios",
                    Subtitle: "Check buttons, radio groups and their rows",
                    IconName: MaterialIcons.CheckBox,
                    Build: () => new ChecksPage()
                ),
                new GalleryEntry(
                    Title: "Sliders & Progress",
                    Subtitle: "Ranges, spin buttons, progress and level bars",
                    IconName: MaterialIcons.Tune,
                    Build: () => new SlidersPage()
                ),
                new GalleryEntry(
                    Title: "Entries",
                    Subtitle: "Text, search and password entries, plain and as rows",
                    IconName: MaterialIcons.TextFields,
                    Build: () => new EntriesPage()
                ),
                new GalleryEntry(
                    Title: "Colour & Completion",
                    Subtitle: "Colour button, suggestion entry and separators",
                    IconName: MaterialIcons.Palette,
                    Build: () => new ColorAndCompletionPage()
                ),
                new GalleryEntry(
                    Title: "Shortcuts",
                    Subtitle: "Key caps and the keyboard-shortcuts dialog",
                    IconName: MaterialIcons.Keyboard,
                    Build: () => new ShortcutsPage()
                ),
                new GalleryEntry(
                    Title: "Menus & Popovers",
                    Subtitle: "Menu buttons, split buttons and popovers",
                    IconName: MaterialIcons.MoreVert,
                    Build: () => new MenusPage()
                ),
            ]
        ),
        new(
            Title: "Lists",
            Entries: [
                new GalleryEntry(
                    Title: "Boxed Lists",
                    Subtitle: "Every row type, from actions to expanders",
                    IconName: MaterialIcons.ViewList,
                    Build: () => new ListsPage()
                ),
                new GalleryEntry(
                    Title: "Preferences",
                    Subtitle: "The preferences dialog pattern",
                    IconName: MaterialIcons.Settings,
                    Build: () => new PreferencesPage()
                ),
                new GalleryEntry(
                    Title: "Large Lists",
                    Subtitle: "Two thousand rows, recycled while you scroll",
                    IconName: MaterialIcons.FormatListNumbered,
                    Build: () => new LargeListsPage()
                ),
            ]
        ),
        new(
            Title: "Feedback",
            Entries: [
                new GalleryEntry(
                    Title: "Banners",
                    Subtitle: "An inline bar for something that needs an answer",
                    IconName: MaterialIcons.Campaign,
                    Build: () => new BannersPage()
                ),
                new GalleryEntry(
                    Title: "Toasts",
                    Subtitle: "Transient messages with an optional action",
                    IconName: MaterialIcons.Notifications,
                    Build: () => new ToastsPage()
                ),
                new GalleryEntry(
                    Title: "Alert Dialogs",
                    Subtitle: "Adaptive dialogs with suggested and destructive responses",
                    IconName: MaterialIcons.WebAsset,
                    Build: () => new AlertsPage()
                ),
                new GalleryEntry(
                    Title: "Spinner",
                    Subtitle: "The indeterminate Adwaita spinner",
                    IconName: MaterialIcons.Autorenew,
                    Build: () => new SpinnerPage()
                ),
                new GalleryEntry(
                    Title: "Status Pages",
                    Subtitle: "Empty, error and welcome states",
                    IconName: MaterialIcons.Info,
                    Build: () => new StatusPagesPage()
                ),
                new GalleryEntry(
                    Title: "Avatar",
                    Subtitle: "Initials, images and fallback icons",
                    IconName: MaterialIcons.AccountCircle,
                    Build: () => new AvatarPage()
                ),
            ]
        ),
        new(
            Title: "Layout",
            Entries: [
                new GalleryEntry(
                    Title: "Clamp",
                    Subtitle: "Reading-width content in a wide window",
                    IconName: MaterialIcons.FitScreen,
                    Build: () => new ClampPage()
                ),
                new GalleryEntry(
                    Title: "Wrap Box",
                    Subtitle: "Children that flow onto new lines",
                    IconName: MaterialIcons.WrapText,
                    Build: () => new WrapBoxPage()
                ),
                new GalleryEntry(
                    Title: "Adaptive",
                    Subtitle: "One layout that answers to the window size",
                    IconName: MaterialIcons.Devices,
                    Build: () => new AdaptivePage()
                ),
            ]
        ),
        new(
            Title: "Style",
            Entries: [
                new GalleryEntry(
                    Title: "Style Classes",
                    Subtitle: "The libadwaita style classes on real widgets",
                    IconName: MaterialIcons.Palette,
                    Build: () => new StylesPage()
                ),
                new GalleryEntry(
                    Title: "Typography",
                    Subtitle: "The Adwaita type scale",
                    IconName: MaterialIcons.FormatSize,
                    Build: () => new TypographyPage()
                ),
                new GalleryEntry(
                    Title: "Colors",
                    Subtitle: "Named colors and the nine system accents",
                    IconName: MaterialIcons.ColorLens,
                    Build: () => new ColorsPage()
                ),
                new GalleryEntry(
                    Title: "Animations",
                    Subtitle: "Curves, transitions and implicit animation",
                    IconName: MaterialIcons.Animation,
                    Build: () => new AnimationsPage()
                ),
            ]
        ),
        new(
            Title: "Zigote",
            Entries: [
                new GalleryEntry(
                    Title: "Reactivity",
                    Subtitle: "Signals, computed values and effects driving the UI",
                    IconName: MaterialIcons.Bolt,
                    Build: () => new ReactivityPage()
                ),
                new GalleryEntry(
                    Title: "Concurrency",
                    Subtitle: "Threads writing signals, frame-budgeted delivery and sliced work",
                    IconName: MaterialIcons.Speed,
                    Build: () => new ConcurrencyPage()
                ),
                new GalleryEntry(
                    Title: "Drag and Drop",
                    Subtitle: "Draggable payloads and drop targets",
                    IconName: MaterialIcons.OpenWith,
                    Build: () => new DragDropPage()
                ),
            ]
        ),
    ];

    /// <summary>Every entry in sidebar order — the index space of <c>AdwSidebar.Selected</c>.</summary>
    public static readonly GalleryEntry[] Entries = [.. Sections.SelectMany(s => s.Entries)];

    public static AdwSidebarSection[] SidebarSections()
    {
        return [
            .. Sections.Select(s => new AdwSidebarSection(
                    title: s.Title,
                    items: [
                        .. s.Entries.Select(e => new AdwSidebarItem(
                                title: e.Title,
                                iconName: e.IconName
                            )
                        ),
                    ]
                )
            ),
        ];
    }

    /// <summary>Index of a page by title, or -1 — the target of an in-page cross link.</summary>
    public static int IndexOf(string title) => Array.FindIndex(
        array: Entries,
        match: e => e.Title == title
    );
}
