using Xunit;
using Zigote.Core;
using Zigote.UI.Host;

namespace Zigote.Tests;

/// <summary>
///     Layer-granularity dirty tracking (the first increment of dirty-region repaint). Verifies that a
///     change confined to one paint layer (root vs overlay) re-walks only that layer — e.g. an idle
///     debug-overlay metrics tick must not re-paint the whole root tree, and a root-widget drag must
///     not re-paint the overlay layer.
/// </summary>
public class RepaintTrackerTests
{
    // Mirror App's paint step: re-walk each dirty layer, then mark it painted.
    private static void PaintFrame(RepaintTracker t)
    {
        if (t.RootDirty) t.RootPainted();
        if (t.OverlayDirty) t.OverlayPainted();
    }

    [Fact]
    public void StartsDirty_FirstFramePaintsBothLayers()
    {
        var t = new RepaintTracker();
        Assert.True(t.RootDirty);
        Assert.True(t.OverlayDirty);
        Assert.True(t.AnyDirty);

        PaintFrame(t);
        Assert.Equal(1, t.RootPaints);
        Assert.Equal(1, t.OverlayPaints);
        Assert.False(t.AnyDirty); // settled — a subsequent idle frame paints nothing
    }

    [Fact]
    public void OverlayOnlyMarks_NeverRepaintRoot()
    {
        var t = new RepaintTracker();
        PaintFrame(t); // settle at (1, 1)

        // e.g. debug-overlay live metrics / tooltip / snackbar ticking every frame
        for (var i = 0; i < 10; i++)
        {
            t.MarkOverlay();
            Assert.True(t.AnyDirty);
            Assert.False(t.RootDirty);
            PaintFrame(t);
        }

        Assert.Equal(1, t.RootPaints); // the (potentially large) root tree is walked exactly once
        Assert.Equal(11, t.OverlayPaints); // overlay re-walked each tick (+ the initial frame)
    }

    [Fact]
    public void RootOnlyMarks_NeverRepaintOverlay()
    {
        var t = new RepaintTracker();
        PaintFrame(t); // settle

        for (var i = 0; i < 10; i++)
        {
            t.MarkRoot(); // e.g. dragging a root slider
            Assert.True(t.RootDirty);
            Assert.False(t.OverlayDirty);
            PaintFrame(t);
        }

        Assert.Equal(11, t.RootPaints);
        Assert.Equal(1, t.OverlayPaints); // overlay never re-walked during the drag
    }

    [Fact]
    public void Settled_NothingDirty_PaintsNothing()
    {
        var t = new RepaintTracker();
        PaintFrame(t);
        Assert.False(t.AnyDirty);

        PaintFrame(t); // idle frame — no layer dirty
        Assert.Equal(1, t.RootPaints);
        Assert.Equal(1, t.OverlayPaints);
    }

    [Fact]
    public void MarkAll_RepaintsBothLayers()
    {
        var t = new RepaintTracker();
        PaintFrame(t);

        t.MarkAll();
        Assert.True(t.RootDirty && t.OverlayDirty);
        PaintFrame(t);
        Assert.Equal(2, t.RootPaints);
        Assert.Equal(2, t.OverlayPaints);
    }

    // ── Sub-rectangle damage accumulation (partial repaint) ─────────────────────

    [Fact]
    public void FirstFrame_IsFullDamage()
    {
        var t = new RepaintTracker();
        Assert.True(t.FullDamage); // nothing preserved yet — the first frame must clear everything
        Assert.Equal(0, t.DamageCount);
        Assert.True(t.Damage.IsEmpty);
    }

    [Fact]
    public void ResetDamage_EntersPartialMode()
    {
        var t = new RepaintTracker();
        t.ResetDamage();
        Assert.False(t.FullDamage);
        Assert.Equal(0, t.DamageCount);
    }

    [Fact]
    public void AddDamageRoot_RecordsPreciseRegion_AndMarksRootOnly()
    {
        var t = new RepaintTracker();
        PaintFrame(t); // settle both layer flags
        t.ResetDamage();

        var region = new Rect(
            10,
            20,
            30,
            40
        );
        t.AddDamageRoot(region);

        Assert.True(t.RootDirty);
        Assert.False(t.OverlayDirty);
        Assert.False(t.FullDamage);
        Assert.Equal(1, t.DamageCount);
        Assert.Equal(region, t.Damage[0]);
    }

