// height: 220
class Demo : ComposedWidget
{
    // Caps content at a readable width and centres it; resize the window to see the cap.
    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        return new AdwClamp(maximumSize: 400, child: new DecoratedBox { Fill = p.CardBg, Radius = 12,
            Child = new Padding(EdgeInsets.All(16), new Label(
                "Lines of text get hard to read past about 80 characters. AdwClamp keeps a column of content at a comfortable width on wide windows, and lets it shrink on narrow ones.",
                fontSize: 14)) });
    }
}
