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
internal sealed class LayoutPage : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return Sections(
            Section(
                "Container & BoxDecoration",
                new Row(
                    mainAxisAlignment: MainAxisAlignment.SpaceBetween,
                    children: [
                        Swatch(Colors.Blue, 12),
                        Swatch(Colors.Green, 12),
                        Swatch(Colors.Orange, 32),
                        new Container(
                            width: 64,
                            height: 64,
                            decoration: new BoxDecoration(
                                borderRadius: BorderRadius.Circular(12),
                                // Adaptive so the outline stays visible on the light background (a
                                // hardcoded white border vanished in light mode).
                                border: Border.All(theme.OnSurface, 2)
                            )
                        ),
                    ]
                )
            ),
            Section(
                "Row alignment (SpaceBetween)",
                new Row(
                    mainAxisAlignment: MainAxisAlignment.SpaceBetween,
                    children: [new Text("Left"), new Text("Center"), new Text("Right")]
                )
            ),
            Section(
                "Wrap",
                new Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                        new Chip("one"), new Chip("two"), new Chip("three"), new Chip("four"),
                        new Chip("five"), new Chip("six"), new Chip("seven"),
                    ]
                )
            ),
            Section(
                "Stack + Positioned",
                new SizedBox(
                    120,
                    120,
                    new Stack(
                        [
                            new Container(width: 120, height: 120, color: Colors.Indigo),
                            new Positioned(
                                new Icon(MaterialIcons.Star) { Color = Colors.White },
                                right: 6,
                                bottom: 6
                            ),
                        ]
                    )
                )
            ),
            Section(
                "AspectRatio · Opacity · Align",
                new Row(
                    [
                        new SizedBox(
                            128,
                            child: new AspectRatio(
                                16.0 / 9.0,
                                new Container(
                                    color: Colors.Teal,
                                    child: new Center(new Text("16:9"))
                                )
                            )
                        ),
                        new SizedBox(24),
                        new Opacity(
                            0.5,
                            new Container(width: 56, height: 56, color: Colors.Purple)
                        ),
                        new SizedBox(24),
                        new Container(
                            width: 90,
                            height: 56,
                            color: Colors.Grey[800],
                            child: new Align(Alignment.BottomRight, new Text("↘"))
                        ),
                    ]
                )
            ),
            Section(
                "ColoredBox · DecoratedBox · ClipRect · Transform · ConstrainedBox",
                new Row(
                    [
                        new ColoredBox(Colors.Indigo, new SizedBox(56, 56)),
                        new SizedBox(16),
                        new DecoratedBox {
                            Fill = Colors.Pink,
                            Radius = 10,
                            BorderColor = theme.OnSurface,
                            Child = new SizedBox(56, 56),
                        },
                        new SizedBox(16),
                        new ClipRect(new ColoredBox(Colors.Teal, new SizedBox(56, 56))),
                        new SizedBox(16),
                        new Transform(
                            new Offset(0, 8),
                            new ColoredBox(Colors.Amber, new SizedBox(56, 40))
                        ),
                        new SizedBox(16),
                        new ConstrainedBox(
                            new Constraints(96, minHeight: 44),
                            new ColoredBox(Colors.Grey[700])
                        ),
                    ]
                )
            ),
            Section(
                "FractionallySizedBox · LayoutBuilder · SafeArea",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new SizedBox(
                            height: 40,
                            child: new FractionallySizedBox(
                                new ColoredBox(Colors.Cyan, new Center(new Text("30% width"))),
                                0.3f,
                                1f
                            )
                        ),
                        new SizedBox(height: 8),
                        new LayoutBuilder((ctx, c) =>
                            new Text($"LayoutBuilder sees ≈ {c.MaxWidth:F0}px of width")
                        ),
                        new SafeArea(new Text("SafeArea (a passthrough on desktop)")),
                    ]
                )
            ),
            Section(
                "GridView.count",
                GridView.Count(
                    4,
                    [
                        Swatch(Colors.Red, 8), Swatch(Colors.Amber, 8), Swatch(Colors.Green, 8),
                        Swatch(Colors.Cyan, 8),
                        Swatch(Colors.Pink, 8), Swatch(Colors.Lime, 8), Swatch(Colors.Brown, 8),
                        Swatch(Colors.BlueGrey, 8),
                    ],
                    8,
                    8
                )
            )
        );
    }
}