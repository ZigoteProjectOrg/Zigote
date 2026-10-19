// height: 200
class Demo : ComposedWidget
{
    // Container bundles colour, padding, margin, size and alignment — Flutter's convenience box.
    protected override Widget Build(BuildContext context) =>
        new Center(new Container(
            color: new Color(0x223584E4),
            padding: EdgeInsets.All(12),
            child: new Container(
                width: 220, height: 100,
                color: new Color(0xFF3584E4),
                alignment: Alignment.BottomRight,
                child: new Padding(EdgeInsets.All(8), new Label("bottom-right", fontSize: 13, color: new Color(0xFFFFFFFF))))));
}
