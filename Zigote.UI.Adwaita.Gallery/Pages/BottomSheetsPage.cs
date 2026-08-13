namespace AdwaitaGallery.Pages;

/// <summary>
///     Bottom Sheet — the sheet with its bottom bar, the way a music app carries a now-playing bar
///     that pulls up into the full player. Drag the bar or the handle: the sheet tracks the pointer
///     and settles to whichever end you let go nearer.
/// </summary>
public sealed class BottomSheetsPage : ComposedWidget
{
    private readonly Signal<bool> _open = new(false);
    private readonly AdwBottomSheet _sheet = new();

    public BottomSheetsPage() => _sheet.OnOpenChanged = open => _open.Value = open;

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
                    new AdwButton(label: "Open the Sheet", onPressed: () => _sheet.Open = true) {
                        Style = AdwButtonStyle.Suggested,
                        Pill = true,
                    },
                    new Watch(() => Demo.Caption(_open.Value ? "State: open" : "State: closed")),
                    // The sheet's two knobs, live: both are read when the sheet tree is built, so
                    // flipping either one and pulling the sheet up again shows the difference.
                    new SizedBox(
                        width: 360f,
                        child: new AdwPreferencesGroup {
                            Rows = {
                                new AdwSwitchRow(
                                    title: "Modal",
                                    subtitle:
                                    "Scrim behind the sheet, and Escape or back closes it",
                                    value: _sheet.Modal,
                                    onChanged: v => _sheet.Modal = v
                                ),
                                new AdwSwitchRow(
                                    title: "Show Drag Handle",
                                    subtitle: "The tap-to-close pill at the top of the sheet",
                                    value: _sheet.ShowDragHandle,
                                    onChanged: v => _sheet.ShowDragHandle = v
                                ),
                            },
                        }
                    ),
                },
            },
        };
        _sheet.Sheet = Sheet(theme);
        _sheet.BottomBar = BottomBar(theme: theme, p: p);
        return _sheet;
    }

    private Widget Sheet(ThemeData theme)
    {
        var header = new AdwHeaderBar {
            Flat = true,
            TitleWidget = new AdwWindowTitle(title: "Aurora Drift", subtitle: "Northbound"),
            ShowStartWindowControls = false,
            ShowEndWindowControls = false,
        };
        header.End.Add(
            Demo.IconButton(icon: MaterialIcons.Close, onPressed: () => _sheet.Open = false)
        );

        var controls = new Row(spacing: Spacing.Xl, mainAxisSize: MainAxisSize.Min) {
            Children = {
                Demo.IconButton(icon: MaterialIcons.SkipPrevious, onPressed: () => { }),
                new AdwButton(onPressed: () => { }) {
                    IconName = MaterialIcons.PlayArrow,
                    Style = AdwButtonStyle.Suggested,
                    Circular = true,
                },
                Demo.IconButton(icon: MaterialIcons.SkipNext, onPressed: () => { }),
            },
        };

        return new AdwToolbarView(
            new Center {
                Child = new Column(spacing: Spacing.Xl, mainAxisSize: MainAxisSize.Min) {
                    Children = {
                        new AdwAvatar(size: 96f, iconName: MaterialIcons.MusicNote),
                        new Label(
                            text: "Aurora Drift",
                            style: AdwTypography.Title2,
                            color: theme.OnBackground
                        ) {
                            Align = TextAlign.Center,
                        },
                        new SizedBox(width: 260f, child: new AdwSlider(0.35f)),
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
                            new AdwAvatar(size: 32f, iconName: MaterialIcons.MusicNote),
                            new Expanded(
                                new Column(
                                    mainAxisSize: MainAxisSize.Min,
                                    crossAxisAlignment: CrossAxisAlignment.Start
                                ) {
                                    Children = {
                                        new Label(
                                            text: "Aurora Drift",
                                            style: AdwTypography.Heading,
                                            color: theme.OnBackground
                                        ) {
                                            MaxLines = 1,
                                            Overflow = TextOverflow.Ellipsis,
                                        },
                                        new Label(
                                            text: "Pull up for the player",
                                            style: AdwTypography.Caption,
                                            color: theme.TextSecondary
                                        ) { MaxLines = 1 },
                                    },
                                }
                            ),
                            new IconGlyph(
                                glyph: MaterialIcons.ExpandLess,
                                size: AdwMetrics.IconSize,
                                color: theme.TextSecondary
                            ),
                        },
                    },
                },
            },
        };
    }
}
