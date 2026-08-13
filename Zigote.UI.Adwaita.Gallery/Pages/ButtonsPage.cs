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
            title: "Buttons",
            description:
            "One widget, five style classes and three shapes — the whole GNOME button vocabulary.",
            iconName: MaterialIcons.SmartButton
        ) {
            Children = {
                Demo.Titled(
                    title: "Style Classes",
                    description:
                    "Regular, suggested and destructive carry weight; flat is for toolbars.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new AdwButton(label: "Regular", onPressed: () => host.Toast("Regular")),
                            new AdwButton(
                                label: "Suggested",
                                onPressed: () => host.Toast("Suggested")
                            ) {
                                Style = AdwButtonStyle.Suggested,
                            },
                            new AdwButton(
                                label: "Destructive",
                                onPressed: () => host.Toast("Destructive")
                            ) {
                                Style = AdwButtonStyle.Destructive,
                            },
                            new AdwButton(label: "Flat", onPressed: () => host.Toast("Flat")) {
                                Style = AdwButtonStyle.Flat,
                            },
                            new AdwButton("Disabled") { Enabled = false }
                        )
                    )
                ),
                Demo.Titled(
                    title: "Shapes and Sizes",
                    description:
                    "Pills for status pages and dialogs, circles for icons, compact for toolbars.",
                    child: Demo.Stage(
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
                    title: "Content",
                    description:
                    "An icon and a label in one button — AdwButtonContent, as GNOME packs it.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new AdwButton {
                                Content = new AdwButtonContent(
                                    iconName: MaterialIcons.FolderOpen,
                                    label: "Open"
                                ),
                            },
                            new AdwButton {
                                Content = new AdwButtonContent(
                                    iconName: MaterialIcons.Send,
                                    label: "Send"
                                ),
                                Style = AdwButtonStyle.Suggested,
                            },
                            new AdwLinkButton(
                                label: "A link button",
                                onPressed: () => host.Toast("Link activated")
                            )
                        )
                    )
                ),
                Demo.Titled(
                    title: "Split Buttons",
                    description: "A default action next to the menu of everything else.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new Tooltip(
                                message: "Open",
                                child: new AdwSplitButton {
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
                    title: "Button Rows",
                    description: "The full-width button inside a boxed list.",
                    new AdwButtonRow(
                        title: "Add Account",
                        onPressed: () => host.Toast("Account added")
                    ) {
                        IconName = MaterialIcons.Add,
                    },
                    new AdwButtonRow(
                        title: "Remove Account",
                        onPressed: () => host.Toast("Account removed")
                    ) {
                        IconName = MaterialIcons.Delete,
                        Destructive = true,
                    }
                ),
            },
        };
    }
}
