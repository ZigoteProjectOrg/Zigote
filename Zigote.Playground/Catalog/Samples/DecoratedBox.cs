// height: 220
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        Widget Tile(string label, DecoratedBox box)
        {
            box.Child = new SizedBox(width: 130, height: 90, child: new Center(new Label(label, fontSize: 13)));
            return box;
        }

        return new Center(new Wrap(spacing: 16, runSpacing: 16, children: [
            Tile("Fill", new DecoratedBox { Fill = p.CardBg }),
            Tile("Fill + radius", new DecoratedBox { Fill = p.CardBg, Radius = 16 }),
            Tile("Border", new DecoratedBox { BorderColor = p.Accent, BorderWidth = 2, Radius = 12 }),
            Tile("Squircle", new DecoratedBox { Fill = new Color(0x333584E4), Radius = 28, Squircle = true }),
        ]));
    }
}
