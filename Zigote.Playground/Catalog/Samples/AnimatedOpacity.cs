// height: 200
class Demo : ComposedWidget
{
    // Retained: it animates from its current opacity whenever the target changes.
    private readonly AnimatedOpacity _fade = new(1f, duration: 0.4f, child: new Label("Now you see me", fontSize: 24));

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
            _fade,
            new AdwButton(label: "Toggle", onPressed: () => _fade.Opacity = _fade.Opacity > 0.5f ? 0f : 1f) { Pill = true },
        ]));
}
