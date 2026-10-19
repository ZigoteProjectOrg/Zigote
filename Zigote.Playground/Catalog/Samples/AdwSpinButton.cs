// height: 160
class Demo : ComposedWidget
{
    private readonly Signal<double> _copies = new(2);

    // Step buttons plus typed input, clamped to [min, max].
    protected override Widget Build(BuildContext context) =>
        new Center(new Row(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new Label("Copies", fontSize: 15),
            new AdwSpinButton(value: 2, min: 1, max: 99, step: 1, onChanged: v => _copies.Value = v),
            new Watch(() => new Label($"→ {_copies.Value:0} page sets", fontSize: 13)),
        ]));
}
