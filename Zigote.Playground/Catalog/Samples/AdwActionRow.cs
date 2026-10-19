// height: 260
class Demo : ComposedWidget
{
    private readonly Signal<string> _opened = new("");

    // A row with a title, a subtitle and any widgets at its end (suffixes).
    protected override Widget Build(BuildContext context) =>
        new AdwClamp(child: new Padding(EdgeInsets.All(20), new Column(spacing: 12, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwPreferencesGroup(title: "Storage") {
                Rows = {
                    new AdwActionRow("Downloads") { Subtitle = "12.4 GB", Suffixes = { new AdwButton(label: "Open", onPressed: () => _opened.Value = "Downloads") { Style = AdwButtonStyle.Flat } } },
                    new AdwActionRow("Trash") { Subtitle = "843 MB", Suffixes = { new AdwButton(label: "Empty", onPressed: () => _opened.Value = "Trash emptied") { Style = AdwButtonStyle.Destructive } } },
                },
            },
            new Watch(() => new Label(_opened.Value, fontSize: 13)),
        ])));
}
