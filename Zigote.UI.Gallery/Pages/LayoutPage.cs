using Zigote.Core;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     Layout primitives. Reads the ambient theme via <see cref="ThemeProvider.Of" /> (registering a
///     dependency), so the adaptive outlines rebuild on appearance change.
/// </summary>
internal sealed class LayoutPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        // The two demo strips below are hoisted so each size class can arrange the same boxes: a
        // Row never wraps, and both strips are intrinsically wider than a phone card's 302px.
        var figures = new List<Widget> {
            new SizedBox(
                width: 128,
                child: new AspectRatio(
                    aspectRatio: 16.0 / 9.0,
                    child: new Container(color: Colors.Teal, child: new Center(new Text("16:9")))
                )
            ),
            new Opacity(
                opacity: 0.5,
                child: new Container(width: 56, height: 56, color: Colors.Purple)
            ),
            new Container(
                width: 90,
                height: 56,
                color: Colors.Grey[800],
                child: new Align(alignment: Alignment.BottomRight, child: new Text("↘"))
            ),
        };

        var boxes = new List<Widget> {
            new ColoredBox(color: Colors.Indigo, child: new SizedBox(width: 56, height: 56)),
            new DecoratedBox {
                Fill = Colors.Pink,
                Radius = 10,
                BorderColor = theme.OnSurface,
                Child = new SizedBox(width: 56, height: 56),
            },
            new ClipRect(
                new ColoredBox(color: Colors.Teal, child: new SizedBox(width: 56, height: 56))
            ),
            new Transform(
                translation: new Offset(x: 0, y: 8),
                child: new ColoredBox(
                    color: Colors.Amber,
                    child: new SizedBox(width: 56, height: 40)
                )
            ),
            new ConstrainedBox(
                constraints: new Constraints(minWidth: 96, minHeight: 44),
                child: new ColoredBox(Colors.Grey[700])
            ),
        };

        return Sections(
            Section(
                title: "Container & BoxDecoration",
                child: new Row(
                    mainAxisAlignment: MainAxisAlignment.SpaceBetween,
                    children: [
                        Swatch(color: Colors.Blue, radius: 12),
                        Swatch(color: Colors.Green, radius: 12),
                        Swatch(color: Colors.Orange, radius: 32),
                        new Container(
                            width: 64,
                            height: 64,
                            decoration: new BoxDecoration(
                                borderRadius: BorderRadius.Circular(12),
                                // Adaptive so the outline stays visible on the light background (a
                                // hardcoded white border vanished in light mode).
                                border: Border.All(color: theme.OnSurface, width: 2)
                            )
                        ),
                    ]
                )
            ),
            Section(
                title: "Row alignment (SpaceBetween)",
                child: new Row(
                    mainAxisAlignment: MainAxisAlignment.SpaceBetween,
                    children: [new Text("Left"), new Text("Center"), new Text("Right")]
                )
            ),
            Section(
                title: "Wrap",
                child: new Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                        new Chip("one"), new Chip("two"), new Chip("three"), new Chip("four"),
                        new Chip("five"), new Chip("six"), new Chip("seven"),
                    ]
                )
            ),
            Section(
                title: "Stack + Positioned",
                child: new SizedBox(
                    width: 120,
                    height: 120,
                    child: new Stack(
                        [
                            new Container(width: 120, height: 120, color: Colors.Indigo),
                            new Positioned(
                                child: new Icon(MaterialIcons.Star) { Color = Colors.White },
                                right: 6,
                                bottom: 6
                            ),
                        ]
                    )
                )
            ),
            Section(
                title: "AspectRatio · Opacity · Align",
                child: new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                    // 322px of boxes in a 302px card: the trailing Container would be silently
                    // squashed by the leftover width, breaking the very widget it demonstrates.
                    ? new Wrap(children: figures, spacing: 24, runSpacing: 24)
                    : new Row(children: figures, spacing: 24)
                )
            ),
            Section(
                title: "ColoredBox · DecoratedBox · ClipRect · Transform · ConstrainedBox",
                child: new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                    // 384px wide: the ConstrainedBox has a 96px minimum, so it wins over the
                    // remaining width and paints outside the card instead of shrinking.
                    ? new Wrap(children: boxes, spacing: 16, runSpacing: 16)
                    : new Row(children: boxes, spacing: 16)
                )
            ),
            Section(
                title: "FractionallySizedBox · LayoutBuilder · SafeArea",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new SizedBox(
                            height: 40,
                            child: new FractionallySizedBox(
                                child: new ColoredBox(
                                    color: Colors.Cyan,
                                    child: new Center(new Text("30% width"))
                                ),
                                widthFactor: 0.3f,
                                heightFactor: 1f
                            )
                        ),
                        new SizedBox(height: 8),
                        new LayoutBuilder((ctx, c) =>
                            new Text($"LayoutBuilder sees ≈ {c.MaxWidth:F0}px of width")
                        ),
                        new SafeArea(
                            new Text("SafeArea (real insets on mobile, passthrough on desktop)")
                        ),
                    ]
                )
            ),
            Section(
                title: "GridView.count",
                // The column count follows the width available rather than a fixed desktop number;
                // four columns leave 69px cells on a phone.
                child: new AdaptiveBuilder((_, size) => GridView.Count(
                        crossAxisCount: size == WindowSizeClass.Compact ? 3 : 4,
                        children: [
                            Swatch(color: Colors.Red, radius: 8),
                            Swatch(color: Colors.Amber, radius: 8),
                            Swatch(color: Colors.Green, radius: 8),
                            Swatch(color: Colors.Cyan, radius: 8),
                            Swatch(color: Colors.Pink, radius: 8),
                            Swatch(color: Colors.Lime, radius: 8),
                            Swatch(color: Colors.Brown, radius: 8),
                            Swatch(color: Colors.BlueGrey, radius: 8),
                        ],
                        mainAxisSpacing: 8,
                        crossAxisSpacing: 8
                    )
                )
            )
        );
    }
}
