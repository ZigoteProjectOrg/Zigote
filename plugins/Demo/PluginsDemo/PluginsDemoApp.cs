using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace PluginsDemo;

/// <summary>
///     The demo window: an Adwaita navigation split view with one sidebar row per plugin page.
///     Every page calls the plugin exactly the way an app would and shows what came back, so a
///     platform that cannot do the thing says so on screen instead of in a comment.
/// </summary>
public sealed class PluginsDemoApp : AdwaitaApp
{
    public PluginsDemoApp() : base(home: new SafeArea(new DemoShell()), title: "Zigote Plugins")
    {
        Width = 900;
        Height = 680;
    }
}

/// <summary>One page per entry, built on demand and kept — a page's state survives leaving it.</summary>
internal sealed class DemoShell : ComposedWidget
{
    private static readonly (string Title, string Icon, Func<Widget> Build)[] Pages =
    [
        ("Device", MaterialIcons.Devices, () => new DevicePage()),
        ("Battery", MaterialIcons.BatteryFull, () => new BatteryPage()),
        ("Network", MaterialIcons.Wifi, () => new NetworkPage()),
        ("Secrets", MaterialIcons.Lock, () => new SecretsPage()),
        ("Share", MaterialIcons.Share, () => new SharePage()),
        ("Files", MaterialIcons.Folder, () => new FilesPage()),
        ("Links", MaterialIcons.Link, () => new LinksPage()),
        ("Notifications", MaterialIcons.Notifications, () => new NotificationsPage()),
        ("Location", MaterialIcons.LocationOn, () => new LocationPage()),
        ("Mobile", MaterialIcons.PhoneAndroid, () => new MobilePage()),
    ];

    private readonly Widget?[] _built = new Widget?[Pages.Length];
    private readonly Signal<int> _selected = new(0);
    private readonly AdwSidebar _sidebar;

    private readonly AdwNavigationSplitView _split = new()
    {
        AutoCollapseBelow = 620f,
        SidebarWidth = 240f,
    };

    private readonly AnimatedSwitcher _switcher;

    public DemoShell()
    {
        _sidebar = new AdwSidebar(new AdwSidebarSection(
            "Plugins",
            [.. Pages.Select(p => new AdwSidebarItem(p.Title, p.Icon))]));
        _switcher = new AnimatedSwitcher(child: Page(0), duration: 0.18f);
        _sidebar.OnSelected = index =>
        {
            _selected.Value = index;
            _split.ShowContent = true;   // no-op while the panes are side by side
        };
        _selected.Changed += index => _switcher.Child = Page(index);
    }

    private Widget Page(int index) => _built[index] ??= Pages[index].Build();

    protected override Widget Build(BuildContext context)
    {
        _split.Sidebar = new AdwToolbarView(_sidebar)
        {
            TopBars = { new AdwHeaderBar { Title = "Zigote Plugins" } },
        };
        _split.Content = new AdwToolbarView(_switcher)
        {
            TopBars =
            {
                new Watch(() => new AdwHeaderBar
                {
                    Title = Pages[_selected.Value].Title,
                    OnBack = () => _split.ShowContent = false,
                }),
            },
        };
        return _split;
    }
}
