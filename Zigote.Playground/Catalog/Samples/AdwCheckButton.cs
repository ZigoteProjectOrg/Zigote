// height: 200
class Demo : ComposedWidget
{
    private readonly Signal<int> _checked = new(1);

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 8, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new AdwCheckButton(label: "Send crash reports", value: true, onChanged: v => _checked.Value += v ? 1 : -1),
            new AdwCheckButton(label: "Check for updates", onChanged: v => _checked.Value += v ? 1 : -1),
            new AdwCheckButton(label: "Remember window size", onChanged: v => _checked.Value += v ? 1 : -1),
            new Watch(() => new Label($"{_checked.Value} of 3 enabled", fontSize: 13)),
        ]));
}
