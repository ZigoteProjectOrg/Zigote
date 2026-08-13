using Xunit;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage for the styled-span paragraph widget. Tests run without a native engine, so
///     <c>TextMeasure</c> uses its deterministic heuristic: width = chars × fontSize × 0.55. All
///     expectations below are derived from that (fontSize 10 → 5.5 px per char).
/// </summary>
public class RichTextTests
{
    private const float Fs = 10f;
    private const float CharW = Fs * 0.55f; // 5.5

    private static RichText Make(params TextSpan[] spans)
    {
        return new RichText(spans) {
            FontSize = Fs,
            LineHeight = 1.2f,
        };
    }

    private static void Frame(Widget w, float maxWidth, float maxHeight = 600f)
    {
        w.Measure(new Constraints(maxWidth: maxWidth, maxHeight: maxHeight));
        w.Layout(Offset.Zero);
    }

    // ── Wrapping ──

    [Fact]
    public void SingleSpan_SingleLine_MeasuresIntrinsically()
    {
        var rt = Make(new TextSpan("Hello"));
        var size = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f));

        Assert.Equal(expected: 5 * CharW, actual: size.Width, precision: 2);
        Assert.Equal(expected: Fs * 1.2f, actual: size.Height, precision: 2);
        Assert.Equal(expected: 1, actual: rt.LineCount);
        Assert.Equal(expected: 1, actual: rt.RunCount);
        Assert.Equal(expected: "Hello", actual: rt.Runs[0].Slice);
    }

    [Fact]
    public void AdjacentSpans_OnOneLine_AdvanceHorizontally()
    {
        var rt = Make(new TextSpan("ab"), new TextSpan(text: "cd", color: Color.Red));
        Frame(w: rt, maxWidth: 1000f);

        Assert.Equal(expected: 2, actual: rt.RunCount);
        Assert.Equal(expected: 0f, actual: rt.Runs[0].X, precision: 2);
        Assert.Equal(expected: 2 * CharW, actual: rt.Runs[1].X, precision: 2);
        Assert.Equal(expected: 0, actual: rt.Runs[0].Line);
        Assert.Equal(expected: 0, actual: rt.Runs[1].Line);
    }

    [Fact]
    public void WrapsAtSpaces_AcrossSpanBoundaries()
    {
        // "Hello " + "world": 5 chars fits a 30px line, " world" doesn't → wraps to line 1.
        var rt = Make(new TextSpan("Hello "), new TextSpan("world"));
        var size = rt.Measure(new Constraints(maxWidth: 30f, maxHeight: 600f));

        Assert.Equal(expected: 2, actual: rt.LineCount);
        Assert.Equal(expected: 2, actual: rt.RunCount);
        Assert.Equal(
            expected: "Hello",
            actual: rt.Runs[0].Slice
        ); // trailing space dropped at the wrap
        Assert.Equal(expected: "world", actual: rt.Runs[1].Slice);
        Assert.Equal(expected: 1, actual: rt.Runs[1].Line);
        Assert.Equal(expected: 0f, actual: rt.Runs[1].X, precision: 2);
        Assert.Equal(expected: 2 * Fs * 1.2f, actual: size.Height, precision: 2);
    }

    [Fact]
    public void MidLine_SpanBoundary_WithoutSpace_StaysContiguous()
    {
        // "ab" + "cd efgh" in 30px: "abcd" is one visual word split across spans — both runs stay
        // on line 0 (span boundaries are not break opportunities), "efgh" wraps.
        var rt = Make(new TextSpan("ab"), new TextSpan("cd efgh"));
        Frame(w: rt, maxWidth: 30f);

        Assert.Equal(expected: 2, actual: rt.LineCount);
        Assert.Equal(expected: "ab", actual: rt.Runs[0].Slice);
        Assert.Equal(expected: "cd", actual: rt.Runs[1].Slice);
        Assert.Equal(expected: "efgh", actual: rt.Runs[2].Slice);
        Assert.Equal(expected: 0, actual: rt.Runs[1].Line);
        Assert.Equal(expected: 1, actual: rt.Runs[2].Line);
    }

    [Fact]
    public void HardNewline_BreaksLine_AndTrailingNewlineAddsEmptyLine()
    {
        var rt = Make(new TextSpan("a\nb\n"));
        Frame(w: rt, maxWidth: 1000f);

        Assert.Equal(expected: 3, actual: rt.LineCount); // "a", "b", trailing empty (Label parity)
        Assert.Equal(expected: 2, actual: rt.RunCount);
        Assert.Equal(expected: 0, actual: rt.Runs[0].Line);
        Assert.Equal(expected: 1, actual: rt.Runs[1].Line);
    }

    [Fact]
    public void InteriorSpaces_MergeIntoOneRun()
    {
        var rt = Make(new TextSpan("a b c"));
        Frame(w: rt, maxWidth: 1000f);

        Assert.Equal(expected: 1, actual: rt.RunCount);
        Assert.Equal(expected: "a b c", actual: rt.Runs[0].Slice);
        Assert.Equal(expected: 5 * CharW, actual: rt.Runs[0].Width, precision: 2);
    }

    [Fact]
    public void LongWord_OverflowsItsOwnLine_InsteadOfInfiniteLoop()
    {
        var rt = Make(new TextSpan("abcdefghij xy"));
        Frame(w: rt, maxWidth: 20f); // word is 55px wide, line is 20px

        Assert.Equal(expected: 2, actual: rt.LineCount);
        Assert.Equal(expected: "abcdefghij", actual: rt.Runs[0].Slice);
        Assert.Equal(expected: "xy", actual: rt.Runs[1].Slice);
    }

    // ── Styling ──

    [Fact]
    public void MixedFontSizes_LineHeightTracksLargestSpan()
    {
        var rt = Make(new TextSpan("big") { FontSize = 20f }, new TextSpan("small"));
        var size = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f));

        Assert.Equal(expected: 20f * 1.2f, actual: size.Height, precision: 2);
        Assert.Equal(expected: 1, actual: rt.LineCount);
        // The small span advances by its own width, positioned after the big one.
        Assert.Equal(expected: 3 * 20f * 0.55f, actual: rt.Runs[1].X, precision: 2);
    }

    [Fact]
    public void UnderlineAndBackground_EmitRects()
    {
        var rt = Make(
            new TextSpan("plain "),
            new TextSpan("link") {
                Underline = true,
                Background = Color.Yellow,
            }
        );
        Frame(w: rt, maxWidth: 1000f);

        var paint = new PaintList();
        rt.Paint(paint);
        int rects = 0;
        for (int i = 0; i < paint.DebugCommands.Count; i++)
        {
            if ((PaintCommandKind)paint.DebugCommands[i].Kind == PaintCommandKind.Rect)
                rects++;
        }

        Assert.Equal(expected: 2, actual: rects); // one background + one underline
    }

    // ── MaxLines / ellipsis ──

    [Fact]
    public void MaxLines_Ellipsis_TruncatesLastRun()
    {
        var rt = Make(new TextSpan("aa bb cc dd ee ff gg hh"));
        rt.MaxLines = 2;
        rt.Overflow = TextOverflow.Ellipsis;
        Frame(w: rt, maxWidth: 30f); // 5 chars per line

        Assert.Equal(expected: 2, actual: rt.LineCount);
        Assert.EndsWith(expectedEndString: "…", actualString: rt.Runs[rt.RunCount - 1].Slice);
    }

    [Fact]
    public void MaxLines_Clip_JustStops()
    {
        var rt = Make(new TextSpan("aa bb cc dd ee ff"));
        rt.MaxLines = 2;
        Frame(w: rt, maxWidth: 30f);

        Assert.Equal(expected: 2, actual: rt.LineCount);
        for (int i = 0; i < rt.RunCount; i++)
            Assert.True(rt.Runs[i].Line < 2);
    }

    // ── Invalidation ──

    [Fact]
    public void InPlaceSpanMutation_RequiresInvalidateSpans()
    {
        var span = new TextSpan("short");
        var rt = Make(span);
        Frame(w: rt, maxWidth: 1000f);
        float w1 = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f)).Width;

        span.Text = "considerably longer";
        float w2 = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f)).Width;
        Assert.Equal(expected: w1, actual: w2, precision: 2); // cached layout still live

        rt.InvalidateSpans();
        float w3 = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f)).Width;
        Assert.True(w3 > w1);
    }

    // ── RTL ──

    [Fact]
    public void Rtl_MirrorsRunPlacement()
    {
        var rt = Make(new TextSpan("ab "), new TextSpan("cd"));
        rt.LayoutDirection = TextDirection.Rtl;
        Frame(w: rt, maxWidth: 1000f);

        // Line width = "ab cd" = 5 chars. First span run occupies the RIGHT side.
        float lineW = 5 * CharW;
        Assert.Equal(
            expected: lineW - (3 * CharW),
            actual: rt.Runs[0].X,
            precision: 2
        ); // "ab " mirrored
        Assert.Equal(
            expected: 0f,
            actual: rt.Runs[1].X,
            precision: 2
        ); // "cd" hugs the line start (left end of mirrored line)
    }

    [Fact]
    public void Rtl_DefaultAlignment_IsRightEdge()
    {
        var rt = Make(new TextSpan("ab"));
        rt.LayoutDirection = TextDirection.Rtl;
        rt.Measure(Constraints.Tight(width: 100f, height: 20f));
        rt.Layout(Offset.Zero);

        var paint = new PaintList();
        rt.Paint(paint);
        // The single text command's baseline X sits at box right minus text width.
        bool found = false;
        for (int i = 0; i < paint.DebugCommands.Count; i++)
        {
            var cmd = paint.DebugCommands[i];
            if ((PaintCommandKind)cmd.Kind != PaintCommandKind.Text) continue;
            Assert.Equal(expected: 100f - (2 * CharW), actual: cmd.BaselineX, precision: 2);
            found = true;
        }

        Assert.True(found);
    }
}

