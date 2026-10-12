using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.RuntimeBench;

/// <summary>Real SDL/wgpu frames. Frame wall time includes waits; it is not GPU execution time.</summary>
internal static class WindowPerformanceProbe
{
    public static void Run(string[] args)
    {
        var report = new PerformanceReport("native-window");
        report.Metadata["scope"] = "App.Frame wall time including native rendering and present waits. No GPU timestamps or display presentation feedback; not input-to-photon.";
        Profiler.Enabled = true;
        using var app = new App("Zigote performance probe", 1200, 900);
        app.Engine.MainWindowSetVisible(true);
        app.Engine.AppSetDockVisible(true);
        ulong initialTextureBytes = app.Engine.GetEngineStats().GpuTextureMemory;
        report.Metadata["gpu"] = app.Engine.ActiveGpu()?.ToString() ?? "unknown";
        report.Metadata["refresh_hz"] = app.DisplayRefreshHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
        report.Metadata["scale"] = app.HostScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        report.Metadata["pacing"] = "Default vsync, no software FPS cap. Profiler enabled; normal partial repaint except explicit full-repaint case.";
        var list = PerformanceProbe.BuildList(10000, "native");
        app.Root = new ColoredBox(Color.Black, list);

        // Native split of each frame: swapchain acquire wait, CPU encode+submit, present call.
        List<double>? acquire = null, encode = null, present = null;
        void Frame()
        {
            PerformanceReport.Require(!app.ShouldQuit, "Benchmark window was closed.");
            app.Frame();
            if (encode is null) return;
            Zigote.Core.Engine.ZigoteEngine.GetFrameTimings(out double a, out double e, out double p);
            acquire!.Add(a); encode.Add(e); present!.Add(p);
        }

        void Split(string name, Action measure)
        {
            // Warm separately so the split's samples line up with the measured frames only loosely:
            // it records every frame in the section, warmup included.
            acquire = []; encode = []; present = [];
            measure();
            report.Add($"{name}.native_acquire_wait", "ms", acquire.ToArray());
            report.Add($"{name}.native_encode_submit", "ms", encode.ToArray());
            report.Add($"{name}.native_present", "ms", present.ToArray());
            acquire = encode = present = null;
        }

        long readyStart = Stopwatch.GetTimestamp();
        long dropsSeen = 0;
        void Drops(string section)
        {
            report.Metadata[$"dropped_frames.{section}"] = (app.DroppedFrames - dropsSeen).ToString();
            dropsSeen = app.DroppedFrames;
        }

        // A successful FrameEnd can still mean the surface was unavailable. Require evidence
        // that the main-window persistent scene texture was actually allocated before timing.
        while (app.Engine.GetEngineStats().GpuTextureMemory <= initialTextureBytes)
        {
            app.RequestPaint();
            Frame();
            PerformanceReport.Require(Stopwatch.GetElapsedTime(readyStart).TotalSeconds < 5,
                $"No scene texture after five seconds; surface may be occluded/unavailable (paused={app.IsPaused}, hidden={app.Hidden}, dropped={app.DroppedFrames}).");
        }

        // While a window is still appearing the swapchain hands out images without pacing (frames
        // of ~0.5 ms), then blocks one acquire for hundreds of milliseconds as the window server
        // catches up. That transient was the ~295 ms "full repaint" outlier in the first baseline.
        // Wait for presentation to be paced before timing anything.
        long settleStart = Stopwatch.GetTimestamp();
        for (int paced = 0; paced < 30 && Stopwatch.GetElapsedTime(settleStart).TotalSeconds < 3;)
        {
            app.RequestPaint();
            Frame();
            Zigote.Core.Engine.ZigoteEngine.GetFrameTimings(out double acquireMs, out _, out _);
            paced = acquireMs > 2 ? paced + 1 : 0;
        }
        report.Metadata["settle_seconds"] = Stopwatch.GetElapsedTime(settleStart).TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Drops("startup");
        Split("window.full_repaint", () =>
            report.Measure("window.full_repaint", 120, 240, Frame, app.RequestPaint));
        report.Measure("window.idle_wait", 20, 120, Frame);
        // Idle cost: nothing changes for three seconds. Wakeups and CPU are what an idle app
        // costs the battery; frame time here is intentional sleep.
        {
            using var self = Process.GetCurrentProcess();
            for (int i = 0; i < 30; i++) app.Frame(); // settle
            self.Refresh();
            var cpuBefore = self.TotalProcessorTime;
            long idleStart = Stopwatch.GetTimestamp();
            int wakeups = 0;
            while (Stopwatch.GetElapsedTime(idleStart).TotalSeconds < 3) { app.Frame(); wakeups++; }
            double seconds = Stopwatch.GetElapsedTime(idleStart).TotalSeconds;
            self.Refresh();
            double cpuMs = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            report.Add("window.idle.wakeups_per_second", "1/s", [wakeups / seconds]);
            report.Add("window.idle.cpu_percent_of_one_core", "%", [cpuMs / (seconds * 10)]);
        }

        int frame = 0;
        report.Measure("window.warm_scroll", 120, 240, Frame,
            () => list.OffsetY = (frame++ % 64) * 12);
        float offset = list.OffsetY;
        Split("window.new_rows", () =>
            report.Measure("window.new_rows", 0, 240, Frame, () => list.OffsetY = offset += 120));

        Drops("paced");
        // The same frames with vsync and the software pacer off: what one frame costs the CPU
        // (UI work plus native encode and submit) when it is not waiting for the display. This is
        // the upper bound on what moving rendering to another thread could take off the UI thread.
        // Present may still block briefly on drawable availability, so read it as a ceiling.
        app.VSync = false;
        app.FrameRateLimit = -1;
        report.Measure("window.unpaced.full_repaint", 120, 240, Frame, app.RequestPaint);
        report.Measure("window.unpaced.warm_scroll", 120, 240, Frame,
            () => list.OffsetY = (frame++ % 64) * 12);
        report.Measure("window.unpaced.new_rows", 0, 240, Frame, () => list.OffsetY = offset += 120);
        report.Metadata["frame_reserve_ms_unpaced"] = app.FrameReserveMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        Drops("unpaced");
        app.FrameRateLimit = 0;
        app.VSync = true;
        for (int i = 0; i < 10; i++) { app.RequestPaint(); Frame(); }

        int completed = 0;
        Action cheap = () => completed++;
        report.Measure("window.post_10000.frame", 3, 30, Frame, () =>
        {
            completed = 0;
            for (int i = 0; i < 10000; i++) app.Post(cheap);
        });
        PerformanceReport.Require(completed == 10000, "App.Post did not drain every queued action.");
        Action expensive = () => { PerformanceReport.BusyFor(.1); completed++; };
        report.Measure("window.post_400_100us.frame", 3, 30, Frame, () =>
        {
            completed = 0;
            for (int i = 0; i < 400; i++) app.Post(expensive);
        });
        PerformanceReport.Require(completed == 400, "App.Post burst lost actions.");

        // Same flood under the opt-in bounded drain: every frame is a sample until the queue is empty.
        app.PostDrainBudgetMs = 1;
        completed = 0;
        for (int i = 0; i < 400; i++) app.Post(expensive);
        report.Measure("window.post_400_100us.bounded_1ms.frame", 0, 30, Frame);
        while (app.PostedPending > 0) Frame();
        app.PostDrainBudgetMs = 0;
        PerformanceReport.Require(completed == 400, "Bounded App.Post drain lost actions.");

        Drops("posts");
        ImageBurst(app, report);
        PartialVersusFull(app, report);
        Drops("images");
        // Frames native skipped because the swapchain image was unavailable; timings from a run
        // with any are not presentation-paced and should be discarded.
        report.Metadata["dropped_frames"] = app.DroppedFrames.ToString();
        report.Save(args);
    }

