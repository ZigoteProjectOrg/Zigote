// height: 200
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new Row(children: [
            // Fills its space and centres the child in it...
            new Expanded(new DecoratedBox { Fill = new Color(0x223584E4), Child = new Center(new Label("Center()", fontSize: 15)) }),
            // ...or, with a factor, sizes itself to a multiple of the child.
            new Expanded(new Center(new DecoratedBox { Fill = new Color(0x22E66100),
                Child = new Center(new Label("heightFactor: 3", fontSize: 15), heightFactor: 3) })),
        ]);
}
