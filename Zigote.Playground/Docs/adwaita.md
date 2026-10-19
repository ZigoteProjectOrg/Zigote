# Controls

`Zigote.UI.Adwaita` is both the GNOME Adwaita design system and the framework's control library: buttons, entries, switches, sliders, rows, dialogs, toasts, split views, tab views, pickers and more, about 150 `Adw*` types. The kernel deliberately ships no text field or switch of its own.

## Buttons and toggles

```csharp run height=230
class Demo : ComposedWidget
{
    private readonly Signal<string> _said = new("Press something");

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
            new Wrap(spacing: 8, runSpacing: 8, children: [
                new AdwButton(label: "Default", onPressed: () => _said.Value = "Default"),
                new AdwButton(label: "Suggested", onPressed: () => _said.Value = "Suggested") { Style = AdwButtonStyle.Suggested },
                new AdwButton(label: "Destructive", onPressed: () => _said.Value = "Destructive") { Style = AdwButtonStyle.Destructive },
                new AdwButton(label: "Flat", onPressed: () => _said.Value = "Flat") { Style = AdwButtonStyle.Flat },
                new AdwButton(label: "Pill", onPressed: () => _said.Value = "Pill") { Pill = true },
                new AdwButton(onPressed: () => _said.Value = "Icon") { IconName = MaterialIcons.Favorite, Circular = true },
            ]),
            new AdwToggleGroup(labels: ["Day", "Week", "Month"], onActive: i => _said.Value = $"Toggle {i}"),
            new Row(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
                new AdwSwitch(value: true, onChanged: v => _said.Value = $"Switch {v}"),
                new AdwCheckButton(label: "Check", onChanged: v => _said.Value = $"Check {v}"),
            ]),
            new Watch(() => new Label(_said.Value, fontSize: 13)),
        ]));
}
```

## Boxed lists

GNOME preference screens are built from `AdwPreferencesGroup`s of rows. Each row type is a complete control: `AdwActionRow`, `AdwSwitchRow`, `AdwEntryRow`, `AdwComboRow`, `AdwSpinRow` and `AdwExpanderRow`.

```csharp run height=420
class Demo : ComposedWidget
{
    private readonly Signal<float> _volume = new(0.6f);

    protected override Widget Build(BuildContext context) =>
        new SingleChildScrollView {
            Child = new AdwClamp(child: new Padding(EdgeInsets.All(20), new Column(spacing: 24, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
                new AdwPreferencesGroup(title: "Account", description: "Shown on your profile") {
                    Rows = {
                        new AdwEntryRow(title: "Name", text: "Ada"),
                        new AdwComboRow(title: "Language", items: ["English", "Français", "Deutsch", "日本語"]),
                        new AdwSwitchRow(title: "Public profile", subtitle: "Anyone can find you", value: true),
                    },
                },
                new AdwPreferencesGroup(title: "Sound") {
                    Rows = {
                        new AdwActionRow("Volume") {
                            Suffixes = {
                                new SizedBox(width: 180, child: new AdwSlider(value: _volume.Peek(), onChanged: v => _volume.Value = v)),
                                new SizedBox(width: 44, child: new Watch(() => new Label($"{_volume.Value:P0}", fontSize: 13))),
                            },
                        },
                        new AdwSpinRow(title: "Balance", value: 0, min: -10, max: 10),
                    },
                },
            ]))),
        };
}
```

## Dialogs and toasts

`AdwAlertDialog` asks a question; responses come back as string ids through `OnResponse`. Dialogs float on the window's overlay layer, above everything, including this playground.

```csharp run height=160
class Demo : ComposedWidget
{
    private readonly Signal<string> _answer = new("No answer yet");

    private void Ask()
    {
        var dialog = new AdwAlertDialog(heading: "Discard changes?", body: "Unsaved edits will be lost.") {
            CloseResponse = "cancel",
            OnResponse = id => _answer.Value = $"You chose: {id}",
        };
        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("discard", "Discard", AdwResponseAppearance.Destructive);
        dialog.Show();
    }

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new AdwButton(label: "Ask", onPressed: Ask) { Pill = true },
            new Watch(() => new Label(_answer.Value)),
        ]));
}
```

## Shell widgets

| Widget | Shape |
|---|---|
| `AdwToolbarView` | Content with top and bottom bars; the scaffold of every page |
| `AdwHeaderBar` | Title bar with window controls, title, start and end slots |
| `AdwNavigationSplitView` | Sidebar + content that folds into one pane when narrow (this app) |
| `AdwNavigationView` | A push/pop page stack with a header that follows it |
| `AdwViewStack` + `AdwViewSwitcher` | Top-level tabs in the header or a bottom bar |
| `AdwTabView` + `AdwTabBar` | Document tabs: pinned, closable, reorderable |
| `AdwToastOverlay` | Transient messages over the content |

> The **Adwaita Demo** app (`Zigote.UI.Adwaita.Gallery`) shows every control with its options. Use it alongside this page.