/// <summary>RTL mirroring of the layout primitives (Row, Wrap, directional Padding).</summary>
public class RtlLayoutTests
{
    [Fact]
    public void Row_Rtl_MirrorsChildOrderAndAlignment()
    {
        var a = new SizedBox(width: 30f, height: 10f);
        var b = new SizedBox(width: 50f, height: 10f);
        var row = new Row([a, b]) { LayoutDirection = TextDirection.Rtl };

        row.Measure(Constraints.Tight(width: 200f, height: 20f));
        row.Layout(Offset.Zero);

        // MainAxisAlignment.Start under RTL hugs the RIGHT edge; first child rightmost.
        Assert.Equal(expected: 170f, actual: a.Bounds.X, precision: 2);
        Assert.Equal(expected: 120f, actual: b.Bounds.X, precision: 2);
    }

    [Fact]
    public void Row_Ltr_Unchanged()
    {
        var a = new SizedBox(width: 30f, height: 10f);
        var b = new SizedBox(width: 50f, height: 10f);
        var row = new Row([a, b]);

        row.Measure(Constraints.Tight(width: 200f, height: 20f));
        row.Layout(Offset.Zero);

        Assert.Equal(expected: 0f, actual: a.Bounds.X, precision: 2);
        Assert.Equal(expected: 30f, actual: b.Bounds.X, precision: 2);
    }

