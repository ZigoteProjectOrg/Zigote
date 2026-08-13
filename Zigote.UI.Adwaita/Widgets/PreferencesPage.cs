namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwPreferencesPage — a scrollable, clamped (600px) column of
///     <see cref="AdwPreferencesGroup" />s. <see cref="Title" /> and <see cref="IconName" /> are the
///     page's identity for view switchers; the page itself renders only the groups.
/// </summary>
public sealed class AdwPreferencesPage : ComposedWidget
{
    // init, not set: the switchers read these once when they build their toggle, so a later
    // assignment would silently never show up.
    public string Title { get; init; } = "";
    public string? IconName { get; init; }

    /// <summary>The preference groups, top to bottom. Populate before mounting.</summary>
    public List<Widget> Groups { get; init; } = [];

    protected override Widget Build(BuildContext context)
    {
        // `preferencespage > … > box { margin: 24px 12px; border-spacing: 24px }`.
        var column = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min,
            spacing: AdwMetrics.PageSpacing
        );
        foreach (var group in Groups) column.Children.Add(group);

        return new SingleChildScrollView {
            Child = new AdwClamp(
                child: new Padding(
                    padding: EdgeInsets.Symmetric(
                        horizontal: AdwMetrics.PageMarginX,
                        vertical: AdwMetrics.PageMarginY
                    ),
                    child: column
                ),
                maximumSize: AdwMetrics.ClampWidth
            ),
        };
    }
}
