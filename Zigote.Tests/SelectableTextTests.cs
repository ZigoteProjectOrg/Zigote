using Xunit;
using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     Headless interaction coverage for <see cref="SelectableText" />: synthetic pointer/key events
///     against the deterministic heuristic measurer (fontSize 10 → 5.5 px per char).
/// </summary>
public class SelectableTextTests
{
    private const float Fs = 10f;
    private const float CharW = Fs * 0.55f;
    private const uint ScLeft = 80, ScRight = 79, ScEscape = 41, ScEnd = 77;

    private static SelectableText Make(string text, float maxWidth = 1000f)
    {
        var st = new SelectableText(text) {
            FontSize = Fs,
            LineHeight = 1.2f,
        };
        st.Measure(new Constraints(maxWidth: maxWidth, maxHeight: 600f));
        st.Layout(Offset.Zero);
        return st;
    }

    private static void Drag(SelectableText st, Offset from, Offset to)
    {
        st.OnPointerDown(from);
        st.OnPointerMove(to);
        st.OnPointerUp(to);
    }

    [Fact]
    public void DragSelects_CharacterRange()
    {
        var st = Make("Hello world");

        // Caret 2 sits at x=11; caret 5 at 27.5. Points near those land on them.
        Drag(
            st: st,
            from: new Offset(x: (2 * CharW) + 1f, y: 5f),
            to: new Offset(x: (5 * CharW) + 1f, y: 5f)
        );

        Assert.True(st.HasSelection);
        Assert.Equal(expected: "llo", actual: st.SelectedText);
    }

    [Fact]
    public void Click_WithoutDrag_ClearsSelection()
    {
        var st = Make("Hello");
        Drag(st: st, from: new Offset(x: 0f, y: 5f), to: new Offset(x: 3 * CharW, y: 5f));
        Assert.True(st.HasSelection);

        // Click far from the drag origin (headless Time is constant, so a nearby second press
        // would alias into the double-click window and word-select instead).
        var p = new Offset(x: (4 * CharW) + 1f, y: 5f);
        st.OnPointerDown(p);
        st.OnPointerUp(p);
        Assert.False(st.HasSelection);
    }

    [Fact]
    public void DoubleClick_SelectsWord()
    {
        var st = Make("Hello world");
        var p = new Offset(x: 7 * CharW, y: 5f); // inside "world"

        st.OnPointerDown(p);
        st.OnPointerUp(p);
        st.OnPointerDown(p); // headless Time is constant → within double-click window
        st.OnPointerUp(p);

        Assert.Equal(expected: "world", actual: st.SelectedText);
    }

    [Fact]
    public void DragAcrossWrappedLines_SelectsThroughLineBreak()
    {
        var st = Make(text: "Hello world", maxWidth: 30f); // wraps to "Hello" / "world"

        float lineH = Fs * 1.2f;
        Drag(
            st: st,
            from: new Offset(x: 2 * CharW, y: lineH * 0.5f),
            to: new Offset(x: 2 * CharW, y: lineH * 1.5f)
        );

        // From caret 2 on line 0 to caret 2 of "world" (global index 8): "llo w" minus the
        // wrap-collapsed space renders as "llo" + "wo" of the concatenated source text.
        Assert.Equal(expected: "llo wo", actual: st.SelectedText);
    }

    [Fact]
    public void SelectAll_And_ShiftArrows()
    {
        var st = Make("abc def");

        st.SelectAll();
        Assert.Equal(expected: "abc def", actual: st.SelectedText);

        st.OnKey(
            keyChar: '\0',
            scancode: ScEscape,
            down: true,
            mods: Modifiers.None
        );
        Assert.False(st.HasSelection);

        // Shift+Right twice from the cleared caret (position 0) → "ab".
        st.OnKey(
            keyChar: '\0',
            scancode: ScRight,
            down: true,
            mods: Modifiers.Shift
        );
        st.OnKey(
            keyChar: '\0',
            scancode: ScRight,
            down: true,
            mods: Modifiers.Shift
        );
        Assert.Equal(expected: "ab", actual: st.SelectedText);

        // Shift+Left shrinks back to "a".
        st.OnKey(
            keyChar: '\0',
            scancode: ScLeft,
            down: true,
            mods: Modifiers.Shift
        );
        Assert.Equal(expected: "a", actual: st.SelectedText);

        // Shift+End extends to the end.
        st.OnKey(
            keyChar: '\0',
            scancode: ScEnd,
            down: true,
            mods: Modifiers.Shift
        );
        Assert.Equal(expected: "abc def", actual: st.SelectedText);
    }

    [Fact]
    public void CmdA_SelectsAll_HeadlessCopyIsSafe()
    {
        var st = Make("abc");
        st.OnKey(
            keyChar: 'a',
            scancode: 0,
            down: true,
            mods: Modifiers.Cmd
        );
        Assert.Equal(expected: "abc", actual: st.SelectedText);

        // No native engine loaded — copy must be a no-op, not a crash.
        st.OnKey(
            keyChar: 'c',
            scancode: 0,
            down: true,
            mods: Modifiers.Cmd
        );
        Assert.Equal(expected: "abc", actual: st.SelectedText);
    }

