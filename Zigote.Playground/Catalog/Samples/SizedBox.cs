// height: 200
class Demo : ComposedWidget
{
    private static Widget Box(uint argb) => new DecoratedBox { Fill = new Color(argb), Radius = 8 };

    protected override Widget Build(BuildContext context) =>
        new Center(new Row(mainAxisSize: MainAxisSize.Min, children: [
            new SizedBox(width: 80, height: 80, child: Box(0xFF3584E4)),   // a fixed size for its child
            new SizedBox(width: 24),                                       // an empty gap
            new SizedBox(width: 140, height: 40, child: Box(0xFF2EC27E)),
            new SizedBox(width: 24),
            SizedBox.Shrink(),                                             // nothing at all: 0×0
            new SizedBox(width: 40, height: 120, child: Box(0xFFE66100)),
        ]));
}
