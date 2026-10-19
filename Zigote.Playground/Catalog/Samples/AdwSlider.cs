// height: 180
class Demo : ComposedWidget
{
    private readonly Signal<float> _volume = new(0.6f);

    protected override Widget Build(BuildContext context) =>
        new Center(new SizedBox(width: 360, child: new Row(spacing: 12, children: [
            new IconGlyph(glyph: MaterialIcons.VolumeUp, size: 20),
            new Expanded(new AdwSlider(value: 0.6f, onChanged: v => _volume.Value = v)),
            new SizedBox(width: 44, child: new Watch(() => new Label($"{_volume.Value * 100:0}%", fontSize: 14))),
        ])));
}
