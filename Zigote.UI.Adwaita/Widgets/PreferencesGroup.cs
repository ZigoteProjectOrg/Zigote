namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwPreferencesGroup — an optional header (title, dim description, right-aligned suffix) above
///     a boxed list: a CardBg card at radius 12 whose rows are separated by 1px CardShade hairlines.
/// </summary>
public sealed class AdwPreferencesGroup : StatelessWidget
{
    private string? _title;
    private string? _description;
    private Widget? _headerSuffix;

    public AdwPreferencesGroup(string? title = null, string? description = null)
    {
        _title = title;
        _description = description;
    }

    public string? Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public string? Description
    {
        get => _description;
        set => this.Set(ref _description, value);
    }

    /// <summary>The boxed-list rows. Populate before mounting.</summary>
    public List<Widget> Rows { get; init; } = [];

    /// <summary>Widget shown at the end of the header line (e.g. an add button).</summary>
    public Widget? HeaderSuffix
    {
        get => _headerSuffix;
        set => this.Set(ref _headerSuffix, value);
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

        var hasTitle = !string.IsNullOrEmpty(Title);
        var hasDescription = !string.IsNullOrEmpty(Description);
        if (hasTitle || hasDescription || HeaderSuffix is not null)
        {
            var text = new Column(
                crossAxisAlignment: CrossAxisAlignment.Start,
                mainAxisSize: MainAxisSize.Min,
                spacing: Spacing.Xxs
            );
            if (hasTitle)
                text.Children.Add(new Label(Title!, AdwTypography.Heading, theme.OnBackground));
            if (hasDescription)
                text.Children.Add(
                    new Label(Description!, AdwTypography.Caption, p.DimLabel) { MaxLines = 3 }
                );

            var header = new Row(crossAxisAlignment: CrossAxisAlignment.End) {
                Children = { new Expanded(text) },
            };
            if (HeaderSuffix is not null)
            {
                header.Children.Add(new SizedBox(Spacing.Sm));
                header.Children.Add(HeaderSuffix);
            }

            outer.Children.Add(header);
        }

        var list = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        for (var i = 0; i < Rows.Count; i++)
        {
            if (i > 0)
                list.Children.Add(
                    new Container {
                        Height = 1f,
                        Background = p.CardShade,
                    }
                );
            list.Children.Add(Rows[i]);
        }

        outer.Children.Add(
            new DecoratedBox {
                Fill = p.CardBg,
                // Adwaita outlines the boxed list: without it a white card on the near-white light
                // window background has no edge at all.
                BorderColor = p.CardShade,
                Radius = AdwMetrics.CardRadius,
                Child = new ClipRRect(AdwMetrics.CardRadius, list),
            }
        );
        return outer;
    }
}