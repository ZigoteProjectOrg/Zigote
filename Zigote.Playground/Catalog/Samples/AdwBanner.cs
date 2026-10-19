// height: 200
class Demo : ComposedWidget
{
    private AdwBanner? _banner;

    // A strip at the top of a view for a persistent state, with at most one action.
    protected override Widget Build(BuildContext context)
    {
        _banner ??= new AdwBanner(title: "You are offline — changes will sync later", buttonLabel: "Retry",
            onButtonClicked: () => _banner!.Revealed = false) { Revealed = true };
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            _banner,
            new Expanded(new Center(new AdwButton(label: "Show banner again", onPressed: () => _banner.Revealed = true))),
        ]);
    }
}
