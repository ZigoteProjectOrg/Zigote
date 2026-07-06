using Xunit;
using Xunit.Abstractions;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Measures the command-kind distribution a representative UI frame emits, to decide whether a
///     ZgPaintCommand side-table split (moving "heavy" fields off the common command) would actually
///     pay off — the win only materialises if geometric (core-only) commands dominate.
/// </summary>
public class PaintCommandMixTests(ITestOutputHelper output)
{
    private static Widget Panel()
    {
        // Approximate a real editor/settings panel: a header, then many rows each with a label and a
        // couple of coloured surfaces (proxying controls), grouped into cards.
        var rows = new List<Widget>();
        for (var section = 0; section < 4; section++)
        {
            rows.Add(new Label($"Section {section}") { FontSize = 15f });
            for (var i = 0; i < 8; i++)
                rows.Add(
                    new Padding(
                        EdgeInsets.All(6f),
                        new Row(
                            [
                                new SizedBox(140f, 20f, new Label($"Property {i}")),
                                new SizedBox(8f, 0f),
                                new SizedBox(60f, 20f, new ColoredBox(new Color(0.3f, 0.5f, 0.8f))),
                                new SizedBox(8f, 0f),
                                new SizedBox(24f, 20f, new ColoredBox(new Color(0.8f, 0.3f, 0.3f))),
                            ]
                        )
                    )
                );
        }

        return new ColoredBox(
            new Color(0.1f, 0.1f, 0.12f),
            new Padding(EdgeInsets.All(12f), new Column(rows))
        );
    }

    [Fact]
    public void MeasurePaintCommandMix()
    {
        var root = Panel();
        var paint = new PaintList();
        root.Measure(Constraints.Tight(420f, 700f));
        root.Layout(Offset.Zero);
        root.Paint(paint);

        var counts = new Dictionary<PaintCommandKind, int>();
        foreach (var cmd in paint.DebugCommands)
        {
            var k = (PaintCommandKind)cmd.Kind;
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }

        var total = paint.Count;
        output.WriteLine($"total commands: {total}");
        foreach (var (kind, n) in counts.OrderByDescending(kv => kv.Value))
            output.WriteLine($"  {kind,-12} {n,4}  {100.0 * n / total,5:F1}%");

        // Fields that a side-table split would move off the common command are used by Text/Image/
        // GlyphRun/TextLayout/Polygon. Report how many commands would still need the "heavy" extension.
        var heavy = counts.GetValueOrDefault(PaintCommandKind.Text)
                    + counts.GetValueOrDefault(PaintCommandKind.Image)
                    + counts.GetValueOrDefault(PaintCommandKind.GlyphRun)
                    + counts.GetValueOrDefault(PaintCommandKind.TextLayout)
                    + counts.GetValueOrDefault(PaintCommandKind.Polygon);
        output.WriteLine(
            $"commands needing the heavy extension: {heavy} ({100.0 * heavy / total:F1}%)"
        );
        output.WriteLine(
            $"core-only (would shrink): {total - heavy} ({100.0 * (total - heavy) / total:F1}%)"
        );

        Assert.True(total > 0);
    }
}