using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.Core.Paint;
using Zigote.Core.Threading;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.RuntimeBench;

/// <summary>Production managed paths, headless. Does not model native text, GPU work or presentation.</summary>
internal static class PerformanceProbe
{
    public static void Run(string[] args)
    {
        var report = new PerformanceReport("headless");
        report.Metadata["scope"] = "Managed production paths; headless text fallback; no GPU or presentation. Timing includes instrumentation overhead.";
        report.Metadata["sampling"] = "Nearest-rank quantiles, raw chronological samples, no forced GC; run three fresh processes.";
        Profiler.Enabled = false;
        BackgroundCases(report);
        PaintCases(report);
        ListCases(report);
        report.Save(args);
    }

    private static void BackgroundCases(PerformanceReport report)
    {
        using var background = Background.Manual();
        report.Measure("background.empty_1000_calls", 200, 1000, () =>
        {
            for (int i = 0; i < 1000; i++) background.RunFrame(TimeSpan.Zero);
        });
        foreach (int count in new[] { 1000, 10000 })
        {
            int completed = 0;
            Action callback = () => completed++;
            report.Measure($"background.enqueue.{count}", 10, 100,
                () => { for (int i = 0; i < count; i++) background.Post(callback, Deliver.WhenIdle); },
                () => { background.RunFrame(TimeSpan.FromSeconds(10)); completed = 0; });
            background.RunFrame(TimeSpan.FromSeconds(10));
            PerformanceReport.Require(completed == count, "Enqueued callbacks were lost.");
            report.Measure($"background.drain.{count}", 10, 100,
                () => background.RunFrame(TimeSpan.FromSeconds(10)),
                () => { completed = 0; for (int i = 0; i < count; i++) background.Post(callback, Deliver.WhenIdle); });
            PerformanceReport.Require(completed == count && background.FrameIdle, "Drain did not finish.");
        }

        const int burst = 400;
        int delivered = 0;
        for (int i = 0; i < burst; i++) background.Post(() =>
        {
            PerformanceReport.BusyFor(.1);
            delivered++;
        }, Deliver.WhenIdle);
        var drainTimes = new List<double>();
        var units = new List<double>();
        while (!background.FrameIdle)
        {
            int before = delivered;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            background.RunFrame(TimeSpan.FromMilliseconds(.5));
            drainTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            units.Add(delivered - before);
            PerformanceReport.Require(drainTimes.Count <= burst, "Budgeted drain made no progress.");
        }
        PerformanceReport.Require(delivered == burst, "Budgeted callbacks were lost.");
        report.Add("background.burst_400_100us.budget_0.5", "ms", drainTimes.ToArray());
        report.Add("background.burst_400_100us.units", "callbacks", units.ToArray());
        report.Metadata["burst_drain_calls"] = drainTimes.Count.ToString();

        // Current policy guarantees one idle callback AND one slice unit despite a zero budget.
        using var overrun = Background.Manual();
        int steps = 0, idle = 0;
        overrun.Slice("expensive", 1000, _ => { PerformanceReport.BusyFor(2); steps++; }, firstFrame: TimeSpan.Zero);
        report.Measure("background.zero_budget.two_2ms_units", 5, 40,
            () => overrun.RunFrame(TimeSpan.Zero),
            () => overrun.Post(() => { PerformanceReport.BusyFor(2); idle++; }, Deliver.WhenIdle));
        PerformanceReport.Require(idle == 45 && steps == 46, "Zero-budget progress contract changed.");
    }

    private static void PaintCases(PerformanceReport report)
    {
        foreach (int count in new[] { 1000, 10000 })
        {
            var paint = new PaintList();
            var snapshot = new PaintSnapshot();
            bool alternate = false;
            void Build(bool changed)
            {
                paint.Clear();
                for (int i = 0; i < count; i++) paint.AddRect(
                    new Rect((i % 100) * 12, (i / 100) * 12, 10, 10),
                    changed && i == count / 2 ? Color.White : Color.Black);
            }
            Build(false);
            snapshot.Capture(paint);
            report.Measure($"paint.record.{count}", 200, 512, () => Build(false));
            PaintDiffResult result = default;
            void Diff()
            {
                Span<Rect> rects = stackalloc Rect[PaintSnapshot.MaxChangedRects];
                result = snapshot.Diff(paint, rects, out _);
                snapshot.Capture(paint);
            }
            report.Measure($"paint.diff_identical_capture.{count}", 200, 512, Diff);
            PerformanceReport.Require(result == PaintDiffResult.Identical, "Identical paint differs.");
            report.Measure($"paint.diff_one_change_capture.{count}", 200, 512, Diff,
                () => { alternate = !alternate; Build(alternate); });
            PerformanceReport.Require(result == PaintDiffResult.Bounded, "One changed rect was not bounded.");
        }
    }

    internal static ListView BuildList(int count, string prefix)
    {
        var list = new ListView(itemExtent: 120);
        list.SetBuilder(count, i => new Padding(EdgeInsets.All(8), new Row([
            new ColoredBox(Color.Rgb(40 + i % 60, 44, 52), new SizedBox(160, 100)),
            new Padding(EdgeInsets.All(12), new Column([
                new Label($"{prefix} item {i}"), new Label($"Subtitle for {prefix} row {i}")
            ]))
        ])));
        return list;
    }

    private static void ListCases(PerformanceReport report)
    {
        var list = BuildList(10000, "warm");
        var paint = new PaintList();
        var snapshot = new PaintSnapshot();
        var constraints = Constraints.Tight(1200, 900);
        PaintDiffResult diff = default;
        void Frame()
        {
            list.Measure(constraints);
            list.Layout(Offset.Zero);
            paint.Clear();
            list.Paint(paint);
            Span<Rect> rects = stackalloc Rect[PaintSnapshot.MaxChangedRects];
            diff = snapshot.Diff(paint, rects, out _);
            snapshot.Capture(paint);
        }
        report.Measure("list.static_10000", 300, 1000, Frame);
        PerformanceReport.Require(paint.Count > 0 && diff == PaintDiffResult.Identical, "Static tree was not painted.");
        int frame = 0;
        report.Measure("list.warm_scroll_10000", 300, 1000, Frame,
            () => list.OffsetY = (frame++ % 64) * 12);
        float startOffset = list.OffsetY;
        report.Measure("list.new_rows_10000", 0, 1000, Frame,
            () => list.OffsetY = startOffset += 120);
        PerformanceReport.Require(list.OffsetY > 100000 && paint.Count > 0, "Scroll benchmark stopped moving.");
        report.Metadata["list_final_paint_commands"] = paint.Count.ToString();
        report.Metadata["list_new_rows"] = "Unique row labels during measured pass after machinery warmup; headless text sizing. Offset setter excluded from measured frame.";
    }
}
