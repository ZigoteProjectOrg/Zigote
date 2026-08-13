namespace AdwaitaGallery;

/// <summary>
///     The scaffold every gallery page is built on — the shape of a GNOME preferences page: a
///     scrolling, clamped column under a hero (dim icon, title, one-line description), with the
///     standard page padding and group spacing. Pages therefore describe only their content, and
///     every page shares one rhythm.
/// </summary>
internal sealed class GalleryPage : ComposedWidget
{
    public GalleryPage(string title, string description, string iconName = "")
    {
        Title = title;
        Description = description;
        IconName = iconName;
    }

    public string Title { get; set; }
    public string Description { get; set; }
    public string IconName { get; set; }

    /// <summary>The page body — <see cref="Demo.Group" />s, stages, whatever the page shows.</summary>
    public List<Widget> Children { get; set; } = [];

    /// <summary>Content width cap. The Adwaita 600 by default; widen for pages with big stages.</summary>
    public float ClampWidth { get; set; } = AdwMetrics.ClampWidth;

    /// <summary>Off for a page that opens straight onto its content (the title is in the header).</summary>
    public bool ShowHero { get; set; } = true;

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        var column = new Column(
            spacing: Spacing.Xxl,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        if (ShowHero) column.Children.Add(Hero(theme));
        foreach (var child in Children) column.Children.Add(child);

        return new SingleChildScrollView {
            Child = new AdwClamp(
                new Padding(
                    EdgeInsets.Only(
                        Spacing.Lg,
                        Spacing.Xxl,
                        Spacing.Lg,
                        Spacing.Xxxl
                    ),
                    column
                ),
                ClampWidth
            ),
        };
    }

    private Widget Hero(ThemeData theme)
    {
        var column = new Column(
            spacing: Spacing.Xs,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );
        if (IconName.Length > 0)
        {
            column.Children.Add(new IconGlyph(IconName, 72f, theme.Label3));
            column.Children.Add(new SizedBox(height: Spacing.Sm));
        }

        // No title here: the content header bar above already names the page, and two titles from
        // two sources (the registry's and the page's own) drift apart.
        column.Children.Add(
            new Label(Description, AdwTypography.Body, theme.TextSecondary) {
                Align = TextAlign.Center,
            }
        );
        return column;
    }
}

/// <summary>
///     The gallery's shared page furniture: boxed-list groups, framed stages for live widgets, and
///     the captions and chips the samples are annotated with. Each helper is its own widget so it
///     resolves — and re-resolves — the theme through its own build, which is what makes an accent
///     or light/dark switch repaint the whole gallery without anything rebuilding by hand.
/// </summary>
internal static class Demo
{
    /// <summary>
    ///     A titled boxed list — the GNOME preferences group. The description is not optional in the
    ///     signature (pass null): with a params tail, an omitted string? and a Widget are ambiguous.
    /// </summary>
    public static AdwPreferencesGroup Group(string title, string? description,
        params Widget[] rows)
    {
        var group = new AdwPreferencesGroup(title, description);
        foreach (var row in rows) group.Rows.Add(row);
        return group;
    }

    /// <summary>A framed stage for live widgets — the card a specimen sits on.</summary>
    public static Widget Stage(Widget child, float padding = Spacing.Xl)
    {
        return new DemoStage(child) { Padding = padding };
    }

    /// <summary>
    ///     The common stage: a centered column of a control and the chips and captions that annotate
    ///     it. The gallery's default specimen envelope — reach for <see cref="Stage" /> only when the
    ///     content is not a column.
    /// </summary>
    public static Widget Specimen(params Widget[] children)
    {
        var column = new Column(
            spacing: Spacing.Lg,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );
        foreach (var child in children) column.Children.Add(child);
        return Stage(column);
    }