    [Fact]
    public void AddDamageOverlay_MarksOverlayLayer()
    {
        var t = new RepaintTracker();
        PaintFrame(t); // settle both layer flags
        t.ResetDamage();

        t.AddDamageOverlay(
            new Rect(
                0,
                0,
                5,
                5
            )
        );
        Assert.True(t.OverlayDirty);
        Assert.False(t.RootDirty);
        Assert.Equal(1, t.DamageCount);
    }

    [Fact]
    public void DisjointRegions_AreKeptSeparate()
    {
        var t = new RepaintTracker();
        t.ResetDamage();

        t.AddDamageRoot(
            new Rect(
                0,
                0,
                10,
                10
            )
        );
        t.AddDamageRoot(
            new Rect(
                100,
                100,
                10,
                10
            )
        );
        Assert.Equal(2, t.DamageCount);
    }

    [Fact]
    public void TouchingRegions_DoNotMerge()
    {
        // Edge-sharing rects do not overlap (scissor is half-open), so no pixel is drawn twice — keep them apart.
        var t = new RepaintTracker();
        t.ResetDamage();

        t.AddDamageRoot(
            new Rect(
                0,
                0,
                10,
                10
            )
        );
        t.AddDamageRoot(
            new Rect(
                10,
                0,
                10,
                10
            )
        );
        Assert.Equal(2, t.DamageCount);
    }

    [Fact]
    public void OverlappingRegions_MergeToBoundingUnion()
    {
        var t = new RepaintTracker();
        t.ResetDamage();

        t.AddDamageRoot(
            new Rect(
                0,
                0,
                10,
                10
            )
        );
        t.AddDamageRoot(
            new Rect(
                5,
                5,
                10,
                10
            )
        ); // overlaps the first
        Assert.Equal(1, t.DamageCount);
        Assert.Equal(
            new Rect(
                0,
                0,
                15,
                15
            ),
            t.Damage[0]
        );
    }

    [Fact]
    public void Merge_CascadesToFixpoint()
    {
        // Two disjoint rects; a third bridges them so both must fold into one.
        var t = new RepaintTracker();
        t.ResetDamage();

        t.AddDamageRoot(
            new Rect(
                0,
                0,
                10,
                10
            )
        );
        t.AddDamageRoot(
            new Rect(
                20,
                0,
                10,
                10
            )
        );
        Assert.Equal(2, t.DamageCount);

        t.AddDamageRoot(
            new Rect(
                8,
                0,
                14,
                10
            )
        ); // overlaps both -> all three collapse
        Assert.Equal(1, t.DamageCount);
        Assert.Equal(
            new Rect(
                0,
                0,
                30,
                10
            ),
            t.Damage[0]
        );
    }

    [Fact]
    public void EmptyRegion_FallsBackToFullDamage()
    {
        // A dirty layer with no locatable region cannot be repainted partially.
        var t = new RepaintTracker();
        t.ResetDamage();

        t.AddDamageRoot(Rect.Zero);
        Assert.True(t.RootDirty);
        Assert.True(t.FullDamage);
        Assert.Equal(0, t.DamageCount);
    }

    [Fact]
    public void MarkAll_AfterPreciseDamage_ForcesFull()
    {
        var t = new RepaintTracker();
        t.ResetDamage();

        t.AddDamageRoot(
            new Rect(
                0,
                0,
                10,
                10
            )
        );
        t.MarkAll();
        Assert.True(t.FullDamage);
        Assert.True(
            t.Damage.IsEmpty
        ); // full-frame span is empty — native reads it as "clear everything"
    }

    [Fact]
    public void PreciseDamage_AfterMarkAll_IsIgnored()
    {
        var t = new RepaintTracker();
        t.ResetDamage();

        t.MarkAll();
        t.AddDamageRoot(
            new Rect(
                0,
                0,
                10,
                10
            )
        ); // full already won — no partial regions
        Assert.True(t.FullDamage);
        Assert.Equal(0, t.DamageCount);
    }

