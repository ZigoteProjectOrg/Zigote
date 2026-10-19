// height: 200
class Demo : ComposedWidget
{
    private readonly Signal<string> _name = new("");

    protected override Widget Build(BuildContext context) =>
        new Center(new SizedBox(width: 320, child: new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwEntry { Placeholder = "Your name", OnChanged = t => _name.Value = t, OnSubmitted = t => _name.Value = $"{t} (submitted)" },
            new Watch(() => new Label(_name.Value.Length == 0 ? "Type, then press Enter" : $"Hello, {_name.Value}!", fontSize: 15)),
        ])));
}
