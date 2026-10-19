// height: 200
class Demo : ComposedWidget
{
    // Chooses its shape from the space it is actually given. Resize the window to see it switch.
    protected override Widget Build(BuildContext context) =>
        new LayoutBuilder((ctx, constraints) =>
        {
            Widget a = new AdwButton(label: "Save", onPressed: () => { }) { Style = AdwButtonStyle.Suggested };
            Widget b = new AdwButton(label: "Cancel", onPressed: () => { });
            var note = new Label($"{constraints.MaxWidth:0} pt available", fontSize: 13);
            return constraints.MaxWidth > 520
                ? new Center(new Row(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [note, a, b]))
                : new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [note, a, b]));
        });
}
