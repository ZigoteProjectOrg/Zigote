// height: 160
class Demo : ComposedWidget
{
    private readonly Signal<int> _taps = new(0);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new RichText(
                new TextSpan("One paragraph, many styles: "),
                new TextSpan("bold") { Weight = FontWeight.Bold },
                new TextSpan(", "),
                new TextSpan("italic") { Style = FontStyle.Italic },
                new TextSpan(", "),
                new TextSpan("coloured", theme.Accent),
                new TextSpan(", "),
                new TextSpan("struck") { Strikethrough = true },
                new TextSpan(" and a "),
                new TextSpan("link", theme.Accent) { Underline = true, OnTap = () => _taps.Value++ },
                new TextSpan(" — all wrapping together.")) { FontSize = 16 },
            new Watch(() => new Label($"Link tapped {_taps.Value}×", fontSize: 12)),
        ]));
    }
}
