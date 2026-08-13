namespace AdwaitaGallery.Pages;

/// <summary>
///     Breakpoints and multi-layout views. Both stages below are live and sized by the slider, not
///     by the window — which is the point of a breakpoint bin: it answers for the box it was given,
///     so a bin inside a narrow pane folds even on a wide display.
/// </summary>
public sealed class BreakpointsPage : ComposedWidget
{
    // Retained: the slider mutates these boxes' Width. Rebuilding the stages from a Watch would
    // discard and re-create the breakpoint bin, its panes and the shared entry on every pointer
    // move of the drag — which is exactly the state a multi-layout view exists to preserve.
    private readonly SizedBox _binStage = new(width: 720f, height: 120f);
    private readonly SizedBox _layoutStage = new(width: 720f, height: 140f);
    private readonly Signal<float> _width = new(720f);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        Widget Pane(string label, Color fill)
        {
            return new DecoratedBox {
                Fill = fill,
                Radius = AdwMetrics.CardRadius,
                Child = new Center(
                    new Label(text: label, style: AdwTypography.Body, color: theme.OnBackground)
                ),
            };
        }

        // One shared child, re-parented between arrangements — a multi-layout view never rebuilds
        // it, so anything stateful inside survives the fold.
        var shared = new AdwEntry { Placeholder = "Type here, then fold the layout" };

        return new GalleryPage(
            title: "Breakpoints",
            description:
            "Containers that answer for their own size, and layouts that share their children.",
            iconName: MaterialIcons.Rule
        ) {
            ClampWidth = 760f,
            Children = {
                Demo.Group(
                    title: "Allocation",
                    description:
                    "Drag to resize the stages below. Nothing here reads the window size.",
                    new AdwActionRow("Width") {
                        Suffixes = {
                            new SizedBox(
                                width: 240f,
                                child: new AdwSlider(
                                    value: 720f,
                                    min: 260f,
                                    max: 760f,
                                    onChanged: v =>
                                    {
                                        float w = MathF.Round(v);
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
                    title: "Breakpoint Bin",
                    description:
                    "Breakpoints are listed narrowest-first and the LAST match wins, so the list " +
                    "reads top-to-bottom like a stylesheet.",
                    child: new Align(
                        alignment: Alignment.TopCenter,
                        child: Stage(box: _binStage, content: Bin(pane: Pane, p: p))
                    ) {
                        HeightFactor = 1f,
                    }
                ),
                Demo.Titled(
                    title: "Multi-Layout View",
                    description:
                    "The same entry moves between arrangements; its text and caret survive.",
                    child: new Align(
                        alignment: Alignment.TopCenter,
                        child: Stage(
                            box: _layoutStage,
                            content: MultiLayout(shared: shared, pane: Pane, p: p)
                        )
                    ) {
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
                    new SizedBox(width: 200f, child: pane(arg1: "Sidebar", arg2: p.SidebarBg)),
                    new Expanded(pane(arg1: "Content — wide", arg2: p.ViewBg)),
                },
            }
        );
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) {
                Child = pane(arg1: "Content — folded", arg2: p.ViewBg),
            }
        );
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(360f)) {
                Child = pane(arg1: "Content — narrow", arg2: p.CardBg),
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
                    name: "wide",
                    content: new Row(spacing: Spacing.Md) {
                        Children = {
                            new SizedBox(
                                width: 180f,
                                child: pane(arg1: "Sidebar", arg2: p.SidebarBg)
                            ),
                            new Expanded(new Center(new AdwLayoutSlot("entry"))),
                        },
                    }
                ),
                new AdwLayout(
                    name: "narrow",
                    content: new Column(
                        spacing: Spacing.Md,
                        crossAxisAlignment: CrossAxisAlignment.Stretch
                    ) {
                        Children = {
                            new SizedBox(
                                height: 40f,
                                child: pane(arg1: "Sidebar", arg2: p.SidebarBg)
                            ),
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
