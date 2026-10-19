// height: 320
class Demo : ComposedWidget
{
    // Built lazily: only the rows on screen exist, so ten thousand cost what twenty do.
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return ListView.Builder(itemCount: 10_000, itemExtent: 48, itemBuilder: i =>
            new Padding(EdgeInsets.Symmetric(horizontal: 16), new Row(spacing: 12, children: [
                new AdwAvatar(size: 32, text: $"Contact {i}"),
                new Expanded(new Column(mainAxisAlignment: MainAxisAlignment.Center, crossAxisAlignment: CrossAxisAlignment.Start, children: [
                    new Label($"Contact {i + 1}", fontSize: 14),
                    new Label(text: $"contact{i + 1}@example.com", style: AdwTypography.Caption, color: theme.TextMuted),
                ])),
            ])));
    }
}
