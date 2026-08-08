namespace AdwaitaGallery.Pages;

/// <summary>Boxed Lists — every row type libadwaita offers, in the order of the GNOME demo.</summary>
public sealed class ListsPage : StatelessWidget
{
    /// <summary>The GtkLicense enum nicks the demo feeds to its enumeration combo row.</summary>
    private static readonly string[] Licenses = [
        "unknown", "custom", "gpl-2-0", "gpl-3-0", "lgpl-2-1", "lgpl-3-0", "bsd", "mit-x11",
        "artistic", "gpl-2-0-only", "gpl-3-0-only", "lgpl-2-1-only", "lgpl-3-0-only", "agpl-3-0",
        "agpl-3-0-only", "bsd-3", "apache-2-0", "mpl-2-0", "0bsd",
    ];

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var host = GalleryHost.Of(context);

        // ponytail: no "separate-rows" group style — the Entry Rows and Button Rows groups render as
        // one card here; a full version needs a per-row card in AdwPreferencesGroup.
        return new GalleryPage(
            "Boxed Lists",
            "Every row type libadwaita offers, in the card that groups them.",
            MaterialIcons.ViewList
        ) {
            Children = {
                TitleAndSuffixGroup(),
                PrefixGroup(),
                EntryRowsGroup(host),
                new AdwPreferencesGroup("Spin Rows") {
                    Rows = {
                        new AdwSpinRow(
                            "Spin Row",
                            value: 50,
                            min: 0,
                            max: 100,
                            step: 1
                        ),
                    },
                },
                new AdwPreferencesGroup("Switch Rows") {
                    Rows = { new AdwSwitchRow("Switch Row") },
                },
                ComboRowsGroup(),
                ExpanderRowsGroup(host),
                PropertyRowsGroup(theme),
                SuffixGroup(),
                ButtonRowsGroup(),
            },
        };
    }

    private static Widget TitleAndSuffixGroup()
    {
        return new AdwPreferencesGroup {
            Rows = {
                new AdwActionRow("Rows Have a Title", "They also have a subtitle"),
                new AdwActionRow("Rows Can Have Suffix Widgets") {
                    Suffixes = { new AdwButton("Action", () => { }) },
                },
            },
        };
    }

    private static Widget PrefixGroup()
    {
        // Radios group by convention in Zigote: each one clears its sibling on selection.
        AdwRadioButton first = null!, second = null!;
        first = new AdwRadioButton(value: true, onChanged: _ => second.Value = false);
        second = new AdwRadioButton(onChanged: _ => first.Value = false);

        return new AdwPreferencesGroup {
            Rows = {
                new AdwActionRow("Rows Can Have Prefix Widgets") {
                    Prefix = first,
                    OnActivated = () =>
                    {
                        first.Value = true;
                        second.Value = false;
                    },
                },
                new AdwActionRow("Rows Can Have Prefix Widgets") {
                    Prefix = second,
                    OnActivated = () =>
                    {
                        second.Value = true;
                        first.Value = false;
                    },
                },
            },
        };
    }

    private static Widget EntryRowsGroup(GalleryHost host)
    {
        return new AdwPreferencesGroup("Entry Rows") {
            Rows = {
                new AdwEntryRow("Entry Row"),
                new AdwEntryRow("Entry With Confirmation") {
                    Suffix = Demo.IconButton(
                        MaterialIcons.Check,
                        () => host.Toast("Changes applied")
                    ),
                },
                new AdwEntryRow("Entry With Suffix") {
                    Suffix = new Tooltip(
                        "Copy",
                        Demo.IconButton(
                            MaterialIcons.ContentCopy,
                            () => host.Toast("Copied to clipboard")
                        )
                    ),
                },
                new AdwPasswordEntryRow("Password Entry"),
            },
        };
    }

    private static Widget ComboRowsGroup()
    {
        // ponytail: no searchable combo popover — the enumeration row lists all 19 nicks;
        // a full version needs a filter entry inside the AdwComboRow popover.
        return new AdwPreferencesGroup("Combo Rows") {
            Rows = {
                new AdwComboRow("Combo Row", ["Foo", "Bar", "Baz"]),
                new AdwComboRow(
                    "Enumeration Combo Row",
                    Licenses,
                    subtitle: "This combo row was created from an enumeration"
                ),
            },
        };
    }

    private static Widget ExpanderRowsGroup(GalleryHost host)
    {
        return new AdwPreferencesGroup("Expander Rows") {
            Rows = {
                new AdwExpanderRow("Expander Row") {
                    Rows = {
                        Nested(),
                        AnotherNested(),
                    },
                },
                new AdwExpanderRow("Expander Row With an Action") {
                    HeaderSuffix = new Tooltip(
                        "Copy",
                        Demo.IconButton(
                            MaterialIcons.ContentCopy,
                            () => host.Toast("Copied to clipboard")
                        )
                    ),
                    Rows = {
                        Nested(),
                        AnotherNested(),
                    },
                },
                // Starts gated, as in the GNOME demo: the switch is what is being shown, so the
                // row only becomes expandable once it is turned on (enable-expansion).
                new AdwExpanderRow("Toggleable Expander Row") {
                    ShowEnableSwitch = true,
                    EnableExpansion = false,
                    Rows = {
                        Nested(),
                        AnotherNested(),
                    },
                },
            },
        };
    }

    private static Widget Nested()
    {
        return new AdwActionRow("A Nested Row");
    }

    private static Widget AnotherNested()
    {
        return new AdwActionRow("Another Nested Row");
    }

    private static Widget PropertyRowsGroup(ThemeData theme)
    {
        // The "property" style puts the (dim, caption) title above an emphasised value; built here
        // as a plain row prefix column since Zigote rows have no property style.
        return new AdwPreferencesGroup("Property Rows") {
            Rows = {
                new AdwActionRow {
                    Prefix = new Column(
                        spacing: Spacing.Xxs,
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        mainAxisSize: MainAxisSize.Min
                    ) {
                        Children = {
                            new Label("Property Row", AdwTypography.Caption, theme.TextSecondary),
                            new Label("Value", AdwTypography.Body, theme.OnSurface),
                        },
                    },
                },
            },
        };
    }

    private static Widget SuffixGroup()
    {
        return new AdwPreferencesGroup("Groups With Suffix") {
            HeaderSuffix = new AdwButton("Suffix", () => { }) {
                IconName = MaterialIcons.Add,
                Style = AdwButtonStyle.Flat,
                Compact = true,
            },
            Rows = { new AdwActionRow("Groups Can Have a Header Suffix") },
        };
    }

    private static Widget ButtonRowsGroup()
    {
        return new AdwPreferencesGroup("Button Rows") {
            Rows = {
                new AdwButtonRow("Add Input Source", () => { }, MaterialIcons.Add),
                new AdwButtonRow("Add Calendar", () => { }) {
                    EndIconName = MaterialIcons.OpenInNew,
                },
                new AdwButtonRow("Delete Event", () => { }) { Destructive = true },
                new AdwButtonRow("Search", () => { }) { Suggested = true },
            },
        };
    }
}