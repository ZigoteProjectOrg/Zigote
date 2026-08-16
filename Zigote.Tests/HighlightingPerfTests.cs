using System.Diagnostics;
using System.Text;
using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.Modules.UI.CodeEditor;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>
///     Guards the throughput of the XParsec-based <see cref="Highlighting" /> tokenizers under heavy
///     editor usage: full-document re-lex of a large file, steady scrolling paint through a
///     <see cref="CodeEditor" />, and pathological single-line inputs (minified JSON, unterminated
///     strings). Budgets are ~5-10x the locally measured cost so CI noise doesn't flake them; they
///     exist to catch order-of-magnitude regressions (e.g. backtracking blowups), not micro-drift.
/// </summary>
[Collection(
    "Ticker"
)] // static Ticker.Active is shared; AdvanceAll in one class ticks another class's widgets
public class HighlightingPerfTests
{
    /// <summary>A realistic C# document: code, doc comments, strings, and a block-comment section.</summary>
    private static string[] CSharpDocument(int lines)
    {
        var result = new string[lines];
        for (int i = 0; i < lines; i++)
        {
            result[i] = (i % 8) switch
            {
                0 => $"/// <summary>Computes value {i} for the frame budget.</summary>",
                1 => $"public static int Value{i}(int x, ReadOnlySpan<char> name)",
                2 => "{",
                3 => $"    var label = $\"item {i}: {{x}}\"; // interpolated + trailing comment",
                4 => $"    if (x > 0x{i:X4} && name.Length != {i}) return x * {i} + 42;",
                5 => "    /* block comment opening on this line",
                6 => "       still inside the block comment */ return -1;",
                _ => "}",
            };
        }

        return result;
    }

    private static double MeasureFullRelex(ILineTokenizer tok, string[] lines, int passes)
    {
        var output = new List<Token>();

        // Warm the JIT and any lazily built parser state.
        int state = ILineTokenizer.StateDefault;
        foreach (string line in lines)
        {
            output.Clear();
            state = tok.Tokenize(line: line, state: state, output: output);
        }

        var watch = Stopwatch.StartNew();
        for (int p = 0; p < passes; p++)
        {
            state = ILineTokenizer.StateDefault;
            foreach (string line in lines)
            {
                output.Clear();
                state = tok.Tokenize(line: line, state: state, output: output);
            }
        }

        watch.Stop();
        return watch.Elapsed.TotalMilliseconds / passes;
    }

    [Fact]
    public void FullRelexOf2000LineCSharpFile_WithinOpenFileBudget()
    {
        string[] lines = CSharpDocument(2000);

        double msPerPass = MeasureFullRelex(tok: Highlighting.CSharp, lines: lines, passes: 5);

        // An open-file / paste re-highlight of a 2000-line document must feel instant.
        Assert.True(
            condition: msPerPass < 250,
            userMessage: $"full 2000-line C# re-lex took {msPerPass:F1} ms; budget is 250 ms."
        );
    }

    [Fact]
    public void MinifiedJsonLine_TokenizesLinearly()
    {
        // One 24k-char line: the worst case for a per-line lexer (no line boundaries to hide behind).
        var sb = new StringBuilder("{");
        for (int i = 0; i < 400; i++)
            sb.Append($"\"key{i}\":{{\"n\":{i},\"s\":\"value {i}\",\"b\":true,\"x\":[1,2.5e3,0x0]}},");
        sb.Append('}');
        string line = sb.ToString();

        double msPerPass = MeasureFullRelex(
            tok: Highlighting.Json,
            lines: [line],
            passes: 10
        );

        Assert.True(
            condition: msPerPass < 40,
            userMessage:
            $"minified {line.Length}-char JSON line took {msPerPass:F2} ms; budget is 40 ms."
        );
    }

    [Fact]
    public void PathologicalLines_DoNotBlowUpBacktracking()
    {
        // Inputs chosen to stress the choice/backtrack machinery: unterminated strings, an
        // operator wall, digit runs that repeatedly fail the hex/binary prefixes, and a
        // catch-all-only line. Each must stay linear, not exponential.
        string[] lines =
        [
            "\"" + new string(c: 'a', count: 8000),
            new string(c: '=', count: 8000),
            string.Concat(Enumerable.Repeat(element: "0x_ 0b_ 0.e ", count: 600)),
            new string(c: '\\', count: 8000),
        ];

        var output = new List<Token>();
        var watch = Stopwatch.StartNew();
        foreach (string line in lines)
        {
            output.Clear();
            Highlighting.CSharp.Tokenize(line: line, state: 0, output: output);
        }

        watch.Stop();

        Assert.True(
            condition: watch.Elapsed.TotalMilliseconds < 200,
            userMessage:
            $"pathological lines took {watch.Elapsed.TotalMilliseconds:F1} ms; budget is 200 ms."
        );
    }

    [Fact]
    public void XParsecHighlighting_SteadyScrollPaintFits144HzCpuBudget()
    {
        // Same scenario as CodeEditorPerfTests.SixHundredLineEditor_… but through the real
        // XParsec-backed factory tokenizer instead of the built-in C# lexer.
        string source = string.Join(separator: '\n', values: CSharpDocument(600));
        var ed = new CodeEditor(source) {
            Tokenizer = Highlighting.CSharp,
            SoftWrap = false,
        };
        ed.Measure(Constraints.Tight(width: 1200f, height: 800f));
        ed.Layout(Offset.Zero);
        var paint = new PaintList();

        for (int i = 0; i < 20; i++)
        {
            paint.Clear();
            ed.Paint(paint);
        }

        const int frames = 240;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            ed.OnScroll(dx: 0f, dy: -0.35f);
            Ticker.AdvanceAll(1f / 144f);
            paint.Clear();
            ed.Paint(paint);
        }

        watch.Stop();

        double millisecondsPerFrame = watch.Elapsed.TotalMilliseconds / frames;
        Assert.True(
            condition: millisecondsPerFrame < 6.9,
            userMessage:
            $"XParsec-highlighted paint cost was {millisecondsPerFrame:F3} ms/frame; 144 Hz budget is 6.9 ms."
        );
    }
}
