using Zigote.Core;
using Zigote.Core.Paint;
using Xunit;

namespace Zigote.Tests;

/// <summary>
///     The partial-repaint consistency diff (<see cref="PaintSnapshot" />): changed commands must
///     contribute covering bounds, structural/state changes must degrade to a full repaint, and
///     identical lists must report no difference — the invariants App.PaintAndPresent relies on to
///     stop unmarked visual changes from tearing inside other widgets' damage rects.
/// </summary>
public class PaintSnapshotDiffTests
{
    private static PaintList List(Action<PaintList> build)
    {
        var list = new PaintList();
        build(list);
        return list;
    }

    private static PaintDiffResult Diff(PaintList prev, PaintList cur, out Rect[] rects)
    {
        var snap = new PaintSnapshot();
        snap.Capture(prev);
        Span<Rect> changed = stackalloc Rect[PaintSnapshot.MaxChangedRects];
        var result = snap.Diff(cur, changed, out var count);
        rects = changed[..count].ToArray();
        return result;
    }

    [Fact]
    public void Identical_lists_diff_as_identical()
    {
        var a = List(p =>
        {
            p.AddRect(new Rect(0, 0, 800, 600), new Color(0.1f, 0.1f, 0.1f, 1f));
            p.AddText("hello", 20, 40, new Color(1f, 1f, 1f, 1f), 14f);
        });
        var b = List(p =>
        {
            p.AddRect(new Rect(0, 0, 800, 600), new Color(0.1f, 0.1f, 0.1f, 1f));
            p.AddText("hello", 20, 40, new Color(1f, 1f, 1f, 1f), 14f);
        });

        Assert.Equal(PaintDiffResult.Identical, Diff(a, b, out _));
    }

    [Fact]
    public void Changed_rect_fill_reports_bounds_covering_the_rect()
    {
        var a = List(p =>
        {
            p.AddRect(new Rect(0, 0, 800, 600), new Color(0.1f, 0.1f, 0.1f, 1f));
            p.AddRect(new Rect(100, 100, 200, 80), new Color(0.3f, 0.3f, 0.3f, 1f));
        });
        var b = List(p =>
        {
            p.AddRect(new Rect(0, 0, 800, 600), new Color(0.1f, 0.1f, 0.1f, 1f));
            p.AddRect(new Rect(100, 100, 200, 80), new Color(0.5f, 0.5f, 0.5f, 1f)); // hover shade
        });

        var result = Diff(a, b, out var rects);

        Assert.Equal(PaintDiffResult.Bounded, result);
        var union = rects.Aggregate(rects[0], Rect.Union);
        Assert.True(union.X <= 100 && union.Y <= 100 &&
                    union.X + union.Width >= 300 && union.Y + union.Height >= 180);
    }

    [Fact]
    public void Moved_rect_covers_both_old_and_new_positions()
    {
        var a = List(p => p.AddRect(new Rect(10, 10, 50, 50), new Color(1f, 0f, 0f, 1f)));
        var b = List(p => p.AddRect(new Rect(400, 300, 50, 50), new Color(1f, 0f, 0f, 1f)));

        var result = Diff(a, b, out var rects);

        Assert.Equal(PaintDiffResult.Bounded, result);
        var union = rects.Aggregate(rects[0], Rect.Union);
        Assert.True(union.Contains(35, 35), "old position must be covered");
        Assert.True(union.Contains(425, 325), "new position must be covered");
    }

    [Fact]
    public void Changed_text_reports_bounds_covering_the_glyphs()
    {
        var a = List(p => p.AddText("68 fps", 1180, 30, new Color(1f, 1f, 1f, 1f), 12f));
        var b = List(p => p.AddText("59 fps", 1180, 30, new Color(1f, 1f, 1f, 1f), 12f));

        var result = Diff(a, b, out var rects);

        Assert.Equal(PaintDiffResult.Bounded, result);
        var union = rects.Aggregate(rects[0], Rect.Union);
        // Generous glyph box around the baseline: must at least span the text's origin area.
        Assert.True(union.X <= 1180 && union.X + union.Width >= 1180 + 6 * 6);
        Assert.True(union.Y <= 30 && union.Y + union.Height >= 30);
    }