    /// <summary>
    ///     Float a demo window: the flat header bar with just a title and a close button — no window
    ///     controls, since a dialog has none — over the content in an AdwToolbarView, exactly the
    ///     shape libadwaita's own demo opens its samples in. <paramref name="raised" /> gives the
    ///     header the titlebar background and a hairline (for content that scrolls under it);
    ///     <paramref name="headerStart" /> packs one widget at the start of the bar.
    /// </summary>
    public static void ShowDialog(string title, Widget content, float width, float height,
        bool raised = false, Widget? headerStart = null)
    {
        AdwDialog? dialog = null;
        var header = new AdwHeaderBar {
            Flat = true,
            Title = title,
            ShowStartWindowControls = false,
            ShowEndWindowControls = false,
        };
        if (headerStart is not null) header.Start.Add(headerStart);
        header.End.Add(IconButton(MaterialIcons.Close, () => dialog?.Close()));

        dialog = new AdwDialog(
            new AdwToolbarView(content) {
                TopBars = { header },
                RaisedTopBar = raised,
            }
        ) {
            ContentWidth = width,
            ContentHeight = height,
        };
        dialog.Show();
    }

    /// <summary>A stage under a group-style caption: a titled specimen without a boxed list.</summary>
    public static Widget Titled(string title, string? description, Widget child)
    {
        return new DemoTitled(title, description, child);
    }

    /// <summary>Centered controls that wrap onto more rows when the page is narrow.</summary>
    public static Widget Bar(params Widget[] children)
    {
        var wrap = new Wrap(spacing: Spacing.Sm, runSpacing: Spacing.Sm);
        foreach (var child in children) wrap.Children.Add(child);
        return new Align(Alignment.Center, wrap) { HeightFactor = 1f };
    }

    /// <summary>A dim caption — the "what you are looking at" line under a specimen.</summary>
    public static Widget Caption(string text)
    {
        return new DemoCaption(text);
    }

    /// <summary>A monospace chip for live state — readable at a glance next to a control.</summary>
    public static Widget Value(string text)
    {
        return new DemoValue(text);
    }

    /// <summary>A flat circular toolbar button.</summary>
    public static AdwButton IconButton(string icon, Action onPressed, bool suggested = false)
    {
        return new AdwButton(onPressed: onPressed) {
            IconName = icon,
            Style = suggested ? AdwButtonStyle.Suggested : AdwButtonStyle.Flat,
            Circular = true,
        };
    }
}

internal sealed class DemoStage(Widget child) : ComposedWidget
{
    public float Padding { get; set; } = Spacing.Xl;

    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        return new DecoratedBox {
            Fill = p.CardBg,
            Radius = AdwMetrics.CardRadius,
            BorderColor = p.CardShade,
            BorderWidth = 1f,
            Child = new Padding(
                EdgeInsets.All(Padding),
                new Align(Alignment.Center, child) { HeightFactor = 1f }
            ),
        };
    }
}

internal sealed class DemoTitled(string title, string? description, Widget child) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var column = new Column(
            spacing: Spacing.Xs,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new Padding(
                    EdgeInsets.Only(Spacing.Xs),
                    new Label(title, AdwTypography.Heading, theme.OnBackground)
                ),
            },
        };
        if (description is { } text)
            column.Children.Add(
                new Padding(
                    EdgeInsets.Only(
                        Spacing.Xs,
                        0f,
                        Spacing.Xs,
                        Spacing.Xxs
                    ),
                    new Label(text, AdwTypography.Caption, theme.TextSecondary)
                )
            );
        column.Children.Add(child);
        return column;
    }
}

internal sealed class DemoCaption(string text) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        return new Label(
            text,
            AdwTypography.Caption,
            ThemeProvider.Of(context).TextSecondary
        ) { Align = TextAlign.Center };
    }
}

internal sealed class DemoValue(string text) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new DecoratedBox {
            Fill = theme.Fill2,
            Radius = Radii.Md,
            Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Sm, Spacing.Xxs),
                new Label(text, AdwTypography.Monospace, theme.OnBackground)
            ),
        };
    }
}
