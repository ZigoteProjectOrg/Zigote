namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwStatusPage — a centered placeholder page: a large dim icon, a title, an optional
///     description and an optional child (typically a pill suggested-action button). Used for
///     empty states, errors, and welcome screens. <see cref="Compact" /> shrinks it for
///     embedding inside cards and sidebars.
/// </summary>
public sealed class AdwStatusPage : ComposedWidget
{
    private string? _iconName;
    private string _title = "";
    private string? _description;
    private Widget? _child;
    private bool _compact;

    /// <summary>Icon glyph (a <see cref="MaterialIcons" /> / <see cref="Icons" /> constant), or null for none.</summary>
    public string? IconName
    {
        get => _iconName;
        set => this.Set(ref _iconName, value);
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public string? Description
    {
        get => _description;
        set => this.Set(ref _description, value);
    }

    /// <summary>Optional widget under the description — typically a pill suggested AdwButton.</summary>
    public Widget? Child
    {
        get => _child;
        set => this.Set(ref _child, value);
    }

    /// <summary>Smaller icon and title with tighter spacing, for embedding in panes.</summary>
    public bool Compact
    {
        get => _compact;
        set => this.Set(ref _compact, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        var col = new Column(
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );

        if (!string.IsNullOrEmpty(IconName))
        {
            col.Children.Add(new IconGlyph(IconName!, Compact ? 48f : 96f, theme.Label3));
            col.Children.Add(new SizedBox(height: Compact ? 8f : 12f));
        }

        col.Children.Add(
            new Label(
                Title,
                Compact ? AdwTypography.Title4 : AdwTypography.Title1,
                theme.OnBackground
            ) {
                Align = TextAlign.Center,
            }
        );

        if (!string.IsNullOrEmpty(Description))
        {
            col.Children.Add(new SizedBox(height: Compact ? 4f : 6f));
            col.Children.Add(
                new Label(Description!, AdwTypography.Body, theme.TextSecondary) {
                    Align = TextAlign.Center,
                }
            );
        }

        if (Child is not null)
        {
            col.Children.Add(new SizedBox(height: Compact ? 16f : 24f));
            col.Children.Add(Child);
        }

        // AdwClamp top-aligns; use its ConstrainedBox core directly so the content is
        // vertically centered too, like the GNOME status page.
        return new Center {
            Child = new ConstrainedBox(
                new Constraints(
                    0f,
                    420f,
                    0f,
                    float.PositiveInfinity
                ),
                new Padding(EdgeInsets.All(Spacing.Md), col)
            ),
        };
    }
}