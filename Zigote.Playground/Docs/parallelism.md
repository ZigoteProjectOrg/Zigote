# Parallelism

The UI thread must stay free to draw. Anything CPU-heavy (image processing, simulation, search, parsing, compression) belongs on other cores. .NET has everything needed, and Zigote's part is the hand-off: workers compute, the UI thread presents, and signals connect the two.

The pattern, in three rules:

1. **Compute off the UI thread**, using as many cores as you like: `Task.Run`, `Parallel.For`, PLINQ, channels.
2. **Report through signals.** Workers may write signals from any thread; a `Watch` rebuilds on the UI thread. Throttle progress writes so you don't flood the UI with thousands of updates.
3. **Hand results over whole.** Build the result privately, then publish it in one step. The UI never sees a half-written buffer.

## A real parallel render

This renders the Mandelbrot set. Each row is independent, so `Parallel.For` spreads the rows across every core. Press **Parallel** and keep hovering or scrolling the page: the spinner keeps turning and the UI stays responsive, because the UI thread only uploads the finished image. Press **On UI thread** to see the same work block the window. Press **Parallel** repeatedly: each run cancels the one before it.

```csharp run height=460
sealed class Fractal : LeafWidget
{
    public const int W = 640, H = 400;
    private ulong _texture;
    private byte[]? _pending;          // finished pixels waiting to be uploaded on the UI thread
    private Size _size;

    // Called from any thread with a finished buffer; the upload happens in Paint, on the UI thread.
    public void Publish(byte[] rgba) { Volatile.Write(ref _pending, rgba); Owner?.Post(MarkNeedsPaint); }

    public override Size Measure(Constraints c) => _size = new Size(c.MaxWidth, c.MaxHeight);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    protected override void OnUnmount()
    {
        if (_texture != 0) ZigoteEngine.ReleaseTexture(_texture);   // textures are caller-owned
        _texture = 0;
    }

    public override void Paint(PaintList paint)
    {
        if (Interlocked.Exchange(ref _pending, null) is { } rgba)
        {
            if (_texture != 0) ZigoteEngine.ReleaseTexture(_texture);
            _texture = ZigoteEngine.LoadTextureFromRgba(rgba, W, H);
        }
        if (_texture == 0) return;
        float scale = MathF.Min(Bounds.Width / W, Bounds.Height / H);
        var r = new Rect(Bounds.X + (Bounds.Width - W * scale) / 2, Bounds.Y, W * scale, H * scale);
        paint.AddImage(r, W, H, null, cacheKey: _texture);
    }

    // Pure CPU work: safe on any thread, touches nothing shared but its own output row.
    public static void RenderRow(byte[] rgba, int y, double zoom)
    {
        for (int x = 0; x < W; x++)
        {
            double cr = -0.743643887 + (x - W / 2.0) / (W * zoom), ci = 0.131825904 + (y - H / 2.0) / (W * zoom);
            double zr = 0, zi = 0; int i = 0;
            for (; i < 600 && zr * zr + zi * zi < 4; i++) (zr, zi) = (zr * zr - zi * zi + cr, 2 * zr * zi + ci);
            int o = (y * W + x) * 4;
            double t = i / 600.0;
            rgba[o] = (byte)(9 * (1 - t) * t * t * t * 255);
            rgba[o + 1] = (byte)(15 * (1 - t) * (1 - t) * t * t * 255);
            rgba[o + 2] = (byte)(8.5 * (1 - t) * (1 - t) * (1 - t) * t * 255);
            rgba[o + 3] = 255;
        }
    }
}

class Demo : ComposedWidget
{
    private readonly Fractal _view = new();
    private readonly Signal<string> _status = new($"{Environment.ProcessorCount} cores available");
    private readonly Signal<float> _progress = new(0f);
    private CancellationTokenSource? _cts;
    private double _zoom = 0.6;

    private void Parallel()
    {
        _cts?.Cancel();                                   // latest wins
        var cts = _cts = new CancellationTokenSource();
        double zoom = _zoom *= 1.6;
        _status.Value = "Rendering on all cores…";
        Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rgba = new byte[Fractal.W * Fractal.H * 4];  // private until published
            int done = 0;
            try
            {
                System.Threading.Tasks.Parallel.For(0, Fractal.H, new ParallelOptions { CancellationToken = cts.Token }, y =>
                {
                    Fractal.RenderRow(rgba, y, zoom);
                    // Throttled progress: one signal write per 20 rows, from whichever core finished.
                    if (Interlocked.Increment(ref done) % 20 == 0) _progress.Value = done / (float)Fractal.H;
                });
            }
            catch (OperationCanceledException) { return; }   // superseded by a newer render
            _progress.Value = 1f;
            _view.Publish(rgba);
            _status.Value = $"Parallel: {clock.ElapsedMilliseconds} ms on {Environment.ProcessorCount} cores (zoom ×{zoom:0.0})";
        });
    }

    private void OnUiThread()
    {
        _cts?.Cancel();
        double zoom = _zoom *= 1.6;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var rgba = new byte[Fractal.W * Fractal.H * 4];
        for (int y = 0; y < Fractal.H; y++) Fractal.RenderRow(rgba, y, zoom);   // blocks every frame meanwhile
        _view.Publish(rgba);
        _status.Value = $"UI thread: blocked the window for {clock.ElapsedMilliseconds} ms";
    }

    protected override Widget Build(BuildContext context) =>
        new Column(crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Padding(EdgeInsets.All(12), new Row(spacing: 8, children: [
                new AdwButton(label: "Parallel", onPressed: Parallel) { Style = AdwButtonStyle.Suggested },
                new AdwButton(label: "On UI thread", onPressed: OnUiThread) { Style = AdwButtonStyle.Destructive },
                new AdwSpinner(),
                new Expanded(new Watch(() => new AdwProgressBar(_progress.Value))),
            ])),
            new Padding(EdgeInsets.Symmetric(horizontal: 12), new Watch(() => new Label(_status.Value, fontSize: 13))),
            new Expanded(new Padding(EdgeInsets.All(12), _view)),
        ]);
}
```

