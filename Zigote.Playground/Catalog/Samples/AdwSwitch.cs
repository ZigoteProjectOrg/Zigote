// height: 160
class Demo : ComposedWidget
{
    private readonly Signal<bool> _on = new(true);

    protected override Widget Build(BuildContext context) =>
        new Center(new Row(mainAxisSize: MainAxisSize.Min, spacing: 14, children: [
            new AdwSwitch(value: true, onChanged: v => _on.Value = v),
            new Watch(() => new Label(_on.Value ? "Wi-Fi is on" : "Wi-Fi is off", fontSize: 16)),
        ]));
}
