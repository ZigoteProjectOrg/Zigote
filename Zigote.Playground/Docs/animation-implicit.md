# Implicit Animations

Animation runs on the frame loop. A `Ticker` fires once per frame while it is active, and when nothing animates the app sleeps and draws nothing. There are two styles:

- **Implicit.** You set a new *target* value and the widget animates there by itself. You never see a controller. Use this for most UI motion: things that move, fade, resize or swap in response to state.
- **Explicit.** You own an `AnimationController` or spring and drive it yourself: start, stop, reverse, loop, scrub. See *Explicit Animations*.

Start implicit and move to explicit only when you need control over the timeline.

## The Animated* widgets

Each one holds a current value. Change its target property and it eases from wherever it is now to the new value, even mid-flight.

| Widget | Animates | Target |
|---|---|---|
| `AnimatedOpacity` | Opacity | `Opacity` |
| `AnimatedAlign` | Position inside its box | `Alignment` |
| `AnimatedPadding` | Insets | `Insets` |
| `AnimatedSize` | Its own size, following its child | `Child` |
| `AnimatedContainer` | Width, height and colour | `AnimateTo(width, height, color)` |
| `AnimatedSwitcher` | Cross-fade between children | `Child` |
| `TweenAnimationBuilder<T>` | Any value through a `Tween` | rebuilds per tick |

```csharp run height=380
class Demo : ComposedWidget
{
    private readonly AnimatedAlign _align;
    private readonly AnimatedOpacity _fade;
    private readonly AnimatedContainer _box;
    private readonly AnimatedSwitcher _switcher = new(child: new Label("🙂", fontSize: 40), duration: 0.3f);
    private int _corner;
    private bool _wide;

    public Demo()
    {
        _align = new AnimatedAlign(Alignment.TopLeft, duration: 0.5f, curve: Curves.EaseInOut,
            child: new DecoratedBox { Fill = new Color(0xFF3584E4), Radius = 12, Child = new SizedBox(width: 56, height: 56) });
        _fade = new AnimatedOpacity(1f, duration: 0.4f, child: new Label("Now you see me", fontSize: 15));
        _box = new AnimatedContainer(width: 80, height: 40, color: new Color(0xFF2EC27E), durationSeconds: 0.4f);
    }

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(16), new Row(spacing: 16, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Expanded(_align),
            new SizedBox(width: 240, child: new Column(spacing: 10, mainAxisAlignment: MainAxisAlignment.Center, children: [
                new AdwButton(label: "Move", onPressed: () =>
                {
                    _corner = (_corner + 1) % 4;
                    _align.Alignment = _corner switch {
                        0 => Alignment.TopLeft, 1 => Alignment.TopRight, 2 => Alignment.BottomRight, _ => Alignment.BottomLeft,
                    };
                }),
                new AdwButton(label: "Fade", onPressed: () => _fade.Opacity = _fade.Opacity > 0.5f ? 0f : 1f),
                _fade,
                new AdwButton(label: "Resize", onPressed: () =>
                {
                    _wide = !_wide;
                    _box.AnimateTo(width: _wide ? 200 : 80, color: _wide ? new Color(0xFFE66100) : new Color(0xFF2EC27E));
                }),
                _box,
                new AdwButton(label: "Swap", onPressed: () =>
                    _switcher.Child = new Label(Random.Shared.GetItems(["😎", "🤖", "🦊", "🌈", "🚀"], 1)[0], fontSize: 40)),
                _switcher,
            ])),
        ]));
}
```

Press **Move** several times quickly: each new target is picked up from wherever the square is, without a jump.

## Keep implicit widgets retained

An implicit animation animates *from its current value*, and that value lives on the widget instance. Keep the instance (in a field, as above) and change its property. If you create a new `AnimatedOpacity` inside a `Watch` on every change, each one starts at its target and nothing animates.

```csharp
// ✗ A fresh instance every time: it starts at its target, so nothing animates.
new Watch(() => new AnimatedOpacity(_visible.Value ? 1f : 0f, child: _panel))

// ✓ One instance; an effect retargets it.
_fade = new AnimatedOpacity(1f, child: _panel);
OwnEffect(() => _fade.Opacity = _visible.Value ? 1f : 0f);   // in OnMount
```

## Curves and durations

Durations are in seconds. Curves are plain `Func<float, float>` functions over 0..1, so you can pass your own lambda:

| Curve | Feels like |
|---|---|
| `Curves.EaseOut` | The default for things *entering*: fast start, gentle stop |
| `Curves.EaseIn` | Things *leaving*: gentle start, fast exit |
| `Curves.EaseInOut` | Movement between two resting places |
| `Curves.EaseOutBack` | A small overshoot; playful |
| `Curves.BounceOut`, `Curves.ElasticOut` | Use sparingly |

Typical UI durations are 150–300 ms. Motion that takes longer than about 400 ms starts to feel slow.

## Declarative effects

`.Animate()` wraps any widget in a chain of effects that plays when it mounts: `Fade`, `Scale`, `Move`, `Slide`, `Rotate`, `Shake`, joined with `Then`. Presets such as `FadeInUp`, `PopIn`, `ZoomIn`, `Pulse`, `Breathe` and `Spin` cover common cases, and `Stagger` offsets a list of them.

```csharp run height=260
class Demo : ComposedWidget
{
    private readonly Signal<int> _replay = new(0);

    protected override Widget Build(BuildContext context) =>
        new Column(children: [
            new Padding(EdgeInsets.All(12), new AdwButton(label: "Replay", onPressed: () => _replay.Value++)),
            new Expanded(new Watch(() =>
            {
                _ = _replay.Value;   // a new Watch build re-mounts the cards, which replays them
                return new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 20, children: [
                    new Row(mainAxisSize: MainAxisSize.Min, spacing: 12, children:
                        Enumerable.Range(0, 5)
                            .Select(i => (Widget)new DecoratedBox {
                                Fill = new Color(0xFF3584E4), Radius = 14,
                                Child = new SizedBox(width: 56, height: 72, child: new Center(new Label($"{i + 1}", fontSize: 20, color: new Color(0xFFFFFFFF)))),
                            })
                            .Select(card => (Widget)card.FadeInUp())
                            .Stagger(TimeSpan.FromMilliseconds(80))),
                    new Label("Pop!", fontSize: 28).PopIn(),
                    new Label("❤️", fontSize: 28).Pulse(),
                ]));
            })),
        ]);
}
```

`Loop` and `LoopReverse` repeat a chain; `OnComplete` tells you when it finished. `Animate` exposes its `Controller`, so you can call `Play()`, `Reverse()` or `Restart()` from code when mounting is not the trigger you want.
