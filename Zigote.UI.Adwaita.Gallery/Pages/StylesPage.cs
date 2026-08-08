namespace AdwaitaGallery.Pages;

/// <summary>Style Classes — the demo's big style-class reference, laid out as a gallery page.</summary>
public sealed class StylesPage : StatelessWidget
{
    /// <summary>Flat OSD surface used for the .osd samples (no OSD style class in the toolkit).</summary>
    private static readonly Color Osd = Color.Rgba(
        0,
        0,
        6,
        0.7f
    );

    /// <summary>
    ///     The whole reference, inline: the GNOME demo hides it behind a dialog, but a gallery page
    ///     IS the dialog — one scroll, no modal in the way. Hover anything to see its class name.
    /// </summary>
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        var progress = new Signal<bool>(false);

        var page = new GalleryPage(
            "Style Classes",
            "Every style class libadwaita ships, on the widget it belongs to.",
            MaterialIcons.Palette
        ) {
            Children = {
                new Label(
                    "Hover over widgets to see their exact style class names",
                    AdwTypography.Caption,
                    p.DimLabel
                ) {
                    Align = TextAlign.Center,
                },
                ButtonsGroup(theme),
                EntriesGroup(theme),
                TogglesGroup(theme),
                LinkedGroup(theme),
                LabelsGroup(theme),
                CardsGroup(theme),
                AppIconsGroup(theme),
                ChecksGroup(theme),
                ToolbarsGroup(theme),
                BackgroundsGroup(theme),
                MiscGroup(progress),
            },
        };

