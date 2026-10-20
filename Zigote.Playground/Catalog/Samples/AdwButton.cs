// height: 220
class Demo : ComposedWidget
{
    private readonly Signal<string> _said = new("Press something");

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
            new Wrap(spacing: 8, runSpacing: 8, children: [
                new AdwButton(label: "Regular", onPressed: () => _said.Value = "Regular"),
                new AdwButton(label: "Suggested", onPressed: () => _said.Value = "Suggested") { Style = AdwButtonStyle.Suggested },
                new AdwButton(label: "Destructive", onPressed: () => _said.Value = "Destructive") { Style = AdwButtonStyle.Destructive },
                new AdwButton(label: "Flat", onPressed: () => _said.Value = "Flat") { Style = AdwButtonStyle.Flat },
                new AdwButton(label: "Pill", onPressed: () => _said.Value = "Pill") { Pill = true },
                new AdwButton(label: "With icon", onPressed: () => _said.Value = "With icon") { IconName = MaterialIcons.Download },
                new AdwButton(onPressed: () => _said.Value = "Circular") { IconName = MaterialIcons.Favorite, Circular = true },
                new AdwButton(label: "Disabled"),   // no onPressed: disabled
            ]) { CrossAlignment = CrossAxisAlignment.Center },  // the pill is taller than the rest
            new Watch(() => new Label(_said.Value, fontSize: 13)),
        ]));
}
