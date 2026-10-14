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
        var report = new PerformanceReport("headless") {
            Metadata = {
                ["scope"] =
                    "Managed production paths; headless text fallback; no GPU or presentation. Timing includes instrumentation overhead.",
                ["sampling"] =
                    "Nearest-rank quantiles, raw chronological samples, no forced GC; run three fresh processes.",
            },
        };
        Profiler.Enabled = false;
        BackgroundCases(report);
        PaintCases(report);
        ListCases(report);
        report.Save(args);
    }

    private static void BackgroundCases(PerformanceReport report)
    {
        using var background = Background.Manual();
        report.Measure(
            name: "background.empty_1000_calls",
            warmup: 200,
            samples: 1000,
            action: () =>
            {
                for (int i = 0; i < 1000; i++) background.RunFrame(TimeSpan.Zero);
            }
        );
        foreach (int count in new[] {
                     1000,
                     10000,
                 })
        {
            int completed = 0;
            Action callback = () => completed++;
            report.Measure(
                name: $"background.enqueue.{count}",
                warmup: 10,
                samples: 100,
                action: () =>
                {
                    for (int i = 0; i < count; i++)
                        background.Post(ui: callback, deliver: Deliver.WhenIdle);
                },
                setup: () =>
                {
                    background.RunFrame(TimeSpan.FromSeconds(10));
                    completed = 0;
                }
            );
            background.RunFrame(TimeSpan.FromSeconds(10));
            PerformanceReport.Require(
                condition: completed == count,
                message: "Enqueued callbacks were lost."
            );
            report.Measure(
                name: $"background.drain.{count}",
                warmup: 10,
                samples: 100,
                action: () => background.RunFrame(TimeSpan.FromSeconds(10)),
                setup: () =>
                {
                    completed = 0;
                    for (int i = 0; i < count; i++)
                        background.Post(ui: callback, deliver: Deliver.WhenIdle);
                }
            );
            PerformanceReport.Require(
                condition: completed == count && background.FrameIdle,
                message: "Drain did not finish."
            );
        }

        const int burst = 400;
        int delivered = 0;
        for (int i = 0; i < burst; i++)
        {
            background.Post(
                ui: () =>
                {
                    PerformanceReport.BusyFor(.1);
                    delivered++;
                },
                deliver: Deliver.WhenIdle
            );
        }

        var drainTimes = new List<double>();
        var units = new List<double>();
        while (!background.FrameIdle)
        {
            int before = delivered;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            background.RunFrame(TimeSpan.FromMilliseconds(.5));
            drainTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            units.Add(delivered - before);
            PerformanceReport.Require(
                condition: drainTimes.Count <= burst,
                message: "Budgeted drain made no progress."
            );
        }

        PerformanceReport.Require(
            condition: delivered == burst,
            message: "Budgeted callbacks were lost."
        );
        report.Add(
            name: "background.burst_400_100us.budget_0.5",
            unit: "ms",
            samples: drainTimes.ToArray()
        );
        report.Add(
            name: "background.burst_400_100us.units",
            unit: "callbacks",
            samples: units.ToArray()
        );
        report.Metadata["burst_drain_calls"] = drainTimes.Count.ToString();

        // Current policy guarantees one idle callback AND one slice unit despite a zero budget.
        using var overrun = Background.Manual();
        int steps = 0, idle = 0;
        overrun.Slice(
            key: "expensive",
            count: 1000,
            step: _ =>
            {
                PerformanceReport.BusyFor(2);
                steps++;
            },
            firstFrame: TimeSpan.Zero
        );
        report.Measure(
            name: "background.zero_budget.two_2ms_units",
            warmup: 5,
            samples: 40,
            action: () => overrun.RunFrame(TimeSpan.Zero),
            setup: () => overrun.Post(
                ui: () =>
                {
                    PerformanceReport.BusyFor(2);
                    idle++;
                },
                deliver: Deliver.WhenIdle
            )
        );
        PerformanceReport.Require(
            condition: idle == 45 && steps == 46,
            message: "Zero-budget progress contract changed."
        );
    }

    private static void PaintCases(PerformanceReport report)
    {
        foreach (int count in new[] {
                     1000,
                     10000,
                 })
        {
            var paint = new PaintList();
            var snapshot = new PaintSnapshot();
            bool alternate = false;

            void Build(bool changed)
            {
                paint.Clear();
                for (int i = 0; i < count; i++)
                {
                    paint.AddRect(
                        bounds: new Rect(
                            x: i % 100 * 12,
                            y: i / 100 * 12,
                            width: 10,
                            height: 10
                        ),
                        color: changed && i == count / 2 ? Color.White : Color.Black
                    );
                }
            }

            Build(false);
            snapshot.Capture(paint);
            report.Measure(
                name: $"paint.record.{count}",
                warmup: 200,
                samples: 512,
                action: () => Build(false)
            );
            PaintDiffResult result = default;

            void Diff()
            {
                Span<Rect> rects = stackalloc Rect[PaintSnapshot.MaxChangedRects];
                result = snapshot.Diff(current: paint, changed: rects, changedCount: out _);
                snapshot.Capture(paint);
            }

            report.Measure(
                name: $"paint.diff_identical_capture.{count}",
                warmup: 200,
                samples: 512,
                action: Diff
            );
            PerformanceReport.Require(
                condition: result == PaintDiffResult.Identical,
                message: "Identical paint differs."
            );
            report.Measure(
                name: $"paint.diff_one_change_capture.{count}",
                warmup: 200,
                samples: 512,
                action: Diff,
                setup: () =>
                {
                    alternate = !alternate;
                    Build(alternate);
                }
            );
            PerformanceReport.Require(
                condition: result == PaintDiffResult.Bounded,
                message: "One changed rect was not bounded."
            );
        }
    }

    internal static ListView BuildList(int count, string prefix)
    {
        var list = new ListView(itemExtent: 120);
        list.SetBuilder(
            itemCount: count,
            itemBuilder: i => new Padding(
                padding: EdgeInsets.All(8),
                child: new Row(
                    [
                        new ColoredBox(
                            color: Color.Rgb(r: 40 + (i % 60), g: 44, b: 52),
                            child: new SizedBox(width: 160, height: 100)
                        ),
                        new Padding(
                            padding: EdgeInsets.All(12),
                            child: new Column(
                                [
                                    new Label($"{prefix} item {i}"),
                                    new Label($"Subtitle for {prefix} row {i}"),
                                ]
                            )
                        ),
                    ]
                )
            )
        );
        return list;
    }

    private static void ListCases(PerformanceReport report)
    {
        var list = BuildList(count: 10000, prefix: "warm");
        var paint = new PaintList();
        var snapshot = new PaintSnapshot();
        var constraints = Constraints.Tight(width: 1200, height: 900);
        PaintDiffResult diff = default;

        void Frame()
        {
            list.Measure(constraints);
            list.Layout(Offset.Zero);
            paint.Clear();
            list.Paint(paint);
            Span<Rect> rects = stackalloc Rect[PaintSnapshot.MaxChangedRects];
            diff = snapshot.Diff(current: paint, changed: rects, changedCount: out _);
            snapshot.Capture(paint);
        }

        report.Measure(
            name: "list.static_10000",
            warmup: 300,
            samples: 1000,
            action: Frame
        );
        PerformanceReport.Require(
            condition: paint.Count > 0 && diff == PaintDiffResult.Identical,
            message: "Static tree was not painted."
        );
        int frame = 0;
        report.Measure(
            name: "list.warm_scroll_10000",
            warmup: 300,
            samples: 1000,
            action: Frame,
            setup: () => list.OffsetY = frame++ % 64 * 12
        );
        float startOffset = list.OffsetY;
        report.Measure(
            name: "list.new_rows_10000",
            warmup: 0,
            samples: 1000,
            action: Frame,
            setup: () => list.OffsetY = startOffset += 120
        );
        PerformanceReport.Require(
            condition: list.OffsetY > 100000 && paint.Count > 0,
            message: "Scroll benchmark stopped moving."
        );
        report.Metadata["list_final_paint_commands"] = paint.Count.ToString();
        report.Metadata["list_new_rows"] =
            "Unique row labels during measured pass after machinery warmup; headless text sizing. Offset setter excluded from measured frame.";
    }
}
