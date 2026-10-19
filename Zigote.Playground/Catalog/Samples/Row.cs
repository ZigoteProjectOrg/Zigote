// height: 180
class Demo : ComposedWidget
{
    private static Widget Box(string label, uint argb) =>
        new DecoratedBox { Fill = new Color(argb), Radius = 8,
            Child = new SizedBox(height: 48, child: new Center(new Label(label, fontSize: 13, color: new Color(0xFFFFFFFF)))) };

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(16), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            // Fixed, flexible 1 and flexible 2: Expanded children share what is left over.
            new Row(spacing: 8, children: [
                new SizedBox(width: 90, child: Box("90 pt", 0xFF3584E4)),
                new Expanded(Box("flex 1", 0xFF2EC27E)),
                new Expanded(Box("flex 2", 0xFFE66100), flex: 2),
            ]),
            new Row(mainAxisAlignment: MainAxisAlignment.SpaceEvenly, children: [
                new SizedBox(width: 70, child: Box("A", 0xFF9141AC)),
                new SizedBox(width: 70, child: Box("B", 0xFF9141AC)),
                new SizedBox(width: 70, child: Box("C", 0xFF9141AC)),
            ]),
        ]));
}
