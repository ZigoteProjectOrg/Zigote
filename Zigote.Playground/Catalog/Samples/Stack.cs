// height: 220
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        // A card with a badge pinned to its top-right corner: later children paint on top.
        return new Center(new SizedBox(width: 260, height: 150, child: new Stack(children: [
            new Positioned(left: 0, top: 10, right: 10, bottom: 0,
                child: new DecoratedBox { Fill = p.CardBg, BorderColor = p.CardShade, Radius = 14,
                    Child = new Center(new Label(text: "Inbox", style: AdwTypography.Title3)) }),
            new Positioned(right: 0, top: 0,
                child: new DecoratedBox { Fill = p.DestructiveBg, Radius = 12,
                    Child = new Padding(EdgeInsets.Symmetric(horizontal: 9, vertical: 3), new Label("12", fontSize: 13, color: new Color(0xFFFFFFFF))) }),
        ])));
    }
}
