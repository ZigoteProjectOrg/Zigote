using Playground.Catalog;

namespace Playground;

/// <summary>
///     The window: a searchable sidebar beside the page, folding into one navigable pane on narrow
///     windows. Two modes share it — <b>Docs</b> (the hand-written guides) and <b>Catalog</b> (the
///     generated widget reference) — each with its own sidebar and page cache. Links inside pages
///     (<c>type:Full.Name</c>, <c>doc:Page Title</c>) navigate like a browser, with a Back history.
///     Pages are built once and kept, so edited examples survive navigation and mode switches.
/// </summary>
internal sealed class Shell : ComposedWidget
{
    /// <summary>Where the window is: the mode, each mode's sidebar selection, and an open type page.</summary>
    private sealed record Location(bool Catalog, int Doc, int Widget, Type? TypePage, SharedExample? Shared = null);

    /// <summary>A share link to open at startup (a command-line argument), before anything is built.</summary>
    public static SharedExample? StartWith { get; set; }

    private readonly Stack<Location> _back = new();
    private readonly Signal<Location> _location = new(new Location(false, 0, 0, null));

    // Docs
    private readonly Widget?[] _docPages = new Widget?[Toc.Entries.Length];
    private readonly AdwSidebar _docSidebar = new(Toc.SidebarSections());

    // Catalog: sidebar index 0 is the overview, then one page per widget in sidebar order.
    private readonly Widget?[] _catalogPages = new Widget?[WidgetCatalog.Entries.Length + 1];
    private readonly AdwSidebar _catalogSidebar = new(CatalogSidebarSections());
    private readonly Dictionary<Type, Widget> _typePages = [];
    private readonly Dictionary<SharedExample, Widget> _sharedPages = [];

    private readonly AdwToggleGroup _mode;
    private readonly AdwSearchEntry _search = new() { Placeholder = "Search the docs or paste a link" };
    private readonly AnimatedSwitcher _sidebarSwitcher;
    private readonly AdwNavigationSplitView _split = new() { AutoCollapseBelow = 720f, SidebarWidth = 260f };
    private readonly AnimatedSwitcher _switcher;
    private bool _syncing; // sidebar/toggle writes made by Apply, not by the user

