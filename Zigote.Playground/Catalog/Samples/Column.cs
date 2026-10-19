// height: 260
static class Swatch
{
    public static Widget Of(uint argb, float w) => new DecoratedBox { Fill = new Color(argb), Radius = 8, Child = new SizedBox(width: w, height: 36) };
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new Row(spacing: 24, mainAxisAlignment: MainAxisAlignment.Center, children: [
            // Children stacked top to bottom, centred on the cross axis.
            new Column(spacing: 8, mainAxisAlignment: MainAxisAlignment.Center, children: [
                Swatch.Of(0xFF3584E4, 120), Swatch.Of(0xFF3584E4, 80), Swatch.Of(0xFF3584E4, 160),
            ]),
            // Stretch makes every child as wide as the column.
            new SizedBox(width: 140, child: new Column(spacing: 8, mainAxisAlignment: MainAxisAlignment.Center, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
                Swatch.Of(0xFF2EC27E, 0), Swatch.Of(0xFF2EC27E, 0), Swatch.Of(0xFF2EC27E, 0),
            ])),
            // SpaceBetween pushes the first and last child to the ends.
            new SizedBox(height: 220, child: new Column(mainAxisAlignment: MainAxisAlignment.SpaceBetween, children: [
                Swatch.Of(0xFFE66100, 60), Swatch.Of(0xFFE66100, 60), Swatch.Of(0xFFE66100, 60),
            ])),
        ]);
}
