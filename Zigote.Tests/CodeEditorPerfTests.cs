using System.Diagnostics;
using System.Text;
using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>
///     Guards the CodeEditor paint-cost optimizations: the per-line tokenizer entering-state is cached
///     across frames (no re-lex from line 0 every frame while scrolled), and multi-line lexer state is
///     still carried correctly across the visible window.
/// </summary>
public class CodeEditorPerfTests
{
    private static (float, float, float, float) Col(ZgPaintCommand c)
    {
        return (c.ColorR, c.ColorG, c.ColorB, c.ColorA);
    }

    [Fact]
    public void ScrolledPaint_CachesLineStates_SoSecondFrameSkipsReplay()
    {
        var tok = new BlockCommentTokenizer();
        var sb = new StringBuilder();
        for (var i = 0; i < 60; i++) sb.Append("line ").Append(i).Append('\n');
        var ed = new CodeEditor(sb.ToString().TrimEnd('\n')) { Tokenizer = tok };

        ed.Measure(
            Constraints.Tight(400f, 120f)
        ); // short viewport → only a handful of visible lines
        ed.Layout(Offset.Zero);

        // Scroll near the bottom and settle the smooth-scroll ticker so `first` is deep in the file.
        ed.OnScroll(0f, -1000f);
        Ticker.AdvanceAll(10f);

        tok.Calls = 0;
        ed.Paint(new PaintList());
        var firstPaint =
            tok.Calls; // replays 0..first to build the cache, then paints the visible window

        tok.Calls = 0;
        ed.Paint(new PaintList());
        var cachedPaint = tok.Calls; // cache hit → only the visible window is tokenized

        Assert.True(
            cachedPaint < firstPaint,
            $"cached paint ({cachedPaint}) should tokenize fewer lines than the first ({firstPaint})"
        );
        Assert.True(
            firstPaint - cachedPaint >= 10,
            $"first paint should have replayed many lines; replayed only {firstPaint - cachedPaint}"
        );
        Assert.True(
            cachedPaint <= 16,
            $"cached paint should tokenize ~the visible window only, got {cachedPaint}"
        );
    }

    [Fact]
    public void EditInvalidatesStateCache_ThenRebuilds()
    {
        var tok = new BlockCommentTokenizer();
        var sb = new StringBuilder();
        for (var i = 0; i < 60; i++) sb.Append("line ").Append(i).Append('\n');
        var ed = new CodeEditor(sb.ToString().TrimEnd('\n')) { Tokenizer = tok };

        ed.Measure(Constraints.Tight(400f, 120f));
        ed.Layout(Offset.Zero);
        ed.OnScroll(0f, -1000f);
        Ticker.AdvanceAll(10f);

        ed.Paint(new PaintList()); // build cache
        tok.Calls = 0;
        ed.Paint(new PaintList());
        var cached = tok.Calls; // cheap (cache hit)

        // Mutating the text must drop the cache. Re-tokenize the longest line so Text changes; the
        // SetTextInternal path also resets scroll, so the next paint rebuilds from the top.
        ed.Text += "\nappended line that is fairly long to matter";

        ed.Measure(Constraints.Tight(400f, 120f)); // re-measure after content change
        ed.Layout(Offset.Zero);
        tok.Calls = 0;
        ed.Paint(new PaintList());
        var afterEdit = tok.Calls;

        Assert.True(afterEdit > 0, "a fresh paint after an edit must re-tokenize");
        // The cache was cleared (not silently reused as if nothing changed).
        Assert.True(
            afterEdit >= cached,
            "post-edit paint should re-lex, not reuse a stale empty cache"
        );
    }

    [Fact]
    public void CarriesBlockCommentStateAcrossVisibleLines()
    {
        var tok = new BlockCommentTokenizer();
        // code1 | /* | comment | */ | code2  →  Keyword, Comment, Comment, Comment, Keyword
        var ed = new CodeEditor("code1\n/*\ncomment\n*/\ncode2") { Tokenizer = tok };

        ed.Measure(Constraints.Tight(600f, 600f)); // tall enough that all five lines are visible
        ed.Layout(Offset.Zero);

        var paint = new PaintList();
        ed.Paint(paint);

        Assert.Equal([0, 0, 1, 1, 0], tok.StatesSeen);
        var colors = paint.DebugCommands
            .Where(c => c.Kind == (byte)PaintCommandKind.Text)
            .Select(Col)
            .Distinct()
            .ToArray();
        Assert.True(
            colors.Length >= 2,
            "highlight overlays should use a color distinct from base text"
        );
    }

    [Fact]
    public void WrappedRows_TokenizePhysicalLineOnce()
    {
        var tok = new BlockCommentTokenizer();
        var ed = new CodeEditor(string.Join(' ', Enumerable.Repeat("identifier", 80))) {
            Tokenizer = tok,
            SoftWrap = true,
        };

        ed.Measure(Constraints.Tight(120f, 300f));
        ed.Layout(Offset.Zero);
        ed.Paint(new PaintList());

        Assert.Equal(1, tok.Calls);

        tok.Calls = 0;
        ed.Paint(new PaintList());
        Assert.Equal(0, tok.Calls); // tokens and entering state are retained across frames
    }

    [Fact]
    public void SixHundredLineEditor_SteadyScrollPaintFits144HzCpuBudget()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, 600).Select(i =>
                $"public static int Value{i} => {i} * 2; // cached colored row"
            )
        );
        var ed = new CodeEditor(source) {
            Tokenizer = new BuiltInCodeTokenizer(CodeLanguage.CSharp),
            SoftWrap = false,
        };
        ed.Measure(Constraints.Tight(1200f, 800f));
        ed.Layout(Offset.Zero);
        var paint = new PaintList();

        // Warm JIT, paint buffers, token spans and the initial visible-row cache.
        for (var i = 0; i < 20; i++)
        {
            paint.Clear();
            ed.Paint(paint);
        }

        const int frames = 240;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            ed.OnScroll(0f, -0.35f);
            Ticker.AdvanceAll(1f / 144f);
            paint.Clear();
            ed.Paint(paint);
        }

        watch.Stop();

        var millisecondsPerFrame = watch.Elapsed.TotalMilliseconds / frames;
        Assert.True(
            millisecondsPerFrame < 6.9,
            $"CodeEditor CPU paint cost was {millisecondsPerFrame:F3} ms/frame; 144 Hz budget is 6.9 ms."
        );
    }

    // Stateful, call-counting tokenizer: state 1 = "inside a /* … */ block comment".
    private sealed class BlockCommentTokenizer : ILineTokenizer
    {
        public readonly List<int> StatesSeen = [];
        public int Calls;

        public int Tokenize(string line, int state, List<Token> output)
        {
            Calls++;
            StatesSeen.Add(state);
            var outState = state;
            if (state == 1)
            {
                if (line.Length > 0) output.Add(new Token(0, line.Length, TokenKind.Comment));
                if (line.Contains("*/")) outState = 0;
            }
            else if (line.Contains("/*"))
            {
                if (line.Length > 0) output.Add(new Token(0, line.Length, TokenKind.Comment));
                if (!line.Contains("*/")) outState = 1;
            }
            else if (line.Length > 0)
            {
                output.Add(new Token(0, line.Length, TokenKind.Keyword));
            }

            return outState;
        }
    }
}