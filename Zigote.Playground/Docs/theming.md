# Theming

A theme is one immutable `ThemeData` record: colours, typography and metrics. `ThemeProvider`, an `InheritedWidget` that `ZigoteApp` places at the root, hands it down the tree. A widget reads it with `ThemeProvider.Of(context)` in its `Build`, and that read **registers a dependency**: when the theme changes, exactly the widgets that read it rebuild.

`AdwaitaApp` follows the system appearance and accent out of the box. To change the theme yourself, assign `Theme` on the app:

```csharp
Theme = AdwTheme.Create(accent: AdwAccent.Purple, dark: true);
```

## Scoped themes

A `ThemeProvider` can also sit anywhere in the tree to re-theme one subtree. The example keeps one provider in a field and replaces its `Data`. Everything below it that read the theme rebuilds; the switch rows themselves are never recreated, so they keep their state.

```csharp run height=330
class Demo : ComposedWidget
{
    private readonly Signal<bool> _dark = new(true);
    private readonly Signal<AdwAccent> _accent = new(AdwAccent.Purple);
    private readonly ThemeProvider _scope;

    public Demo()
    {
        _scope = new ThemeProvider(AdwTheme.Create(_accent.Value, _dark.Value), child: new Swatch());
        _dark.Changed += _ => Apply();
        _accent.Changed += _ => Apply();
    }

    private void Apply() => _scope.Data = AdwTheme.Create(_accent.Value, _dark.Value);

    protected override Widget Build(BuildContext context) =>
        new Row(crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new SizedBox(width: 260, child: new Padding(EdgeInsets.All(16), new Column(spacing: 8, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
                new AdwSwitchRow(title: "Dark", value: _dark.Value, onChanged: v => _dark.Value = v),
                new Wrap(spacing: 6, runSpacing: 6, children: [
                    .. Enum.GetValues<AdwAccent>().Select(a =>
                        new AdwButton(label: a.ToString(), onPressed: () => _accent.Value = a) { Pill = true }),
                ]),
            ]))),
            new Expanded(_scope),
        ]);
}

// Reads the theme in Build, so it rebuilds whenever the scope's Data changes.
sealed class Swatch : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        return new DecoratedBox {
            Fill = p.WindowBg,
            Child = new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
                new Label(text: theme.IsDark ? "Dark" : "Light", style: AdwTypography.Title2, color: p.WindowFg),
                new AdwButton(label: "Suggested", onPressed: () => { }) { Style = AdwButtonStyle.Suggested, Pill = true },
                new AdwButton(label: "Destructive", onPressed: () => { }) { Style = AdwButtonStyle.Destructive, Pill = true },
                new AdwSwitch(value: true),
            ])),
        };
    }
}
```

## Where colours come from

- `ThemeData` holds the kernel's semantic colours: `Background`, `Surface`, `OnSurface`, `TextSecondary`, `TextMuted`, `Accent`, `Error`, `Success`…
- `AdwPalette.For(theme)` returns the full Adwaita set (`AccentBg`, `CardBg`, `HeaderbarBg`, `ViewBg`, `SidebarBg`, …), matching libadwaita's named colours.
- `AdwPalette.Fill(theme, 0.1f)` is a translucent wash of the foreground colour, which is how Adwaita draws neutral hover and pressed states.

> Read the theme in `Build`, not in a constructor or a field initializer. Only a read made through the `BuildContext` registers the dependency that makes a theme flip reach your widget.

## Your own inherited data

`ThemeProvider` is just an `InheritedWidget`. Subclass `InheritedWidget` to pass anything down the tree, then read it with `context.DependOn<T>()` (subscribes) or `context.Read<T>()` (one-off lookup). For app services, plain constructor injection is usually simpler; Zigote has no DI container and does not need one.
