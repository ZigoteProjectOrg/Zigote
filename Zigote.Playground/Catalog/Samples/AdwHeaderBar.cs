// height: 200
class Demo : ComposedWidget
{
    // A title bar: a centred title (or any widget) with start and end slots.
    protected override Widget Build(BuildContext context) =>
        new Column(crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwHeaderBar {
                TitleWidget = new AdwWindowTitle(title: "Documents", subtitle: "3 files"),
                Start = { new AdwButton(onPressed: () => { }) { IconName = MaterialIcons.Add, Style = AdwButtonStyle.Flat, Circular = true } },
                End = { new AdwButton(onPressed: () => { }) { IconName = MaterialIcons.Search, Style = AdwButtonStyle.Flat, Circular = true } },
                ShowStartWindowControls = false,
                ShowEndWindowControls = false,
            },
            new Expanded(new Center(new Label("Content", fontSize: 15))),
        ]);
}
