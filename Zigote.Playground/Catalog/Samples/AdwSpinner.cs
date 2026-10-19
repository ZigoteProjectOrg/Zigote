// height: 180
class Demo : ComposedWidget
{
    private readonly Signal<bool> _busy = new(true);

    // An indeterminate busy indicator. Show it only while work is really happening.
    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 14, children: [
            new SizedBox(height: 40, child: new Watch(() => _busy.Value ? new AdwSpinner() : new Label("Done", fontSize: 16))),
            new AdwButton(label: "Toggle", onPressed: () => _busy.Value = !_busy.Value) { Pill = true },
        ]));
}
