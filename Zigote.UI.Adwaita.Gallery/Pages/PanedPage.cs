namespace AdwaitaGallery.Pages;

/// <summary>
///     Paned — two children sharing a box, split by a draggable hairline. The stage below is live:
///     drag the handle and the readout follows, and the panes refuse to shrink past the minimum.
/// </summary>
public sealed class PanedPage : ComposedWidget
{
    private readonly Signal<float> _horizontal = new(0.4f);
    private readonly Signal<bool> _vertical = new(false);
    private readonly Signal<float> _minPane = new(120f);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        Widget Pane(string label, Color fill)
        {
            return new DecoratedBox {
                Fill = fill,
                Child = new Center(new Label(label, AdwTypography.Body, theme.OnBackground)),
            };
        }

        return new GalleryPage(
            "Paned",
            "Two panes, one draggable handle — the resizable split every editor is built on.",
            MaterialIcons.Splitscreen
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Group(
                    "Options",
                    "The handle is a hairline in a wider grab gutter, so it is easy to catch " +
                    "without being heavy to look at.",
                    new AdwSwitchRow(
                        "Vertical",
                        "Split top/bottom instead of left/right",
                        false,
                        v => _vertical.Value = v
                    ),
                    new AdwActionRow("Minimum pane") {
                        Suffixes = {
                            new SizedBox(
                                200f,
                                child: new AdwSlider(
                                    120f,
                                    40f,
                                    260f,
                                    v => _minPane.Value = MathF.Round(v)
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
                            new AdwPaned(
                                Pane("First", AdwPalette.For(theme).SidebarBg),
                                Pane("Second", AdwPalette.For(theme).ViewBg),
                                _vertical.Value
                            ) {
                                Position = _horizontal.Peek(),
                                MinPaneSize = _minPane.Value,
                                OnPositionChanged = p => _horizontal.Value = p,
                            },
                            Spacing.Md
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
