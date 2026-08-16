using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;

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

    private static bool Covers(Rect outer, Rect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static PaintDiffResult Diff(PaintList prev, PaintList cur, out Rect[] rects)
    {
        var snap = new PaintSnapshot();
        snap.Capture(prev);
        Span<Rect> changed = stackalloc Rect[PaintSnapshot.MaxChangedRects];
        var result = snap.Diff(current: cur, changed: changed, changedCount: out int count);
        rects = changed[..count].ToArray();
        return result;
    }

    [Fact]
    public void Identical_lists_diff_as_identical()
    {
        var a = List(p =>
            {
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 800,
                        height: 600
                    ),
                    color: new Color(
                        r: 0.1f,
                        g: 0.1f,
                        b: 0.1f,
                        a: 1f
                    )
                );
                p.AddText(
                    text: "hello",
                    baselineX: 20,
                    baselineY: 40,
                    color: new Color(
                        r: 1f,
                        g: 1f,
                        b: 1f,
                        a: 1f
                    ),
                    fontSize: 14f
                );
            }
        );
        var b = List(p =>
            {
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 800,
                        height: 600
                    ),
                    color: new Color(
                        r: 0.1f,
                        g: 0.1f,
                        b: 0.1f,
                        a: 1f
                    )
                );
                p.AddText(
                    text: "hello",
                    baselineX: 20,
                    baselineY: 40,
                    color: new Color(
                        r: 1f,
                        g: 1f,
                        b: 1f,
                        a: 1f
                    ),
                    fontSize: 14f
                );
            }
        );

        Assert.Equal(
            expected: PaintDiffResult.Identical,
            actual: Diff(prev: a, cur: b, rects: out _)
        );
    }

    [Fact]
    public void Changed_rect_fill_reports_bounds_covering_the_rect()
    {
        var a = List(p =>
            {
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 800,
                        height: 600
                    ),
                    color: new Color(
                        r: 0.1f,
                        g: 0.1f,
                        b: 0.1f,
                        a: 1f
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 100,
                        y: 100,
                        width: 200,
                        height: 80
                    ),
                    color: new Color(
                        r: 0.3f,
                        g: 0.3f,
                        b: 0.3f,
                        a: 1f
                    )
                );
            }
        );
        var b = List(p =>
            {
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 800,
                        height: 600
                    ),
                    color: new Color(
                        r: 0.1f,
                        g: 0.1f,
                        b: 0.1f,
                        a: 1f
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 100,
                        y: 100,
                        width: 200,
                        height: 80
                    ),
                    color: new Color(
                        r: 0.5f,
                        g: 0.5f,
                        b: 0.5f,
                        a: 1f
                    )
                ); // hover shade
            }
        );

        var result = Diff(prev: a, cur: b, rects: out var rects);

        Assert.Equal(expected: PaintDiffResult.Bounded, actual: result);
        var union = rects.Aggregate(seed: rects[0], func: Rect.Union);
        Assert.True(
            union.X <= 100 && union.Y <= 100 &&
            union.X + union.Width >= 300 && union.Y + union.Height >= 180
        );
    }

    [Fact]
    public void Moved_rect_covers_both_old_and_new_positions()
    {
        var a = List(p => p.AddRect(
                bounds: new Rect(
                    x: 10,
                    y: 10,
                    width: 50,
                    height: 50
                ),
                color: new Color(
                    r: 1f,
                    g: 0f,
                    b: 0f,
                    a: 1f
                )
            )
        );
        var b = List(p => p.AddRect(
                bounds: new Rect(
                    x: 400,
                    y: 300,
                    width: 50,
                    height: 50
                ),
                color: new Color(
                    r: 1f,
                    g: 0f,
                    b: 0f,
                    a: 1f
                )
            )
        );

        var result = Diff(prev: a, cur: b, rects: out var rects);

        Assert.Equal(expected: PaintDiffResult.Bounded, actual: result);
        var union = rects.Aggregate(seed: rects[0], func: Rect.Union);
        Assert.True(
            condition: union.Contains(px: 35, py: 35),
            userMessage: "old position must be covered"
        );
        Assert.True(
            condition: union.Contains(px: 425, py: 325),
            userMessage: "new position must be covered"
        );
    }

    [Fact]
    public void Changed_text_reports_bounds_covering_the_glyphs()
    {
        var a = List(p => p.AddText(
                text: "68 fps",
                baselineX: 1180,
                baselineY: 30,
                color: new Color(
                    r: 1f,
                    g: 1f,
                    b: 1f,
                    a: 1f
                ),
                fontSize: 12f
            )
        );
        var b = List(p => p.AddText(
                text: "59 fps",
                baselineX: 1180,
                baselineY: 30,
                color: new Color(
                    r: 1f,
                    g: 1f,
                    b: 1f,
                    a: 1f
                ),
                fontSize: 12f
            )
        );

        var result = Diff(prev: a, cur: b, rects: out var rects);

        Assert.Equal(expected: PaintDiffResult.Bounded, actual: result);
        var union = rects.Aggregate(seed: rects[0], func: Rect.Union);
        // Generous glyph box around the baseline: must at least span the text's origin area.
        Assert.True(union.X <= 1180 && union.X + union.Width >= 1180 + (6 * 6));
        Assert.True(union.Y <= 30 && union.Y + union.Height >= 30);
    }

    [Fact]
    public void Inserted_command_is_bounded_and_removal_is_symmetric()
    {
        var baseline = List(p =>
            {
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 800,
                        height: 600
                    ),
                    color: new Color(
                        r: 0.1f,
                        g: 0.1f,
                        b: 0.1f,
                        a: 1f
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 700,
                        y: 500,
                        width: 60,
                        height: 40
                    ),
                    color: new Color(
                        r: 0.9f,
                        g: 0.9f,
                        b: 0.9f,
                        a: 1f
                    )
                );
            }
        );
        var withBubble = List(p =>
            {
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 800,
                        height: 600
                    ),
                    color: new Color(
                        r: 0.1f,
                        g: 0.1f,
                        b: 0.1f,
                        a: 1f
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 300,
                        y: 200,
                        width: 120,
                        height: 32
                    ),
                    color: new Color(
                        r: 0.2f,
                        g: 0.2f,
                        b: 0.2f,
                        a: 1f
                    )
                ); // tooltip bubble
                p.AddRect(
                    bounds: new Rect(
                        x: 700,
                        y: 500,
                        width: 60,
                        height: 40
                    ),
                    color: new Color(
                        r: 0.9f,
                        g: 0.9f,
                        b: 0.9f,
                        a: 1f
                    )
                );
            }
        );

        var inserted = Diff(prev: baseline, cur: withBubble, rects: out var rects);
        Assert.Equal(expected: PaintDiffResult.Bounded, actual: inserted);
        Assert.True(rects.Aggregate(seed: rects[0], func: Rect.Union).Contains(px: 360, py: 216));

        var removed = Diff(prev: withBubble, cur: baseline, rects: out rects);
        Assert.Equal(expected: PaintDiffResult.Bounded, actual: removed);
        Assert.True(rects.Aggregate(seed: rects[0], func: Rect.Union).Contains(px: 360, py: 216));
    }

    [Fact]
    public void Change_inside_a_transform_scope_is_unbounded()
    {
        var a = List(p =>
            {
                p.PushTransform(Matrix2D.Translation(dx: 50f, dy: 50f));
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 40,
                        height: 40
                    ),
                    color: new Color(
                        r: 1f,
                        g: 0f,
                        b: 0f,
                        a: 1f
                    )
                );
                p.PopTransform();
            }
        );
        var b = List(p =>
            {
                p.PushTransform(Matrix2D.Translation(dx: 50f, dy: 50f));
                p.AddRect(
                    bounds: new Rect(
                        x: 0,
                        y: 0,
                        width: 40,
                        height: 40
                    ),
                    color: new Color(
                        r: 0f,
                        g: 1f,
                        b: 0f,
                        a: 1f
                    )
                );
                p.PopTransform();
            }
        );

        Assert.Equal(
            expected: PaintDiffResult.Unbounded,
            actual: Diff(prev: a, cur: b, rects: out _)
        );
    }

    /// <summary>
    ///     A changed clip scope stays bounded: suffix draws under it render with different
    ///     visibility, but only within old rect ∪ new rect — both must be reported as damage.
    /// </summary>
    [Fact]
    public void Changed_clip_scope_is_bounded_by_both_rects()
    {
        var a = List(p =>
            {
                p.AddClipStart(
                    new Rect(
                        x: 0,
                        y: 0,
                        width: 100,
                        height: 100
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 10,
                        y: 10,
                        width: 20,
                        height: 20
                    ),
                    color: new Color(
                        r: 1f,
                        g: 1f,
                        b: 1f,
                        a: 1f
                    )
                );
                p.AddClipEnd();
            }
        );
        var b = List(p =>
            {
                p.AddClipStart(
                    new Rect(
                        x: 0,
                        y: 0,
                        width: 200,
                        height: 200
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 10,
                        y: 10,
                        width: 20,
                        height: 20
                    ),
                    color: new Color(
                        r: 1f,
                        g: 1f,
                        b: 1f,
                        a: 1f
                    )
                );
                p.AddClipEnd();
            }
        );

        Assert.Equal(
            expected: PaintDiffResult.Bounded,
            actual: Diff(prev: a, cur: b, rects: out var rects)
        );
        Assert.Contains(
            rects,
            r => Covers(
                outer: r,
                inner: new Rect(
                    x: 0,
                    y: 0,
                    width: 100,
                    height: 100
                )
            )
        );
        Assert.Contains(
            rects,
            r => Covers(
                outer: r,
                inner: new Rect(
                    x: 0,
                    y: 0,
                    width: 200,
                    height: 200
                )
            )
        );
    }

    /// <summary>
    ///     The scroll case the clip-containment rule exists for: content (including text, which has
    ///     no per-op bounds) shifting under an UNCHANGED ClipStart must stay partial, with damage
    ///     covered by the clip rect — not degrade to a full repaint.
    /// </summary>
    [Fact]
    public void Scrolled_text_under_unchanged_clip_is_bounded_by_the_clip_rect()
    {
        var clip = new Rect(
            x: 50,
            y: 50,
            width: 300,
            height: 200
        );
        PaintList Frame(float scrollY) => List(p =>
            {
                p.AddClipStart(clip);
                for (int line = 0; line < 5; line++)
                {
                    p.AddText(
                        text: $"line {line}",
                        baselineX: 60,
                        baselineY: 70 + (line * 20) - scrollY,
                        color: new Color(
                            r: 1f,
                            g: 1f,
                            b: 1f,
                            a: 1f
                        ),
                        fontSize: 14f
                    );
                }

                p.AddClipEnd();
            }
        );

        var result = Diff(prev: Frame(0f), cur: Frame(35f), rects: out var rects);

        Assert.Equal(expected: PaintDiffResult.Bounded, actual: result);
        Assert.All(
            collection: rects,
            action: r => Assert.True(Covers(outer: clip, inner: r))
        );
    }

    [Fact]
    public void Identical_commands_inside_identical_clip_scopes_stay_identical()
    {
        var a = List(p =>
            {
                p.AddClipStart(
                    new Rect(
                        x: 0,
                        y: 0,
                        width: 100,
                        height: 100
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 10,
                        y: 10,
                        width: 20,
                        height: 20
                    ),
                    color: new Color(
                        r: 1f,
                        g: 1f,
                        b: 1f,
                        a: 1f
                    )
                );
                p.AddClipEnd();
            }
        );
        var b = List(p =>
            {
                p.AddClipStart(
                    new Rect(
                        x: 0,
                        y: 0,
                        width: 100,
                        height: 100
                    )
                );
                p.AddRect(
                    bounds: new Rect(
                        x: 10,
                        y: 10,
                        width: 20,
                        height: 20
                    ),
                    color: new Color(
                        r: 1f,
                        g: 1f,
                        b: 1f,
                        a: 1f
                    )
                );
                p.AddClipEnd();
            }
        );

        Assert.Equal(
            expected: PaintDiffResult.Identical,
            actual: Diff(prev: a, cur: b, rects: out _)
        );
    }

    [Fact]
    public void Many_scattered_changes_still_return_bounded_with_merged_rects()
    {
        var a = List(p =>
            {
                for (int i = 0; i < 24; i++)
                {
                    p.AddRect(
                        bounds: new Rect(
                            x: i * 30,
                            y: i * 20,
                            width: 20,
                            height: 10
                        ),
                        color: new Color(
                            r: 0.2f,
                            g: 0.2f,
                            b: 0.2f,
                            a: 1f
                        )
                    );
                }
            }
        );
        var b = List(p =>
            {
                for (int i = 0; i < 24; i++)
                {
                    p.AddRect(
                        bounds: new Rect(
                            x: i * 30,
                            y: i * 20,
                            width: 20,
                            height: 10
                        ),
                        color: new Color(
                            r: 0.4f,
                            g: 0.4f,
                            b: 0.4f,
                            a: 1f
                        )
                    );
                }
            }
        );

        var result = Diff(prev: a, cur: b, rects: out var rects);

        Assert.Equal(expected: PaintDiffResult.Bounded, actual: result);
        Assert.True(rects.Length <= PaintSnapshot.MaxChangedRects);
        var union = rects.Aggregate(seed: rects[0], func: Rect.Union);
        Assert.True(
            union.Contains(px: 5, py: 5) && union.Contains(px: (23 * 30) + 10, py: (23 * 20) + 5)
        );
    }
}