    [Fact]
    public void Inserted_command_is_bounded_and_removal_is_symmetric()
    {
        var baseline = List(p =>
        {
            p.AddRect(new Rect(0, 0, 800, 600), new Color(0.1f, 0.1f, 0.1f, 1f));
            p.AddRect(new Rect(700, 500, 60, 40), new Color(0.9f, 0.9f, 0.9f, 1f));
        });
        var withBubble = List(p =>
        {
            p.AddRect(new Rect(0, 0, 800, 600), new Color(0.1f, 0.1f, 0.1f, 1f));
            p.AddRect(new Rect(300, 200, 120, 32), new Color(0.2f, 0.2f, 0.2f, 1f)); // tooltip bubble
            p.AddRect(new Rect(700, 500, 60, 40), new Color(0.9f, 0.9f, 0.9f, 1f));
        });

        var inserted = Diff(baseline, withBubble, out var rects);
        Assert.Equal(PaintDiffResult.Bounded, inserted);
        Assert.True(rects.Aggregate(rects[0], Rect.Union).Contains(360, 216));

        var removed = Diff(withBubble, baseline, out rects);
        Assert.Equal(PaintDiffResult.Bounded, removed);
        Assert.True(rects.Aggregate(rects[0], Rect.Union).Contains(360, 216));
    }

    [Fact]
    public void Change_inside_a_transform_scope_is_unbounded()
    {
        var a = List(p =>
        {
            p.PushTransform(Matrix2D.Translation(50f, 50f));
            p.AddRect(new Rect(0, 0, 40, 40), new Color(1f, 0f, 0f, 1f));
            p.PopTransform();
        });
        var b = List(p =>
        {
            p.PushTransform(Matrix2D.Translation(50f, 50f));
            p.AddRect(new Rect(0, 0, 40, 40), new Color(0f, 1f, 0f, 1f));
            p.PopTransform();
        });

        Assert.Equal(PaintDiffResult.Unbounded, Diff(a, b, out _));
    }

    [Fact]
    public void Changed_clip_scope_is_unbounded()
    {
        var a = List(p =>
        {
            p.AddClipStart(new Rect(0, 0, 100, 100));
            p.AddRect(new Rect(10, 10, 20, 20), new Color(1f, 1f, 1f, 1f));
            p.AddClipEnd();
        });
        var b = List(p =>
        {
            p.AddClipStart(new Rect(0, 0, 200, 200));
            p.AddRect(new Rect(10, 10, 20, 20), new Color(1f, 1f, 1f, 1f));
            p.AddClipEnd();
        });

        Assert.Equal(PaintDiffResult.Unbounded, Diff(a, b, out _));
    }

    [Fact]
    public void Identical_commands_inside_identical_clip_scopes_stay_identical()
    {
        var a = List(p =>
        {
            p.AddClipStart(new Rect(0, 0, 100, 100));
            p.AddRect(new Rect(10, 10, 20, 20), new Color(1f, 1f, 1f, 1f));
            p.AddClipEnd();
        });
        var b = List(p =>
        {
            p.AddClipStart(new Rect(0, 0, 100, 100));
            p.AddRect(new Rect(10, 10, 20, 20), new Color(1f, 1f, 1f, 1f));
            p.AddClipEnd();
        });

        Assert.Equal(PaintDiffResult.Identical, Diff(a, b, out _));
    }

    [Fact]
    public void Many_scattered_changes_still_return_bounded_with_merged_rects()
    {
        var a = List(p =>
        {
            for (var i = 0; i < 24; i++)
                p.AddRect(new Rect(i * 30, i * 20, 20, 10), new Color(0.2f, 0.2f, 0.2f, 1f));
        });
        var b = List(p =>
        {
            for (var i = 0; i < 24; i++)
                p.AddRect(new Rect(i * 30, i * 20, 20, 10), new Color(0.4f, 0.4f, 0.4f, 1f));
        });

        var result = Diff(a, b, out var rects);

        Assert.Equal(PaintDiffResult.Bounded, result);
        Assert.True(rects.Length <= PaintSnapshot.MaxChangedRects);
        var union = rects.Aggregate(rects[0], Rect.Union);
        Assert.True(union.Contains(5, 5) && union.Contains(23 * 30 + 10, 23 * 20 + 5));
    }
}
