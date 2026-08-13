namespace AdwaitaGallery.Pages;

/// <summary>
///     Toggles — the linked toggle group (labels, icons, flat, round) and the latching button, with
///     the live selection echoed underneath.
/// </summary>
public sealed class TogglesPage : ComposedWidget
{
    private static readonly string[] Views = ["Grid", "List", "Columns"];
    private readonly Signal<string> _align = new("Left");
    private readonly Signal<bool> _bold = new(false);

    private readonly Signal<int> _view = new(0);

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Toggles",
            description: "Exactly one of a linked set, or a single button that stays pressed.",
            iconName: MaterialIcons.ToggleOn
        ) {
            Children = {
                Demo.Titled(
                    title: "Linked Group",
                    description: "One rounded trough, hairline-separated segments, one active.",
                    child: Demo.Specimen(
                        new AdwToggleGroup(
                            labels: Views,
                            active: 0,
                            onActive: i => _view.Value = i
                        ),
                        new Watch(() => Demo.Value($"view = {Views[_view.Value]}"))
                    )
                ),
                Demo.Titled(
                    title: "Icons",
                    description: "Icon-only segments carry a tooltip instead of a label.",
                    child: Demo.Specimen(
                        new AdwToggleGroup(
                            toggles: [
                                new AdwToggle(
                                    IconName: MaterialIcons.FormatAlignLeft,
                                    Tooltip: "Left"
                                ),
                                new AdwToggle(
                                    IconName: MaterialIcons.FormatAlignCenter,
                                    Tooltip: "Center"
                                ),
                                new AdwToggle(
                                    IconName: MaterialIcons.FormatAlignRight,
                                    Tooltip: "Right"
                                ),
                                new AdwToggle(
                                    IconName: MaterialIcons.FormatAlignJustify,
                                    Tooltip: "Justify"
                                ),
                            ],
                            active: 0,
                            onActive: i => _align.Value = new[] {
                                "Left",
                                "Center",
                                "Right",
                                "Justify",
                            }[i]
                        ),
                        new Watch(() => Demo.Value($"align = {_align.Value}"))
                    )
                ),
                Demo.Titled(
                    title: "Flat and Round",
                    description:
                    "Flat drops the trough for a toolbar; round makes the group a pill.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new AdwToggleGroup(
                                [
                                    new AdwToggle(
                                        IconName: MaterialIcons.FormatBold,
                                        Tooltip: "Bold"
                                    ),
                                    new AdwToggle(
                                        IconName: MaterialIcons.FormatItalic,
                                        Tooltip: "Italic"
                                    ),
                                    new AdwToggle(
                                        IconName: MaterialIcons.FormatUnderlined,
                                        Tooltip: "Underline"
                                    ),
                                ]
                            ) { Flat = true },
                            new AdwToggleGroup(
                                [
                                    new AdwToggle(
                                        IconName: MaterialIcons.PhotoCamera,
                                        Tooltip: "Photo"
                                    ),
                                    new AdwToggle(
                                        IconName: MaterialIcons.Videocam,
                                        Tooltip: "Video"
                                    ),
                                ]
                            ) { Round = true }
                        )
                    )
                ),
                Demo.Titled(
                    title: "Toggle Button",
                    description: "A single button that latches — suggested while active.",
                    child: Demo.Specimen(
                        new AdwToggleButton(
                            label: "Bold",
                            active: false,
                            onToggled: v => _bold.Value = v
                        ) {
                            Style = AdwButtonStyle.Suggested,
                        },
                        new Watch(() => Demo.Value($"bold = {_bold.Value}"))
                    )
                ),
                Demo.Group(
                    title: "In Rows",
                    description: "The pattern GNOME uses when a setting has two or three choices.",
                    new AdwActionRow("Clock Format") {
                        Suffixes = { new AdwToggleGroup(["24 h", "AM / PM"]) },
                    },
                    new AdwActionRow("Sidebar") {
                        Suffixes = {
                            new AdwToggleGroup(
                                [
                                    new AdwToggle(
                                        IconName: MaterialIcons.ViewList,
                                        Tooltip: "List"
                                    ),
                                    new AdwToggle(
                                        IconName: MaterialIcons.GridView,
                                        Tooltip: "Grid"
                                    ),
                                ]
                            ),
                        },
                    }
                ),
            },
        };
    }
}
