using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Ecs;
using Zigote.Physics2D;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Zero-allocation gates for the per-frame hot paths that
///     <see cref="HotPathAllocationTests" />'s Measure→Layout→Paint pass does not reach: hit
///     testing (runs per pointer move, up to 1000 Hz), the animation ticker, a scrolling frame,
///     the partial-repaint damage diff, ECS query iteration, structural attach/detach, and the 2D
///     character controller step. Each is the steady-state loop a running app repeats every frame
///     — one byte here is a regression the in-app "UI alloc / frame" readout would show.
/// </summary>
public class FrameHotPathAllocationTests
{
    // ── hit testing ──────────────────────────────────────────────────────────

    [Fact]
    public void HitTest_OverNestedTree_AllocatesZero()
    {
        var root = new ColoredBox(
            color: Color.White,
            child: new Padding(
                padding: EdgeInsets.All(8f),
                child: new Column(
                    [
                        new Row([new SizedBox(width: 24f, height: 24f), new Label("Toolbar")]),
                        new Center(new Label("Body")),
                        new Row([new Label("Alpha"), new Label("Beta"), new Label("Gamma")]),
                    ]
                )
            )
        );
        root.Measure(Constraints.Tight(width: 800f, height: 600f));
        root.Layout(Offset.Zero);

        // Corner, toolbar row, body, bottom row — the walk each pointer move repeats.
        AllocGuard.AssertZeroAlloc(() =>
            {
                root.HitTest(new Offset(x: 1f, y: 1f));
                root.HitTest(new Offset(x: 30f, y: 20f));
                root.HitTest(new Offset(x: 400f, y: 300f));
                root.HitTest(new Offset(x: 40f, y: 580f));
            }
        );
    }

    // ── animation tick ───────────────────────────────────────────────────────

    [Fact]
    public void TickerAdvanceAll_SteadyState_AllocatesZero()
    {
        var tickers = new Ticker[4];
        for (int i = 0; i < tickers.Length; i++)
        {
            tickers[i] = new Ticker(static _ => { });
            tickers[i].Start();
        }

        try
        {
            AllocGuard.AssertZeroAlloc(static () => Ticker.AdvanceAll(1f / 60f));
        }
        finally
        {
            foreach (var t in tickers) t.Dispose();
        }
    }

    // ── scrolling frame ──────────────────────────────────────────────────────

    [Fact]
    public void ScrollingFrame_SteadyState_AllocatesZero()
    {
        var rows = new Widget[40];
        for (int i = 0; i < rows.Length; i++) rows[i] = new SizedBox(width: 200f, height: 40f);
        var scroll = new ScrollView(new Column(rows)) { ScrollVertical = true };
        var paint = new PaintList();
        var c = Constraints.Tight(width: 400f, height: 300f);
        int dir = 0;

        AllocGuard.AssertZeroAlloc(() =>
            {
                // Alternate direction so the offset keeps moving inside the range forever.
                scroll.OnScroll(dx: 0f, dy: (dir++ & 1) == 0 ? -1f : 1f);
                Ticker.AdvanceAll(1f / 60f); // drives the SmoothScroller ease
                paint.Clear();
                scroll.Measure(c);
                scroll.Layout(Offset.Zero);
                scroll.Paint(paint);
            }
        );
        Assert.True(paint.Count > 0);
    }

    // ── damage diff (partial repaint) ────────────────────────────────────────

    [Fact]
    public void PaintSnapshotCaptureAndDiff_SteadyState_AllocatesZero()
    {
        var root = new Column(
            [
                new ColoredBox(color: Color.White, child: new SizedBox(width: 100f, height: 40f)),
                new Label("Damage"),
                new ColoredBox(
                    color: Color.Rgb(r: 200, g: 10, b: 10),
                    child: new SizedBox(width: 80f, height: 20f)
                ),
            ]
        );
        var paint = new PaintList();
        var c = Constraints.Tight(width: 400f, height: 300f);
        var snap = new PaintSnapshot();

        AllocGuard.AssertZeroAlloc(() =>
            {
                paint.Clear();
                root.Measure(c);
                root.Layout(Offset.Zero);
                root.Paint(paint);
                Span<Rect> changed = stackalloc Rect[PaintSnapshot.MaxChangedRects];
                snap.Diff(current: paint, changed: changed, changedCount: out _);
                snap.Capture(paint);
            }
        );
    }

    // ── structural attach/detach (scroll row realization, reconciler churn) ──

    [Fact]
    public void AttachDetach_Subtree_SteadyState_AllocatesZero()
    {
        var root = new Column(
            [
                new Row([new SizedBox(width: 10f, height: 10f), new Label("A")]),
                new Padding(padding: EdgeInsets.All(4f), child: new Label("B")),
                new Center(new SizedBox(width: 20f, height: 20f)),
            ]
        );

        AllocGuard.AssertZeroAlloc(() =>
            {
                root.Attach(owner: null!, parent: null);
                root.Detach();
            }
        );
    }

    // ── ECS query iteration ──────────────────────────────────────────────────

    private struct Pos
    {
        public float X, Y;
    }

    [Fact]
    public void EcsForEach_SteadyState_AllocatesZero()
    {
        using var w = new EcsWorld();
        for (int i = 0; i < 1000; i++)
            w.Set(e: w.CreateEntity(), c: new Pos { X = i, Y = i });

        // The delegate is created once here; per-frame cost is the cached-query dispatch alone.
        Action<Span<Pos>> body = static span =>
        {
            foreach (ref var p in span) p.X += 0.5f;
        };

        AllocGuard.AssertZeroAlloc(() => w.ForEach(body));
    }

    // ── 2D character controller step ─────────────────────────────────────────

    [Fact]
    public void CharacterController2D_Move_SteadyState_AllocatesZero()
    {
        const float dt = 1f / 120f;
        var world = new CollisionWorld2D();
        world.AddBox(center: new Vec2(x: 0f, y: -0.5f), halfExtents: new Vec2(x: 50f, y: 0.5f));
        var c = new CharacterController2D(world: world, halfExtents: new Vec2(x: 0.4f, y: 0.5f)) {
            Position = new Vec2(x: 0f, y: 0.6f),
        };
        int dir = 0;

        AllocGuard.AssertZeroAlloc(() =>
            {
                // Walk back and forth under gravity — grounded moves plus slides, no drift off the box.
                c.Velocity = new Vec2(x: (dir++ & 1) == 0 ? 2f : -2f, y: c.Velocity.Y - (30f * dt));
                c.Move(dt);
            }
        );
    }
}
