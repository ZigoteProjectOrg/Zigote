namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwPreferencesGroup — an optional header (title, dim description, right-aligned suffix) above
///     a boxed list: a CardBg card at radius 12 whose rows are separated by 1px CardShade hairlines.
/// </summary>
public sealed class AdwPreferencesGroup : ComposedWidget
{
    private string? _description;
    private Widget? _headerSuffix;
    private string? _title;

    public AdwPreferencesGroup(string? title = null, string? description = null)
    {
        _title = title;
        _description = description;
    }

    public string? Title
    {
        get => _title;
        set => this.Set(field: ref _title, value: value);
    }

    public string? Description
    {
        get => _description;
        set => this.Set(field: ref _description, value: value);
    }

    /// <summary>The boxed-list rows. Populate before mounting.</summary>
    public List<Widget> Rows { get; init; } = [];

    /// <summary>Widget shown at the end of the header line (e.g. an add button).</summary>
    public Widget? HeaderSuffix
    {
        get => _headerSuffix;
        set => this.Set(field: ref _headerSuffix, value: value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var outer = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min,
            spacing: Spacing.Md
        );

        bool hasTitle = !string.IsNullOrEmpty(Title);
        bool hasDescription = !string.IsNullOrEmpty(Description);
        if (hasTitle || hasDescription || HeaderSuffix is not null)
        {
            // `preferencesgroup > box, box.labels { border-spacing: 6px }`.
            var text = new Column(
                crossAxisAlignment: CrossAxisAlignment.Start,
                mainAxisSize: MainAxisSize.Min,
                spacing: AdwMetrics.RowSpacing
            );
            if (hasTitle)
            {
                text.Children.Add(
                    new Label(text: Title!, style: AdwTypography.Heading, color: theme.OnBackground)
                );
            }

            if (hasDescription)
            {
                text.Children.Add(
                    new Label(text: Description!, style: AdwTypography.Caption, color: p.DimLabel) {
                        MaxLines = 3,
                    }
                );
            }

            var header = new Row(crossAxisAlignment: CrossAxisAlignment.End) {
                Children = { new Expanded(text) },
            };
            if (HeaderSuffix is not null)
            {
                header.Children.Add(new SizedBox(AdwMetrics.RowSpacing));
                header.Children.Add(HeaderSuffix);
            }

            outer.Children.Add(header);
        }

        var list = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        for (int i = 0; i < Rows.Count; i++)
        {
            if (i > 0)
            {
                list.Children.Add(
                    new Container {
                        Height = 1f,
                        Background = p.CardShade,
                    }
                );
            }

            list.Children.Add(Rows[i]);
        }

        outer.Children.Add(
            new DecoratedBox {
                Fill = p.CardBg,
                // `%card` is a 1px ring plus two soft shadow layers. The ring is the border here;
                // the lift is CardShadow — without it a white card on the near-white light window
                // background has no edge at all, and the list stops reading as a card.
                BorderColor = p.CardShade,
                Elevation = AdwMetrics.CardShadow,
                Radius = AdwMetrics.CardRadius,
                Child = new ClipRRect(radius: AdwMetrics.CardRadius, child: list),
            }
        );
        return outer;
    }
}
