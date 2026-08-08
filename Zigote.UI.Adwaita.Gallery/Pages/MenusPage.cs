namespace AdwaitaGallery.Pages;

/// <summary>
///     Menus &amp; Popovers — the menu button with its sections, headers, radios and checks, the
///     drop-down, and the combo row that wraps one for a boxed list.
/// </summary>
public sealed class MenusPage : StatelessWidget
{
    private static readonly string[] Zooms = ["50 %", "100 %", "200 %"];
    private static readonly string[] Fonts = ["Inter", "Iosevka", "Cantarell", "Source Sans"];

    private readonly Signal<int> _zoom = new(1);
    private readonly Signal<bool> _sidebar = new(true);
    private readonly Signal<int> _font = new(0);

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            "Menus & Popovers",
            "The GNOME menu: sections with headers, radio groups and checkable items, in a popover.",
            MaterialIcons.MoreVert
        ) {
            Children = {
                Demo.Titled(
                    "Menu Button",
                    "Sections are separated by hairlines; a header names one.",
                    Demo.Specimen(
                        new Watch(() => Menu(host)),
                        new Watch(() => Demo.Value(
                                $"zoom = {Zooms[_zoom.Value]} · sidebar = {(_sidebar.Value ? "on" : "off")}"
                            )
                        )
                    )
                ),
                Demo.Titled(
                    "Drop Down",
                    "For picking one of a list, when the list is the point.",
                    Demo.Specimen(
                        new AdwDropDown(Fonts, 0, i => _font.Value = i),
                        new Watch(() => Demo.Value($"font = {Fonts[_font.Value]}"))
                    )
                ),
                Demo.Titled(
                    "Split Button",
                    "A default action, with the rest of them one click to the right.",
                    Demo.Stage(
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
                    "Combo Rows",
                    "The same choice inside a boxed list — the row is the button.",
                    new AdwComboRow(
                        "Font",
                        Fonts,
                        0,
                        i => _font.Value = i
                    ),
                    new AdwComboRow(
                        "Zoom",
                        Zooms,
                        1,
                        i => _zoom.Value = i,
                        "Applies to new windows"
                    )
                ),
            },
        };
    }

    private Widget Menu(GalleryHost host)
    {
        return new AdwMenuButton(MaterialIcons.Menu) {
            MenuWidth = 240f,
            Sections = [
                [
                    new AdwMenuItem("New Window", host.App.NewWindow) { Accel = "Ctrl+N" },
                    new AdwMenuItem("Open…", () => host.Toast("Open")) { Accel = "Ctrl+O" },
                ],
                [
                    AdwMenuItem.Header("Zoom"),
                    AdwMenuItem.Radio(Zooms[0], _zoom.Value == 0, () => _zoom.Value = 0),
                    AdwMenuItem.Radio(Zooms[1], _zoom.Value == 1, () => _zoom.Value = 1),
                    AdwMenuItem.Radio(Zooms[2], _zoom.Value == 2, () => _zoom.Value = 2),
                ],
                [
                    new AdwMenuItem("Show Sidebar", () => _sidebar.Value = !_sidebar.Value) {
                        Role = AdwMenuItemRole.Check,
                        Checked = _sidebar.Value,
                        Accel = "F9",
                    },
                    new AdwMenuItem("Preferences", host.ShowPreferences) { Accel = "Ctrl+," },
                    new AdwMenuItem("Unavailable") { Enabled = false },
                ],
            ],
        };
    }
}