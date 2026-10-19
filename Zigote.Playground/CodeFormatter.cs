using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace Playground;

/// <summary>
///     Formats example code in the docs' house style, as minimal edits (so the caret survives and
///     one undo reverts it). Three passes over the text:
///     <list type="number">
///         <item>Roslyn's formatter — spacing, Allman braces for types, members and statements.</item>
///         <item>
///             Spacing inside multi-line collection expressions and initializers, which Roslyn leaves
///             exactly as typed — and nested widget trees are made of nothing else. Only unambiguous
///             rules (commas, operators, named-argument colons, parentheses); anything else is kept.
///         </item>
///         <item>
///             Indentation by bracket nesting, for every line: one level deeper than the line that
///             opened the innermost bracket, a closer back at its opener's line, a continuation line
///             (a wrapped expression or statement) one level more, and a <c>{</c> starting a line
///             aligned with the line its owner (lambda, initializer, member, statement) starts on.
///             Roslyn never re-indents lines inside argument lists, so this pass does all of it.
///         </item>
///     </list>
/// </summary>
internal static class CodeFormatter
{
    private const int IndentSize = 4;
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly Lazy<AdhocWorkspace> Workspace = new(() => new());

    public static IReadOnlyList<CodeTextEdit> Format(string source, CancellationToken ct = default)
    {
        string text = Roslyn(source, ct);
        text = Respace(text, ct);
        text = Reindent(text, ct);
        return Diff(source, text);
    }

    private static SyntaxTree Parse(string text) => CSharpSyntaxTree.ParseText(text, ParseOptions);

    // ── 1. Roslyn ─────────────────────────────────────────────────────────────