        // The osd progress bar floats over the content pinned to its top, so toggling it never
        // moves the page (GtkOverlay child with valign=start in the demo).
        return new Stack {
            Children = {
                page,
                new Watch(() => new Align(Alignment.TopCenter) {
                        Child = progress.Value
                            ? new Tooltip("osd", new AdwProgressBar(0.5f))
                            : null,
                    }
                ),
            },
        };
    }

    /// <summary>An AdwPreferencesGroup header over free-form content instead of a boxed list.</summary>
    private static Widget Group(ThemeData theme, string title, string? description, Widget content)
    {
        var column = new Column(
            spacing: Spacing.Md,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = { new Label(title, AdwTypography.Heading, theme.OnBackground) },
        };
        if (description is not null)
            column.Children.Add(
                new Label(description, AdwTypography.Caption, AdwPalette.For(theme).DimLabel)
            );
        column.Children.Add(content);
        return column;
    }

    private static Widget ButtonsGroup(ThemeData theme)
    {
        return Group(
            theme,
            "Buttons",
            "The \"flat\", \"suggested-action\" and \"destructive\" style classes action can be used together with \"pill\" or \"circular\".\n\nThe \"opaque\" style class allows to create buttons with custom colors that look similar to \"suggested-action\".",
            new Column(
                spacing: Spacing.Md,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Expanded(new AdwButton("Regular", Nothing)),
                            new Expanded(
                                new Tooltip(
                                    "flat",
                                    new AdwButton("Flat", Nothing) { Style = AdwButtonStyle.Flat }
                                )
                            ),
                            new Expanded(
                                new Tooltip(
                                    "suggested-action",
                                    new AdwButton("Suggested", Nothing) {
                                        Style = AdwButtonStyle.Suggested,
                                    }
                                )
                            ),
                            new Expanded(
                                new Tooltip(
                                    "destructive-action",
                                    new AdwButton("Destructive", Nothing) {
                                        Style = AdwButtonStyle.Destructive,
                                    }
                                )
                            ),
                        },
                    },
                    new Row(spacing: Spacing.Sm, mainAxisAlignment: MainAxisAlignment.SpaceEvenly) {
                        Children = {
                            new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    new Tooltip(
                                        "circular",
                                        new AdwButton(onPressed: Nothing) {
                                            IconName = MaterialIcons.Add,
                                            Circular = true,
                                        }
                                    ),
                                    new Tooltip(
                                        "circular",
                                        new AdwButton(onPressed: Nothing) {
                                            Circular = true,
                                            Content = new Label("A", AdwTypography.Heading),
                                        }
                                    ),
                                },
                            },
                            new Tooltip(
                                "pill",
                                new AdwButton("Pill Button", Nothing) { Pill = true }
                            ),
                            new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    OsdButton(MaterialIcons.ArrowBack),
                                    OsdButton(MaterialIcons.ArrowForward),
                                },
                            },
                        },
                    },
                },
            }
        );
    }

    private static Widget EntriesGroup(ThemeData theme)
    {
        var p = AdwPalette.For(theme);
        return Group(
            theme,
            "Entries",
            null,
            new Column(
                spacing: Spacing.Sm,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Expanded(StateEntry("Regular", null, null)),
                            new Expanded(StateEntry("Success", "success", p.Success)),
                        },
                    },
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Expanded(StateEntry("Warning", "warning", p.Warning)),
                            new Expanded(StateEntry("Error", "error", p.Destructive)),
                        },
                    },
                },
            }
        );
    }

    /// <summary>
    ///     ponytail: AdwEntry has no state style classes and no secondary icon, so the success /
    ///     warning / error variants are drawn as a tinted border around a plain entry.
    /// </summary>
    private static Widget StateEntry(string text, string? tooltip, Color? accent)
    {
        Widget entry = new AdwEntry {
            Text = text,
            Placeholder = text,
        };
        if (accent is not null)
            entry = new DecoratedBox {
                BorderColor = accent.Value,
                BorderWidth = 2f,
                Radius = AdwMetrics.ControlRadius,
                Child = entry,
            };
        return tooltip is null ? entry : new Tooltip(tooltip, entry);
    }

    private static Widget TogglesGroup(ThemeData theme)
    {
        return Group(
            theme,
            "Toggle Groups",
            "The \"flat\", \"round\" and \"osd\" style classes action can all be used together",
            new Row(spacing: Spacing.Sm) {
                Children = {
                    new Expanded(
                        new Tooltip("flat", new AdwToggleGroup(["Flat", "Flat"]) { Flat = true })
                    ),
                    new Expanded(
                        new Tooltip(
                            "round",
                            new AdwToggleGroup(["Round", "Round"]) { Round = true }
                        )
                    ),
                    // ponytail: no .osd toggle group — rendered as a plain group.
                    new Expanded(new Tooltip("osd", new AdwToggleGroup(["OSD", "OSD"]))),
                },
            }
        );
    }

    private static Widget LinkedGroup(ThemeData theme)
    {
        return Group(
            theme,
            "Linked Controls",
            "The \"linked\" style on GtkBox and similar containers allows to visually join related button-like and entry-like widgets",
            new Column(
                spacing: Spacing.Sm,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Tooltip(
                                "linked",
                                new AdwToggleGroup(
                                    [
                                        new AdwToggle(IconName: MaterialIcons.GridView),
                                        new AdwToggle(IconName: MaterialIcons.ViewList),
                                    ]
                                )
                            ),
                            // ponytail: no .linked container — the joined look is a clipped row, so
                            // the inner controls keep their own rounded corners.
                            new Expanded(
                                new Tooltip(
                                    "linked",
                                    new ClipRRect(
                                        AdwMetrics.ControlRadius,
                                        new Row {
                                            Children = {
                                                new Expanded(
                                                    new AdwEntry { Placeholder = "Entry" }
                                                ),
                                                new Expanded(
                                                    new AdwEntry { Placeholder = "Entry" }
                                                ),
                                                new AdwButton("Button", Nothing),
                                            },
                                        }
                                    )
                                )
                            ),
                        },
                    },
                    new Row(spacing: Spacing.Sm, crossAxisAlignment: CrossAxisAlignment.Start) {
                        Children = {
                            new Tooltip(
                                "linked",
                                new ClipRRect(
                                    AdwMetrics.ControlRadius,
                                    new Column(mainAxisSize: MainAxisSize.Min) {
                                        Children = {
                                            IconButton(MaterialIcons.ContentCut),
                                            IconButton(MaterialIcons.ContentCopy),
                                            IconButton(MaterialIcons.ContentPaste),
                                        },
                                    }
                                )
                            ),
                            new Expanded(
                                new Tooltip(
                                    "linked",
                                    new ClipRRect(
                                        AdwMetrics.ControlRadius,
                                        new Column(
                                            crossAxisAlignment: CrossAxisAlignment.Stretch,
                                            mainAxisSize: MainAxisSize.Min
                                        ) {
                                            Children = {
                                                new AdwEntry { Placeholder = "Street" },
                                                new AdwEntry { Placeholder = "City" },
                                                new AdwEntry { Placeholder = "Province" },
                                            },
                                        }
                                    )
                                )
                            ),
                        },
                    },
                },
            }
        );
    }

    private static Widget LabelsGroup(ThemeData theme)
    {
        var p = AdwPalette.For(theme);

        var left = new Column(
            spacing: Spacing.Md,
            crossAxisAlignment: CrossAxisAlignment.Start,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                Styled(
                    "Title 1",
                    "title-1",
                    AdwTypography.Title1,
                    theme.OnBackground
                ),
                Styled(
                    "Title 2",
                    "title-2",
                    AdwTypography.Title2,
                    theme.OnBackground
                ),
                Styled(
                    "Title 3",
                    "title-3",
                    AdwTypography.Title3,
                    theme.OnBackground
                ),
                Styled(
                    "Title 4",
                    "title-4",
                    AdwTypography.Title4,
                    theme.OnBackground
                ),
                Styled(
                    "Monospace",
                    "monospace",
                    AdwTypography.Monospace,
                    theme.OnBackground
                ),
                Styled(
                    "Numeric (1234567890)",
                    "numeric",
                    AdwTypography.Body,
                    theme.OnBackground
                ),
                Styled(
                    "Accent",
                    "accent",
                    AdwTypography.Body,
                    p.Accent
                ),
                Styled(
                    "Success",
                    "success",
                    AdwTypography.Body,
                    p.Success
                ),
                Styled(
                    "Warning",
                    "warning",
                    AdwTypography.Body,
                    p.Warning
                ),
                Styled(
                    "Error",
                    "error",
                    AdwTypography.Body,
                    p.Destructive
                ),
            },
        };

        var right = new Column(
            spacing: Spacing.Md,
            crossAxisAlignment: CrossAxisAlignment.Start,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                Styled(
                    "This is a document paragraph. It should be used for the app's main content.",
                    "document",
                    AdwTypography.Body,
                    theme.OnBackground
                ),
                Styled(
                    "Heading",
                    "heading",
                    AdwTypography.Heading,
                    theme.OnBackground
                ),
                Styled(
                    "This is a paragraph of a body copy, to be used for medium-long text such as descriptions in the UI.",
                    "body",
                    AdwTypography.Body,
                    theme.OnBackground
                ),
                Styled(
                    "Caption Heading",
                    "caption-heading",
                    AdwTypography.CaptionHeading,
                    theme.OnBackground
                ),
                Styled(
                    "Caption body text, to be used for body copy on image captions and the like",
                    "caption",
                    AdwTypography.Caption,
                    theme.OnBackground
                ),
                Styled(
                    "This is a dimmed paragraph, mostly used for secondary labels or descriptions.",
                    "dimmed",
                    AdwTypography.Body,
                    p.DimLabel
                ),
            },
        };

        return Group(
            theme,
            "Labels",
            null,
            new Row(spacing: Spacing.Xl, crossAxisAlignment: CrossAxisAlignment.Start) {
                Children = {
                    new Expanded(left),
                    new Expanded(right),
                },
            }
        );
    }

    private static Widget Styled(string text, string styleClass, TextStyle style, Color color)
    {
        return new Tooltip(styleClass, new Label(text, style, color));
    }

    private static Widget CardsGroup(ThemeData theme)
    {
        var group = new AdwPreferencesGroup {
            Rows = {
                new AdwActionRow("Row"),
                new AdwActionRow("Row (Activatable)") { OnActivated = Nothing },
            },
        };

        return Group(
            theme,
            "Cards and Boxed Lists",
            "The \"boxed-list\" style class can be used with GtkListBox to create boxed lists.\n\nThe \"card\" style class can be used to achieve the same style with GtkBox or similar containers, and with GtkButton. If used together with \"activatable\" style class, or on a GtkButton, the card will also have hover and press styles.",
            new Column(
                spacing: Spacing.Md,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new SizedBox(
                        height: 100f,
                        child: new Row(spacing: Spacing.Md) {
                            Children = {
                                new Expanded(
                                    SampleCard(
                                        theme,
                                        "Card",
                                        "card",
                                        false
                                    )
                                ),
                                new Expanded(
                                    SampleCard(
                                        theme,
                                        "Card (Activatable)",
                                        "card, activatable",
                                        true
                                    )
                                ),
                                new Expanded(
                                    SampleCard(
                                        theme,
                                        "Card (Button)",
                                        "card",
                                        true
                                    )
                                ),
                            },
                        }
                    ),
                    new Tooltip("boxed-list", group),
                },
            }
        );
    }

    private static Widget SampleCard(ThemeData theme, string label, string tooltip,
        bool activatable)
    {
        var p = AdwPalette.For(theme);
        var box = new DecoratedBox {
            Fill = p.CardBg,
            Radius = AdwMetrics.CardRadius,
            Child = new Center { Child = new Label(label, AdwTypography.Body, theme.OnBackground) },
        };
        if (!activatable) return new Tooltip(tooltip, box);

        var press = new Pressable {
            OnPressed = Nothing,
            FocusRadius = AdwMetrics.CardRadius,
            Child = box,
        };
        press.OnStateChanged = () =>
        {
            box.Fill = press.Pressed ? theme.Fill2 : press.Hovered ? theme.Fill4 : p.CardBg;
            box.MarkNeedsPaint();
        };
        return new Tooltip(tooltip, press);
    }

    private static Widget AppIconsGroup(ThemeData theme)
    {
        return Group(
            theme,
            "App Icons",
            "The \"icon-dropshadow\" style class ensures legibility when displaying app icons. For 32x32 and smaller app icons, \"lowres-icon\" should be used instead.",
            new Row(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.End) {
                Children = {
                    AppIcon(theme, 128f, "icon-dropshadow"),
                    AppIcon(theme, 64f, "icon-dropshadow"),
                    AppIcon(theme, 32f, "lowres-icon"),
                },
            }
        );
    }

    private static Widget AppIcon(ThemeData theme, float size, string tooltip)
    {
        return new Column(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
            Children = {
                new Tooltip(
                    tooltip,
                    new IconGlyph(MaterialIcons.Apps, size, theme.OnBackground)
                ),
                new Label($"{size:0}", AdwTypography.Body, theme.OnBackground),
            },
        };
    }

    private static Widget ChecksGroup(ThemeData theme)
    {
        return Group(
            theme,
            "Check Buttons",
            "The \"selection-mode\" style class can be used with GtkCheckButton to make them large and round",
            new Row(spacing: Spacing.Xl, mainAxisSize: MainAxisSize.Min) {
                Children = {
                    CheckPair(theme, "Regular", null),
                    // ponytail: no .selection-mode check button — same widget, tooltipped.
                    CheckPair(theme, "Selection Mode", "selection-mode"),
                },
            }
        );
    }

    private static Widget CheckPair(ThemeData theme, string label, string? tooltip)
    {
        Widget checks = new Row(spacing: Spacing.Md, mainAxisSize: MainAxisSize.Min) {
            Children = {
                new AdwCheckButton(value: true),
                new AdwCheckButton(),
            },
        };
        if (tooltip is not null) checks = new Tooltip(tooltip, checks);
        return new Column(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
            Children = {
                checks,
                new Label(label, AdwTypography.Body, theme.OnBackground),
            },
        };
    }

    private static Widget ToolbarsGroup(ThemeData theme)
    {
        var p = AdwPalette.For(theme);

        // ponytail: no labelled AdwMenuButton — the "Open" menu button is an AdwSplitButton whose
        // arrow opens the same Item 1/2/3 menu.
        var toolbar = new Row(spacing: Spacing.Sm) {
            Children = {
                new AdwSplitButton("Open", Nothing) {
                    Style = AdwButtonStyle.Flat,
                    MenuItems = ["Item 1", "Item 2", "Item 3"],
                },
                IconButton(MaterialIcons.Tab),
                new Spacer(),
                IconButton(MaterialIcons.Undo),
                IconButton(MaterialIcons.Redo),
                new SizedBox(Spacing.Sm),
                new AdwMenuButton(MaterialIcons.MoreHoriz) {
                    Sections = {
                        new List<AdwMenuItem> {
                            new("Item 1", Nothing),
                            new("Item 2", Nothing),
                            new("Item 3", Nothing),
                        },
                    },
                },
            },
        };

        var osdToolbar = new Container {
            Background = Osd,
            CornerRadius = AdwMetrics.CardRadius,
            Padding = EdgeInsets.All(Spacing.Sm),
            Child = new Row(spacing: Spacing.Sm) {
                Children = {
                    OsdGlyph(MaterialIcons.SkipPrevious),
                    OsdGlyph(MaterialIcons.Pause),
                    OsdGlyph(MaterialIcons.SkipNext),
                    new Expanded(new AdwSlider(0.5f)),
                    OsdGlyph(MaterialIcons.VolumeUp),
                },
            },
        };

        return Group(
            theme,
            "Toolbars",
            "The \"toolbar\" style class on GtkBox and similar containers gives the same padding, spacing and button appearance as GtkHeaderBar and GtkActionBar have. A toolbar can additionally have the \"osd\" style class, useful for floating media controls.\n\nThe \"raised\" style class can be used to make a button inside a toolbar use default appearance instead.",
            new Column(
                spacing: Spacing.Md,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Tooltip(
                        "toolbar",
                        new DecoratedBox {
                            BorderColor = p.Border,
                            Radius = AdwMetrics.CardRadius,
                            Child = new Padding(EdgeInsets.All(Spacing.Sm), toolbar),
                        }
                    ),
                    new Tooltip("toolbar, osd", osdToolbar),
                },
            }
        );
    }

    private static Widget BackgroundsGroup(ThemeData theme)
    {
        var p = AdwPalette.For(theme);
        return Group(
            theme,
            "Backgrounds",
            "These style classes can be applied to any widgets that need the specific background and text color",
            new SizedBox(
                height: 100f,
                child: new Row {
                    Children = {
                        new Expanded(
                            Pane(
                                "Background",
                                "background",
                                p.WindowBg,
                                p.WindowFg
                            )
                        ),
                        new Expanded(
                            Pane(
                                "View",
                                "view",
                                p.ViewBg,
                                p.ViewFg
                            )
                        ),
                        new Expanded(
                            Pane(
                                "OSD",
                                "osd",
                                Osd,
                                Color.White
                            )
                        ),
                    },
                }
            )
        );
    }

    private static Widget Pane(string label, string tooltip, Color background, Color foreground)
    {
        return new Tooltip(
            tooltip,
            new Container {
                Background = background,
                Child = new Center { Child = new Label(label, AdwTypography.Body, foreground) },
            }
        );
    }

    private static Widget MiscGroup(Signal<bool> progress)
    {
        return new AdwPreferencesGroup("Misc") {
            Rows = {
                new AdwActionRow("Status Pages") {
                    ShowChevron = true,
                    OnActivated = ShowStatusPagesDialog,
                },
                new AdwActionRow("Sidebar") {
                    ShowChevron = true,
                    OnActivated = ShowSidebarDialog,
                },
                // ponytail: no .devel window style in the toolkit, so this switch is inert.
                new AdwSwitchRow(
                    "Development Window",
                    "The \"devel\" style class on GtkWindow — not implemented here"
                ),
                new AdwSwitchRow(
                    "OSD Progress Bar",
                    "\"osd\" style class on GtkProgressBar",
                    onChanged: v => progress.Value = v
                ),
            },
        };
    }

    private static void ShowStatusPagesDialog()
    {
        var split = new AdwOverlaySplitView {
            Sidebar = new Tooltip(
                "compact",
                new AdwStatusPage {
                    IconName = MaterialIcons.WavingHand,
                    Title = "Compact",
                    Description = "This status page has the \"compact\" style class",
                    Compact = true,
                    Child = new AdwButton("Button", Nothing) { Pill = true },
                }
            ),
            Content = new AdwStatusPage {
                IconName = MaterialIcons.WavingHand,
                Title = "Regular",
                Description = "This is a regular status page",
                Child = new AdwButton("Button", Nothing) { Pill = true },
            },
        };

        Demo.ShowDialog(
            "Status Pages",
            split,
            640f,
            480f,
            true,
            // The demo reveals this toggle at its breakpoint; here it is always available.
            Demo.IconButton(
                MaterialIcons.ViewSidebar,
                () => split.ShowSidebar = !split.ShowSidebar
            )
        );
    }

    private static void ShowSidebarDialog()
    {
        var split = new AdwNavigationSplitView {
            SidebarWidth = 240f,
            Content = new AdwStatusPage {
                Title = "Sidebar",
                Description = "\"navigation-sidebar\" style class on GtkListBox or GtkListView",
            },
        };
        // AdwSidebar is the navigation-sidebar list; the demo's rows are plain labels, so the
        // items carry no icon.
        split.Sidebar = new Tooltip(
            "navigation-sidebar",
            new AdwSidebar(
                new AdwSidebarSection(
                    null,
                    new AdwSidebarItem("Item 1", ""),
                    new AdwSidebarItem("Item 2", ""),
                    new AdwSidebarItem("Item 3", ""),
                    new AdwSidebarItem("Item 4", ""),
                    new AdwSidebarItem("Item 5", "")
                )
            ) {
                OnSelected = _ => split.ShowContent = true,
            }
        );

        Demo.ShowDialog(
            "Sidebar",
            split,
            720f,
            480f
        );
    }

    private static Widget IconButton(string icon)
    {
        return new AdwButton(onPressed: Nothing) {
            IconName = icon,
            Style = AdwButtonStyle.Flat,
        };
    }

    /// <summary>ponytail: .osd buttons are static glyphs on a dark surface — no press states.</summary>
    private static Widget OsdButton(string icon)
    {
        return new Tooltip(
            "osd",
            new Container {
                Width = AdwMetrics.ButtonHeight,
                Height = AdwMetrics.ButtonHeight,
                Background = Osd,
                CornerRadius = AdwMetrics.ControlRadius,
                Child = new Center {
                    Child = new IconGlyph(icon, AdwMetrics.IconSize, Color.White),
                },
            }
        );
    }

    private static Widget OsdGlyph(string icon)
    {
        return new SizedBox(
            AdwMetrics.ButtonHeight,
            AdwMetrics.ButtonHeight,
            new Center {
                Child = new IconGlyph(icon, AdwMetrics.IconSize, Color.White),
            }
        );
    }

    /// <summary>The demo's sample widgets are inert; AdwButton renders disabled without a callback.</summary>
    private static void Nothing()
    {
    }
}