// height: 220
class Demo : ComposedWidget
{
    private readonly Signal<bool> _notify = new(true);

    protected override Widget Build(BuildContext context) =>
        new AdwClamp(child: new Padding(EdgeInsets.All(20), new Column(spacing: 12, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwPreferencesGroup(title: "Notifications") {
                Rows = {
                    new AdwSwitchRow(title: "Show banners", subtitle: "Pop up new messages", value: true, onChanged: v => _notify.Value = v),
                    new AdwSwitchRow(title: "Lock screen", subtitle: "Show on the lock screen"),
                },
            },
            new Watch(() => new Label(_notify.Value ? "Banners on" : "Banners off", fontSize: 13)),
        ])));
}