    [Fact]
    public void SelectionPaintsTintRects_UnderText()
    {
        var st = Make(text: "Hello world", maxWidth: 30f);
        var clean = new PaintList();
        st.Paint(clean);
        int before = CountRects(clean);

        st.SelectAll();
        var selected = new PaintList();
        st.Paint(selected);

        // One tint rect per run (two wrapped lines → two runs).
        Assert.Equal(expected: before + 2, actual: CountRects(selected));
    }

    [Fact]
    public void MultiSpan_SelectionCrossesStyleBoundaries()
    {
        var st = new SelectableText(
            new TextSpan(text: "red ", color: Color.Red),
            new TextSpan("blue") { Weight = FontWeight.Bold }
        ) {
            FontSize = Fs,
            LineHeight = 1.2f,
        };
        st.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f));
        st.Layout(Offset.Zero);

        Drag(
            st: st,
            from: new Offset(x: (2 * CharW) + 1f, y: 5f),
            to: new Offset(x: (6 * CharW) + 1f, y: 5f)
        );
        Assert.Equal(expected: "d bl", actual: st.SelectedText);
    }

    [Fact]
    public void LayoutRebuild_ClampsStaleSelection()
    {
        var st = Make("a long sentence here");
        st.SelectAll();

        st.Spans = [new TextSpan("ab")];
        st.Measure(new Constraints(maxWidth: 1000f, maxHeight: 600f));
        st.Layout(Offset.Zero);

        // Stale indices were clamped; slicing must not throw.
        _ = st.SelectedText;
        Assert.True(st.SelectionEnd <= 2);
    }

    private static int CountRects(PaintList paint)
    {
        int n = 0;
        for (int i = 0; i < paint.DebugCommands.Count; i++)
        {
            if ((PaintCommandKind)paint.DebugCommands[i].Kind == PaintCommandKind.Rect)
                n++;
        }

        return n;
    }
}

/// <summary>Zero-GC steady-state gates for the new text widgets (HotPathAllocationTests pattern).</summary>
public class RichTextAllocationTests
{
    private static void Frame(Widget root, PaintList paint, Constraints c)
    {
        paint.Clear();
        root.Measure(c);
        root.Layout(Offset.Zero);
        root.Paint(paint);
    }

    [Fact]
    public void RichText_SteadyStateFrame_AllocatesZero()
    {
        var root = new RichText(
            new TextSpan("The quick "),
            new TextSpan("brown fox") {
                Weight = FontWeight.Bold,
                Underline = true,
            },
            new TextSpan(" jumps over the lazy dog. ") { Color = Color.Red },
            new TextSpan("Inline code") {
                Background = new Color(
                    r: 0.5f,
                    g: 0.5f,
                    b: 0.2f,
                    a: 0.4f
                ),
            }
        ) { FontSize = 12f };
        var paint = new PaintList();
        var c = new Constraints(maxWidth: 180f, maxHeight: 600f);

        for (int i = 0; i < 200; i++) Frame(root: root, paint: paint, c: c);
        Assert.True(paint.Count > 0);

        const int frames = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) Frame(root: root, paint: paint, c: c);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage:
            $"RichText hot path allocated {allocated} B over {frames} frames; expected 0."
        );
    }

    [Fact]
    public void SelectableText_WithActiveSelection_SteadyStateFrame_AllocatesZero()
    {
        var root = new SelectableText("The quick brown fox jumps over the lazy dog") {
            FontSize = 12f,
        };
        var paint = new PaintList();
        var c = new Constraints(maxWidth: 120f, maxHeight: 600f);

        Frame(root: root, paint: paint, c: c);
        root.SelectAll();

        for (int i = 0; i < 200; i++) Frame(root: root, paint: paint, c: c);

        const int frames = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) Frame(root: root, paint: paint, c: c);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage:
            $"SelectableText hot path allocated {allocated} B over {frames} frames; expected 0."
        );
    }

    [Fact]
    public void SelectableText_PointerDragTracking_AllocatesZero()
    {
        var root = new SelectableText("The quick brown fox jumps over the lazy dog") {
            FontSize = 12f,
        };
        var c = new Constraints(maxWidth: 120f, maxHeight: 600f);
        root.Measure(c);
        root.Layout(Offset.Zero);

        // Warm the drag path (first move may lazily JIT).
        root.OnPointerDown(new Offset(x: 5f, y: 5f));
        for (int i = 0; i < 100; i++)
            root.OnPointerMove(new Offset(x: 5f + (i % 60), y: 5f + (i % 30)));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
            root.OnPointerMove(new Offset(x: 5f + (i % 60), y: 5f + (i % 30)));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        root.OnPointerUp(new Offset(x: 60f, y: 30f));

        Assert.True(
            condition: allocated == 0,
            userMessage:
            $"Selection drag tracking allocated {allocated} B over 500 moves; expected 0."
        );
    }
}
