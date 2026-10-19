// height: 200
class Demo : ComposedWidget
{
    private static readonly TextStyle Big = new(fontSize: 28, fontWeight: FontWeight.Bold, letterSpacing: -0.5);

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new Text("Flutter-shaped text", style: Big),
            new Text("Centered, at most two lines, with an ellipsis when it overflows the space it was given — try narrowing the window.",
                     textAlign: TextAlign.Center, maxLines: 2, overflow: TextOverflow.Ellipsis),
        ]));
}
