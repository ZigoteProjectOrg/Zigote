// height: 280
class Demo : ComposedWidget
{
    private AdwViewStack? _stack;

    // Top-level pages switched by a view switcher; the stack keeps every page alive.
    protected override Widget Build(BuildContext context)
    {
        _stack ??= new AdwViewStack(
            new AdwViewStackPage("world", "World", new Center(new Label("🌍 World clocks", fontSize: 20)), iconName: MaterialIcons.Public),
            new AdwViewStackPage("alarms", "Alarms", new Center(new Label("⏰ Alarms", fontSize: 20)), iconName: MaterialIcons.Alarm, badge: 2),
            new AdwViewStackPage("timer", "Timer", new Center(new Label("⏱ Timer", fontSize: 20)), iconName: MaterialIcons.Timer));
        return new AdwToolbarView(_stack) {
            // heightFactor: 1 — a plain Center would fill the bar's whole height and leave the content none.
            TopBars = { new Padding(EdgeInsets.All(8), new Center(new AdwViewSwitcher(_stack), heightFactor: 1)) },
        };
    }
}
