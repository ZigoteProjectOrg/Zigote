// height: 220
class Demo : ComposedWidget
{
    private static readonly string[] Formats = ["PDF", "PNG", "SVG"];
    private readonly Signal<string> _last = new("");

    // A main action plus a menu of variants.
    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new AdwSplitButton(label: "Export", onPressed: () => _last.Value = "Exported as PDF") {
                MenuItems = Formats,
                OnMenuSelected = i => _last.Value = $"Exported as {Formats[i]}",
            },
            new Watch(() => new Label(_last.Value, fontSize: 13)),
        ]));
}
