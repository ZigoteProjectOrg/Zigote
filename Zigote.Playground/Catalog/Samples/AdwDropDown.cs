// height: 220
class Demo : ComposedWidget
{
    private static readonly string[] Sizes = ["Small", "Medium", "Large", "Extra large"];
    private readonly Signal<int> _size = new(1);

    protected override Widget Build(BuildContext context) =>
        new Center(new Row(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new Label("Text size", fontSize: 15),
            new AdwDropDown(Sizes, selectedIndex: 1, onSelected: i => _size.Value = i),
            new Watch(() => new Label("Aa", fontSize: 14 + _size.Value * 6)),
        ]));
}
