namespace AdwaitaGallery.Pages;

/// <summary>
///     Buttons — the Adwaita button in every style, shape and size it ships with, plus the split
///     button and the button row.
/// </summary>
public sealed class ButtonsPage : ComposedWidget
{
    private static readonly string[] SampleMenu = ["Save As…", "Duplicate", "Export"];

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            "Buttons",
            "One widget, five style classes and three shapes — the whole GNOME button vocabulary.",
            MaterialIcons.SmartButton
        ) {
            Children = {
                Demo.Titled(
                    "Style Classes",
                    "Regular, suggested and destructive carry weight; flat is for toolbars.",
                    Demo.Stage(
                        Demo.Bar(
                            new AdwButton("Regular", () => host.Toast("Regular")),
                            new AdwButton("Suggested", () => host.Toast("Suggested")) {
                                Style = AdwButtonStyle.Suggested,
                            },
                            new AdwButton("Destructive", () => host.Toast("Destructive")) {
                                Style = AdwButtonStyle.Destructive,
                            },
                            new AdwButton("Flat", () => host.Toast("Flat")) {
                                Style = AdwButtonStyle.Flat,
                            },
                            new AdwButton("Disabled") { Enabled = false }
                        )
                    )
                ),
                Demo.Titled(
                    "Shapes and Sizes",
                    "Pills for status pages and dialogs, circles for icons, compact for toolbars.",
                    Demo.Stage(
                        Demo.Bar(
                            new AdwButton("Pill") { Pill = true },
                            new AdwButton("Pill Suggested") {
                                Pill = true,
                                Style = AdwButtonStyle.Suggested,
                            },
                            new AdwButton("Compact") { Compact = true },
                            new AdwButton {
                                IconName = MaterialIcons.Add,
                                Circular = true,
                            },
                            new AdwButton {
                                IconName = MaterialIcons.Delete,
                                Circular = true,
                                Style = AdwButtonStyle.Destructive,
                            }
                        )
                    )
                ),
                Demo.Titled(
                    "Content",
                    "An icon and a label in one button — AdwButtonContent, as GNOME packs it.",
                    Demo.Stage(
                        Demo.Bar(
                            new AdwButton {
                                Content = new AdwButtonContent(MaterialIcons.FolderOpen, "Open"),
                            },
                            new AdwButton {
                                Content = new AdwButtonContent(MaterialIcons.Send, "Send"),
                                Style = AdwButtonStyle.Suggested,
                            },
                            new AdwLinkButton("A link button", () => host.Toast("Link activated"))
                        )
                    )
                ),
                Demo.Titled(
                    "Split Buttons",
                    "A default action next to the menu of everything else.",
                    Demo.Stage(
                        Demo.Bar(
                            new Tooltip(
                                "Open",
                                new AdwSplitButton {
                                    IconName = MaterialIcons.FolderOpen,
                                    MenuItems = SampleMenu,
                                    OnPressed = () => host.Toast("Open"),
                                    OnMenuSelected = i => host.Toast(SampleMenu[i]),
                                }
                            ),
                            new AdwSplitButton("Open") {
                                MenuItems = SampleMenu,
                                OnPressed = () => host.Toast("Open"),
                                OnMenuSelected = i => host.Toast(SampleMenu[i]),
                            },
                            new AdwSplitButton("Open") {
                                IconName = MaterialIcons.FolderOpen,
                                MenuItems = SampleMenu,
                                Style = AdwButtonStyle.Suggested,
                                OnPressed = () => host.Toast("Open"),
                                OnMenuSelected = i => host.Toast(SampleMenu[i]),
                            }
                        )
                    )
                ),
                Demo.Group(
                    "Button Rows",
                    "The full-width button inside a boxed list.",
                    new AdwButtonRow("Add Account", () => host.Toast("Account added")) {
                        IconName = MaterialIcons.Add,
                    },
                    new AdwButtonRow("Remove Account", () => host.Toast("Account removed")) {
                        IconName = MaterialIcons.Delete,
                        Destructive = true,
                    }
                ),
            },
        };
    }
}