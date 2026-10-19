# Text

Text is shaped natively by HarfBuzz and rasterised by FreeType into a GPU glyph atlas. On the C# side you choose a widget and a style; shaping, bidi, emoji fallback and caching happen below.

| Widget | For |
|---|---|
| `Label` | One style, any length, wraps by default |
| `Text` | `Label` with a constructor shaped like Flutter's (`style:`, `textAlign:`, `maxLines:`) |
| `RichText` | A paragraph of differently styled `TextSpan`s that wrap together |
| `SelectableText` | `RichText` the user can select and copy |
| `AdwEntry`, `AdwTextField` | Editable text (see *Controls*) |

```csharp run height=320
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        return new Padding(EdgeInsets.All(20), new Column(spacing: 14, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new Label(text: "Title 1", style: AdwTypography.Title1),
            new Label(text: "Heading — the Adwaita type ramp", style: AdwTypography.Heading),
            new Text("A long paragraph that is limited to one line and ends in an ellipsis when it runs out of room, however wide the window is.",
                     maxLines: 1, overflow: TextOverflow.Ellipsis),
            new RichText(
                new TextSpan("Mix "),
                new TextSpan("bold") { Weight = FontWeight.Bold },
                new TextSpan(", "),
                new TextSpan("italic") { Style = FontStyle.Italic },
                new TextSpan(", "),
                new TextSpan("colour", p.Accent),
                new TextSpan(" and "),
                new TextSpan("code") { FontFamily = AdwTypography.Monospace.FontFamily, Background = AdwPalette.Fill(theme, 0.1f) },
                new TextSpan(" in one wrapping paragraph. Emoji work too: 🚀✨")) { FontSize = 15 },
            new SelectableText("Drag across me to select, then ⌘C to copy."),
            new Label(text: "مرحبا بالعالم — right-to-left runs are reordered by the shaper.", style: AdwTypography.Body),
        ]));
    }
}
```

## Links

Give a `TextSpan` an `OnTap` and it becomes a link: the pointer cursor appears over it, and the
callback fires when a press starts and ends on that span. Style it yourself; `OnTap` doesn't change
how the span looks.

```csharp run height=140
class Demo : ComposedWidget
{
    private readonly Signal<int> _taps = new(0);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new RichText(
                new TextSpan("Read the "),
                new TextSpan("license", theme.Accent) { Underline = true, OnTap = () => _taps.Value++ },
                new TextSpan(" before you continue.")) { FontSize = 16 },
            new Watch(() => new Label($"Link tapped {_taps.Value}×", fontSize: 13)),
        ]));
    }
}
```

## Styles

`TextStyle` bundles size, weight, slant, line height, letter spacing and family. `AdwTypography` holds the Adwaita ramp: `Title1` to `Title4`, `Heading`, `Body`, `Caption`, `CaptionHeading` and `Monospace`. Colours come from the theme (`theme.OnSurface`, `theme.TextSecondary`, `theme.TextMuted`), so text follows light and dark mode without extra code.

```csharp run height=160
class Demo : ComposedWidget
{
    private static readonly TextStyle Poster = new(fontSize: 34, fontWeight: FontWeight.Black, letterSpacing: -1);

    protected override Widget Build(BuildContext context) =>
        new Center(new Text("Custom style", style: Poster));
}
```

> The default UI font is Inter and the monospace font is Iosevka; both ship with the framework. Text outside their coverage falls back to system fonts.
