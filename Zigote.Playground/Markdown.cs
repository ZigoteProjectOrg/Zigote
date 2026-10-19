namespace Playground;

/// <summary>One block of a page. Only the subset of Markdown the docs use.</summary>
internal abstract record Block
{
    public sealed record Heading(int Level, string Text) : Block;

    public sealed record Paragraph(string Text) : Block;

    public sealed record Bullets(string[] Items, bool Numbered) : Block;

    public sealed record Note(string Text) : Block;

    public sealed record Table(string[][] Rows) : Block;

    /// <summary>A fenced block. <c>```csharp run</c> makes it a live example.</summary>
    public sealed record Code(string Source, bool Run, float Height) : Block;
}

/// <summary>
///     Line-oriented Markdown: headings, paragraphs, lists, block quotes, pipe tables and fences.
///     Inline: <c>`code`</c>, <c>**bold**</c>, <c>*italic*</c> and <c>[text](target)</c> links, whose
///     target the page hands to its link handler (<c>type:Full.Name</c>, <c>doc:Page Title</c>).
///     Nothing else — the docs are ours.
/// </summary>
internal static class Markdown
{
    public static List<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var para = new List<string>();

        void Flush()
        {
            if (para.Count > 0) blocks.Add(new Block.Paragraph(string.Join(' ', para)));
            para.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```"))
            {
                Flush();
                string[] info = trimmed[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool run = info.Contains("run");
                float height = info.Select(s => s.StartsWith("height=") &&
                                                float.TryParse(s[7..], out float h) ? h : 0f)
                    .FirstOrDefault(h => h > 0f, 220f);
                var body = new List<string>();
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```")) body.Add(lines[i]);
                blocks.Add(new Block.Code(string.Join('\n', body), run, height));
            }
            else if (trimmed.Length == 0)
                Flush();
            else if (trimmed.StartsWith('#'))
            {
                Flush();
                int level = trimmed.TakeWhile(c => c == '#').Count();
                blocks.Add(new Block.Heading(level, trimmed[level..].Trim()));
            }
            else if (trimmed.StartsWith('>'))
            {
                Flush();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quote.Add(lines[i].TrimStart()[1..].Trim());
                i--;
                blocks.Add(new Block.Note(string.Join(' ', quote)));
            }
            else if (trimmed.StartsWith('|'))
            {
                Flush();
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    string[] cells = [.. lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim())];
                    if (!cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'))) rows.Add(cells);
                }
                i--;
                blocks.Add(new Block.Table([.. rows]));
            }
            else if (ListItem(trimmed) is { } first)
            {
                Flush();
                bool numbered = char.IsDigit(trimmed[0]);
                var items = new List<string> { first };
                // Indented lines continue the item above them.
                while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0)
                {
                    string next = lines[i + 1];
                    if (ListItem(next.Trim()) is { } item) items.Add(item);
                    else if (next.StartsWith("  ")) items[^1] += " " + next.Trim();
                    else break;
                    i++;
                }
                blocks.Add(new Block.Bullets([.. items], numbered));
            }
            else
                para.Add(trimmed);
        }

        Flush();
        return blocks;
    }

    private static string? ListItem(string line)
    {
        if (line.StartsWith("- ") || line.StartsWith("* ")) return line[2..];
        int dot = line.IndexOf(". ", StringComparison.Ordinal);
        return dot is > 0 and < 4 && line[..dot].All(char.IsDigit) ? line[(dot + 2)..] : null;
    }

    /// <summary>Split inline markup into spans: code, bold, italic and link runs.</summary>
    public static List<TextSpan> Inline(string text, Color codeBg, Color linkColor = default,
        Action<string>? onLink = null)
    {
        var spans = new List<TextSpan>();
        var run = new System.Text.StringBuilder();

        void Plain()
        {
            if (run.Length > 0) spans.Add(new TextSpan(run.ToString()));
            run.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            // [label](target) — the label may itself be `code`.
            if (text[i] == '[' && text.IndexOf("](", i + 1, StringComparison.Ordinal) is var mid and > 0 &&
                text.IndexOf(')', mid + 2) is var close and > 0)
            {
                Plain();
                string label = text[(i + 1)..mid];
                string target = text[(mid + 2)..close];
                bool code = label.Length > 1 && label[0] == '`' && label[^1] == '`';
                var span = new TextSpan(code ? label[1..^1] : label) { Underline = !code };
                if (linkColor.A > 0) span.Color = linkColor;
                if (code)
                {
                    span.FontFamily = AdwTypography.Monospace.FontFamily;
                    span.Background = codeBg;
                }

                if (onLink is not null) span.OnTap = () => onLink(target);
                spans.Add(span);
                i = close;
            }
            else if (text[i] == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                Plain();
                spans.Add(new TextSpan(text[(i + 1)..end]) {
                    FontFamily = AdwTypography.Monospace.FontFamily,
                    Background = codeBg,
                });
                i = end;
            }
            else if (text.AsSpan(i).StartsWith("**") && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var bEnd and > 0)
            {
                Plain();
                spans.Add(new TextSpan(text[(i + 2)..bEnd]) { Weight = FontWeight.Bold });
                i = bEnd + 1;
            }
            else if (text[i] == '*' && text.IndexOf('*', i + 1) is var iEnd and > 0)
            {
                Plain();
                spans.Add(new TextSpan(text[(i + 1)..iEnd]) { Style = FontStyle.Italic });
                i = iEnd;
            }
            else
                run.Append(text[i]);
        }

        Plain();
        return spans;
    }
}