    public Shell()
    {
        // Automation hooks: open a docs page or a catalog widget by title at startup
        // (ZIGOTE_PLAYGROUND_PAGE="Layout", ZIGOTE_PLAYGROUND_WIDGET="AdwButton"). Resolved before
        // anything is built, so the first page is simply the right one — no swap before mount.
        var start = _location.Peek();
        if (Environment.GetEnvironmentVariable("ZIGOTE_PLAYGROUND_PAGE") is { Length: > 0 } title &&
            Array.FindIndex(Toc.Entries, e => e.Title == title) is var doc and >= 0)
            start = start with { Doc = doc };
        if (Environment.GetEnvironmentVariable("ZIGOTE_PLAYGROUND_WIDGET") is { Length: > 0 } widget &&
            Array.FindIndex(WidgetCatalog.Entries, e => e.Title == widget) is var w and >= 0)
            start = start with { Catalog = true, Widget = w + 1 };
        if (StartWith is { } shared) start = start with { Shared = shared };
        _location.Value = start;

        _switcher = new AnimatedSwitcher(child: PageFor(start), duration: 0.15f);
        _sidebarSwitcher = new AnimatedSwitcher(child: start.Catalog ? _catalogSidebar : _docSidebar, duration: 0.12f);
        _docSidebar.Selected = start.Doc;
        _catalogSidebar.Selected = start.Widget;
        _mode = new AdwToggleGroup(
            labels: ["Docs", "Catalog"],
            onActive: i =>
            {
                if (!_syncing) Navigate(_location.Peek() with { Catalog = i == 1, TypePage = null, Shared = null });
            }
        );

        _search.OnChanged = query =>
        {
            // A share link pasted into search opens its example instead of filtering.
            if (query.StartsWith(ShareLink.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                if (ShareLink.Parse(query) is { } link) Navigate(_location.Peek() with { Shared = link, TypePage = null });
                _search.Text = "";
                return;
            }

            if (_location.Peek().Catalog) _catalogSidebar.Filter = query;
            else _docSidebar.Filter = query;
        };
        _docSidebar.OnSelected = index =>
        {
            if (!_syncing) Navigate(_location.Peek() with { Catalog = false, Doc = index, TypePage = null, Shared = null });
            _split.ShowContent = true;
        };
        _catalogSidebar.OnSelected = index =>
        {
            if (!_syncing) Navigate(_location.Peek() with { Catalog = true, Widget = index, TypePage = null, Shared = null });
            _split.ShowContent = true;
        };
        _mode.Active = start.Catalog ? 1 : 0;
        _search.Placeholder = start.Catalog ? $"Search {WidgetCatalog.Entries.Length} widgets" : "Search the docs or paste a link";
        _location.Changed += Apply;
    }

    private static AdwSidebarSection[] CatalogSidebarSections() => [
        new AdwSidebarSection(title: null, new AdwSidebarItem(title: "Overview", iconName: MaterialIcons.ViewModule)),
        .. WidgetCatalog.Sections.Select(s => new AdwSidebarSection(
            title: s.Title,
            items: [.. s.Entries.Select(e => new AdwSidebarItem(title: e.Title, iconName: MaterialIcons.Widgets))]
        )),
    ];

    // ── Navigation ────────────────────────────────────────────────────────────

    private void Navigate(Location next)
    {
        var current = _location.Peek();
        if (next == current) return;
        _back.Push(current);
        _location.Value = next;
    }

    private void Back()
    {
        if (_back.Count > 0) _location.Value = _back.Pop();
    }

    /// <summary>A page's link: <c>type:Full.Name</c> opens a widget page or a type page; <c>doc:Title</c> a guide.</summary>
    private void OnLink(string target)
    {
        var here = _location.Peek();
        if (target.StartsWith("type:", StringComparison.Ordinal) && TypeIndex.Find(target[5..]) is { } type)
        {
            int widget = WidgetCatalog.IndexOf(type);
            Navigate(widget >= 0
                ? here with { Catalog = true, Widget = widget + 1, TypePage = null, Shared = null }
                : here with { Catalog = true, TypePage = type, Shared = null });
        }
        else if (target.StartsWith("doc:", StringComparison.Ordinal) &&
                 Array.FindIndex(Toc.Entries, e => e.Title == target[4..]) is var doc and >= 0)
            Navigate(here with { Catalog = false, Doc = doc, TypePage = null, Shared = null });
    }

    // Bring the sidebars, the mode toggle and the page in line with the location.
    private void Apply(Location loc)
    {
        _syncing = true;
        try
        {
            _mode.Active = loc.Catalog ? 1 : 0;
            _sidebarSwitcher.Child = loc.Catalog ? _catalogSidebar : _docSidebar;
            _search.Placeholder = loc.Catalog ? $"Search {WidgetCatalog.Entries.Length} widgets" : "Search the docs or paste a link";
            if (loc.Catalog) _catalogSidebar.Selected = loc.Widget;
            else _docSidebar.Selected = loc.Doc;
        }
        finally
        {
            _syncing = false;
        }

        _switcher.Child = PageFor(loc);
        _split.ShowContent = true;
    }

    private Widget PageFor(Location loc)
    {
        if (loc.Shared is { } shared)
        {
            if (!_sharedPages.TryGetValue(shared, out var page))
                _sharedPages[shared] = page = SharedPage(shared);
            return page;
        }

        if (loc.TypePage is { } t)
        {
            if (!_typePages.TryGetValue(t, out var page))
                _typePages[t] = page = new DocPage(WidgetCatalog.TypePageMarkdown(t)) { OnLink = OnLink, Origin = $"type:{t.FullName}" };
            return page;
        }

        if (loc.Catalog)
        {
            return _catalogPages[loc.Widget] ??= new DocPage(loc.Widget == 0
                ? WidgetCatalog.IndexMarkdown()
                : WidgetCatalog.PageMarkdown(WidgetCatalog.Entries[loc.Widget - 1])) {
                OnLink = OnLink,
                Origin = loc.Widget == 0 ? null : $"type:{WidgetCatalog.Entries[loc.Widget - 1].Type.FullName}",
            };
        }

        return _docPages[loc.Doc] ??= new DocPage(Toc.Read(Toc.Entries[loc.Doc].File) + Footer(loc.Doc)) {
            OnLink = OnLink,
            Origin = $"doc:{Toc.Entries[loc.Doc].Title}",
        };
    }

    // A link's example on a page of its own, held until the reader chooses to run it.
    private DocPage SharedPage(SharedExample shared)
    {
        string origin = shared.From switch {
            ['d', 'o', 'c', ':', .. var title] when Toc.Entries.Any(e => e.Title == title) => $" It was shared from [{title}]({shared.From}).",
            ['t', 'y', 'p', 'e', ':', .. var name] when TypeIndex.Find(name) is { } t => $" It was shared from [{WidgetCatalog.Name(t)}]({shared.From}).",
            _ => "",
        };
        var blocks = Markdown.Parse(
            $"""
             # Shared example

             Someone sent you this example as a link.{origin}

             > **Read the code before you run it.** An example is ordinary C#: it runs with your permissions and can do anything this app can. Nothing runs until you press **Run**.
             """);
        // Built as a block, not a ``` fence: the code may contain anything, fences included.
        blocks.Add(new Block.Code(shared.Source, Run: true, Height: shared.Height));
        return new DocPage(blocks) { OnLink = OnLink, Origin = shared.From, Untrusted = true };
    }

    // "← Previous · Next →" under each guide, so the docs read front to back like a book.
    private static string Footer(int doc)
    {
        var links = new List<string>();
        if (doc > 0) links.Add($"[← {Toc.Entries[doc - 1].Title}](doc:{Toc.Entries[doc - 1].Title})");
        if (doc < Toc.Entries.Length - 1) links.Add($"[{Toc.Entries[doc + 1].Title} →](doc:{Toc.Entries[doc + 1].Title})");
        return "\n\n" + string.Join("   ·   ", links) + "\n";
    }

    // ── Chrome ────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        _split.Sidebar = new AdwToolbarView(_sidebarSwitcher) {
            TopBars = {
                new Watch(() => new AdwHeaderBar {
                        Title = "Zigote Docs",
                        ShowEndWindowControls = _split.IsCollapsed.Value,
                    }
                ),
                new Padding(
                    EdgeInsets.Only(left: Spacing.Sm, right: Spacing.Sm, bottom: Spacing.Sm),
                    new Column(spacing: Spacing.Sm, crossAxisAlignment: CrossAxisAlignment.Stretch, mainAxisSize: MainAxisSize.Min, children: [_mode, _search])
                ),
            },
        };
        _split.Content = new AdwToolbarView(_switcher) {
            TopBars = { new Watch(ContentHeader) },
        };
        return _split;
    }

    private Widget ContentHeader()
    {
        var loc = _location.Value;
        string title, subtitle;
        if (loc.Shared is not null)
        {
            title = "Shared example";
            subtitle = "From a link — not run until you press Run";
        }
        else if (loc.TypePage is { } t)
        {
            title = WidgetCatalog.Name(t);
            subtitle = $"Type · {t.Namespace}";
        }
        else if (loc.Catalog)
        {
            var entry = loc.Widget == 0 ? null : WidgetCatalog.Entries[loc.Widget - 1];
            title = entry?.Title ?? "Widget Catalog";
            subtitle = entry is null ? $"{WidgetCatalog.Entries.Length} widgets, generated from the code" : $"{entry.Library} · {entry.Category}";
        }
        else
        {
            var entry = Toc.Entries[loc.Doc];
            title = entry.Title;
            subtitle = Toc.Sections.First(s => s.Entries.Contains(entry)).Title;
        }

        bool collapsed = _split.IsCollapsed.Value;
        return new AdwHeaderBar {
            TitleWidget = new AdwWindowTitle(title: title, subtitle: subtitle),
            ShowStartWindowControls = collapsed,
            // Back walks the link history; folded with nothing behind, it returns to the sidebar.
            ShowBackButton = _back.Count > 0 || collapsed,
            OnBack = () =>
            {
                if (_back.Count > 0) Back();
                else _split.ShowContent = false;
            },
        };
    }
}
