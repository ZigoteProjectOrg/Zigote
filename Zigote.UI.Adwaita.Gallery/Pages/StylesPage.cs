namespace AdwaitaGallery.Pages;

/// <summary>Style Classes — the demo's big style-class reference, laid out as a gallery page.</summary>
public sealed class StylesPage : ComposedWidget
{
    /// <summary>Flat OSD surface used for the .osd samples (no OSD style class in the toolkit).</summary>
    private static readonly Color Osd = Color.Rgba(
        r: 0,
        g: 0,
        b: 6,
        a: 0.7f
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
            title: "Style Classes",
            description: "Every style class libadwaita ships, on the widget it belongs to.",
            iconName: MaterialIcons.Palette
        ) {
            Children = {
                new Label(
                    text: "Hover over widgets to see their exact style class names",
                    style: AdwTypography.Caption,
                    color: p.DimLabel
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
                            ? new Tooltip(message: "osd", child: new AdwProgressBar(0.5f))
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
            Children = {
                new Label(text: title, style: AdwTypography.Heading, color: theme.OnBackground),
            },
        };
        if (description is not null)
        {
            column.Children.Add(
                new Label(
                    text: description,
                    style: AdwTypography.Caption,
                    color: AdwPalette.For(theme).DimLabel
                )
            );
        }

        column.Children.Add(content);
        return column;
    }

    private static Widget ButtonsGroup(ThemeData theme)
    {
        return Group(
            theme: theme,
            title: "Buttons",
            description:
            "The \"flat\", \"suggested-action\" and \"destructive\" style classes action can be used together with \"pill\" or \"circular\".\n\nThe \"opaque\" style class allows to create buttons with custom colors that look similar to \"suggested-action\".",
            content: new Column(
                spacing: Spacing.Md,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Expanded(new AdwButton(label: "Regular", onPressed: Nothing)),
                            new Expanded(
                                new Tooltip(
                                    message: "flat",
                                    child: new AdwButton(label: "Flat", onPressed: Nothing) {
                                        Style = AdwButtonStyle.Flat,
                                    }
                                )
                            ),
                            new Expanded(
                                new Tooltip(
                                    message: "suggested-action",
                                    child: new AdwButton(label: "Suggested", onPressed: Nothing) {
                                        Style = AdwButtonStyle.Suggested,
                                    }
                                )
                            ),
                            new Expanded(
                                new Tooltip(
                                    message: "destructive-action",
                                    child: new AdwButton(label: "Destructive", onPressed: Nothing) {
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
                                        message: "circular",
                                        child: new AdwButton(onPressed: Nothing) {
                                            IconName = MaterialIcons.Add,
                                            Circular = true,
                                        }
                                    ),
                                    new Tooltip(
                                        message: "circular",
                                        child: new AdwButton(onPressed: Nothing) {
                                            Circular = true,
                                            Content = new Label(
                                                text: "A",
                                                style: AdwTypography.Heading
                                            ),
                                        }
                                    ),
                                },
                            },
                            new Tooltip(
                                message: "pill",
                                child: new AdwButton(label: "Pill Button", onPressed: Nothing) {
                                    Pill = true,
                                }
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
            theme: theme,
            title: "Entries",
            description: null,
            content: new Column(
                spacing: Spacing.Sm,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Expanded(StateEntry(text: "Regular", tooltip: null, accent: null)),
                            new Expanded(
                                StateEntry(text: "Success", tooltip: "success", accent: p.Success)
                            ),
                        },
                    },
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Expanded(
                                StateEntry(text: "Warning", tooltip: "warning", accent: p.Warning)
                            ),
                            new Expanded(
                                StateEntry(text: "Error", tooltip: "error", accent: p.Destructive)
                            ),
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
        {
            entry = new DecoratedBox {
                BorderColor = accent.Value,
                BorderWidth = 2f,
                Radius = AdwMetrics.ControlRadius,
                Child = entry,
            };
        }

        return tooltip is null ? entry : new Tooltip(message: tooltip, child: entry);
    }

    private static Widget TogglesGroup(ThemeData theme)
    {
        return Group(
            theme: theme,
            title: "Toggle Groups",
            description:
            "The \"flat\", \"round\" and \"osd\" style classes action can all be used together",
            content: new Row(spacing: Spacing.Sm) {
                Children = {
                    new Expanded(
                        new Tooltip(
                            message: "flat",
                            child: new AdwToggleGroup(["Flat", "Flat"]) { Flat = true }
                        )
                    ),
                    new Expanded(
                        new Tooltip(
                            message: "round",
                            child: new AdwToggleGroup(["Round", "Round"]) { Round = true }
                        )
                    ),
                    // ponytail: no .osd toggle group — rendered as a plain group.
                    new Expanded(
                        new Tooltip(message: "osd", child: new AdwToggleGroup(["OSD", "OSD"]))
                    ),
                },
            }
        );
    }

    private static Widget LinkedGroup(ThemeData theme)
    {
        return Group(
            theme: theme,
            title: "Linked Controls",
            description:
            "The \"linked\" style on GtkBox and similar containers allows to visually join related button-like and entry-like widgets",
            content: new Column(
                spacing: Spacing.Sm,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(spacing: Spacing.Sm) {
                        Children = {
                            new Tooltip(
                                message: "linked",
                                child: new AdwToggleGroup(
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
                                    message: "linked",
                                    child: new ClipRRect(
                                        radius: AdwMetrics.ControlRadius,
                                        child: new Row {
                                            Children = {
                                                new Expanded(
                                                    new AdwEntry { Placeholder = "Entry" }
                                                ),
                                                new Expanded(
                                                    new AdwEntry { Placeholder = "Entry" }
                                                ),
                                                new AdwButton(label: "Button", onPressed: Nothing),
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
                                message: "linked",
                                child: new ClipRRect(
                                    radius: AdwMetrics.ControlRadius,
                                    child: new Column(mainAxisSize: MainAxisSize.Min) {
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
                                    message: "linked",
                                    child: new ClipRRect(
                                        radius: AdwMetrics.ControlRadius,
                                        child: new Column(
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
                    text: "Title 1",
                    styleClass: "title-1",
                    style: AdwTypography.Title1,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Title 2",
                    styleClass: "title-2",
                    style: AdwTypography.Title2,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Title 3",
                    styleClass: "title-3",
                    style: AdwTypography.Title3,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Title 4",
                    styleClass: "title-4",
                    style: AdwTypography.Title4,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Monospace",
                    styleClass: "monospace",
                    style: AdwTypography.Monospace,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Numeric (1234567890)",
                    styleClass: "numeric",
                    style: AdwTypography.Body,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Accent",
                    styleClass: "accent",
                    style: AdwTypography.Body,
                    color: p.Accent
                ),
                Styled(
                    text: "Success",
                    styleClass: "success",
                    style: AdwTypography.Body,
                    color: p.Success
                ),
                Styled(
                    text: "Warning",
                    styleClass: "warning",
                    style: AdwTypography.Body,
                    color: p.Warning
                ),
                Styled(
                    text: "Error",
                    styleClass: "error",
                    style: AdwTypography.Body,
                    color: p.Destructive
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
                    text:
                    "This is a document paragraph. It should be used for the app's main content.",
                    styleClass: "document",
                    style: AdwTypography.Body,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Heading",
                    styleClass: "heading",
                    style: AdwTypography.Heading,
                    color: theme.OnBackground
                ),
                Styled(
                    text:
                    "This is a paragraph of a body copy, to be used for medium-long text such as descriptions in the UI.",
                    styleClass: "body",
                    style: AdwTypography.Body,
                    color: theme.OnBackground
                ),
                Styled(
                    text: "Caption Heading",
                    styleClass: "caption-heading",
                    style: AdwTypography.CaptionHeading,
                    color: theme.OnBackground
                ),
                Styled(
                    text:
                    "Caption body text, to be used for body copy on image captions and the like",
                    styleClass: "caption",
                    style: AdwTypography.Caption,
                    color: theme.OnBackground
                ),
                Styled(
                    text:
                    "This is a dimmed paragraph, mostly used for secondary labels or descriptions.",
                    styleClass: "dimmed",
                    style: AdwTypography.Body,
                    color: p.DimLabel
                ),
            },
        };

        return Group(
            theme: theme,
            title: "Labels",
            description: null,
            content: new Row(spacing: Spacing.Xl, crossAxisAlignment: CrossAxisAlignment.Start) {
                Children = {
                    new Expanded(left),
                    new Expanded(right),
                },
            }
        );
    }

    private static Widget Styled(string text, string styleClass, TextStyle style, Color color) =>
        new Tooltip(message: styleClass, child: new Label(text: text, style: style, color: color));

    private static Widget CardsGroup(ThemeData theme)
    {
        var group = new AdwPreferencesGroup {
            Rows = {
                new AdwActionRow("Row"),
                new AdwActionRow("Row (Activatable)") { OnActivated = Nothing },
            },
        };

        return Group(
            theme: theme,
            title: "Cards and Boxed Lists",
            description:
            "The \"boxed-list\" style class can be used with GtkListBox to create boxed lists.\n\nThe \"card\" style class can be used to achieve the same style with GtkBox or similar containers, and with GtkButton. If used together with \"activatable\" style class, or on a GtkButton, the card will also have hover and press styles.",
            content: new Column(
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
                                        theme: theme,
                                        label: "Card",
                                        tooltip: "card",
                                        activatable: false
                                    )
                                ),
                                new Expanded(
                                    SampleCard(
                                        theme: theme,
                                        label: "Card (Activatable)",
                                        tooltip: "card, activatable",
                                        activatable: true
                                    )
                                ),
                                new Expanded(
                                    SampleCard(
                                        theme: theme,
                                        label: "Card (Button)",
                                        tooltip: "card",
                                        activatable: true
                                    )
                                ),
                            },
                        }
                    ),
                    new Tooltip(message: "boxed-list", child: group),
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
            Child = new Center {
                Child = new Label(
                    text: label,
                    style: AdwTypography.Body,
                    color: theme.OnBackground
                ),
            },
        };
        if (!activatable) return new Tooltip(message: tooltip, child: box);

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
        return new Tooltip(message: tooltip, child: press);
    }

    private static Widget AppIconsGroup(ThemeData theme)
    {
        return Group(
            theme: theme,
            title: "App Icons",
            description:
            "The \"icon-dropshadow\" style class ensures legibility when displaying app icons. For 32x32 and smaller app icons, \"lowres-icon\" should be used instead.",
            content: new Row(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.End) {
                Children = {
                    AppIcon(theme: theme, size: 128f, tooltip: "icon-dropshadow"),
                    AppIcon(theme: theme, size: 64f, tooltip: "icon-dropshadow"),
                    AppIcon(theme: theme, size: 32f, tooltip: "lowres-icon"),
                },
            }
        );
    }

    private static Widget AppIcon(ThemeData theme, float size, string tooltip)
    {
        return new Column(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
            Children = {
                new Tooltip(
                    message: tooltip,
                    child: new IconGlyph(
                        glyph: MaterialIcons.Apps,
                        size: size,
                        color: theme.OnBackground
                    )
                ),
                new Label(text: $"{size:0}", style: AdwTypography.Body, color: theme.OnBackground),
            },
        };
    }

    private static Widget ChecksGroup(ThemeData theme)
    {
        return Group(
            theme: theme,
            title: "Check Buttons",
            description:
            "The \"selection-mode\" style class can be used with GtkCheckButton to make them large and round",
            content: new Row(spacing: Spacing.Xl, mainAxisSize: MainAxisSize.Min) {
                Children = {
                    CheckPair(theme: theme, label: "Regular", tooltip: null),
                    // ponytail: no .selection-mode check button — same widget, tooltipped.
                    CheckPair(theme: theme, label: "Selection Mode", tooltip: "selection-mode"),
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
        if (tooltip is not null) checks = new Tooltip(message: tooltip, child: checks);
        return new Column(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
            Children = {
                checks,
                new Label(text: label, style: AdwTypography.Body, color: theme.OnBackground),
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
                new AdwSplitButton(label: "Open", onPressed: Nothing) {
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
                            new(label: "Item 1", onActivated: Nothing),
                            new(label: "Item 2", onActivated: Nothing),
                            new(label: "Item 3", onActivated: Nothing),
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
            theme: theme,
            title: "Toolbars",
            description:
            "The \"toolbar\" style class on GtkBox and similar containers gives the same padding, spacing and button appearance as GtkHeaderBar and GtkActionBar have. A toolbar can additionally have the \"osd\" style class, useful for floating media controls.\n\nThe \"raised\" style class can be used to make a button inside a toolbar use default appearance instead.",
            content: new Column(
                spacing: Spacing.Md,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Tooltip(
                        message: "toolbar",
                        child: new DecoratedBox {
                            BorderColor = p.Border,
                            Radius = AdwMetrics.CardRadius,
                            Child = new Padding(
                                padding: EdgeInsets.All(Spacing.Sm),
                                child: toolbar
                            ),
                        }
                    ),
                    new Tooltip(message: "toolbar, osd", child: osdToolbar),
                },
            }
        );
    }

    private static Widget BackgroundsGroup(ThemeData theme)
    {
        var p = AdwPalette.For(theme);
        return Group(
            theme: theme,
            title: "Backgrounds",
            description:
            "These style classes can be applied to any widgets that need the specific background and text color",
            content: new SizedBox(
                height: 100f,
                child: new Row {
                    Children = {
                        new Expanded(
                            Pane(
                                label: "Background",
                                tooltip: "background",
                                background: p.WindowBg,
                                foreground: p.WindowFg
                            )
                        ),
                        new Expanded(
                            Pane(
                                label: "View",
                                tooltip: "view",
                                background: p.ViewBg,
                                foreground: p.ViewFg
                            )
                        ),
                        new Expanded(
                            Pane(
                                label: "OSD",
                                tooltip: "osd",
                                background: Osd,
                                foreground: Color.White
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
            message: tooltip,
            child: new Container {
                Background = background,
                Child = new Center {
                    Child = new Label(text: label, style: AdwTypography.Body, color: foreground),
                },
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
                    title: "Development Window",
                    subtitle: "The \"devel\" style class on GtkWindow — not implemented here"
                ),
                new AdwSwitchRow(
                    title: "OSD Progress Bar",
                    subtitle: "\"osd\" style class on GtkProgressBar",
                    onChanged: v => progress.Value = v
                ),
            },
        };
    }

    private static void ShowStatusPagesDialog()
    {
        var split = new AdwOverlaySplitView {
            Sidebar = new Tooltip(
                message: "compact",
                child: new AdwStatusPage {
                    IconName = MaterialIcons.WavingHand,
                    Title = "Compact",
                    Description = "This status page has the \"compact\" style class",
                    Compact = true,
                    Child = new AdwButton(label: "Button", onPressed: Nothing) { Pill = true },
                }
            ),
            Content = new AdwStatusPage {
                IconName = MaterialIcons.WavingHand,
                Title = "Regular",
                Description = "This is a regular status page",
                Child = new AdwButton(label: "Button", onPressed: Nothing) { Pill = true },
            },
        };

        Demo.ShowDialog(
            title: "Status Pages",
            content: split,
            width: 640f,
            height: 480f,
            raised: true,
            // The demo reveals this toggle at its breakpoint; here it is always available.
            headerStart: Demo.IconButton(
                icon: MaterialIcons.ViewSidebar,
                onPressed: () => split.ShowSidebar = !split.ShowSidebar
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
            message: "navigation-sidebar",
            child: new AdwSidebar(
                new AdwSidebarSection(
                    title: null,
                    new AdwSidebarItem(title: "Item 1", iconName: ""),
                    new AdwSidebarItem(title: "Item 2", iconName: ""),
                    new AdwSidebarItem(title: "Item 3", iconName: ""),
                    new AdwSidebarItem(title: "Item 4", iconName: ""),
                    new AdwSidebarItem(title: "Item 5", iconName: "")
                )
            ) {
                OnSelected = _ => split.ShowContent = true,
            }
        );

        Demo.ShowDialog(
            title: "Sidebar",
            content: split,
            width: 720f,
            height: 480f
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
            message: "osd",
            child: new Container {
                Width = AdwMetrics.ButtonHeight,
                Height = AdwMetrics.ButtonHeight,
                Background = Osd,
                CornerRadius = AdwMetrics.ControlRadius,
                Child = new Center {
                    Child = new IconGlyph(
                        glyph: icon,
                        size: AdwMetrics.IconSize,
                        color: Color.White
                    ),
                },
            }
        );
    }

    private static Widget OsdGlyph(string icon)
    {
        return new SizedBox(
            width: AdwMetrics.ButtonHeight,
            height: AdwMetrics.ButtonHeight,
            child: new Center {
                Child = new IconGlyph(glyph: icon, size: AdwMetrics.IconSize, color: Color.White),
            }
        );
    }

    /// <summary>The demo's sample widgets are inert; AdwButton renders disabled without a callback.</summary>
    private static void Nothing() { }
}
