// height: 200
class Demo : ComposedWidget
{
    private static readonly string[] Tags = ["Zig", "C#", "wgpu", "Signals", "Retained mode", "Adwaita", "HarfBuzz", "Jolt", "flecs", "Native AOT", "Hot reload", "Live examples"];
    private readonly Signal<string?> _picked = new(null);

    // Children flow onto as many lines as they need.
    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Watch(() => new Wrap(spacing: 8, runSpacing: 8, children: [
            .. Tags.Select(t => new AdwChip(t, selected: t == _picked.Value, onPressed: () => _picked.Value = t)),
        ])));
}
