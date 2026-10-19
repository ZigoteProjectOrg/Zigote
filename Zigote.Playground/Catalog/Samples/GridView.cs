// height: 320
class Demo : ComposedWidget
{
    private static readonly uint[] Palette = [0xFF3584E4, 0xFF2EC27E, 0xFFF5C211, 0xFFE66100, 0xFFC01C28, 0xFF9141AC];

    protected override Widget Build(BuildContext context) =>
        GridView.Builder(crossAxisCount: 5, itemCount: 300, mainAxisSpacing: 8, crossAxisSpacing: 8, itemBuilder: i =>
            new DecoratedBox { Fill = new Color(Palette[i % Palette.Length]), Radius = 12,
                Child = new Center(new Label($"{i + 1}", fontSize: 15, color: new Color(0xFFFFFFFF))) });
}
