// height: 220
class Demo : ComposedWidget
{
    private static readonly string[] Faces = ["🙂", "😎", "🤖", "🦊", "🌈", "🚀"];
    private readonly AnimatedSwitcher _switcher = new(child: new Label(Faces[0], fontSize: 64), duration: 0.3f);
    private int _i;

    // Assign a new Child and the switcher cross-fades from the old one.
    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
            new SizedBox(height: 90, child: new Center(_switcher)),
            new AdwButton(label: "Next", onPressed: () => _switcher.Child = new Label(Faces[++_i % Faces.Length], fontSize: 64)) { Pill = true },
        ]));
}
