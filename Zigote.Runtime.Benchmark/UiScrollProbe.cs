using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.RuntimeBench;

/// <summary>
///     Per-phase cost of a <em>scrolling</em> frame, headless.
///     <para>
///         The existing `UiPerformanceTests` / `ComplexUiPerformanceTests` time a static
///         Measure→Layout→Paint over a settled tree. A scroll frame is the one that was actually
///         measured to hurt (`ZIGOTE_IMAGEGRID_BENCH`: 73% of a core), and it runs three things they
///         do not: the scroll physics tick, row realisation/eviction as the window moves, and the
///         `PaintSnapshot` damage diff that decides whether the frame is partial or full.
///     </para>
///     <para>
///         Phases are timed separately and each is reported against the 60 Hz and 144 Hz budgets, so
///         the output says which phase to attack rather than that the frame is fast overall.
///     </para>
/// </summary>
internal static class UiScrollProbe
{
    private const int Frames = 2000;
    private const float ViewW = 1200f, ViewH = 900f;

    private static int _sink;

    public static void Run(string[] args)
    {
        int items = args.Length > 0 && int.TryParse(s: args[0], result: out int parsed)
            ? parsed
            : 2000;

        Console.WriteLine(
            $"UI scroll probe — virtualised list of {items:N0} rows, "
            + $"{ViewW:F0}×{ViewH:F0} viewport, {Frames:N0} frames"
        );
        Console.WriteLine(new string(c: '─', count: 84));

        var list = BuildList(items: items, withText: true);

        var paint = new PaintList();
        var snapshot = new PaintSnapshot();
        var c = Constraints.Tight(width: ViewW, height: ViewH);

        // Warm: JIT, row realisation, the text shaping/measure caches, and one captured snapshot so
        // the first diffed frame is a steady-state one rather than a first-capture one.
        for (int i = 0; i < 300; i++)
        {
            list.OnScroll(dx: 0f, dy: -1f);
            Ticker.AdvanceAll(1f / 60f);
            paint.Clear();
            list.Measure(c);
            list.Layout(Offset.Zero);
            list.Paint(paint);
            snapshot.Capture(paint);
        }

        // Each phase is measured in its own pass over the same scroll sequence, so a phase never
        // pays for the one before it in the same timer. Every pass runs the whole frame — only the
        // stopwatch moves — otherwise the tree drifts out of the state the next phase expects.
        double scrollNs = Pass(
            list: list,
            paint: paint,
            snapshot: snapshot,
            c: c,
            phase: Phase.Scroll
        );
        double measureNs = Pass(
            list: list,
            paint: paint,
            snapshot: snapshot,
            c: c,
            phase: Phase.Measure
        );
        double layoutNs = Pass(
            list: list,
            paint: paint,
            snapshot: snapshot,
            c: c,
            phase: Phase.Layout
        );
        double paintNs = Pass(
            list: list,
            paint: paint,
            snapshot: snapshot,
            c: c,
            phase: Phase.Paint
        );
        double diffNs = Pass(
            list: list,
            paint: paint,
            snapshot: snapshot,
            c: c,
            phase: Phase.Diff
        );
        double hitNs = Pass(
            list: list,
            paint: paint,
            snapshot: snapshot,
            c: c,
            phase: Phase.HitTest
        );

        double total = scrollNs + measureNs + layoutNs + paintNs + diffNs + hitNs;

        Console.WriteLine(
            $"{"phase",-22}{"µs/frame",12}{"% of 60Hz",12}{"% of 144Hz",12}{"% of frame",13}"
        );
        Console.WriteLine(new string(c: '─', count: 84));
        Row(label: "scroll tick", ns: scrollNs, total: total);
        Row(label: "measure", ns: measureNs, total: total);
        Row(label: "layout", ns: layoutNs, total: total);
        Row(label: "paint walk", ns: paintNs, total: total);
        Row(label: "damage diff+capture", ns: diffNs, total: total);
        Row(label: "hit test", ns: hitNs, total: total);
        Console.WriteLine(new string(c: '─', count: 84));
        Row(label: "TOTAL", ns: total, total: total);
        Console.WriteLine();
        Console.WriteLine($"paint commands/frame : {paint.Count}");
        Console.WriteLine();
        Console.WriteLine(
            total / 1000f > 0.20 * 16_670f / 1000f * 1000f
                ? "The C# frame pipeline is a real share of the budget — attack the hottest phase above."
                : "The C# frame pipeline is a small share of the budget. If a real app is dropping\n"
                  + "frames while scrolling, the cost is NOT here — look at image decode/upload, the\n"
                  + "FFI submit, and the render graph + vsync block (see the render-thread section of\n"
                  + "docs/notes/parallelism-simd-and-atomics.md)."
        );

        // ── Is `measure` text? ───────────────────────────────────────────────────
        // The same tree with each Label swapped for a SizedBox of the same footprint. Everything
        // else — row count, realisation churn, the Row/Column/Padding flex work — is identical, so
        // the delta is what text sizing costs on a scroll frame.
        var textless = BuildList(items: items, withText: false);
        var paint2 = new PaintList();
        var snap2 = new PaintSnapshot();
        for (int i = 0; i < 300; i++)
        {
            textless.OnScroll(dx: 0f, dy: -1f);
            Ticker.AdvanceAll(1f / 60f);
            paint2.Clear();
            textless.Measure(c);
            textless.Layout(Offset.Zero);
            textless.Paint(paint2);
            snap2.Capture(paint2);
        }

        // Interleaved, not sequential. Measuring the two trees one after the other lets this
        // machine's frequency drift land entirely on whichever ran second — the same mistake that
        // produced a retracted "-13%" elsewhere in this harness. Alternate them round by round.
        (double withText, double measureNoText) = PassPair(
            a: list,
            aPaint: paint,
            aSnap: snapshot,
            b: textless,
            bPaint: paint2,
            bSnap: snap2,
            c: c
        );
        Console.WriteLine();
        Console.WriteLine("measure, with vs without Labels (same row count and layout work,");
        Console.WriteLine("interleaved round-by-round so clock drift hits both equally):");
        Console.WriteLine($"  with text    : {withText / 1000f,7:F2} µs");
        Console.WriteLine($"  without text : {measureNoText / 1000f,7:F2} µs");
        Console.WriteLine(
            $"  => text is {(withText - measureNoText) / withText * 100,5:F1}% of measure, "
            + $"{(withText - measureNoText) / total * 100,5:F1}% of the whole frame"
        );

        // ── Warm cache vs cold ───────────────────────────────────────────────────
        // Everything above oscillates over a ~64-frame window, so the same rows — and the same
        // strings — recur and TextMeasure hits every time. A real "flick through a long list"
        // never revisits a row: every label is a cache MISS. That is the case ImageGrid runs.
        //
        // NOTE what this can and cannot see. Headless, ZigoteEngine is absent, so a TextMeasure
        // miss falls back to the coarse proportional estimate. The delta below is therefore the
        // C#-side miss cost only — string materialisation, hashing, dictionary insert, generation
        // eviction. With a live engine a miss also pays an FFI call and HarfBuzz shaping, which
        // this probe structurally cannot measure. Treat the number as a floor.
        Console.WriteLine();
        Console.WriteLine("text cache, warm vs cold (monotonic scroll never revisits a row):");
        Console.WriteLine($"  warm (oscillating) : {withText / 1000f,7:F2} µs");
        double cold = ColdPass(list: BuildList(items: items, withText: true), c: c, items: items);
        Console.WriteLine($"  cold (monotonic)   : {cold / 1000f,7:F2} µs");
        Console.WriteLine(
            $"  => a never-seen-before row costs {cold / withText,4:F1}× a cached one to measure"
        );
        Console.WriteLine("     (C#-side only — a live engine adds FFI + HarfBuzz on every miss)");
    }

