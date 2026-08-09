namespace AdwaitaGallery;

/// <summary>
///     One gallery window: a toast host over an adaptive navigation split view. The sidebar carries
///     the searchable table of contents under its own header bar; the content pane carries the page
///     under a header that names it — and grows a back button only once the window is narrow enough
///     for the two panes to become one.
/// </summary>
internal sealed class Shell : ComposedWidget
{
    // The width at which the panes fold into a single navigable page (the demo's phone breakpoint).
    private const float CollapseWidth = 620f;

    private readonly GalleryApp _app;
    private readonly Signal<int> _selected = new(0);
    private readonly Signal<bool> _searchOn = new(false);

    // Pages are built once and kept: leaving a page and coming back should find the animation
    // transport, the carousel position and the toast counter where they were left. The pages
    // re-create their tickers and effects in Attach, so a cached one re-animates on its return.
    private readonly Widget?[] _pages = new Widget?[GalleryRegistry.Entries.Length];

    // Retained (house pattern: hoist stateful widgets out of Watch) so the search text survives the
    // header rebuilds and the page cross-fade survives a selection change.
    private readonly AdwSearchEntry _searchEntry = new() { Placeholder = "Search pages" };
    private readonly AdwSidebar _sidebar = new(GalleryRegistry.SidebarSections());

    private readonly AdwNavigationSplitView _split = new() {
        AutoCollapseBelow = CollapseWidth,
        // Wider than the Adwaita 260: this sidebar's header also carries the window controls.
        SidebarWidth = 280f,
    };

    private readonly AdwToastOverlay _toasts = new(SizedBox.Shrink());
    private readonly AnimatedSwitcher _switcher;

    // The search bar slides open rather than appearing: AnimatedSize eases the top bar's height
    // between nothing and the entry, clipping the entry while it grows (GtkSearchBar's reveal).
    private readonly AnimatedSize _searchReveal = new(SizedBox.Shrink(), 0.16f);

    public Shell(GalleryApp app)
    {
        _app = app;
        _switcher = new AnimatedSwitcher(Page(0), 0.18f);

        // Filter is signal-backed inside AdwSidebar: a keystroke re-filters the rows in place.
        _searchEntry.OnChanged = query => _sidebar.Filter = query;
        _sidebar.Placeholder = new AdwStatusPage {
            IconName = MaterialIcons.SearchOff,
            Title = "No Results Found",
            Description = "Try a different search",
            Compact = true,
        };
        _sidebar.OnSelected = index =>
        {
            _selected.Value = index;
            _split.ShowContent = true; // no-op while side by side
        };
        _selected.Changed += index => _switcher.Child = Page(index);
        _searchOn.Changed += on => _searchReveal.Child = on ? SearchField() : SizedBox.Shrink();
    }

    private GalleryEntry Current => GalleryRegistry.Entries[_selected.Value];

    private Widget Page(int index)
    {
        return _pages[index] ??= GalleryRegistry.Entries[index].Build();
    }

    protected override Widget Build(BuildContext context)
    {
        _split.Sidebar = new AdwToolbarView(_sidebar) {
            TopBars = {
                new Watch(SidebarHeader),
                _searchReveal,
            },
        };
        _split.Content = new AdwToolbarView(_switcher) {
            TopBars = { new Watch(ContentHeader) },
        };
        _toasts.Child = _split;
        return new GalleryHost(_app, this, _toasts);
    }

    // ── Services the pages, the menu and the shortcuts use ────────────────────

    public void Toast(AdwToast toast)
    {
        _toasts.AddToast(toast);
    }

    /// <summary>
    ///     Reveal the search bar and put the caret in it (Ctrl+F). Pressing it again while the bar
    ///     is open just re-focuses the entry, as GNOME's search does.
    /// </summary>
    public void FocusSearch()
    {
        // Setting the signal mounts the entry synchronously (the reveal's Child setter attaches
        // it), so the caret can go in on this frame; the Post is the belt for the case where the
        // shell itself has not been laid out yet.
        _searchOn.Value = true;
        _searchEntry.Focus();
        Owner?.Post(_searchEntry.Focus);
    }

    /// <summary>Open a page by title, from a cross link or the welcome page.</summary>
    public void Open(string pageTitle)
    {
        var index = GalleryRegistry.IndexOf(pageTitle);
        if (index < 0) return;
        _sidebar.Selected = index; // fires OnSelected: page swap + collapsed push
    }

    public void ShowPreferences()
    {
        GalleryPreferences.Show(_app, title => Toast(new AdwToast(title)));
    }

    // ── Chrome ────────────────────────────────────────────────────────────────

    private Widget SidebarHeader()
    {
        // No subtitle here: with the window controls, the search toggle and the menu on the same
        // 280 px row, the title is already down to what fits between them.
        var bar = new AdwHeaderBar {
            Title = "Adwaita Demo",
            ShowEndWindowControls = false,
        };
        bar.Start.Add(
            new Tooltip(
                "Search (Ctrl+F)",
                Demo.IconButton(MaterialIcons.Search, () => SetSearch(!_searchOn.Value))
            )
        );
        bar.End.Add(GalleryMenu.Build(_app, this));
        return bar;
    }

    /// <summary>The GtkSearchBar's content — revealed by <see cref="_searchReveal" />.</summary>
    private Widget SearchField()
    {
        return new Padding(
            EdgeInsets.Only(
                Spacing.Sm,
                0f,
                Spacing.Sm,
                Spacing.Sm
            ),
            _searchEntry
        );
    }

    private Widget ContentHeader()
    {
        var entry = Current;
        var bar = new AdwHeaderBar {
            TitleWidget = new AdwWindowTitle(entry.Title, entry.Subtitle),
            ShowStartWindowControls = false,
            // Only once the panes have folded is there anywhere to go back to. Read off the split
            // view's own fold state — it computed the breakpoint already, this Watch just follows.
            ShowBackButton = _split.IsCollapsed.Value,
            OnBack = () => _split.ShowContent = false,
        };
        return bar;
    }

    private void SetSearch(bool on)
    {
        _searchOn.Value = on;
        if (on)
        {
            _searchEntry.Focus();
            Owner?.Post(_searchEntry.Focus);
            return;
        }

        _searchEntry.Text = string.Empty;
        _sidebar.Filter = string.Empty;
        Owner?.ClearFocus();
    }
}