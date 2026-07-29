using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Charts;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Gallery;

/// <summary>Shared building blocks for gallery pages: section cards, grids and small helpers.</summary>
internal static class GalleryUi
{
    public static void Toast(string message)
    {
        App.Active?.ShowSnackbar(message);
    }

    /// <summary>A titled card wrapping one demo, the standard page building block.</summary>
    public static Widget Section(string title, Widget child)
    {
        return new Padding(
            EdgeInsets.Only(bottom: 16),
            new Card(
                new Padding(
                    EdgeInsets.All(16),
                    new Column(
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        children: [
                            new Text(title, new TextStyle(15, fontWeight: FontWeight.SemiBold)),
                            new SizedBox(height: 12),
                            child,
                        ]
                    )
                )
            )
        );
    }

    public static Widget Sections(params Widget[] sections)
    {
        var col = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);
        foreach (var s in sections) col.Children.Add(s);
        return col;
    }

    /// <summary>
    ///     Lay sections out in a two-column <see cref="GridView" /> (uniform cells, sized to fit the
    ///     tallest section's content at the default window width). It sizes to content, so it scrolls
    ///     inside the page's scroll view.
    ///     <para>
    ///         Desktop widths only: the grid gives every cell the same fixed height, derived from the
    ///         column width. Narrower than <see cref="WindowSizeClass.Expanded" /> that height is
    ///         shorter than the demos inside it (charts, trees, lists all declare 150–220 px), so the
    ///         cells would clip their content and overpaint each other. Below Expanded the sections
    ///         go into the content-driven single column instead.
    ///     </para>
    /// </summary>
    public static Widget Grid2(params Widget[] sections)
    {
        return new AdaptiveBuilder((_, size) => size == WindowSizeClass.Expanded
            ? GridView.Count(
                2,
                sections,
                crossAxisSpacing: 16,
                childAspectRatio: 1.85
            )
            : Sections(sections));
    }

    /// <summary>
    ///     A control with its label beside it. On phones the row is the tap target: pass
    ///     <paramref name="onTap" /> with the same intent the control writes, since a bare glyph
    ///     (and the inert label next to it) is a poor thing to aim a finger at.
    /// </summary>
    public static Widget LabeledRow(Widget control, string label, Action? onTap = null)
    {
        return new AdaptiveBuilder((_, size) =>
        {
            var touch = size == WindowSizeClass.Compact;

            // A zero-width strut raises the row to a finger-sized band without widening it — the
            // row is Min-sized and sits inside horizontal groups (the radio group), so growing it
            // on the main axis would push its siblings off the card.
            Widget[] children = touch
                ? [new SizedBox(0, 44), control, new SizedBox(8), new Text(label)]
                : [control, new SizedBox(8), new Text(label)];

            var row = new Row(
                crossAxisAlignment: CrossAxisAlignment.Center,
                mainAxisSize: MainAxisSize.Min,
                children: children
            );

            // The detector captures the whole row, control included, so the tap has to carry the
            // control's intent — that is what onTap is for.
            return touch && onTap is not null ? new GestureDetector(row, onTap) : row;
        });
    }

    public static Widget Swatch(Color color, float radius)
    {
        return new Container(
            width: 64,
            height: 64,
            decoration: new BoxDecoration(color, BorderRadius.Circular(radius))
        );
    }

    public static Widget ChartBox(Chart chart)
    {
        return new SizedBox(height: 220, child: chart);
    }
}