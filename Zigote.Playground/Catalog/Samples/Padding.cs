// height: 220
class Demo : ComposedWidget
{
    private static Widget Framed(EdgeInsets insets, string what)
    {
        // The outer box shows the padding; the inner one is the child.
        return new DecoratedBox { Fill = new Color(0x333584E4), Radius = 8,
            Child = new Padding(insets, new DecoratedBox { Fill = new Color(0xFF3584E4), Radius = 6,
                Child = new SizedBox(width: 120, height: 36, child: new Center(new Label(what, fontSize: 12, color: new Color(0xFFFFFFFF)))) }) };
    }

    protected override Widget Build(BuildContext context) =>
        new Center(new Wrap(spacing: 16, runSpacing: 16, children: [
            Framed(EdgeInsets.All(16), "All(16)"),
            Framed(EdgeInsets.Symmetric(horizontal: 32, vertical: 4), "Symmetric(32, 4)"),
            Framed(EdgeInsets.Only(left: 40, top: 8), "Only(left: 40, top: 8)"),
        ]));
}
