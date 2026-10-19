// height: 180
class Demo : ComposedWidget
{
    private static readonly string[] Views = ["Day", "Week", "Month", "Year"];
    private readonly Signal<int> _view = new(1);

    // One choice among a few: the GNOME segmented control.
    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
            new AdwToggleGroup(labels: Views, active: 1, onActive: i => _view.Value = i),
            new Watch(() => new Label($"Showing the {Views[_view.Value].ToLowerInvariant()} view", fontSize: 15)),
        ]));
}
