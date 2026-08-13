namespace AdwaitaGallery.Pages;

/// <summary>
///     Adaptive — the same content answering to the space it is given. The card strip becomes a
///     column, the toolbar sheds its labels, and the readout names the breakpoint you are in. Resize
///     the window (or drag it narrow enough to fold the shell) and watch it move.
/// </summary>
public sealed class AdaptivePage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var media = MediaQuery.Of(context);

        return new GalleryPage(
            title: "Adaptive",
            description:
            "One layout, three widths — decided at measure time, not by a media query file.",
            iconName: MaterialIcons.Devices
        ) {
            ClampWidth = 760f,
            ShowHero = true,
            Children = {
                new LayoutBuilder((_, c) => Readout(width: c.MaxWidth, media: media)),
                Demo.Titled(
                    title: "Reflowing Content",
                    description: "Wide: a row of cards. Narrow: the same cards stacked.",
                    child: new LayoutBuilder((_, c) => Cards(c.MaxWidth < 520f))
                ),
                Demo.Titled(
                    title: "Shedding Detail",
                    description:
                    "The toolbar keeps its labels while there is room and drops to icons when there is not.",
                    child: Demo.Stage(new LayoutBuilder((_, c) => Toolbar(c.MaxWidth < 420f)))
                ),
                Demo.Group(
                    title: "How It Is Done",
                    description: null,
                    new AdwActionRow(
                        title: "LayoutBuilder",
                        subtitle: "Builds from the constraints it is measured with"
                    ) { IconName = MaterialIcons.Straighten },
                    new AdwActionRow(
                        title: "MediaQuery",
                        subtitle: "Window size, scale and safe-area insets"
                    ) {
                        IconName = MaterialIcons.Devices,
                    },
                    new AdwActionRow(
                        title: "AdwNavigationSplitView",
                        subtitle: "Folds its panes below a breakpoint"
                    ) { IconName = MaterialIcons.VerticalSplit }
                ),
            },
        };
    }

    private static Widget Readout(float width, MediaQueryData media)
    {
        string band = width switch {
            < 420f => "compact",
            < 520f => "medium",
            _ => "expanded",
        };
        return Demo.Bar(
            Demo.Value($"content {width:0} px"),
            Demo.Value($"band {band}"),
            Demo.Value($"window {media.Width:0}×{media.Height:0}"),
            Demo.Value($"scale {media.DevicePixelRatio:0.##}×")
        );
    }

    private static Widget Cards(bool stacked)
    {
        var cards = new[] {
            Card(icon: MaterialIcons.Bolt, title: "Fast", body: "Only what changed is measured"),
            Card(
                icon: MaterialIcons.Palette,
                title: "Native",
                body: "The Adwaita palette, not an approximation"
            ),
            Card(
                icon: MaterialIcons.Devices,
                title: "Adaptive",
                body: "The same tree at any width"
            ),
        };

        if (!stacked)
        {
            var row = new Row(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.Stretch);
            foreach (var card in cards) row.Children.Add(new Expanded(card));
            return row;
        }

        var column = new Column(
            spacing: Spacing.Md,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        );
        foreach (var card in cards) column.Children.Add(card);
        return column;
    }

    private static Widget Card(string icon, string title, string body) =>
        new AdaptiveCard(icon: icon, title: title, body: body);

    private static Widget Toolbar(bool iconsOnly)
    {
        var actions = new (string Icon, string Label)[] {
            (MaterialIcons.Add, "New"),
            (MaterialIcons.FolderOpen, "Open"),
            (MaterialIcons.Save, "Save"),
            (MaterialIcons.Share, "Share"),
        };

        var row = new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min);
        foreach ((string icon, string label) in actions)
        {
            row.Children.Add(
                iconsOnly
                    ? new Tooltip(message: label, child: new AdwButton { IconName = icon })
                    : new AdwButton { Content = new AdwButtonContent(iconName: icon, label: label) }
            );
        }

        return row;
    }
}

internal sealed class AdaptiveCard(string icon, string title, string body) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        return new DecoratedBox {
            Fill = p.CardBg,
            Radius = AdwMetrics.CardRadius,
            BorderColor = p.CardShade,
            BorderWidth = 1f,
            Child = new Padding(
                padding: EdgeInsets.All(Spacing.Lg),
                child: new Column(
                    spacing: Spacing.Xs,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Start
                ) {
                    Children = {
                        new IconGlyph(glyph: icon, size: 24f, color: theme.Accent),
                        new SizedBox(height: Spacing.Xs),
                        new Label(
                            text: title,
                            style: AdwTypography.Heading,
                            color: theme.OnBackground
                        ),
                        new Label(
                            text: body,
                            style: AdwTypography.Caption,
                            color: theme.TextSecondary
                        ) { MaxLines = 3 },
                    },
                }
            ),
        };
    }
}
