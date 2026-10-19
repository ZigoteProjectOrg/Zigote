// height: 340
class Demo : ComposedWidget
{
    // The GNOME settings idiom: a titled boxed list of rows, clamped to a readable width.
    protected override Widget Build(BuildContext context) =>
        new SingleChildScrollView {
            Child = new AdwClamp(child: new Padding(EdgeInsets.All(20),
                new AdwPreferencesGroup(title: "Appearance", description: "How windows look and move") {
                    Rows = {
                        new AdwSwitchRow(title: "Dark style", subtitle: "Use dark colours for apps", value: false),
                        new AdwComboRow(title: "Accent", items: ["Blue", "Teal", "Green", "Orange", "Purple"]),
                        new AdwSwitchRow(title: "Reduce motion", value: false),
                        new AdwActionRow("Wallpaper") { Subtitle = "Choose a background" },
                    },
                })),
        };
}
