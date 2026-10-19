// height: 260
class Demo : ComposedWidget
{
    private static readonly string[] Apps = ["Calculator", "Calendar", "Camera", "Clocks", "Contacts", "Disks", "Files", "Fonts", "Maps", "Music", "Photos", "Settings", "Terminal", "Weather"];
    private readonly Signal<string> _query = new("");

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwSearchEntry { Placeholder = "Search apps", OnChanged = q => _query.Value = q },
            new Expanded(new Watch(() => new Wrap(spacing: 6, runSpacing: 6, children: [
                .. Apps.Where(a => a.Contains(_query.Value, StringComparison.OrdinalIgnoreCase))
                       .Select(a => new AdwChip(a)),
            ]))),
        ]));
}
