namespace AdwaitaGallery.Pages;

/// <summary>
///     Breakpoints and multi-layout views. Both stages below are live and sized by the slider, not
///     by the window — which is the point of a breakpoint bin: it answers for the box it was given,
///     so a bin inside a narrow pane folds even on a wide display.
/// </summary>
public sealed class BreakpointsPage : ComposedWidget
{
    private readonly Signal<float> _width = new(720f);

    // Retained: the slider mutates these boxes' Width. Rebuilding the stages from a Watch would
    // discard and re-create the breakpoint bin, its panes and the shared entry on every pointer
    // move of the drag — which is exactly the state a multi-layout view exists to preserve.
    private readonly SizedBox _binStage = new(720f, 120f);
    private readonly SizedBox _layoutStage = new(720f, 140f);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        Widget Pane(string label, Color fill)
        {
            return new DecoratedBox {
                Fill = fill,
                Radius = AdwMetrics.CardRadius,
                Child = new Center(new Label(label, AdwTypography.Body, theme.OnBackground)),
            };
        }

        // One shared child, re-parented between arrangements — a multi-layout view never rebuilds
        // it, so anything stateful inside survives the fold.
        var shared = new AdwEntry { Placeholder = "Type here, then fold the layout" };

        return new GalleryPage(
            "Breakpoints",
            "Containers that answer for their own size, and layouts that share their children.",
            MaterialIcons.Rule
        ) {
            ClampWidth = 760f,
            Children = {
                Demo.Group(
                    "Allocation",
                    "Drag to resize the stages below. Nothing here reads the window size.",
                    new AdwActionRow("Width") {
                        Suffixes = {
                            new SizedBox(
                                240f,
                                child: new AdwSlider(
                                    720f,
                                    260f,
                                    760f,
                                    v =>
                                    {
                                        var w = MathF.Round(v);
                                        _width.Value = w; // drives the readout only
                                        _binStage.Width = w;
                                        _layoutStage.Width = w;
                                        _binStage.MarkNeedsLayout();
                                        _layoutStage.MarkNeedsLayout();
                                    }
                                )
                            ),
                        },
                    },
                    new Watch(() => new AdwActionRow("Given") {
                            Suffixes = { Demo.Value($"{_width.Value:0} px") },
                        }
                    )
                ),
                Demo.Titled(
                    "Breakpoint Bin",
                    "Breakpoints are listed narrowest-first and the LAST match wins, so the list " +
                    "reads top-to-bottom like a stylesheet.",
                    new Align(Alignment.TopCenter, Stage(_binStage, Bin(Pane, p))) {
                        HeightFactor = 1f,
                    }
                ),
                Demo.Titled(
                    "Multi-Layout View",
                    "The same entry moves between arrangements; its text and caret survive.",
                    new Align(Alignment.TopCenter, Stage(_layoutStage, MultiLayout(shared, Pane, p))) {
                        HeightFactor = 1f,
                    }
                ),
                Demo.Caption(
                    "Both stages are inside the same window — only the box they were handed changes."
                ),
            },
        };
    }

    /// <summary>Fill a retained stage box with its content once, and hand it back.</summary>
    private static Widget Stage(SizedBox box, Widget content)
    {
        box.Child = content;
        return box;
    }

    private static Widget Bin(Func<string, Color, Widget> pane, AdwColors p)
    {
        var bin = new AdwBreakpointBin(
            new Row(spacing: Spacing.Md) {
                Children = {
                    new SizedBox(200f, child: pane("Sidebar", p.SidebarBg)),
                    new Expanded(pane("Content — wide", p.ViewBg)),
                },
            }
        );
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) {
                Child = pane("Content — folded", p.ViewBg),
            }
        );
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(360f)) {
                Child = pane("Content — narrow", p.CardBg),
            }
        );
        return bin;
    }

    private static Widget MultiLayout(Widget shared, Func<string, Color, Widget> pane, AdwColors p)
    {
        var view = new AdwMultiLayoutView {
            Children = { ["entry"] = shared },
            Layouts = {
                new AdwLayout(
                    "wide",
                    new Row(spacing: Spacing.Md) {
                        Children = {
                            new SizedBox(180f, child: pane("Sidebar", p.SidebarBg)),
                            new Expanded(
                                new Center(new AdwLayoutSlot("entry"))
                            ),
                        },
                    }
                ),
                new AdwLayout(
                    "narrow",
                    new Column(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.Stretch) {
                        Children = {
                            new SizedBox(height: 40f, child: pane("Sidebar", p.SidebarBg)),
                            new AdwLayoutSlot("entry"),
                        },
                    }
                ),
            },
        };

        // The bin picks the layout; the view owns the children. This pairing is the intended use.
        var bin = new AdwBreakpointBin(view);
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MinWidth(0f)) {
                Apply = () => view.LayoutName = "narrow",
            }
        );
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MinWidth(600f)) {
                Apply = () => view.LayoutName = "wide",
            }
        );
        return bin;
    }
}