    /// <summary>
    ///     Measure cost while scrolling one direction through the whole list, so every row and every
    ///     label string is seen exactly once and <c>TextMeasure</c> misses every time.
    /// </summary>
    private static double ColdPass(ListView list, Constraints c, int items)
    {
        var paint = new PaintList();
        // Warm the JIT and the row machinery without warming the *string* cache: run the frame
        // shape a few hundred times at the top of the list, which touches only the first rows.
        for (int i = 0; i < 300; i++)
        {
            list.Measure(c);
            list.Layout(Offset.Zero);
            paint.Clear();
            list.Paint(paint);
        }

        long acc = 0;
        int frames = 0;
        // One pass down the list. Each frame advances by roughly a row, so the realisation window
        // moves onto fresh indices and fresh strings continuously.
        for (int f = 0; f < items && f < 4000; f++)
        {
            list.OnScroll(dx: 0f, dy: -4f);
            Ticker.AdvanceAll(1f / 60f);
            paint.Clear();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            list.Measure(c);
            acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            list.Layout(Offset.Zero);
            list.Paint(paint);
            frames++;
        }

        return acc * 1e9 / System.Diagnostics.Stopwatch.Frequency / frames;
    }

    /// <summary>
    ///     An ImageGrid-shaped row: a card with a picture-sized block and two lines of text. The
    ///     block stands in for the texture — a real Image widget paints one AddImage command either
    ///     way, and a texture handle needs an initialised engine this probe deliberately avoids.
    ///     <paramref name="withText" /> swaps the Labels for equally-sized boxes, to price text.
    /// </summary>
    private static ListView BuildList(int items, bool withText)
    {
        var list = new ListView(itemExtent: 120d);
        list.SetBuilder(
            itemCount: items,
            itemBuilder: i => new Padding(
                padding: EdgeInsets.All(8f),
                child: new Row(
                    [
                        new ColoredBox(
                            color: Color.Rgb(r: 40 + (i % 60), g: 44, b: 52),
                            child: new SizedBox(width: 160f, height: 100f)
                        ),
                        new Padding(
                            padding: EdgeInsets.All(12f),
                            child: new Column(
                                withText
                                    ? [new Label($"Item {i}"), new Label($"a subtitle for row {i}")]
                                    : [
                                        new SizedBox(width: 60f, height: 18f),
                                        new SizedBox(width: 150f, height: 18f),
                                    ]
                            )
                        ),
                    ]
                )
            )
        );
        return list;
    }

