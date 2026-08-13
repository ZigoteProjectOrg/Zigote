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

        Assert.Equal(5 * CharW, size.Width, 2);
        Assert.Equal(Fs * 1.2f, size.Height, 2);
        Assert.Equal(1, rt.LineCount);
        Assert.Equal(1, rt.RunCount);
        Assert.Equal("Hello", rt.Runs[0].Slice);
    }

    [Fact]
    public void AdjacentSpans_OnOneLine_AdvanceHorizontally()
    {
        var rt = Make(new TextSpan("ab"), new TextSpan("cd", Color.Red));
        Frame(rt, 1000f);

        Assert.Equal(2, rt.RunCount);
        Assert.Equal(0f, rt.Runs[0].X, 2);
        Assert.Equal(2 * CharW, rt.Runs[1].X, 2);
        Assert.Equal(0, rt.Runs[0].Line);
        Assert.Equal(0, rt.Runs[1].Line);
    }

    [Fact]
    public void WrapsAtSpaces_AcrossSpanBoundaries()
    {
        // "Hello " + "world": 5 chars fits a 30px line, " world" doesn't → wraps to line 1.
        var rt = Make(new TextSpan("Hello "), new TextSpan("world"));
        var size = rt.Measure(new Constraints(maxWidth: 30f, maxHeight: 600f));

        Assert.Equal(2, rt.LineCount);
        Assert.Equal(2, rt.RunCount);
        Assert.Equal("Hello", rt.Runs[0].Slice); // trailing space dropped at the wrap
        Assert.Equal("world", rt.Runs[1].Slice);
        Assert.Equal(1, rt.Runs[1].Line);
        Assert.Equal(0f, rt.Runs[1].X, 2);
        Assert.Equal(2 * Fs * 1.2f, size.Height, 2);
    }

    [Fact]
    public void MidLine_SpanBoundary_WithoutSpace_StaysContiguous()
    {
        // "ab" + "cd efgh" in 30px: "abcd" is one visual word split across spans — both runs stay
        // on line 0 (span boundaries are not break opportunities), "efgh" wraps.
        var rt = Make(new TextSpan("ab"), new TextSpan("cd efgh"));
        Frame(rt, 30f);

        Assert.Equal(2, rt.LineCount);
        Assert.Equal("ab", rt.Runs[0].Slice);
        Assert.Equal("cd", rt.Runs[1].Slice);
        Assert.Equal("efgh", rt.Runs[2].Slice);
        Assert.Equal(0, rt.Runs[1].Line);
        Assert.Equal(1, rt.Runs[2].Line);
    }

    [Fact]
    public void HardNewline_BreaksLine_AndTrailingNewlineAddsEmptyLine()
    {
        var rt = Make(new TextSpan("a\nb\n"));
        Frame(rt, 1000f);

        Assert.Equal(3, rt.LineCount); // "a", "b", trailing empty (Label parity)
        Assert.Equal(2, rt.RunCount);
        Assert.Equal(0, rt.Runs[0].Line);
        Assert.Equal(1, rt.Runs[1].Line);
    }

    [Fact]
    public void InteriorSpaces_MergeIntoOneRun()
    {
        var rt = Make(new TextSpan("a b c"));
        Frame(rt, 1000f);

        Assert.Equal(1, rt.RunCount);
        Assert.Equal("a b c", rt.Runs[0].Slice);
        Assert.Equal(5 * CharW, rt.Runs[0].Width, 2);
    }

    [Fact]
    public void LongWord_OverflowsItsOwnLine_InsteadOfInfiniteLoop()
    {
        var rt = Make(new TextSpan("abcdefghij xy"));
        Frame(rt, 20f); // word is 55px wide, line is 20px

        Assert.Equal(2, rt.LineCount);
        Assert.Equal("abcdefghij", rt.Runs[0].Slice);
        Assert.Equal("xy", rt.Runs[1].Slice);
    }

    // ── Styling ──

    [Fact]
    public void MixedFontSizes_LineHeightTracksLargestSpan()
    {
        var rt = Make(new TextSpan("big") { FontSize = 20f }, new TextSpan("small"));
        var size = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f));

        Assert.Equal(20f * 1.2f, size.Height, 2);
        Assert.Equal(1, rt.LineCount);
        // The small span advances by its own width, positioned after the big one.
        Assert.Equal(3 * 20f * 0.55f, rt.Runs[1].X, 2);
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
        Frame(rt, 1000f);

        var paint = new PaintList();
        rt.Paint(paint);
        var rects = 0;
        for (var i = 0; i < paint.DebugCommands.Count; i++)
            if ((PaintCommandKind)paint.DebugCommands[i].Kind == PaintCommandKind.Rect)
                rects++;
        Assert.Equal(2, rects); // one background + one underline
    }

    // ── MaxLines / ellipsis ──

    [Fact]
    public void MaxLines_Ellipsis_TruncatesLastRun()
    {
        var rt = Make(new TextSpan("aa bb cc dd ee ff gg hh"));
        rt.MaxLines = 2;
        rt.Overflow = TextOverflow.Ellipsis;
        Frame(rt, 30f); // 5 chars per line

        Assert.Equal(2, rt.LineCount);
        Assert.EndsWith("…", rt.Runs[rt.RunCount - 1].Slice);
    }

    [Fact]
    public void MaxLines_Clip_JustStops()
    {
        var rt = Make(new TextSpan("aa bb cc dd ee ff"));
        rt.MaxLines = 2;
        Frame(rt, 30f);

        Assert.Equal(2, rt.LineCount);
        for (var i = 0; i < rt.RunCount; i++)
            Assert.True(rt.Runs[i].Line < 2);
    }

    // ── Invalidation ──

    [Fact]
    public void InPlaceSpanMutation_RequiresInvalidateSpans()
    {
        var span = new TextSpan("short");
        var rt = Make(span);
        Frame(rt, 1000f);
        var w1 = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f)).Width;

        span.Text = "considerably longer";
        var w2 = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f)).Width;
        Assert.Equal(w1, w2, 2); // cached layout still live

        rt.InvalidateSpans();
        var w3 = rt.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f)).Width;
        Assert.True(w3 > w1);
    }

    // ── RTL ──

    [Fact]
    public void Rtl_MirrorsRunPlacement()
    {
        var rt = Make(new TextSpan("ab "), new TextSpan("cd"));
        rt.LayoutDirection = TextDirection.Rtl;
        Frame(rt, 1000f);

        // Line width = "ab cd" = 5 chars. First span run occupies the RIGHT side.
        var lineW = 5 * CharW;
        Assert.Equal(lineW - 3 * CharW, rt.Runs[0].X, 2); // "ab " mirrored
        Assert.Equal(0f, rt.Runs[1].X, 2); // "cd" hugs the line start (left end of mirrored line)
    }

    [Fact]
    public void Rtl_DefaultAlignment_IsRightEdge()
    {
        var rt = Make(new TextSpan("ab"));
        rt.LayoutDirection = TextDirection.Rtl;
        rt.Measure(Constraints.Tight(100f, 20f));
        rt.Layout(Offset.Zero);

        var paint = new PaintList();
        rt.Paint(paint);
        // The single text command's baseline X sits at box right minus text width.
        var found = false;
        for (var i = 0; i < paint.DebugCommands.Count; i++)
        {
            var cmd = paint.DebugCommands[i];
            if ((PaintCommandKind)cmd.Kind != PaintCommandKind.Text) continue;
            Assert.Equal(100f - 2 * CharW, cmd.BaselineX, 2);
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
        var a = new SizedBox(30f, 10f);
        var b = new SizedBox(50f, 10f);
        var row = new Row([a, b]) { LayoutDirection = TextDirection.Rtl };

        row.Measure(Constraints.Tight(200f, 20f));
        row.Layout(Offset.Zero);

        // MainAxisAlignment.Start under RTL hugs the RIGHT edge; first child rightmost.
        Assert.Equal(170f, a.Bounds.X, 2);
        Assert.Equal(120f, b.Bounds.X, 2);
    }

    [Fact]
    public void Row_Ltr_Unchanged()
    {
        var a = new SizedBox(30f, 10f);
        var b = new SizedBox(50f, 10f);
        var row = new Row([a, b]);

        row.Measure(Constraints.Tight(200f, 20f));
        row.Layout(Offset.Zero);

        Assert.Equal(0f, a.Bounds.X, 2);
        Assert.Equal(30f, b.Bounds.X, 2);
    }

    [Fact]
    public void Row_Rtl_FromAmbientDirectionality()
    {
        var a = new SizedBox(30f, 10f);
        var b = new SizedBox(50f, 10f);
        var root = new Directionality(
            TextDirection.Rtl,
            new Row([a, b])
        );

        root.Measure(Constraints.Tight(200f, 20f));
        root.Layout(Offset.Zero);

        Assert.Equal(170f, a.Bounds.X, 2);
    }

    [Fact]
    public void Wrap_Rtl_FillsRunsRightToLeft()
    {
        var a = new SizedBox(40f, 10f);
        var b = new SizedBox(40f, 10f);
        var c = new SizedBox(40f, 10f);
        var wrap = new Wrap([a, b, c], spacing: 10) {
            LayoutDirection = TextDirection.Rtl,
            RunSpacing = 0f,
        };

        wrap.Measure(new Constraints(maxWidth: 100f, maxHeight: 600f));
        wrap.Layout(Offset.Zero);

        // Run 0 holds a+b (40+10+40 = 90 ≤ 100); measured width 90. Mirrored: a right, b left.
        Assert.Equal(50f, a.Bounds.X, 2);
        Assert.Equal(0f, b.Bounds.X, 2);
        // Run 1 holds c at the right edge of the 90px extent.
        Assert.Equal(50f, c.Bounds.X, 2);
        Assert.Equal(10f, c.Bounds.Y, 2);
    }

    [Fact]
    public void EdgeInsetsDirectional_ResolvesPerDirection()
    {
        var d = EdgeInsetsDirectional.Only(
            10f,
            end: 3f,
            top: 1f,
            bottom: 2f
        );

        var ltr = d.Resolve(TextDirection.Ltr);
        Assert.Equal(10f, ltr.Left);
        Assert.Equal(3f, ltr.Right);

        var rtl = d.Resolve(TextDirection.Rtl);
        Assert.Equal(3f, rtl.Left);
        Assert.Equal(10f, rtl.Right);
        Assert.Equal(1f, rtl.Top);
        Assert.Equal(2f, rtl.Bottom);
    }

    [Fact]
    public void Padding_DirectionalInsets_FlipUnderAmbientRtl()
    {
        var child = new SizedBox(10f, 10f);
        var pad = new Padding(EdgeInsetsDirectional.Only(20f), child);
        var root = new Directionality(TextDirection.Rtl, pad);

        root.Measure(Constraints.Tight(100f, 20f));
        root.Layout(Offset.Zero);

        // start=20 resolves to the RIGHT side under RTL → child stays at the left edge.
        Assert.Equal(0f, child.Bounds.X, 2);
        Assert.Equal(20f, pad.Insets.Right, 2);
    }
}
