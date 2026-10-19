// height: 240
class Demo : ComposedWidget
{
    private readonly Signal<string> _summary = new("");

    // An entry inside a boxed list: the title floats above the text once you type.
    protected override Widget Build(BuildContext context) =>
        new AdwClamp(child: new Padding(EdgeInsets.All(20), new Column(spacing: 12, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwPreferencesGroup(title: "Profile") {
                Rows = {
                    new AdwEntryRow(title: "Full name", onChanged: t => _summary.Value = t),
                    new AdwEntryRow(title: "Website", text: "https://"),
                },
            },
            new Watch(() => new Label(_summary.Value.Length == 0 ? "" : $"Signed as {_summary.Value}", fontSize: 13)),
        ])));
}