    private static string Roslyn(string source, CancellationToken ct)
    {
        var workspace = Workspace.Value;
        // Allman braces for types and members, but `new X {` initializers stay on their line and
        // keep the author's line breaks between members.
        var options = workspace.Options
            .WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n")
            .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInObjectCollectionArrayInitializers, false)
            .WithChangedOption(CSharpFormattingOptions.NewLineForMembersInObjectInit, false)
            .WithChangedOption(CSharpFormattingOptions.NewLineForMembersInAnonymousTypes, false);
        return Apply(source, Formatter.GetFormattedTextChanges(Parse(source).GetRoot(ct), workspace, options, ct)
            .Select(c => new CodeTextEdit(c.Span.Start, c.Span.Length, c.NewText ?? "")));
    }

    // ── 2. Spacing inside multi-line initializers ─────────────────────────────

    private static string Respace(string text, CancellationToken ct)
    {
        var tree = Parse(text);
        var lines = tree.GetText(ct).Lines;
        var edits = new List<CodeTextEdit>();
        SyntaxToken previous = default;
        foreach (var token in tree.GetRoot(ct).DescendantTokens())
        {
            if (token.IsMissing || token.Span.IsEmpty) continue;
            var (a, b) = (previous, token);
            previous = token;
            if (a.RawKind == 0 || !OnlySpacesBetween(a, b) ||
                lines.GetLineFromPosition(a.SpanStart).LineNumber != lines.GetLineFromPosition(b.SpanStart).LineNumber ||
                !InMultilineInitializer(a, lines) || !InMultilineInitializer(b, lines))
                continue;
            if (Space(a, b) is not { } want) continue;
            int gap = b.SpanStart - a.Span.End;
            if (gap != want) edits.Add(new CodeTextEdit(a.Span.End, gap, want == 0 ? "" : " "));
        }

        return Apply(text, edits);
    }

    private static bool OnlySpacesBetween(SyntaxToken a, SyntaxToken b) =>
        a.TrailingTrivia.Concat(b.LeadingTrivia).All(t => t.IsKind(SyntaxKind.WhitespaceTrivia));

    // Inside a collection expression or initializer that spans lines — and not in an interpolation.
    private static bool InMultilineInitializer(SyntaxToken t, TextLineCollection lines)
    {
        foreach (var node in t.Parent!.AncestorsAndSelf())
        {
            if (node is InterpolatedStringExpressionSyntax) return false;
            if (node is CollectionExpressionSyntax or InitializerExpressionSyntax or AnonymousObjectCreationExpressionSyntax &&
                lines.GetLineFromPosition(node.SpanStart).LineNumber != lines.GetLineFromPosition(node.Span.End).LineNumber)
                return true;
        }

        return false;
    }

    /// <summary>Spaces wanted between two tokens on one line, or null to keep what the author typed.</summary>
    private static int? Space(SyntaxToken a, SyntaxToken b)
    {
        var (ak, bk) = (a.Kind(), b.Kind());
        if (IsBinaryOperator(a) || IsBinaryOperator(b)) return 1;
        if (bk is SyntaxKind.CommaToken or SyntaxKind.SemicolonToken or SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.DotToken) return 0;
        if (ak is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.DotToken) return 0;
        if (ak is SyntaxKind.DotDotToken && a.Parent is SpreadElementSyntax) return 1; // [.. items]
        if (ak is SyntaxKind.DotDotToken || bk is SyntaxKind.DotDotToken) return 0; // a..b
        if (ak is SyntaxKind.CommaToken or SyntaxKind.SemicolonToken) return 1;
        if (bk is SyntaxKind.ColonToken && b.Parent is NameColonSyntax) return 0;
        if (ak is SyntaxKind.ColonToken && a.Parent is NameColonSyntax) return 1;
        if (a.Parent is PrefixUnaryExpressionSyntax p && p.OperatorToken == a) return 0;
        if (b.Parent is PostfixUnaryExpressionSyntax q && q.OperatorToken == b) return 0;
        if (bk is SyntaxKind.QuestionToken && b.Parent is NullableTypeSyntax or ConditionalAccessExpressionSyntax) return 0;
        if (ak is SyntaxKind.QuestionToken && a.Parent is ConditionalAccessExpressionSyntax) return 0;
        if (a.Parent is TypeArgumentListSyntax && ak is SyntaxKind.LessThanToken) return 0;
        if (b.Parent is TypeArgumentListSyntax && bk is SyntaxKind.LessThanToken or SyntaxKind.GreaterThanToken) return 0;
        if (bk is SyntaxKind.OpenParenToken)
            return ak is SyntaxKind.IdentifierToken or SyntaxKind.GreaterThanToken or SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken
                or SyntaxKind.ThisKeyword or SyntaxKind.BaseKeyword or SyntaxKind.TypeOfKeyword or SyntaxKind.NameOfKeyword
                or SyntaxKind.SizeOfKeyword or SyntaxKind.DefaultKeyword ||
                   (ak is SyntaxKind.NewKeyword && a.Parent is ImplicitObjectCreationExpressionSyntax)
                ? 0
                : null;
        if (bk is SyntaxKind.OpenBracketToken && ak is SyntaxKind.IdentifierToken or SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken) return 0;
        if (bk is SyntaxKind.OpenBraceToken || ak is SyntaxKind.OpenBraceToken || bk is SyntaxKind.CloseBraceToken) return 1;
        return null;
    }

    private static bool IsBinaryOperator(SyntaxToken t) => t.Parent switch {
        BinaryExpressionSyntax e => e.OperatorToken == t,
        AssignmentExpressionSyntax e => e.OperatorToken == t,
        ConditionalExpressionSyntax e => e.QuestionToken == t || e.ColonToken == t,
        EqualsValueClauseSyntax e => e.EqualsToken == t,
        NameEqualsSyntax e => e.EqualsToken == t,
        ArrowExpressionClauseSyntax e => e.ArrowToken == t,
        LambdaExpressionSyntax e => e.ArrowToken == t,
        SwitchExpressionArmSyntax e => e.EqualsGreaterThanToken == t,
        BinaryPatternSyntax e => e.OperatorToken == t,
        _ => false,
    };

    // ── 3. Indentation ────────────────────────────────────────────────────────

    private static string Reindent(string text, CancellationToken ct)
    {
        var tree = Parse(text);
        var root = tree.GetRoot(ct);
        var lines = tree.GetText(ct).Lines;
        var tokens = root.DescendantTokens().Where(t => !t.IsMissing && !t.Span.IsEmpty).ToArray();
        int[] indent = new int[lines.Count];
        var output = new string[lines.Count];
        var open = new Stack<Level>(); // the unclosed brackets before the current line
        int next = 0; // first token not yet pushed/popped
        int Line(int position) => lines.GetLineFromPosition(position).LineNumber;

        for (int l = 0; l < lines.Count; l++)
        {
            string line = lines[l].ToString();
            string body = line.Trim();
            int lead = line.Length - line.TrimStart().Length;
            indent[l] = lead;
            output[l] = line;
            if (body.Length == 0)
            {
                output[l] = "";
                continue;
            }

            int p = lines[l].Start + lead;
            for (; next < tokens.Length && tokens[next].SpanStart < p; next++) Track(open, tokens[next], Line(tokens[next].SpanStart));

            // Leave alone what is not code at a line start: string or comment continuations, directives.
            bool insideToken = next > 0 && tokens[next - 1].Span.End > p;
            var trivia = root.FindTrivia(p);
            bool insideTrivia = trivia.Span.Length > 0 && trivia.SpanStart < p;
            if (insideToken || insideTrivia || body[0] == '#' || InInterpolatedString(root.FindToken(p), p)) continue;

            var first = next < tokens.Length ? tokens[next] : default;
            bool tokenAtStart = first.RawKind != 0 && first.SpanStart == p;
            int want;
            if (tokenAtStart && first.IsKind(SyntaxKind.OpenBraceToken) && BraceOwner(first) is { } owner && Line(owner.SpanStart) < l)
                want = indent[Line(owner.SpanStart)];
            else if (open.Count == 0)
                want = 0;
            else
            {
                var level = open.Peek();
                bool closer = tokenAtStart && Closes(level.Opener, first);
                // A brace body indents from where its owner starts (`new X(…,\n …) {` → from `new X`).
                int baseLine = level.Opener.IsKind(SyntaxKind.OpenBraceToken) && BraceOwner(level.Opener) is { } o
                    ? Math.Min(Line(o.SpanStart), Line(level.Opener.SpanStart))
                    : Line(level.Opener.SpanStart);
                want = indent[baseLine] + (closer ? 0 : IndentSize);
                // A continuation sits one level past the line its item (argument, element, statement) began on.
                if (!closer && level.ItemLine >= 0 && level.ItemLine < l && !StartsItem(first, tokenAtStart))
                    want = Math.Max(want, indent[level.ItemLine] + IndentSize);
                // A value put on its own line (after `=>`, `=` or `name:`) is where its item really starts:
                // a `.Select(…)` chain under it continues from here.
                if (tokenAtStart && next > 0 &&
                    (tokens[next - 1].Kind() is SyntaxKind.EqualsGreaterThanToken or SyntaxKind.EqualsToken ||
                     (tokens[next - 1].IsKind(SyntaxKind.ColonToken) && tokens[next - 1].Parent is NameColonSyntax)))
                    level.ItemLine = l;
            }

            indent[l] = want;
            output[l] = new string(' ', want) + body;
        }

        return string.Join('\n', output);
    }

    /// <summary>An unclosed bracket, and the line its current item began on (-1 = no item yet).</summary>
    private sealed class Level(SyntaxToken opener)
    {
        public SyntaxToken Opener { get; } = opener;
        public int ItemLine { get; set; } = -1;
    }

    private static void Track(Stack<Level> open, SyntaxToken t, int line)
    {
        bool closes = open.Count > 0 && Closes(open.Peek().Opener, t);
        if (open.Count > 0 && open.Peek().ItemLine < 0 && !closes) open.Peek().ItemLine = line;
        if (t.Kind() is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenBraceToken)
            open.Push(new Level(t));
        else if (closes)
        {
            var done = open.Pop().Opener;
            // A body or an attribute ends its item: what follows starts a new statement or member.
            bool endsItem = (done.IsKind(SyntaxKind.OpenBraceToken) && done.Parent is not (InitializerExpressionSyntax or AnonymousObjectCreationExpressionSyntax)) ||
                            done.Parent is AttributeListSyntax;
            if (endsItem && open.Count > 0) open.Peek().ItemLine = -1;
        }
        else if (open.Count > 0 && t.Kind() is SyntaxKind.CommaToken or SyntaxKind.SemicolonToken)
            open.Peek().ItemLine = -1;
    }

    private static bool Closes(SyntaxToken opener, SyntaxToken t) => (opener.Kind(), t.Kind()) is
        (SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken) or
        (SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken) or
        (SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken);

    // Inside an interpolated string that began before this point (a raw/verbatim one spanning lines).
    private static bool InInterpolatedString(SyntaxToken t, int position) =>
        t.Parent?.AncestorsAndSelf().Any(n => n is InterpolatedStringExpressionSyntax && n.SpanStart < position) == true;

    // What a `{` that starts a line belongs to: the lambda, creation, member or statement it opens
    // a body for. A bare block statement has no owner — it indents as content.
    private static SyntaxNode? BraceOwner(SyntaxToken brace) => brace.Parent switch {
        BlockSyntax { Parent: BlockSyntax or SwitchSectionSyntax } => null,
        BlockSyntax b => b.Parent,
        InitializerExpressionSyntax i => i.Parent is EqualsValueClauseSyntax ? i.Parent.Parent : i.Parent,
        { } other => other, // type, namespace, accessor list, switch, property pattern, anonymous object
        null => null,
    };

    // Tokens that begin an item of their own even with no separator before them.
    private static bool StartsItem(SyntaxToken first, bool tokenAtStart) => tokenAtStart &&
        (first.Kind() is SyntaxKind.ElseKeyword or SyntaxKind.CatchKeyword or SyntaxKind.FinallyKeyword ||
         (first.Kind() is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword && first.Parent is SwitchLabelSyntax));

    // ── Edits ─────────────────────────────────────────────────────────────────

    private static string Apply(string text, IEnumerable<CodeTextEdit> edits)
    {
        foreach (var e in edits.OrderByDescending(e => e.Start)) text = text[..e.Start] + e.NewText + text[(e.Start + e.Length)..];
        return text;
    }

    /// <summary>
    ///     <paramref name="after" /> as edits to <paramref name="before" />: a line diff (LCS), and
    ///     within a changed line only the part that differs — so a caret on an untouched part of a
    ///     re-indented line keeps its place in the text.
    /// </summary>
    internal static IReadOnlyList<CodeTextEdit> Diff(string before, string after)
    {
        if (before == after) return [];
        string[] a = before.Split('\n'), b = after.Split('\n');
        int[] start = new int[a.Length + 1];
        for (int i = 0; i < a.Length; i++) start[i + 1] = start[i] + a[i].Length + 1;

        // ponytail: O(n·m) LCS table — examples are a few hundred lines; one whole-text edit beyond 4M cells.
        int n = a.Length, m = b.Length;
        if ((long)n * m > 4_000_000) return [new CodeTextEdit(0, before.Length, after)];
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        for (int j = m - 1; j >= 0; j--)
            lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var edits = new List<CodeTextEdit>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x] == b[y])
            {
                x++;
                y++;
                continue;
            }

            // A hunk: lines a[x..x2) became b[y..y2).
            int x2 = x, y2 = y;
            while ((x2 < n || y2 < m) && !(x2 < n && y2 < m && a[x2] == b[y2]))
            {
                if (y2 >= m || (x2 < n && lcs[x2 + 1, y2] >= lcs[x2, y2 + 1])) x2++;
                else y2++;
            }

            if (x2 - x == y2 - y)
                for (int k = 0; k < x2 - x; k++) edits.Add(LineEdit(start[x + k], a[x + k], b[y + k]));
            else if (x2 > x)
            {
                // Replace the old lines (without the final newline) by the new ones.
                string replacement = string.Join('\n', b[y..y2]);
                int s = start[x], e = start[x2] - 1;
                if (y2 == y) // pure deletion: take one newline with it
                    (s, e) = x2 < n ? (s, e + 1) : (Math.Max(0, s - 1), e);
                edits.Add(new CodeTextEdit(s, e - s, replacement));
            }
            else // pure insertion before a[x]
                edits.Add(x < n
                    ? new CodeTextEdit(start[x], 0, string.Join('\n', b[y..y2]) + "\n")
                    : new CodeTextEdit(before.Length, 0, "\n" + string.Join('\n', b[y..y2])));

            (x, y) = (x2, y2);
        }

        return edits;
    }

    private static CodeTextEdit LineEdit(int lineStart, string from, string to)
    {
        int prefix = 0;
        while (prefix < from.Length && prefix < to.Length && from[prefix] == to[prefix]) prefix++;
        int suffix = 0;
        while (suffix < from.Length - prefix && suffix < to.Length - prefix && from[^(suffix + 1)] == to[^(suffix + 1)]) suffix++;
        return new CodeTextEdit(lineStart + prefix, from.Length - prefix - suffix, to[prefix..^suffix]);
    }
}
