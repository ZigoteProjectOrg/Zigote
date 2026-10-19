// height: 220
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new Padding(EdgeInsets.All(20), new Column(spacing: 10, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new Label(text: "Title", style: AdwTypography.Title2),
            new Label(text: "Secondary text in the theme's muted colour", style: AdwTypography.Body, color: theme.TextSecondary),
            new Label("A long label wraps to the width it is given, breaking at spaces, and keeps its line height as it goes.", fontSize: 14),
            new Label("Clipped to one line with an ellipsis, however long the text runs on and on", fontSize: 14) { MaxLines = 1 },
        ]));
    }
}
