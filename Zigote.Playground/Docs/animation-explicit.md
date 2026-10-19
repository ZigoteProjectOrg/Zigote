# Explicit Animations

Use an explicit animation when you need to *control the timeline*: start on demand, reverse, loop, scrub with a slider, chain steps, or drive several properties from one clock.

## AnimationController

An `AnimationController` produces a `Value` that runs from 0 to 1 over `Duration` seconds, shaped by `Curve`. It is driven by a ticker from the widget it belongs to.

| Member | Does |
|---|---|
| `Forward()`, `Reverse()`, `Toggle()` | Play towards 1, towards 0, or flip direction |
| `Repeat(reverse: true)` | Loop; ping-pong when `reverse` is true |
| `Seek(progress)` | Jump to a point on the timeline (scrubbing) |
| `Complete()`, `Dismiss()` | Snap to the end or the start |
| `Value`, `Progress`, `Status` | Curved value, raw 0..1 progress, `Idle`, `Forward`, `Reverse`, `Completed` or `Dismissed` |
| `OnTick`, `OnCompleted`, `OnDismissed` | Events |
| `Speed`, `Curve`, `Duration` | Tune the playback |

The controller needs a ticker from a mounted widget. Create it once, and **attach the ticker in `OnMount`**: a widget's tickers are owned by its mount period and disposed when it unmounts.

```csharp run height=300
class Demo : ComposedWidget
{
    private readonly AnimationController _c = new(durationSeconds: 1.2f) { Curve = Curves.EaseInOut };
    private readonly Signal<string> _status = new("Dismissed");

    public Demo()
    {
        _c.OnCompleted += () => _status.Value = "Completed";
        _c.OnDismissed += () => _status.Value = "Dismissed";
    }

    protected override void OnMount() => _c.AttachTicker(this);

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 14, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            // AnimatedBuilder re-runs its builder on every tick of the controller.
            new SizedBox(height: 80, child: new AnimatedBuilder(_c, (ctx, _) =>
            {
                float t = _c.Value;
                return new Align(alignment: new Alignment(t, 0.5f), child: new DecoratedBox {
                    Fill = new Color(0.21f + 0.7f * t, 0.52f, 0.89f - 0.6f * t),
                    Radius = 8 + 28 * t,
                    Child = new SizedBox(width: 56 + 24 * t, height: 56 + 24 * t),
                });
            })),
            new Wrap(spacing: 8, runSpacing: 8, children: [
                new AdwButton(label: "Forward", onPressed: () => { _status.Value = "Forward"; _c.Forward(); }),
                new AdwButton(label: "Reverse", onPressed: () => { _status.Value = "Reverse"; _c.Reverse(); }),
                new AdwButton(label: "Repeat", onPressed: () => { _status.Value = "Repeating"; _c.Repeat(reverse: true); }),
                new AdwButton(label: "Stop", onPressed: () => { _status.Value = "Stopped"; _c.Seek(_c.Progress); }),
            ]),
            new Row(spacing: 12, children: [
                new Label("Scrub", fontSize: 13),
                new Expanded(new AdwSlider(value: 0, onChanged: v => { _status.Value = "Scrubbing"; _c.Seek(v); })),
            ]),
            new Watch(() => new Label($"Status: {_status.Value}", fontSize: 13)),
        ]));
}
```

## Transition widgets

`FadeTransition`, `SlideTransition` and `ScaleTransition` apply a controller's value as a paint-time effect. They never re-lay-out, so they are cheap even for large subtrees. Several can share one controller, which choreographs them together:

```csharp run height=240
class Demo : ComposedWidget
{
    private readonly AnimationController _c = new(durationSeconds: 0.45f) { Curve = Curves.EaseOutBack };
    private readonly Widget _card;

    public Demo()
    {
        var card = new DecoratedBox {
            Fill = new Color(0xFF9141AC), Radius = 16,
            Child = new SizedBox(width: 220, height: 100, child: new Center(new Label("Hello there 👋", fontSize: 18, color: new Color(0xFFFFFFFF)))),
        };
        _card = new FadeTransition(_c, new SlideTransition(_c, new ScaleTransition(_c, card)) { BeginOffset = new Offset(0, 60) });
    }

    protected override void OnMount() => _c.AttachTicker(this);

    protected override Widget Build(BuildContext context) =>
        new Column(children: [
            new Padding(EdgeInsets.All(12), new AdwButton(label: "Show / hide", onPressed: _c.Toggle) { Pill = true }),
            new Expanded(new Center(_card)),
        ]);
}
```

## Springs

A duration-based animation is the wrong tool for anything the user can interrupt: a drawer they fling, a knob they grab mid-motion, a value that changes again before the animation ends. Retargeting a curve restarts its clock and the motion visibly stutters. A **spring** keeps its velocity when the target changes, so it bends smoothly towards the new one.

`AnimatedFloat` is a single spring-driven value. Call `To(target)` as often as you like and read `Value` when painting:

| `MotionSpec` | Character |
|---|---|
| `MotionSpec.Smooth` | Critically damped; no overshoot. Most UI |
| `MotionSpec.Snappy` | Quick, with a hint of overshoot. Toggles, selections |
| `MotionSpec.Bouncy` | Visible bounce. Playful moments |
| `MotionSpec.Spring(response, damping)` | Your own: response is roughly the settle time in seconds, damping below 1 bounces |
| `MotionSpec.Decay(friction)` | Coasts to a stop from its velocity. Flings |

```csharp run height=260
sealed class Follower : LeafWidget
{
    private readonly AnimatedFloat _x, _y;
    private Size _size;

    public Follower(MotionSpec spec)
    {
        _x = new AnimatedFloat(spec, MarkNeedsPaint, value: 60);
        _y = new AnimatedFloat(spec, MarkNeedsPaint, value: 60);
    }

    protected override void OnMount() { _x.AttachTicker(this); _y.AttachTicker(this); }

    public override Size Measure(Constraints c) => _size = new Size(c.MaxWidth, c.MaxHeight);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    // Retarget on every move: the spring bends towards the pointer without restarting.
    public override void OnPointerMove(Offset p) { _x.To(p.X - Bounds.X); _y.To(p.Y - Bounds.Y); }
    public override void OnPointerDown(Offset p) => OnPointerMove(p);

    public override void Paint(PaintList paint)
    {
        paint.AddText("Move the pointer around", Bounds.X + 12, Bounds.Y + 22, new Color(0xFF888888), 13);
        float x = Bounds.X + _x.Value, y = Bounds.Y + _y.Value;
        paint.AddShadow(new Rect(x - 22, y - 22, 44, 44), new Color(0x553584E4), 22, 12);
        paint.AddRect(new Rect(x - 22, y - 22, 44, 44), new Color(0xFF3584E4), radius: 22);
    }
}

class Demo : ComposedWidget
{
    // Try MotionSpec.Smooth, MotionSpec.Snappy or MotionSpec.Spring(response: 0.8f, damping: 0.4f).
    protected override Widget Build(BuildContext context) => new Follower(MotionSpec.Bouncy);
}
```

An `AnimationController` can run on a spring too: set its `Spec` to a spring `MotionSpec` and `Forward()` and `Reverse()` keep momentum when interrupted.

## Raw tickers

Under all of this is `CreateTicker(dt => …)`, a per-frame callback with the elapsed seconds. Use it for continuous simulation (particles, physics, a game loop) where there is no target at all. *Games & 3D* has a full example.

## Motion settings

- `AnimationController.ReduceMotion` follows the OS accessibility setting. Built-in transitions jump to their end state when it is on, while still firing their completion events. Respect it in your own motion too.
- `AnimationController.TimeScale` slows every animation globally. Set it to 0.2 while you debug a transition to see what it actually does.
- Per-frame deltas are capped (`MaxFrameDelta`, 100 ms), so a GC pause or a debugger break doesn't make an animation jump to its end.
