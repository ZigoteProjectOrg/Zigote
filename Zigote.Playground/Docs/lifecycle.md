# Widget Lifecycle

A retained widget lives through a few well-defined moments:

| Moment | Hook | Runs |
|---|---|---|
| Construction | constructor | Once per instance. Build the children you keep in fields here |
| Mount | `OnMount()` | Each time the widget enters a live tree |
| First measure | `Build(context)` | Once, unless something marks it dirty |
| Unmount | `OnUnmount()` | Each time it leaves the tree, paired 1:1 with mount |

Mount and unmount pair up: a widget that is removed and re-inserted (a tab switch, a route pushed and popped) mounts again. Anything you start in `OnMount` (subscriptions, timers, tickers) should be registered with `Own(...)`, `OwnEffect(...)` or `CreateTicker(...)`, which are torn down automatically on unmount.

```csharp run height=280
sealed class Clock : ComposedWidget
{
    private readonly Signal<DateTime> _now = new(DateTime.Now);

    protected override void OnMount()
    {
        Log.Add("Clock mounted");
        // A ticker runs on the frame loop; owned, so it stops when the clock unmounts.
        var ticker = CreateTicker(_ => _now.Value = DateTime.Now);
        ticker.Start();
    }

    protected override void OnUnmount() => Log.Add("Clock unmounted");

    protected override Widget Build(BuildContext context) =>
        new Watch(() => new Label(_now.Value.ToString("HH:mm:ss.f"), fontSize: 32));
}

static class Log
{
    public static readonly Signal<string[]> Lines = new([]);
    public static void Add(string line) => Lines.Value = [.. Lines.Value.TakeLast(4), $"{DateTime.Now:HH:mm:ss}  {line}"];
}

class Demo : ComposedWidget
{
    private readonly Signal<bool> _show = new(true);
    private readonly Clock _clock = new();   // one instance, mounted and unmounted by the toggle

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new AdwSwitchRow(title: "Show clock", value: true, onChanged: v => _show.Value = v),
            new SizedBox(height: 50, child: new Watch(() => _show.Value ? _clock : SizedBox.Shrink())),
            new Watch(() => new Label(string.Join("\n", Log.Lines.Value), fontSize: 12)),
        ]));
}
```

Toggle the switch and watch the log: the same `Clock` instance is mounted and unmounted, and its ticker stops while it is off screen.

## Where code goes

- **Constructor.** Create the child widgets you keep in fields and wire their callbacks. This happens once.
- **`OnMount`.** Start things that must stop when the widget leaves: effects, subscriptions, tickers.
- **`Build`.** Read the theme and media query, and assemble the tree. Do not start work here; it is not a lifecycle hook.
- **`OnUnmount`.** Only teardown that `Own` cannot express.

> Signals hold their observers strongly. A bare `new Effect(...)` created by a widget keeps running after that widget is gone. Use `OwnEffect`.

## Hot reload

Under `dotnet watch` or the IDE's "apply changes", editing a `Build` method updates the running app in place: instances and their fields are preserved and only `Build` re-runs. Constructor bodies, field initialisers and `OnMount` need a restart, which is the usual reason a hot-reloaded change "did nothing".