    private static void ImageBurst(App app, PerformanceReport report)
    {
        const int count = 64;
        const int dimension = 512;
        byte[] encoded = MakePng(dimension);
        var images = new Image[count];
        var tasks = new Task[count];
        int delivered = 0, failed = 0;
        var rows = new List<Widget>();
        for (int row = 0; row < 8; row++)
        {
            var cells = new List<Widget>();
            for (int column = 0; column < 8; column++)
            {
                var image = images[row * 8 + column] = new Image();
                image.OnLoaded = () => delivered++;
                image.OnFailed = _ => failed++;
                cells.Add(new SizedBox(140, 100, image));
            }
            rows.Add(new Row(cells));
        }
        app.Root = new ColoredBox(Color.Black, new Column(rows));
        for (int i = 0; i < 10; i++) { app.RequestPaint(); app.Frame(); }
        Zigote.Core.Engine.ZigoteEngine.GetImageStats(out int baseCount, out _, out long baseGpu);
        var frameTimes = new List<double>();
        var frameAllocations = new List<double>();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long baselineRss = process.WorkingSet64, peakRss = baselineRss;
        long allAllocated = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        try
        {
            for (int i = 0; i < count; i++)
                // Each request gets its own encoded payload. No filesystem/network variation;
                // this exercises the actual fetch -> decode gate -> native texture -> UI path.
                tasks[i] = images[i].LoadAsync(_ => Task.FromResult((byte[])encoded.Clone()));
            while (delivered + failed < count)
            {
                PerformanceReport.Require(Stopwatch.GetElapsedTime(start).TotalSeconds < 30,
                    "Image burst timed out.");
                PerformanceReport.Require(!app.ShouldQuit, "Benchmark window was closed.");
                long before = GC.GetAllocatedBytesForCurrentThread();
                long frameStart = Stopwatch.GetTimestamp();
                app.Frame();
                double ms = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                frameTimes.Add(ms);
                frameAllocations.Add(bytes);
                process.Refresh();
                peakRss = Math.Max(peakRss, process.WorkingSet64);
            }
            // This is outside App.Frame; all UI deliveries have arrived, so joining workers
            // cannot deadlock against UI callbacks.
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            app.RequestPaint();
            app.Frame(); // ensure every delivered texture has been submitted at least once
            Zigote.Core.Engine.ZigoteEngine.GetImageStats(out int resident, out long cpuBytes, out long gpuBytes);
            PerformanceReport.Require(gpuBytes >= (long)count * dimension * dimension * 4,
                $"Images delivered but not all uploaded: count={resident}, cpu={cpuBytes}, gpu={gpuBytes}, dropped={app.DroppedFrames}.");
            report.Add("images.64x512.end_to_end", "ms", [Stopwatch.GetElapsedTime(start).TotalMilliseconds]);
            report.Add("images.64x512.frames", "ms", frameTimes.ToArray());
            report.Add("images.64x512.ui_alloc", "bytes", frameAllocations.ToArray());
            report.Add("images.64x512.process_alloc", "bytes", [GC.GetTotalAllocatedBytes(true) - allAllocated]);
            report.Add("images.64x512.sampled_peak_rss_delta", "bytes", [Math.Max(0, peakRss - baselineRss)]);
            PerformanceReport.Require(delivered == count && failed == 0 && images.All(x => x.HasTexture),
                $"Image decode failed: delivered={delivered}, failed={failed}");
            report.Metadata["image_fixture"] = $"{count} independent {dimension}x{dimension} RGB PNG payloads, {encoded.Length} bytes each, full resolution; no I/O, no scaling. RSS sampled between frames, not a true peak or leak test.";
            report.Metadata["image_gpu_bytes"] = gpuBytes.ToString();
        }
        finally
        {
            foreach (var image in images) image.Dispose();
            app.Root = new ColoredBox(Color.Black);
            for (int i = 0; i < 10; i++) { app.RequestPaint(); app.Frame(); }
        }
        RequireImagesReclaimed(baseCount, baseGpu, "after the burst");

        // Flood then cancel: every load is disposed while it is queued, fetching, or decoding.
        // Whatever each one had produced by then must be released, not leaked.
        const int flood = 512;
        var cancelled = new Image[flood];
        var cancelledTasks = new Task[flood];
        int fetched = 0;
        for (int i = 0; i < flood; i++)
        {
            cancelled[i] = new Image();
            cancelledTasks[i] = cancelled[i].LoadAsync(_ =>
            {
                Interlocked.Increment(ref fetched);
                return Task.FromResult((byte[])encoded.Clone());
            });
        }
        app.Frame(); // let some of them land before the cancel
        foreach (var image in cancelled) image.Dispose();
        var settled = Task.WhenAll(cancelledTasks).ContinueWith(_ => { });
        long floodStart = Stopwatch.GetTimestamp();
        while (!settled.IsCompleted || app.PostedPending > 0)
        {
            PerformanceReport.Require(Stopwatch.GetElapsedTime(floodStart).TotalSeconds < 30,
                "Cancelled image flood did not settle.");
            app.Frame();
        }
        for (int i = 0; i < 10; i++) { app.RequestPaint(); app.Frame(); }
        RequireImagesReclaimed(baseCount, baseGpu, "after the cancelled flood");
        report.Metadata["image_cancel_flood"] = $"{flood} loads disposed after one frame; {fetched} had started fetching; all textures reclaimed.";
    }

