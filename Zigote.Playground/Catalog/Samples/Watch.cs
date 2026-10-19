// height: 200
class Demo : ComposedWidget
{
    private readonly Signal<int> _count = new(0);
    private readonly Computed<string> _parity;

    public Demo() => _parity = Computed.From(() => _count.Value % 2 == 0 ? "even" : "odd");

    // Only the Watch builders re-run when the signals they read change; the button never rebuilds.
    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new Watch(() => new Label($"{_count.Value}", fontSize: 44)),
            new Watch(() => new Label($"That number is {_parity.Value}", fontSize: 14)),
            new AdwButton(label: "Add one", onPressed: () => _count.Value++) { Style = AdwButtonStyle.Suggested, Pill = true },
        ]));
}
