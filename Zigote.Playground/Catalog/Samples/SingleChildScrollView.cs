// height: 260
class Demo : ComposedWidget
{
    // Scrolls one child of any height. For long uniform lists, prefer ListView.Builder.
    protected override Widget Build(BuildContext context) =>
        new SingleChildScrollView {
            Child = new Padding(EdgeInsets.All(16), new Column(spacing: 8, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
                .. Enumerable.Range(1, 30).Select(i => (Widget)new DecoratedBox { Fill = new Color(0x223584E4), Radius = 8,
                    Child = new Padding(EdgeInsets.All(12), new Label($"Paragraph {i}: scroll with the wheel, trackpad or scrollbar.", fontSize: 14)) }),
            ])),
        };
}
