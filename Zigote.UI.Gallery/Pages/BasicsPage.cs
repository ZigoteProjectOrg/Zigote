using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>Typography, buttons, icon buttons and gestures. Stateless — actions only toast.</summary>
internal sealed class BasicsPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        return Sections(
            Section(
                "Typography",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new Text("Large Title", new TextStyle(28, fontWeight: FontWeight.Bold)),
                        new Text("Title", new TextStyle(20, fontWeight: FontWeight.SemiBold)),
                        new Text("Headline", new TextStyle(15, fontWeight: FontWeight.SemiBold)),
                        new Text("Body — the quick brown fox jumps over the lazy dog."),
                        new Text("Italic caption", new TextStyle(12, fontStyle: FontStyle.Italic)),
                        new Text(
                            "Accent-coloured",
                            new TextStyle(color: Colors.Blue, fontWeight: FontWeight.Medium)
                        ),
                    ]
                )
            ),
            Section(
                "Buttons",
                new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new ElevatedButton(new Text("Elevated"), () => Toast("Elevated")),
                        new FilledButton(new Text("Filled"), () => Toast("Filled")),
                        new OutlinedButton(new Text("Outlined"), () => Toast("Outlined")),
                        new TextButton(new Text("Text"), () => Toast("Text")),
                        new ElevatedButton(new Text("Disabled")),
                        new ElevatedButton(
                            new Row(
                                mainAxisSize: MainAxisSize.Min,
                                children: [
                                    new Icon(MaterialIcons.Add) { Size = 16 }, new SizedBox(6),
                                    new Text("With icon"),
                                ]
                            ),
                            () => Toast("Icon + label")
                        ),
                    ]
                )
            ),
            Section(
                "Icon buttons & gestures",
                new AdaptiveBuilder((_, size) => new Row(
                        [
                            new IconButton(new Icon(MaterialIcons.Home), () => Toast("home")),
                            new IconButton(new Icon(MaterialIcons.Search), () => Toast("search")),
                            new IconButton(
                                new Icon(MaterialIcons.Settings),
                                () => Toast("settings")
                            ),
                            new SizedBox(16),
                            new InkWell(
                                onTap: () => Toast("InkWell tapped"),
                                child: new Container(
                                    // A bare Container gets no control metrics of its own, so its
                                    // padding is the only thing sizing the tap target — 44 px on touch.
                                    padding: size == WindowSizeClass.Compact
                                        ? EdgeInsets.Symmetric(16, 14)
                                        : EdgeInsets.Symmetric(12, 8),
                                    color: Colors.Blue,
                                    child: new Text("InkWell")
                                )
                            ),
                        ]
                    )
                )
            )
        );
    }
}
