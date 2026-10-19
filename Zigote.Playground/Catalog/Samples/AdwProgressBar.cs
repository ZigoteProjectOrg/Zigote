// height: 180
class Demo : ComposedWidget
{
    private readonly Signal<float> _progress = new(0.35f);

    protected override Widget Build(BuildContext context) =>
        new Center(new SizedBox(width: 360, child: new Column(mainAxisSize: MainAxisSize.Min, spacing: 14, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Watch(() => new AdwProgressBar(_progress.Value)),
            new Watch(() => new Label($"{_progress.Value * 100:0}% copied", fontSize: 13)),
            new AdwSlider(value: 0.35f, onChanged: v => _progress.Value = v),
        ])));
}
