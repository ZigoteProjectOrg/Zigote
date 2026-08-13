namespace AdwaitaGallery.Pages;

/// <summary>
///     Bottom Sheet — the sheet with its bottom bar, the way a music app carries a now-playing bar
///     that pulls up into the full player. Drag the bar or the handle: the sheet tracks the pointer
///     and settles to whichever end you let go nearer.
/// </summary>
public sealed class BottomSheetsPage : ComposedWidget
{
    private readonly AdwBottomSheet _sheet = new();
    private readonly Signal<bool> _open = new(false);

    public BottomSheetsPage()
    {
        _sheet.OnOpenChanged = open => _open.Value = open;
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        _sheet.Content = new AdwStatusPage {
            IconName = MaterialIcons.VerticalAlignBottom,
            Title = "Bottom Sheet",
            Description = "Pull the bar up, or drag the handle back down",
            Child = new Column(spacing: Spacing.Md, mainAxisSize: MainAxisSize.Min) {
                Children = {
                    new AdwButton("Open the Sheet", () => _sheet.Open = true) {
                        Style = AdwButtonStyle.Suggested,
                        Pill = true,
                    },
                    new Watch(() => Demo.Caption(_open.Value ? "State: open" : "State: closed")),
                    // The sheet's two knobs, live: both are read when the sheet tree is built, so
                    // flipping either one and pulling the sheet up again shows the difference.
                    new SizedBox(
                        360f,
                        child: new AdwPreferencesGroup {
                            Rows = {
                                new AdwSwitchRow(
                                    "Modal",
                                    "Scrim behind the sheet, and Escape or back closes it",
                                    _sheet.Modal,
                                    v => _sheet.Modal = v
                                ),
                                new AdwSwitchRow(
                                    "Show Drag Handle",
                                    "The tap-to-close pill at the top of the sheet",
                                    _sheet.ShowDragHandle,
                                    v => _sheet.ShowDragHandle = v
                                ),
                            },
                        }
                    ),
                },
            },
        };
        _sheet.Sheet = Sheet(theme);
        _sheet.BottomBar = BottomBar(theme, p);
        return _sheet;
    }

    private Widget Sheet(ThemeData theme)
    {
        var header = new AdwHeaderBar {
            Flat = true,
            TitleWidget = new AdwWindowTitle("Aurora Drift", "Northbound"),
            ShowStartWindowControls = false,
            ShowEndWindowControls = false,
        };
        header.End.Add(Demo.IconButton(MaterialIcons.Close, () => _sheet.Open = false));

        var controls = new Row(spacing: Spacing.Xl, mainAxisSize: MainAxisSize.Min) {
            Children = {
                Demo.IconButton(MaterialIcons.SkipPrevious, () => { }),
                new AdwButton(onPressed: () => { }) {
                    IconName = MaterialIcons.PlayArrow,
                    Style = AdwButtonStyle.Suggested,
                    Circular = true,
                },
                Demo.IconButton(MaterialIcons.SkipNext, () => { }),
            },
        };

        return new AdwToolbarView(
            new Center {
                Child = new Column(spacing: Spacing.Xl, mainAxisSize: MainAxisSize.Min) {
                    Children = {
                        new AdwAvatar(96f, iconName: MaterialIcons.MusicNote),
                        new Label("Aurora Drift", AdwTypography.Title2, theme.OnBackground) {
                            Align = TextAlign.Center,
                        },
                        new SizedBox(260f, child: new AdwSlider(0.35f)),
                        controls,
                    },
                },
            }
        ) { TopBars = { header } };
    }

    private static Widget BottomBar(ThemeData theme, AdwColors p)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new Container {
                    Height = 1f,
                    Background = p.HeaderbarShade,
                },
                new Container {
                    Height = 56f,
                    Background = theme.TitleBar,
                    Padding = EdgeInsets.Symmetric(Spacing.Md),
                    Child = new Row(spacing: Spacing.Md) {
                        Children = {
                            new AdwAvatar(32f, iconName: MaterialIcons.MusicNote),
                            new Expanded(
                                new Column(
                                    mainAxisSize: MainAxisSize.Min,
                                    crossAxisAlignment: CrossAxisAlignment.Start
                                ) {
                                    Children = {
                                        new Label(
                                            "Aurora Drift",
                                            AdwTypography.Heading,
                                            theme.OnBackground
                                        ) {
                                            MaxLines = 1,
                                            Overflow = TextOverflow.Ellipsis,
                                        },
                                        new Label(
                                            "Pull up for the player",
                                            AdwTypography.Caption,
                                            theme.TextSecondary
                                        ) { MaxLines = 1 },
                                    },
                                }
                            ),
                            new IconGlyph(
                                MaterialIcons.ExpandLess,
                                AdwMetrics.IconSize,
                                theme.TextSecondary
                            ),
                        },
                    },
                },
            },
        };
    }
}
