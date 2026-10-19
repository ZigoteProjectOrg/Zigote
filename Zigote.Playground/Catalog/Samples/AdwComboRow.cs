// height: 260
class Demo : ComposedWidget
{
    private static readonly string[] Languages = ["English", "Deutsch", "Español", "Français", "日本語"];
    private readonly Signal<int> _language = new(0);

    protected override Widget Build(BuildContext context) =>
        new AdwClamp(child: new Padding(EdgeInsets.All(20), new Column(spacing: 12, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwPreferencesGroup(title: "Region") {
                Rows = { new AdwComboRow(title: "Language", items: Languages, onSelected: i => _language.Value = i) },
            },
            new Watch(() => new Label($"Interface language: {Languages[_language.Value]}", fontSize: 13)),
        ])));
}
