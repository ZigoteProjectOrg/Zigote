using PaintCommandKind = Zigote.Core.Native.ZgPaintOp;
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
[Collection(
    "Ticker"
)] // static Ticker.Active is shared; AdvanceAll in one class ticks another class's widgets
public class CodeEditorPerfTests
{
    [Fact]
    public void ScrolledPaint_CachesLineStates_SoSecondFrameSkipsReplay()
    {
        var tok = new BlockCommentTokenizer();
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("line ").Append(i).Append('\n');
        var ed = new CodeEditor(sb.ToString().TrimEnd('\n')) { Tokenizer = tok };

        ed.Measure(
            Constraints.Tight(width: 400f, height: 120f)
        ); // short viewport → only a handful of visible lines
        ed.Layout(Offset.Zero);

        // Scroll near the bottom and settle the smooth-scroll ticker so `first` is deep in the file.
        ed.OnScroll(dx: 0f, dy: -1000f);
        Ticker.AdvanceAll(10f);

        tok.Calls = 0;
        ed.Paint(new PaintList());
        int firstPaint =
            tok.Calls; // replays 0..first to build the cache, then paints the visible window

        tok.Calls = 0;
        ed.Paint(new PaintList());
        int cachedPaint = tok.Calls; // cache hit → only the visible window is tokenized

        Assert.True(
            condition: cachedPaint < firstPaint,
            userMessage:
            $"cached paint ({cachedPaint}) should tokenize fewer lines than the first ({firstPaint})"
        );
        Assert.True(
            condition: firstPaint - cachedPaint >= 10,
            userMessage:
            $"first paint should have replayed many lines; replayed only {firstPaint - cachedPaint}"
        );
        Assert.True(
            condition: cachedPaint <= 16,
            userMessage:
            $"cached paint should tokenize ~the visible window only, got {cachedPaint}"
        );
    }

    [Fact]
    public void EditInvalidatesStateCache_ThenRebuilds()
    {
        var tok = new BlockCommentTokenizer();
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("line ").Append(i).Append('\n');
        var ed = new CodeEditor(sb.ToString().TrimEnd('\n')) { Tokenizer = tok };

        ed.Measure(Constraints.Tight(width: 400f, height: 120f));
        ed.Layout(Offset.Zero);
        ed.OnScroll(dx: 0f, dy: -1000f);
        Ticker.AdvanceAll(10f);

        ed.Paint(new PaintList()); // build cache
        tok.Calls = 0;
        ed.Paint(new PaintList());
        int cached = tok.Calls; // cheap (cache hit)

        // Mutating the text must drop the cache. Re-tokenize the longest line so Text changes; the
        // SetTextInternal path also resets scroll, so the next paint rebuilds from the top.
        ed.Text += "\nappended line that is fairly long to matter";

        ed.Measure(
            Constraints.Tight(width: 400f, height: 120f)
        ); // re-measure after content change
        ed.Layout(Offset.Zero);
        tok.Calls = 0;
        ed.Paint(new PaintList());
        int afterEdit = tok.Calls;

        Assert.True(
            condition: afterEdit > 0,
            userMessage: "a fresh paint after an edit must re-tokenize"
        );
        // The cache was cleared (not silently reused as if nothing changed).
        Assert.True(
            condition: afterEdit >= cached,
            userMessage: "post-edit paint should re-lex, not reuse a stale empty cache"
        );
    }

    [Fact]
    public void CarriesBlockCommentStateAcrossVisibleLines()
    {
        var tok = new BlockCommentTokenizer();
        // code1 | /* | comment | */ | code2  →  Keyword, Comment, Comment, Comment, Keyword
        var ed = new CodeEditor("code1\n/*\ncomment\n*/\ncode2") { Tokenizer = tok };

        ed.Measure(
            Constraints.Tight(width: 600f, height: 600f)
        ); // tall enough that all five lines are visible
        ed.Layout(Offset.Zero);

        var paint = new PaintList();
        ed.Paint(paint);

        Assert.Equal(expected: [0, 0, 1, 1, 0], actual: tok.StatesSeen);
        var colors = paint.DebugCommands
            .Where(c => c.Kind == PaintCommandKind.Text)
            .Select(c => (c.ColorR, c.ColorG, c.ColorB, c.ColorA))
            .Distinct()
            .ToArray();
        Assert.True(
            condition: colors.Length >= 2,
            userMessage: "highlight overlays should use a color distinct from base text"
        );
    }

    [Fact]
    public void WrappedRows_TokenizePhysicalLineOnce()
    {
        var tok = new BlockCommentTokenizer();
        var ed = new CodeEditor(
            string.Join(
                separator: ' ',
                values: Enumerable.Repeat(element: "identifier", count: 80)
            )
        ) {
            Tokenizer = tok,
            SoftWrap = true,
        };

        ed.Measure(Constraints.Tight(width: 120f, height: 300f));
        ed.Layout(Offset.Zero);
        ed.Paint(new PaintList());

        Assert.Equal(expected: 1, actual: tok.Calls);

        tok.Calls = 0;
        ed.Paint(new PaintList());
        Assert.Equal(
            expected: 0,
            actual: tok.Calls
        ); // tokens and entering state are retained across frames
    }

    // The steady-scroll 144 Hz paint-budget test lives in HighlightingPerfTests, running through
    // the XParsec-backed Highlighting.CSharp tokenizer (the production path).

    // Stateful, call-counting tokenizer: state 1 = "inside a /* … */ block comment".
    private sealed class BlockCommentTokenizer : ILineTokenizer
    {
        public readonly List<int> StatesSeen = [];
        public int Calls;

        public int Tokenize(string line, int state, List<Token> output)
        {
            Calls++;
            StatesSeen.Add(state);
            int outState = state;
            if (state == 1)
            {
                if (line.Length > 0)
                {
                    output.Add(
                        new Token(start: 0, length: line.Length, kind: TokenKind.Comment)
                    );
                }

                if (line.Contains("*/")) outState = 0;
            }
            else if (line.Contains("/*"))
            {
                if (line.Length > 0)
                {
                    output.Add(
                        new Token(start: 0, length: line.Length, kind: TokenKind.Comment)
                    );
                }

                if (!line.Contains("*/")) outState = 1;
            }
            else if (line.Length > 0)
                output.Add(new Token(start: 0, length: line.Length, kind: TokenKind.Keyword));

            return outState;
        }
    }
}
