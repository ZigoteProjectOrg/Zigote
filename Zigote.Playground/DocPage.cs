using Zigote.Modules.UI.CodeEditor;

namespace Playground;

/// <summary>
///     One page of Markdown laid out as a clamped, scrolling column — a hand-written docs page, or
///     a generated widget-catalog page.
/// </summary>
internal sealed class DocPage(List<Block> blocks) : ComposedWidget
{
    public DocPage(string markdown) : this(Markdown.Parse(markdown)) { }

    /// <summary>Where a tapped <c>[text](target)</c> link goes; links do nothing without it.</summary>
    public Action<string>? OnLink { get; init; }

    /// <summary>This page as a link target (<c>doc:Title</c>, <c>type:Full.Name</c>) — what its examples' share links point back to.</summary>
    public string? Origin { get; init; }

    /// <summary>The examples came from outside (a share link): they wait for Run instead of running on sight.</summary>
    public bool Untrusted { get; init; }

    // Built once: examples are retained so an edit survives switching pages and theme flips.
    private readonly List<Block> _blocks = blocks;
    private readonly Dictionary<Block, Widget> _retained = [];

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        Color codeBg = AdwPalette.Fill(theme: theme, percent: 0.08f);
        _link = p.Accent;

        var column = new Column(
            spacing: Spacing.Lg,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        foreach (var block in _blocks)
            column.Children.Add(Render(block, theme, p, codeBg));

        return new SingleChildScrollView {
            Child = new AdwClamp(
                child: new Padding(
                    EdgeInsets.Only(left: Spacing.Xxl, top: Spacing.Xxl, right: Spacing.Xxl, bottom: 64f),
                    column
                ),
                maximumSize: 860f
            ),
        };
    }

    private Widget Render(Block block, ThemeData theme, AdwColors p, Color codeBg) => block switch {
        Block.Heading h => new Padding(
            EdgeInsets.Only(top: h.Level == 1 ? 0f : Spacing.Md),
            new Label(
                text: h.Text,
                style: h.Level switch {
                    1 => AdwTypography.Title1,
                    2 => AdwTypography.Title2,
                    _ => AdwTypography.Title4,
                }
            )
        ),
        Block.Paragraph para => Prose(para.Text, codeBg),
        Block.Bullets list => Bullets(list, theme, codeBg),
        Block.Note note => new DecoratedBox {
            Fill = AdwPalette.Wash(tint: p.Accent, percent: 0.08f, over: p.ViewBg),
            Radius = AdwMetrics.CardRadius,
            BorderColor = AdwPalette.Wash(tint: p.Accent, percent: 0.25f, over: p.ViewBg),
            Child = new Padding(EdgeInsets.All(Spacing.Lg), Prose(note.Text, codeBg)),
        },
        Block.Table table => Table(table, p, codeBg),
        Block.Code { Run: true } code => Retain(code, () => new ExampleView(code.Source, code.Height, Origin, trusted: !Untrusted)),
        Block.Code code => Retain(code, () => new DecoratedBox {
                Fill = p.CardBg,
                Radius = AdwMetrics.CardRadius,
                BorderColor = p.CardShade,
                Child = new ClipRRect(
                    radius: AdwMetrics.CardRadius,
                    child: new SizedBox(
                        height: (code.Source.Count(c => c == '\n') + 1) * 19f + 16f,
                        child: new AdwCodeEditor(code.Source) {
                            Tokenizer = Highlighting.CSharp,
                            ReadOnly = true,
                        }
                    )
                ),
            }
        ),
        _ => SizedBox.Shrink(),
    };

    // Stateful blocks keep their instance across theme rebuilds (their own Build re-themes them).
    private Widget Retain(Block block, Func<Widget> create)
    {
        if (!_retained.TryGetValue(block, out var widget)) _retained[block] = widget = create();
        return widget;
    }

    private Color _link;

    private RichText Prose(string text, Color codeBg) => new(Markdown.Inline(text, codeBg, _link, OnLink)) {
        FontSize = AdwTypography.Body.Size,
        LineHeight = 1.5f,
    };

    private Widget Bullets(Block.Bullets list, ThemeData theme, Color codeBg)
    {
        var column = new Column(
            spacing: Spacing.Xs,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        for (int i = 0; i < list.Items.Length; i++)
        {
            column.Children.Add(new Row(
                crossAxisAlignment: CrossAxisAlignment.Start,
                spacing: Spacing.Sm,
                children: [
                    new SizedBox(
                        width: 20f,
                        child: new Label(
                            text: list.Numbered ? $"{i + 1}." : "•",
                            style: AdwTypography.Body,
                            color: theme.TextSecondary
                        ) { LineHeight = 1.5f }
                    ),
                    new Expanded(Prose(list.Items[i], codeBg)),
                ]
            ));
        }

        return new Padding(EdgeInsets.Only(left: Spacing.Sm), column);
    }

    private Widget Table(Block.Table table, AdwColors p, Color codeBg)
    {
        var column = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch, mainAxisSize: MainAxisSize.Min);
        for (int r = 0; r < table.Rows.Length; r++)
        {
            var row = new Row(crossAxisAlignment: CrossAxisAlignment.Start, spacing: Spacing.Md);
            foreach (string cell in table.Rows[r])
            {
                var text = Prose(cell, codeBg);
                if (r == 0) text.FontWeight = FontWeight.Bold;
                row.Children.Add(new Expanded(text));
            }

            if (r > 0) column.Children.Add(new ColoredBox(p.CardShade, new SizedBox(height: 1f)));
            column.Children.Add(new Padding(EdgeInsets.Symmetric(horizontal: Spacing.Md, vertical: Spacing.Sm), row));
        }

        return new DecoratedBox {
            Fill = p.CardBg,
            Radius = AdwMetrics.CardRadius,
            BorderColor = p.CardShade,
            Child = column,
        };
    }
}