    private static void Row(string label, double ns, double total)
    {
        double us = ns / 1000f;
        Console.WriteLine(
            $"{label,-22}{us,12:F2}{us / 166.7,11:F2}%{us / 69.4,11:F2}%{ns / total * 100,12:F1}%"
        );
    }

    private enum Phase { Scroll, Measure, Layout, Paint, Diff, HitTest }

    /// <summary>
    ///     Time the Measure phase of two trees, alternating them round by round and keeping each
    ///     one's best. Any clock drift across the run then hits both equally instead of biasing the
    ///     comparison — see [[zigote-bench-variance]]: this machine's spread across sequential runs
    ///     reaches 3×, which is larger than most effects worth measuring here.
    /// </summary>
    private static (double A, double B) PassPair(
        ListView a, PaintList aPaint, PaintSnapshot aSnap,
        ListView b, PaintList bPaint, PaintSnapshot bSnap, Constraints c)
    {
        double bestA = double.MaxValue, bestB = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            bestA = Math.Min(
                val1: bestA,
                val2: OneRound(
                    list: a,
                    paint: aPaint,
                    snap: aSnap,
                    c: c
                )
            );
            bestB = Math.Min(
                val1: bestB,
                val2: OneRound(
                    list: b,
                    paint: bPaint,
                    snap: bSnap,
                    c: c
                )
            );
        }

        return (bestA, bestB);
    }

    private static double OneRound(ListView list, PaintList paint, PaintSnapshot snap,
        Constraints c)
    {
        long acc = 0;
        int dir = 0;
        for (int f = 0; f < Frames; f++)
        {
            list.OnScroll(dx: 0f, dy: (dir++ & 63) < 32 ? -1f : 1f);
            Ticker.AdvanceAll(1f / 60f);
            paint.Clear();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            list.Measure(c);
            acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            list.Layout(Offset.Zero);
            list.Paint(paint);
            snap.Capture(paint);
        }

        return acc * 1e9 / System.Diagnostics.Stopwatch.Frequency / Frames;
    }

    /// <summary>
    ///     Run the whole scroll frame `Frames` times, but only time <paramref name="phase" />.
    ///     Best of five, because this machine's frequency scaling is wider than most of these phases.
    /// </summary>
    private static double Pass(ListView list, PaintList paint, PaintSnapshot snapshot,
        Constraints c, Phase phase)
    {
        double best = double.MaxValue;

        for (int round = 0; round < 5; round++)
        {
            long acc = 0;
            int dir = 0;
            for (int f = 0; f < Frames; f++)
            {
                // Alternate direction so the offset keeps moving inside the range forever, and the
                // row window keeps realising/evicting instead of settling.
                float dy = (dir++ & 63) < 32 ? -1f : 1f;

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                list.OnScroll(dx: 0f, dy: dy);
                Ticker.AdvanceAll(1f / 60f);
                if (phase == Phase.Scroll) acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                paint.Clear();

                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                list.Measure(c);
                if (phase == Phase.Measure) acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                list.Layout(Offset.Zero);
                if (phase == Phase.Layout) acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                list.Paint(paint);
                if (phase == Phase.Paint) acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Span<Rect> changed = stackalloc Rect[PaintSnapshot.MaxChangedRects];
                _sink += (int)snapshot.Diff(current: paint, changed: changed, changedCount: out _);
                snapshot.Capture(paint);
                if (phase == Phase.Diff) acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                // A pointer move lands somewhere over the list every frame while dragging.
                if (list.HitTest(new Offset(x: 300f, y: 100f + (f % 700))) is not null) _sink++;
                if (phase == Phase.HitTest) acc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }

            double ns = acc * 1e9 / System.Diagnostics.Stopwatch.Frequency / Frames;
            best = Math.Min(val1: best, val2: ns);
        }

        return best;
    }
}
