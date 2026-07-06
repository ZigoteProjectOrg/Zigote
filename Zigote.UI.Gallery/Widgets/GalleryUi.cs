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
    /// </summary>
    public static Widget Grid2(params Widget[] sections)
    {
        return GridView.Count(
            2,
            sections,
            crossAxisSpacing: 16,
            childAspectRatio: 1.85
        );
    }

    public static Widget LabeledRow(Widget control, string label)
    {
        return new Row(
            crossAxisAlignment: CrossAxisAlignment.Center,
            mainAxisSize: MainAxisSize.Min,
            children: [
                control,
                new SizedBox(8),
                new Text(label),
            ]
        );
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