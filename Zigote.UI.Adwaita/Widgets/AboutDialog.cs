namespace Zigote.UI.Adwaita;

/// <summary>
///     One row of the about dialog's links list — the website, the issue tracker, the donation page.
///     The dialog does not open anything itself: what a URL means is the app's business, and an app
///     that opens a browser from inside a widget has shipped a browser.
/// </summary>
public sealed record AdwAboutLink(
    string Label,
    string? Subtitle,
    Action OnActivated,
    string? IconName = null);

/// <summary>
///     AdwAboutDialog — the libadwaita about window: a fixed 360px scrollable sheet with the app
///     icon, name, developer, a version pill badge, a boxed-list website row, an optional comments
///     block and the legal (copyright + license) section.
/// </summary>
public sealed class AdwAboutDialog : AdwDialog
{
    public AdwAboutDialog()
    {
        ContentWidth = 360f;
        ContentHeight = 540f;
        Child = new Content(this);
    }

    // init, not set: the content sheet is built once, in the constructor, so a post-construction
    // assignment would never reach the screen. An about dialog is configured then shown.
    public string AppName { get; init; } = "";
    public string? DeveloperName { get; init; }
    public string? Version { get; init; }

    /// <summary>App icon glyph (a <see cref="MaterialIcons" /> constant), drawn at 96px.</summary>
    public string? IconName { get; init; }

    public string? Website { get; init; }

    // Invoked from the row's closure, so this one stays live and settable.
    public Action? OnWebsite { get; set; }
    public string? Comments { get; init; }
    public string? Copyright { get; init; }
    public string? License { get; init; }

    /// <summary>Everywhere else the app lives — issue tracker, translations, source. Shown as a
    ///     boxed list under the website, which is where the HIG puts them.</summary>
    public List<AdwAboutLink> Links { get; init; } = [];

    private sealed class Content(AdwAboutDialog owner) : ComposedWidget
    {
        protected override Widget Build(BuildContext context)
        {
            var theme = ThemeProvider.Of(context);
            var p = AdwPalette.For(theme);

            var col = new Column(
                spacing: Spacing.Md,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            );

            // ── Hero: icon, name, developer, version pill ────────────────────────
            // `dialog.about image.large-icons { -gtk-icon-size: 128px }`.
            if (owner.IconName is { } icon)
                col.Children.Add(
                    new Center { Child = new IconGlyph(icon, 128f, theme.OnBackground) }
                );
            col.Children.Add(
                new Label(owner.AppName, AdwTypography.Title2, theme.OnBackground) {
                    Align = TextAlign.Center,
                }
            );
            if (owner.DeveloperName is { } dev)
                col.Children.Add(
                    new Label(dev, AdwTypography.Caption, p.DimLabel) { Align = TextAlign.Center }
                );
            if (owner.Version is { } version)
                col.Children.Add(
                    new Center {
                        Child = new DecoratedBox {
                            Fill = theme.SelectionTint,
                            Radius = AdwMetrics.Pill,
                            // `.app-version { padding: 3px 18px; border-radius: 999px }`.
                            Child = new Padding(
                                EdgeInsets.Symmetric(18f, 3f),
                                new Label(version, AdwTypography.CaptionHeading, theme.PrimaryDark)
                            ),
                        },
                    }
                );

            // ── Boxed-list rows ──────────────────────────────────────────────────
            AdwActionRow Link(string label, string? subtitle, string? icon, Action onActivated)
            {
                var row = new AdwActionRow(label, subtitle) {
                    IconName = icon,
                    OnActivated = onActivated,
                };
                row.Suffixes.Add(
                    new IconGlyph(Icons.ChevronRight, AdwMetrics.IconSize, p.DimLabel)
                );
                return row;
            }

            var links = new AdwPreferencesGroup();
            if (owner.Website is { } site)
                links.Rows.Add(
                    Link(
                        "Website",
                        site,
                        MaterialIcons.Public,
                        () => owner.OnWebsite?.Invoke()
                    )
                );
            foreach (var link in owner.Links)
                links.Rows.Add(
                    Link(
                        link.Label,
                        link.Subtitle,
                        link.IconName,
                        link.OnActivated
                    )
                );

            if (links.Rows.Count > 0)
                col.Children.Add(new Padding(EdgeInsets.Only(top: Spacing.Md), links));

            if (owner.Comments is { } comments)
                col.Children.Add(
                    new Padding(
                        EdgeInsets.Only(top: Spacing.Md),
                        new Label(comments, AdwTypography.Body, theme.OnBackground) {
                            Align = TextAlign.Center,
                        }
                    )
                );

            // ── Legal ────────────────────────────────────────────────────────────
            if (owner.Copyright is { } copyright)
                col.Children.Add(
                    new Padding(
                        EdgeInsets.Only(top: Spacing.Md),
                        new Label(copyright, AdwTypography.Caption, p.DimLabel) {
                            Align = TextAlign.Center,
                        }
                    )
                );
            if (owner.License is { } license)
                col.Children.Add(
                    new Label(license, AdwTypography.Caption, p.DimLabel) {
                        Align = TextAlign.Center,
                    }
                );

            return new SingleChildScrollView(
                col,
                padding: EdgeInsets.All(Spacing.Xxl)
            );
        }
    }
}
