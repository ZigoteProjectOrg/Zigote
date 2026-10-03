using Zigote.Core.Animation;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwStatusPage — a centered placeholder page: a large dim icon, a title, an optional
///     description and an optional child (typically a pill suggested-action button). Used for
///     empty states, errors, and welcome screens. <see cref="Compact" /> shrinks it for
///     embedding inside cards and sidebars.
///     <para>
///         The content fades up into place the first time it appears (<see cref="Animated" />).
///         A status page arrives as the answer to "there is nothing here" — an entrance keeps that
///         from reading as a rendering glitch. It plays once per mount, never on a rebuild, and
///         nothing about the layout depends on it: the slot is the settled size throughout.
///     </para>
/// </summary>
public sealed class AdwStatusPage : ComposedWidget
{
    // Retained, not rebuilt: a fresh Animate on every property change would re-mount and replay
    // the entrance (see ComposedWidget.EnsureBuilt on why roots are kept).
    private readonly Animate _entrance = new Animate()
        .Fade(duration: 260.ms, curve: Curves.EaseOutCubic)
        .Move(begin: new Offset(x: 0f, y: 10f), end: Offset.Zero);

    private bool _animated = true;
    private Widget? _child;
    private bool _compact;
    private string? _description;
    private string? _iconName;
    private string _title = "";

    /// <summary>
    ///     Icon glyph (a <see cref="MaterialIcons" /> / <see cref="Icons" /> constant), or null for
    ///     none.
    /// </summary>
    public string? IconName
    {
        get => _iconName;
        set => this.Set(field: ref _iconName, value: value);
    }

    /// <summary>Fade the content up on first appearance. Off for a page that must not move.</summary>
    public bool Animated
    {
        get => _animated;
        set => this.Set(field: ref _animated, value: value);
    }

    public string Title
    {
        get => _title;
        set => this.Set(field: ref _title, value: value);
    }

    public string? Description
    {
        get => _description;
        set => this.Set(field: ref _description, value: value);
    }

    /// <summary>Optional widget under the description — typically a pill suggested AdwButton.</summary>
    public Widget? Child
    {
        get => _child;
        set => this.Set(field: ref _child, value: value);
    }

    /// <summary>Smaller icon and title with tighter spacing, for embedding in panes.</summary>
    public bool Compact
    {
        get => _compact;
        set => this.Set(field: ref _compact, value: value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        // `statuspage > … > clamp > box { border-spacing: 12px }` around a big dimmed icon:
        // 128px normally, 96px compact, each with its own bottom margin.
        var col = new Column(
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center,
            spacing: Spacing.Md
        );

        if (!string.IsNullOrEmpty(IconName))
        {
            col.Children.Add(
                new IconGlyph(
                    glyph: IconName!,
                    size: Compact ? 96f : 128f,
                    color: AdwPalette.Fill(theme: theme, percent: AdwStyle.DimOpacity)
                )
            );
            col.Children.Add(new SizedBox(height: Compact ? Spacing.Md : Spacing.Xxl));
        }

        col.Children.Add(
            new Label(
                text: Title,
                style: Compact ? AdwTypography.Title2 : AdwTypography.Title1,
                color: theme.OnBackground
            ) {
                Align = TextAlign.Center,
            }
        );

        if (!string.IsNullOrEmpty(Description))
        {
            col.Children.Add(
                new Label(
                    text: Description!,
                    style: AdwTypography.Body,
                    color: theme.TextSecondary
                ) {
                    Align = TextAlign.Center,
                }
            );
        }

        if (Child is not null) col.Children.Add(Child);

        Widget content = col;
        if (Animated)
        {
            // Feed the retained Animate rather than wrapping a new one, so a title change updates
            // the content without restarting the entrance.
            _entrance.Child = col;
            content = _entrance;
        }

        // AdwClamp top-aligns; use its ConstrainedBox core directly so the content is
        // vertically centered too, like the GNOME status page.
        return new Center {
            Child = new ConstrainedBox(
                constraints: new Constraints(
                    minWidth: 0f,
                    maxWidth: 420f,
                    minHeight: 0f,
                    maxHeight: float.PositiveInfinity
                ),
                // `> box { margin: 36px 12px }`, 24px on the compact variant.
                child: new Padding(
                    padding: EdgeInsets.Symmetric(
                        horizontal: AdwMetrics.PageMarginX,
                        vertical: Compact ? Spacing.Xxl : 36f
                    ),
                    child: content
                )
            ),
        };
    }
}
