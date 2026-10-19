// height: 260
class Demo : ComposedWidget
{
    // The scaffold of an Adwaita page: content with top and bottom bars around it.
    protected override Widget Build(BuildContext context) =>
        new AdwToolbarView(new Center(new Label("Page content", fontSize: 16))) {
            TopBars = { new AdwHeaderBar { Title = "Notes", ShowStartWindowControls = false, ShowEndWindowControls = false } },
            BottomBars = {
                new Padding(EdgeInsets.All(8), new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween, children: [
                    new Label("12 notes", fontSize: 13),
                    new AdwButton(label: "New note", onPressed: () => { }) { Style = AdwButtonStyle.Suggested, Pill = true },
                ])),
            },
        };
}
