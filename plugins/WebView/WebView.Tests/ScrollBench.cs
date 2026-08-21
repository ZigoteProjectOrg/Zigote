using System.Diagnostics;
using Xunit;
using Zigote.Core.Animation;
using Zigote.Core.Engine;

namespace WebView.Tests;

/// <summary>
///     What a scrolling page costs the UI thread on the Wayland texture path, and how evenly it
///     delivers frames — the two numbers behind "the webview stutters". Simulates the engine's
///     60 Hz loop (one <see cref="Ticker.AdvanceAll" /> per frame, which is what drives the GLib
///     pump and the surface conversion) while scrolling every frame.
///     <para>
///         Off by default — it takes five seconds and measures a machine, not a contract. Run it
///         with <c>ZIGOTE_WEBVIEW_BENCH=1</c>, size it with <c>BENCH_W</c>/<c>BENCH_H</c>.
///     </para>
/// </summary>
public class ScrollBench
{
    [Fact]
    public void ScrollCostAndCadence()
    {
        if (Environment.GetEnvironmentVariable("ZIGOTE_WEBVIEW_BENCH") is not { Length: > 0 }) return;
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null &&
            Environment.GetEnvironmentVariable("DISPLAY") is null) return;

        int w = int.Parse(Environment.GetEnvironmentVariable("BENCH_W") ?? "1280");
        int h = int.Parse(Environment.GetEnvironmentVariable("BENCH_H") ?? "800");
        using var controller = new WebViewController();
        controller.EnsureAttached(new NativeParent(NativeParentKind.Wayland, 0, 0));
        var backend = (WebKitOffscreenBackend)controller.TextureBackend!;
        backend.SetSurfaceSize(w, h, 1f);

        // Ordinary document content — text, borders, shadows, gradients — not a synthetic
        // worst case: this is the load a browser tab actually puts on the software rasterizer.
        string rows = string.Concat(Enumerable.Range(0, 400).Select(i =>
            $"<p style='padding:8px;border:1px solid #ccc;box-shadow:0 1px 3px rgba(0,0,0,.3);" +
            $"background:linear-gradient(90deg,#fff,#eef)'>Row {i} — the quick brown fox jumps " +
            "over the lazy dog, repeatedly and with feeling.</p>"));
        controller.LoadHtml($"<body style='margin:0;font:16px sans-serif'>{rows}</body>");

        var warmup = Stopwatch.StartNew();
        while (warmup.Elapsed < TimeSpan.FromSeconds(5))
        {
            Ticker.AdvanceAll(1f / 60f);
            if (backend.FrameVersion > 3) break;
            Thread.Sleep(16);
        }

        const int frames = 300;
        var tickMs = new List<double>(frames);
        var delivered = new List<int>(frames);
        int last = backend.FrameVersion;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            double start = clock.Elapsed.TotalMilliseconds;
            backend.Scroll(dx: 0, dy: -1, x: w / 2f, y: h / 2f, mods: Zigote.Core.Events.Modifiers.None);
            Ticker.AdvanceAll(1f / 60f);
            double cost = clock.Elapsed.TotalMilliseconds - start;
            tickMs.Add(cost);
            int version = backend.FrameVersion;
            delivered.Add(version - last);
            last = version;
            if (16.67 - cost > 0) Thread.Sleep((int)(16.67 - cost));
        }

        var sorted = tickMs.Order().ToList();
        double P(double q) => sorted[(int)(q * (sorted.Count - 1))];
        Console.WriteLine(
            $"BENCH {w}x{h} uiThreadMs p50={P(.5):F2} p90={P(.9):F2} p99={P(.99):F2} " +
            $"overBudget(>16.7ms)={tickMs.Count(t => t > 16.7)}/{frames} " +
            $"framesDelivered={delivered.Sum()}/{frames}");
        Console.WriteLine("BENCH cadence " + string.Concat(delivered.Take(120).Select(d => d > 9 ? "+" : d.ToString())));
    }

    /// <summary>
    ///     What a page that is doing nothing costs. A browser tab that is merely open should be
    ///     close to free — this is the number that decides whether ten tabs drain a battery — and
    ///     it is the one the GTK thread's loop shape (blocking on GLib versus polling on a
    ///     heartbeat) actually moves.
    /// </summary>
    [Fact]
    public void IdlePageCost()
    {
        if (Environment.GetEnvironmentVariable("ZIGOTE_WEBVIEW_BENCH") is not { Length: > 0 }) return;
        using var controller = new WebViewController();
        controller.EnsureAttached(new NativeParent(NativeParentKind.Wayland, 0, 0));
        var backend = (WebKitOffscreenBackend)controller.TextureBackend!;
        backend.SetSurfaceSize(1280, 800, 1f);
        controller.LoadHtml("<body style='background:#204080;margin:0'><h1>static</h1></body>");

        var warm = Stopwatch.StartNew();
        while (warm.Elapsed < TimeSpan.FromSeconds(6))
        {
            Ticker.AdvanceAll(1f / 60f);
            Thread.Sleep(16);
        }

        var self = Process.GetCurrentProcess();
        var (cpu0, v0) = (self.TotalProcessorTime, backend.FrameVersion);
        var wall = Stopwatch.StartNew();
        while (wall.Elapsed < TimeSpan.FromSeconds(10))
        {
            Ticker.AdvanceAll(1f / 60f);
            Thread.Sleep(16);
        }

        self.Refresh();
        double cpuMs = (self.TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.WriteLine($"IDLE threaded={GtkThread.Threaded} cpu={cpuMs:F0}ms over 10s " +
                          $"({cpuMs / 100:F2}% of one core) framesWhileIdle={backend.FrameVersion - v0}");
    }
}
