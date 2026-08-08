namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwToolbarView — top bars, content, bottom bars in a column. Raised bars get the headerbar
///     background plus a hairline toward the content; flat bars are transparent over the content
///     background.
/// </summary>
public sealed class AdwToolbarView : StatelessWidget
{
    private Widget? _content;
    private bool _raisedTopBar;
    private bool _raisedBottomBar;

    public AdwToolbarView(Widget? content = null)
    {
        _content = content;
    }

    /// <summary>Bars above the content (header bars, tab bars…). Populate before mounting.</summary>
    public List<Widget> TopBars { get; init; } = [];

    /// <summary>Bars below the content (action bars…). Populate before mounting.</summary>
    public List<Widget> BottomBars { get; init; } = [];

    public Widget? Content
    {
        get => _content;
        set => this.Set(ref _content, value);
    }

    public bool RaisedTopBar
    {
        get => _raisedTopBar;
        set => this.Set(ref _raisedTopBar, value);
    }

    public bool RaisedBottomBar
    {
        get => _raisedBottomBar;
        set => this.Set(ref _raisedBottomBar, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var col = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);

        foreach (var bar in TopBars) col.Children.Add(Wrap(bar, RaisedTopBar, theme));
        if (RaisedTopBar && TopBars.Count > 0) col.Children.Add(Hairline(p));

        col.Children.Add(new Expanded(Content ?? new SizedBox()));

        if (RaisedBottomBar && BottomBars.Count > 0) col.Children.Add(Hairline(p));
        foreach (var bar in BottomBars) col.Children.Add(Wrap(bar, RaisedBottomBar, theme));

        return col;
    }

    private static Widget Wrap(Widget bar, bool raised, ThemeData theme)
    {
        return raised
            ? new DecoratedBox {
                Fill = theme.TitleBar,
                Child = bar,
            }
            : bar;
    }

    private static Widget Hairline(AdwColors p)
    {
        return new Container {
            Height = 1f,
            Background = p.HeaderbarShade,
        };
    }
}