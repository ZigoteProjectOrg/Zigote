// height: 240
class Demo : ComposedWidget
{
    private readonly Signal<string> _last = new("Tap, double-tap or long-press the card");

    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        return new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 14, children: [
            new GestureDetector(
                child: new DecoratedBox { Fill = p.AccentBg, Radius = 18, Child = new SizedBox(width: 180, height: 110,
                    child: new Center(new Label("Card", fontSize: 18, color: p.AccentFg))) },
                onTap: () => _last.Value = "Tap",
                onDoubleTap: () => _last.Value = "Double tap",
                onLongPress: () => _last.Value = "Long press") {
                OnHoverEnter = () => _last.Value = "Hovering",
                OnHoverExit = () => _last.Value = "Left",
            },
            new Watch(() => new Label(_last.Value, fontSize: 15)),
        ]));
    }
}
