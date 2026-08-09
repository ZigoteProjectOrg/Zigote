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
            null,
            [
                new GalleryEntry(
                    "Welcome",
                    "What this gallery is",
                    MaterialIcons.WavingHand,
                    () => new WelcomePage()
                ),
            ]
        ),
        new(
            "Navigation",
            [
                new GalleryEntry(
                    "Navigation View",
                    "A page stack with a header that follows it",
                    MaterialIcons.Layers,
                    () => new NavigationViewPage()
                ),
                new GalleryEntry(
                    "Split Views",
                    "Sidebar layouts that fold at narrow widths",
                    MaterialIcons.VerticalSplit,
                    () => new SplitViewsPage()
                ),
                new GalleryEntry(
                    "Paned",
                    "Two panes and a draggable handle",
                    MaterialIcons.Splitscreen,
                    () => new PanedPage()
                ),
                new GalleryEntry(
                    "Breakpoints",
                    "Containers that fold on their own size",
                    MaterialIcons.Rule,
                    () => new BreakpointsPage()
                ),
                new GalleryEntry(
                    "View Switcher",
                    "One stack, a header switcher and a bottom bar",
                    MaterialIcons.SwitchLeft,
                    () => new ViewSwitcherPage()
                ),
                new GalleryEntry(
                    "Tab View",
                    "Pinned and closable tabs with a tab menu",
                    MaterialIcons.Tab,
                    () => new TabViewPage()
                ),
                new GalleryEntry(
                    "Bottom Sheet",
                    "A sheet you drag up over the content",
                    MaterialIcons.VerticalAlignBottom,
                    () => new BottomSheetsPage()
                ),
                new GalleryEntry(
                    "Carousel",
                    "Swipeable pages with dot and line indicators",
                    MaterialIcons.ViewCarousel,
                    () => new CarouselPage()
                ),
            ]
        ),
        new(
            "Controls",
            [
                new GalleryEntry(
                    "Buttons",
                    "Every style, size and shape",
                    MaterialIcons.SmartButton,
                    () => new ButtonsPage()
                ),
                new GalleryEntry(
                    "Toggles",
                    "Linked groups, toggle buttons and switches",
                    MaterialIcons.ToggleOn,
                    () => new TogglesPage()
                ),
                new GalleryEntry(
                    "Checks & Radios",
                    "Check buttons, radio groups and their rows",
                    MaterialIcons.CheckBox,
                    () => new ChecksPage()
                ),
                new GalleryEntry(
                    "Sliders & Progress",
                    "Ranges, spin buttons, progress and level bars",
                    MaterialIcons.Tune,
                    () => new SlidersPage()
                ),
                new GalleryEntry(
                    "Entries",
                    "Text, search and password entries, plain and as rows",
                    MaterialIcons.TextFields,
                    () => new EntriesPage()
                ),
                new GalleryEntry(
                    "Colour & Completion",
                    "Colour button, suggestion entry and separators",
                    MaterialIcons.Palette,
                    () => new ColorAndCompletionPage()
                ),
                new GalleryEntry(
                    "Shortcuts",
                    "Key caps and the keyboard-shortcuts dialog",
                    MaterialIcons.Keyboard,
                    () => new ShortcutsPage()
                ),
                new GalleryEntry(
                    "Menus & Popovers",
                    "Menu buttons, split buttons and popovers",
                    MaterialIcons.MoreVert,
                    () => new MenusPage()
                ),
            ]
        ),
        new(
            "Lists",
            [
                new GalleryEntry(
                    "Boxed Lists",
                    "Every row type, from actions to expanders",
                    MaterialIcons.ViewList,
                    () => new ListsPage()
                ),
                new GalleryEntry(
                    "Preferences",
                    "The preferences dialog pattern",
                    MaterialIcons.Settings,
                    () => new PreferencesPage()
                ),
                new GalleryEntry(
                    "Large Lists",
                    "Two thousand rows, recycled while you scroll",
                    MaterialIcons.FormatListNumbered,
                    () => new LargeListsPage()
                ),
            ]
        ),
        new(
            "Feedback",
            [
                new GalleryEntry(
                    "Banners",
                    "An inline bar for something that needs an answer",
                    MaterialIcons.Campaign,
                    () => new BannersPage()
                ),
                new GalleryEntry(
                    "Toasts",
                    "Transient messages with an optional action",
                    MaterialIcons.Notifications,
                    () => new ToastsPage()
                ),
                new GalleryEntry(
                    "Alert Dialogs",
                    "Adaptive dialogs with suggested and destructive responses",
                    MaterialIcons.WebAsset,
                    () => new AlertsPage()
                ),
                new GalleryEntry(
                    "Spinner",
                    "The indeterminate Adwaita spinner",
                    MaterialIcons.Autorenew,
                    () => new SpinnerPage()
                ),
                new GalleryEntry(
                    "Status Pages",
                    "Empty, error and welcome states",
                    MaterialIcons.Info,
                    () => new StatusPagesPage()
                ),
                new GalleryEntry(
                    "Avatar",
                    "Initials, images and fallback icons",
                    MaterialIcons.AccountCircle,
                    () => new AvatarPage()
                ),
            ]
        ),
        new(
            "Layout",
            [
                new GalleryEntry(
                    "Clamp",
                    "Reading-width content in a wide window",
                    MaterialIcons.FitScreen,
                    () => new ClampPage()
                ),
                new GalleryEntry(
                    "Wrap Box",
                    "Children that flow onto new lines",
                    MaterialIcons.WrapText,
                    () => new WrapBoxPage()
                ),
                new GalleryEntry(
                    "Adaptive",
                    "One layout that answers to the window size",
                    MaterialIcons.Devices,
                    () => new AdaptivePage()
                ),
            ]
        ),
        new(
            "Style",
            [
                new GalleryEntry(
                    "Style Classes",
                    "The libadwaita style classes on real widgets",
                    MaterialIcons.Palette,
                    () => new StylesPage()
                ),
                new GalleryEntry(
                    "Typography",
                    "The Adwaita type scale",
                    MaterialIcons.FormatSize,
                    () => new TypographyPage()
                ),
                new GalleryEntry(
                    "Colors",
                    "Named colors and the nine system accents",
                    MaterialIcons.ColorLens,
                    () => new ColorsPage()
                ),
                new GalleryEntry(
                    "Animations",
                    "Curves, transitions and implicit animation",
                    MaterialIcons.Animation,
                    () => new AnimationsPage()
                ),
            ]
        ),
        new(
            "Zigote",
            [
                new GalleryEntry(
                    "Reactivity",
                    "Signals, computed values and effects driving the UI",
                    MaterialIcons.Bolt,
                    () => new ReactivityPage()
                ),
                new GalleryEntry(
                    "Concurrency",
                    "Threads writing signals, frame-budgeted delivery and sliced work",
                    MaterialIcons.Speed,
                    () => new ConcurrencyPage()
                ),
                new GalleryEntry(
                    "Drag and Drop",
                    "Draggable payloads and drop targets",
                    MaterialIcons.OpenWith,
                    () => new DragDropPage()
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
                    s.Title,
                    [.. s.Entries.Select(e => new AdwSidebarItem(e.Title, e.IconName))]
                )
            ),
        ];
    }

    /// <summary>Index of a page by title, or -1 — the target of an in-page cross link.</summary>
    public static int IndexOf(string title)
    {
        return Array.FindIndex(Entries, e => e.Title == title);
    }
}