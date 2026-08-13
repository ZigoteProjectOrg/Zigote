namespace AdwaitaGallery.Pages;

/// <summary>
///     Paned — two children sharing a box, split by a draggable hairline. The stage below is live:
///     drag the handle and the readout follows, and the panes refuse to shrink past the minimum.
/// </summary>
public sealed class PanedPage : ComposedWidget
{
    private readonly Signal<float> _horizontal = new(0.4f);
    private readonly Signal<float> _minPane = new(120f);
    private readonly Signal<bool> _vertical = new(false);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        Widget Pane(string label, Color fill)
        {
            return new DecoratedBox {
                Fill = fill,
                Child = new Center(
                    new Label(text: label, style: AdwTypography.Body, color: theme.OnBackground)
                ),
            };
        }

        return new GalleryPage(
            title: "Paned",
            description:
            "Two panes, one draggable handle — the resizable split every editor is built on.",
            iconName: MaterialIcons.Splitscreen
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Group(
                    title: "Options",
                    description:
                    "The handle is a hairline in a wider grab gutter, so it is easy to catch " +
                    "without being heavy to look at.",
                    new AdwSwitchRow(
                        title: "Vertical",
                        subtitle: "Split top/bottom instead of left/right",
                        value: false,
                        onChanged: v => _vertical.Value = v
                    ),
                    new AdwActionRow("Minimum pane") {
                        Suffixes = {
                            new SizedBox(
                                width: 200f,
                                child: new AdwSlider(
                                    value: 120f,
                                    min: 40f,
                                    max: 260f,
                                    onChanged: v => _minPane.Value = MathF.Round(v)
                                )
                            ),
                        },
                    },
                    new Watch(() => new AdwActionRow("Position") {
                            Suffixes = {
                                Demo.Value(
                                    $"{_horizontal.Value:0.00}  ·  min {_minPane.Value:0} px"
                                ),
                            },
                        }
                    )
                ),
                new Watch(() => new SizedBox(
                        height: 260f,
                        child: Demo.Stage(
                            child: new AdwPaned(
                                first: Pane(label: "First", fill: AdwPalette.For(theme).SidebarBg),
                                second: Pane(label: "Second", fill: AdwPalette.For(theme).ViewBg),
                                vertical: _vertical.Value
                            ) {
                                Position = _horizontal.Peek(),
                                MinPaneSize = _minPane.Value,
                                OnPositionChanged = p => _horizontal.Value = p,
                            },
                            padding: Spacing.Md
                        )
                    )
                ),
                Demo.Caption(
                    "Drag the handle to the far edge: it stops at the minimum rather than " +
                    "collapsing a pane, and reports the new position once, on release."
                ),
            },
        };
    }
}