    [Fact]
    public void TooManyDisjointRegions_DegradeToFull()
    {
        var t = new RepaintTracker();
        t.ResetDamage();

        // One more than the cap, all disjoint, so none merge.
        for (var i = 0; i <= RepaintTracker.MaxDamageRects; i++)
            t.AddDamageRoot(
                new Rect(
                    i * 20,
                    0,
                    10,
                    10
                )
            );

        Assert.True(t.FullDamage);
        Assert.Equal(0, t.DamageCount);
    }

    [Fact]
    public void MarkRoot_ForcesFullDamage()
    {
        // The unknown-region layer marks (used by debug/snackbar/tooltip ticks) must full-clear.
        var t = new RepaintTracker();
        t.ResetDamage();

        t.MarkRoot();
        Assert.True(t.FullDamage);
    }

    [Fact]
    public void ResetDamage_ClearsAccumulatedRegions()
    {
        var t = new RepaintTracker();
        t.ResetDamage();
        t.AddDamageRoot(
            new Rect(
                0,
                0,
                10,
                10
            )
        );
        Assert.Equal(1, t.DamageCount);

        t.ResetDamage();
        Assert.False(t.FullDamage);
        Assert.Equal(0, t.DamageCount);
    }

    // ── App-routing scenarios (model MarkPaintFor / RequestPaintFor call sites) ──
    // These lock in the intent that a value-drag and a hover crossing stay sub-rectangle now that
    // Widget.MarkNeedsPaint routes through App.RequestPaintFor → AddDamage* instead of MarkAll.

    [Fact]
    public void ValueDrag_StaysPartialSingleLayer_AcrossFrames()
    {
        // A Slider drag: OnPointerMove nudges the thumb (its own MarkNeedsPaint) and the App also
        // damages the captured widget — both resolve to the same root-layer region every frame. The
        // frame must never fall back to FullDamage or touch the overlay layer (regression: MarkNeedsPaint
        // used to route through MarkAll, clobbering the precise damage and re-walking both layers).
        var t = new RepaintTracker();
        var thumb = new Rect(40, 10, 24, 24);
        PaintFrame(t); // first frame settles both layers
        t.ResetDamage(); // enter partial mode

        for (var frame = 0; frame < 30; frame++)
        {
            t.AddDamageRoot(thumb); // the widget's own MarkNeedsPaint
            t.AddDamageRoot(thumb); // App.MarkPaintFor(_capturedWidget) — merges, idempotent

            Assert.False(t.FullDamage);
            Assert.Equal(1, t.DamageCount); // one merged region, not a full clear
            Assert.True(t.RootDirty);
            Assert.False(t.OverlayDirty); // dragging a root control never re-walks the overlay layer
            PaintFrame(t);
            t.ResetDamage(); // end-of-frame reset (App does this after present)
        }

        Assert.Equal(1, t.OverlayPaints); // overlay painted only once (the settle) — never during the drag
    }

    [Fact]
    public void HoverCrossing_DamagesBothWidgets_NotFullFrame()
    {
        // Pointer leaves button A and enters button B: the App damages A (remove hover styling) and B
        // (add it). Two disjoint regions, partial, root layer only — not a full-frame clear.
        var t = new RepaintTracker();
        PaintFrame(t); // settle both layers first
        t.ResetDamage();

        var buttonA = new Rect(0, 0, 80, 30);
        var buttonB = new Rect(120, 0, 80, 30);
        t.AddDamageRoot(buttonA); // exited
        t.AddDamageRoot(buttonB); // entered

        Assert.False(t.FullDamage);
        Assert.Equal(2, t.DamageCount);
        Assert.False(t.OverlayDirty);
    }

    [Fact]
    public void HoverIntoEmptySpace_DamagesOnlyExitedWidget()
    {
        // Moving off a control onto bare background: only the exited widget needs its hover styling
        // cleared (the "entered" side is null → no region), so a single rect, still partial.
        var t = new RepaintTracker();
        t.ResetDamage();

        t.AddDamageRoot(new Rect(0, 0, 80, 30)); // exited; entered side contributes nothing

        Assert.False(t.FullDamage);
        Assert.Equal(1, t.DamageCount);
    }
}