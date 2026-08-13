namespace AdwaitaGallery.Pages;

/// <summary>
///     Menus &amp; Popovers — the menu button with its sections, headers, radios and checks, the
///     drop-down, and the combo row that wraps one for a boxed list.
/// </summary>
public sealed class MenusPage : ComposedWidget
{
    private static readonly string[] Zooms = ["50 %", "100 %", "200 %"];
    private static readonly string[] Fonts = ["Inter", "Iosevka", "Cantarell", "Source Sans"];
    private readonly Signal<int> _font = new(0);
    private readonly Signal<bool> _sidebar = new(true);

    private readonly Signal<int> _zoom = new(1);

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            title: "Menus & Popovers",
            description:
            "The GNOME menu: sections with headers, radio groups and checkable items, in a popover.",
            iconName: MaterialIcons.MoreVert
        ) {
            Children = {
                Demo.Titled(
                    title: "Menu Button",
                    description: "Sections are separated by hairlines; a header names one.",
                    child: Demo.Specimen(
                        new Watch(() => Menu(host)),
                        new Watch(() => Demo.Value(
                                $"zoom = {Zooms[_zoom.Value]} · sidebar = {(_sidebar.Value ? "on" : "off")}"
                            )
                        )
                    )
                ),
                Demo.Titled(
                    title: "Drop Down",
                    description: "For picking one of a list, when the list is the point.",
                    child: Demo.Specimen(
                        new AdwDropDown(
                            items: Fonts,
                            selectedIndex: 0,
                            onSelected: i => _font.Value = i
                        ),
                        new Watch(() => Demo.Value($"font = {Fonts[_font.Value]}"))
                    )
                ),
                Demo.Titled(
                    title: "Split Button",
                    description: "A default action, with the rest of them one click to the right.",
                    child: Demo.Stage(
                        new AdwSplitButton("Export") {
                            IconName = MaterialIcons.FileDownload,
                            MenuItems = ["Export as PNG", "Export as SVG", "Export as PDF"],
                            Style = AdwButtonStyle.Suggested,
                            OnPressed = () => host.Toast("Exported"),
                            OnMenuSelected = i => host.Toast($"Export option {i + 1}"),
                        }
                    )
                ),
                Demo.Group(
                    title: "Combo Rows",
                    description: "The same choice inside a boxed list — the row is the button.",
                    new AdwComboRow(
                        title: "Font",
                        items: Fonts,
                        selectedIndex: 0,
                        onSelected: i => _font.Value = i
                    ),
                    new AdwComboRow(
                        title: "Zoom",
                        items: Zooms,
                        selectedIndex: 1,
                        onSelected: i => _zoom.Value = i,
                        subtitle: "Applies to new windows"
                    )
                ),
            },
        };
    }

    private Widget Menu(GalleryHost host)
    {
        return new AdwMenuButton() {
            MenuWidth = 240f,
            Sections = [
                [
                    new AdwMenuItem(label: "New Window", onActivated: host.App.NewWindow) {
                        Accel = "Ctrl+N",
                    },
                    new AdwMenuItem(label: "Open…", onActivated: () => host.Toast("Open")) {
                        Accel = "Ctrl+O",
                    },
                ],
                [
                    AdwMenuItem.Header("Zoom"),
                    AdwMenuItem.Radio(
                        label: Zooms[0],
                        selected: _zoom.Value == 0,
                        onActivated: () => _zoom.Value = 0
                    ),
                    AdwMenuItem.Radio(
                        label: Zooms[1],
                        selected: _zoom.Value == 1,
                        onActivated: () => _zoom.Value = 1
                    ),
                    AdwMenuItem.Radio(
                        label: Zooms[2],
                        selected: _zoom.Value == 2,
                        onActivated: () => _zoom.Value = 2
                    ),
                ],
                [
                    new AdwMenuItem(
                        label: "Show Sidebar",
                        onActivated: () => _sidebar.Value = !_sidebar.Value
                    ) {
                        Role = AdwMenuItemRole.Check,
                        Checked = _sidebar.Value,
                        Accel = "F9",
                    },
                    new AdwMenuItem(label: "Preferences", onActivated: host.ShowPreferences) {
                        Accel = "Ctrl+,",
                    },
                    new AdwMenuItem("Unavailable") { Enabled = false },
                ],
            ],
        };
    }
}
