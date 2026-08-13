namespace AdwaitaGallery.Pages;

/// <summary>
///     Checks &amp; Radios — check buttons, an exclusive radio group and the switch, each shown as a
///     bare control and in the row it usually lives in.
/// </summary>
public sealed class ChecksPage : ComposedWidget
{
    private static readonly string[] Qualities = ["Draft", "Standard", "High"];
    private readonly Signal<int> _checked = new(2);

    private readonly Signal<int> _quality = new(1);
    private readonly Signal<bool> _wifi = new(true);

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Checks & Radios",
            description:
            "Independent choices, exclusive choices, and the switch for things that take effect at once.",
            iconName: MaterialIcons.CheckBox
        ) {
            Children = {
                Demo.Titled(
                    title: "Check Buttons",
                    description: "Independent — any number of them can be on.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Sm,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Start
                        ) {
                            Children = {
                                new AdwCheckButton(
                                    label: "Bold",
                                    value: true,
                                    onChanged: v => Count(v)
                                ),
                                new AdwCheckButton(
                                    label: "Italic",
                                    value: true,
                                    onChanged: v => Count(v)
                                ),
                                new AdwCheckButton(
                                    label: "Underline",
                                    value: false,
                                    onChanged: v => Count(v)
                                ),
                                new AdwCheckButton("Strikethrough") { Enabled = false },
                                new Watch(() => Demo.Value($"{_checked.Value} of 3 checked")),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "Radio Buttons",
                    description: "Exclusive — the group agrees on exactly one.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Sm,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Start
                        ) {
                            Children = {
                                Radios(),
                                new Watch(() => Demo.Value($"quality = {Qualities[_quality.Value]}")
                                ),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "Switches",
                    description: "For a setting that applies the moment it moves.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new AdwSwitch(value: true, onChanged: v => _wifi.Value = v),
                            new AdwSwitch(),
                            new AdwSwitch(true) { Enabled = false },
                            new Watch(() => Demo.Value($"wi-fi = {(_wifi.Value ? "on" : "off")}"))
                        )
                    )
                ),
                Demo.Group(
                    title: "In Rows",
                    description:
                    "The same controls where they normally live — inside a boxed list.",
                    new AdwSwitchRow(
                        title: "Wi-Fi",
                        subtitle: "Connect automatically to known networks",
                        value: true
                    ),
                    new AdwSwitchRow(title: "Bluetooth", value: false),
                    new AdwActionRow("Sync over cellular") {
                        Suffixes = { new AdwCheckButton() },
                    }
                ),
            },
        };
    }

    /// <summary>
    ///     One exclusive group: the radios share a signal, and each is rebuilt from it — a Watch
    ///     around the set is all the "group" a signal-driven tree needs.
    /// </summary>
    private Widget Radios()
    {
        return new Watch(() =>
            {
                var column = new Column(
                    spacing: Spacing.Sm,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Start
                );
                for (int i = 0; i < Qualities.Length; i++)
                {
                    int index = i;
                    column.Children.Add(
                        new AdwRadioButton(
                            label: Qualities[i],
                            value: _quality.Value == i,
                            onChanged: on =>
                            {
                                if (on) _quality.Value = index;
                            }
                        )
                    );
                }

                return column;
            }
        );
    }

    private void Count(bool on) => _checked.Value = Math.Clamp(
        value: _checked.Value + (on ? 1 : -1),
        min: 0,
        max: 3
    );
}
