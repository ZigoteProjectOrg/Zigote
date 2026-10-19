// height: 300
class Demo : ComposedWidget
{
    private AdwNavigationView? _nav;

    // A push/pop page stack whose header follows the top page and grows a back button.
    protected override Widget Build(BuildContext context)
    {
        if (_nav is not null) return _nav;
        AdwNavigationPage Detail(int n) => new(
            new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
                new Label($"Page {n}", fontSize: 22),
                new AdwButton(label: "Deeper", onPressed: () => _nav!.Push(Detail(n + 1))) { Pill = true },
            ])), title: $"Page {n}");
        // Embedded in a page, not the window's root: no window controls on its header bars.
        _nav = new AdwNavigationView(Detail(1)) { AutoHeaderBar = true, ShowStartWindowControls = false, ShowEndWindowControls = false };
        return _nav;
    }
}