    [Fact]
    public void Row_Rtl_FromAmbientDirectionality()
    {
        var a = new SizedBox(width: 30f, height: 10f);
        var b = new SizedBox(width: 50f, height: 10f);
        var root = new Directionality(
            direction: TextDirection.Rtl,
            child: new Row([a, b])
        );

        root.Measure(Constraints.Tight(width: 200f, height: 20f));
        root.Layout(Offset.Zero);

        Assert.Equal(expected: 170f, actual: a.Bounds.X, precision: 2);
    }

    [Fact]
    public void Wrap_Rtl_FillsRunsRightToLeft()
    {
        var a = new SizedBox(width: 40f, height: 10f);
        var b = new SizedBox(width: 40f, height: 10f);
        var c = new SizedBox(width: 40f, height: 10f);
        var wrap = new Wrap(children: [a, b, c], spacing: 10) {
            LayoutDirection = TextDirection.Rtl,
            RunSpacing = 0f,
        };

        wrap.Measure(new Constraints(maxWidth: 100f, maxHeight: 600f));
        wrap.Layout(Offset.Zero);

        // Run 0 holds a+b (40+10+40 = 90 ≤ 100); measured width 90. Mirrored: a right, b left.
        Assert.Equal(expected: 50f, actual: a.Bounds.X, precision: 2);
        Assert.Equal(expected: 0f, actual: b.Bounds.X, precision: 2);
        // Run 1 holds c at the right edge of the 90px extent.
        Assert.Equal(expected: 50f, actual: c.Bounds.X, precision: 2);
        Assert.Equal(expected: 10f, actual: c.Bounds.Y, precision: 2);
    }

    [Fact]
    public void EdgeInsetsDirectional_ResolvesPerDirection()
    {
        var d = EdgeInsetsDirectional.Only(
            start: 10f,
            end: 3f,
            top: 1f,
            bottom: 2f
        );

        var ltr = d.Resolve(TextDirection.Ltr);
        Assert.Equal(expected: 10f, actual: ltr.Left);
        Assert.Equal(expected: 3f, actual: ltr.Right);

        var rtl = d.Resolve(TextDirection.Rtl);
        Assert.Equal(expected: 3f, actual: rtl.Left);
        Assert.Equal(expected: 10f, actual: rtl.Right);
        Assert.Equal(expected: 1f, actual: rtl.Top);
        Assert.Equal(expected: 2f, actual: rtl.Bottom);
    }

    [Fact]
    public void Padding_DirectionalInsets_FlipUnderAmbientRtl()
    {
        var child = new SizedBox(width: 10f, height: 10f);
        var pad = new Padding(padding: EdgeInsetsDirectional.Only(20f), child: child);
        var root = new Directionality(direction: TextDirection.Rtl, child: pad);

        root.Measure(Constraints.Tight(width: 100f, height: 20f));
        root.Layout(Offset.Zero);

        // start=20 resolves to the RIGHT side under RTL → child stays at the left edge.
        Assert.Equal(expected: 0f, actual: child.Bounds.X, precision: 2);
        Assert.Equal(expected: 20f, actual: pad.Insets.Right, precision: 2);
    }
}