    // Correctness, not timing: after each partial repaint the retained scene texture must hold
    // exactly the pixels a forced full repaint of the same tree produces. Every third step also
    // changes a second box WITHOUT marking it, which the paint-diff damage widening must catch.
    private static void PartialVersusFull(App app, PerformanceReport report)
    {
        const int cells = 48, steps = 24;
        var boxes = new ColoredBox[cells];
        var rows = new List<Widget>();
        for (int row = 0; row < 6; row++)
        {
            var line = new List<Widget>();
            for (int column = 0; column < 8; column++)
            {
                int i = row * 8 + column;
                boxes[i] = new ColoredBox(Color.Rgb(30 + i * 3, 60, 90),
                    new SizedBox(130, 120, new Label($"cell {i}")));
                line.Add(new Padding(EdgeInsets.All(4), boxes[i]));
            }
            rows.Add(new Row(line));
        }
        app.Root = new ColoredBox(Color.Black, new Column(rows));
        for (int i = 0; i < 10; i++) { app.RequestPaint(); app.Frame(); }

        string dir = Path.Combine(Path.GetTempPath(), $"zigote-partial-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string partialPath = Path.Combine(dir, "partial.bmp"), fullPath = Path.Combine(dir, "full.bmp");
        long dropsBefore = app.DroppedFrames;
        byte[]? previous = null;
        try
        {
            for (int step = 0; step < steps; step++)
            {
                var marked = boxes[step * 7 % cells];
                marked.Color = Color.Rgb(200, 40 + step * 8, 60);
                app.RequestPaintFor(marked);
                if (step % 3 == 0) boxes[(step * 11 + 5) % cells].Color = Color.Rgb(40, 200, 40 + step * 8);
                app.Frame();
                PerformanceReport.Require(app.Engine.CaptureSceneBmp(partialPath), "Scene capture failed.");

                app.RequestPaint();
                app.Frame();
                PerformanceReport.Require(app.Engine.CaptureSceneBmp(fullPath), "Scene capture failed.");

                byte[] partial = File.ReadAllBytes(partialPath), full = File.ReadAllBytes(fullPath);
                PerformanceReport.Require(partial.AsSpan().SequenceEqual(full),
                    $"Partial repaint left pixels a full repaint does not (step {step}); see {dir}.");
                // The comparison only means something if the capture sees each step's change.
                PerformanceReport.Require(previous is null || !previous.AsSpan().SequenceEqual(full),
                    $"Scene capture did not change between steps (step {step}).");
                previous = full;
            }
        }
        finally
        {
            app.Root = new ColoredBox(Color.Black);
            for (int i = 0; i < 10; i++) { app.RequestPaint(); app.Frame(); }
        }
        PerformanceReport.Require(app.DroppedFrames == dropsBefore,
            "Frames were dropped during the partial/full comparison; rerun with the window visible.");
        Directory.Delete(dir, recursive: true);
        report.Metadata["partial_vs_full"] = $"{steps} partial repaints byte-identical to a forced full repaint of the retained scene texture ({steps / 3} with an unmarked change).";
    }

    private static void RequireImagesReclaimed(int baseCount, long baseGpu, string when)
    {
        Zigote.Core.Engine.ZigoteEngine.GetImageStats(out int count, out _, out long gpu);
        PerformanceReport.Require(count <= baseCount && gpu <= baseGpu,
            $"Image textures not reclaimed {when}: count {baseCount}->{count}, gpu bytes {baseGpu}->{gpu}.");
    }

    // Deterministic RGB PNG fixture, generated before any measured interval. The native
    // UI loader supports PNG/JPEG, not every format supported by the engine's other importers.
    private static byte[] MakePng(int size)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; header[9] = 2;
        WriteChunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[1 + size * 3]; // filter 0
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int p = 1 + x * 3;
                    row[p] = (byte)x; row[p + 1] = (byte)y; row[p + 2] = (byte)(x ^ y);
                }
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte b in type) crc = CrcByte(crc, b);
        foreach (byte b in data) crc = CrcByte(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        return crc;
    }
}