Things to notice:

- The pixel buffer belongs to the workers until `Publish`. Each `Parallel.For` iteration writes only its own row, so no locks are needed.
- GPU textures are created on the UI thread (in `Paint`), never on a worker.
- Progress is throttled. Writing a signal on every row from eight cores would be 400 updates for one bar.
- Cancellation flows through `ParallelOptions.CancellationToken`, and a cancelled run simply never publishes.

## Several independent jobs

`Task.Run` per job, a signal per job, and `Task.WhenAll` to know when all of them are done. Each job here simulates CPU work of a different length:

```csharp run height=300
class Demo : ComposedWidget
{
    private const int Jobs = 5;
    private readonly Signal<float>[] _progress = [.. Enumerable.Range(0, Jobs).Select(_ => new Signal<float>(0f))];
    private readonly Signal<string> _status = new("Idle");

    private static double Spin(int ms)     // real CPU work, not a sleep
    {
        var clock = System.Diagnostics.Stopwatch.StartNew(); double x = 0;
        while (clock.ElapsedMilliseconds < ms) x += Math.Sqrt(x + 1);
        return x;
    }

    private async void Start()
    {
        _status.Value = "Running…";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, Jobs).Select(j => Task.Run(() =>
        {
            for (int step = 1; step <= 20; step++)
            {
                Spin(40 + j * 25);
                _progress[j].Value = step / 20f;
            }
        }));
        await Task.WhenAll(tasks);             // resumes off the UI thread — writing a signal is fine
        _status.Value = $"All {Jobs} jobs finished in {clock.ElapsedMilliseconds} ms (sequentially it would take ~{20 * Enumerable.Range(0, Jobs).Sum(j => 40 + j * 25)} ms)";
    }

    protected override Widget Build(BuildContext context)
    {
        var column = new Column(spacing: 10, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Row(spacing: 8, children: [
                new AdwButton(label: "Start", onPressed: () => { foreach (var p in _progress) p.Value = 0; Start(); }) { Style = AdwButtonStyle.Suggested },
                new AdwSpinner(),
            ]),
        ]);
        for (int j = 0; j < Jobs; j++)
        {
            int job = j;
            column.Children.Add(new Row(spacing: 10, children: [
                new SizedBox(width: 50, child: new Label($"Job {job + 1}", fontSize: 13)),
                new Expanded(new Watch(() => new AdwProgressBar(_progress[job].Value))),
            ]));
        }
        column.Children.Add(new Watch(() => new Label(_status.Value, fontSize: 13)));
        return new Padding(EdgeInsets.All(20), column);
    }
}
```

## Choosing a tool

| Work | Tool |
|---|---|
| One slow call (I/O, a decode) | `Background.Run(work, onUi)` or `await Task.Run(...)` |
| Data-parallel CPU work over an array or image | `Parallel.For` / `Parallel.ForEach` inside `Task.Run` |
| Query-shaped CPU work | PLINQ: `items.AsParallel().Where(...).ToArray()` |
| A pipeline (read → decode → resize → save) | `System.Threading.Channels` with one consumer task per stage |
| Typed/dragged input driving work | `Background.Latest()`: debounce and latest-wins cancellation |
| Huge work that *must* touch widgets | `Background.Slice`, a few items per frame |
| SIMD inside one core | `System.Numerics.Vector<T>` / `Vector128`, AOT-friendly |

## Pitfalls

- **Never call `Parallel.For` on the UI thread.** It blocks until every worker finishes. Wrap it in `Task.Run`.
- **Never `.Wait()` or `.Result`** on the UI thread.
- **Don't share mutable state without ownership.** Give each worker its own slice of the output, or use `Interlocked`/`ConcurrentDictionary`. Signals are thread-safe, ordinary lists are not.
- **Don't spam signals from tight loops.** Throttle, or accumulate privately and publish once.
- **Create GPU resources on the UI thread.** Textures, render targets and shader registration go through the engine, which belongs to the UI thread.
- **Always pass a cancellation token**, and check it in long loops, so stale work stops when the user moves on.
