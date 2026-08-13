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
                title: "Typography",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new Text(
                            data: "Large Title",
                            style: new TextStyle(fontSize: 28, fontWeight: FontWeight.Bold)
                        ),
                        new Text(
                            data: "Title",
                            style: new TextStyle(fontSize: 20, fontWeight: FontWeight.SemiBold)
                        ),
                        new Text(
                            data: "Headline",
                            style: new TextStyle(fontSize: 15, fontWeight: FontWeight.SemiBold)
                        ),
                        new Text("Body — the quick brown fox jumps over the lazy dog."),
                        new Text(
                            data: "Italic caption",
                            style: new TextStyle(fontSize: 12, fontStyle: FontStyle.Italic)
                        ),
                        new Text(
                            data: "Accent-coloured",
                            style: new TextStyle(color: Colors.Blue, fontWeight: FontWeight.Medium)
                        ),
                    ]
                )
            ),
            Section(
                title: "Buttons",
                child: new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new ElevatedButton(
                            child: new Text("Elevated"),
                            onPressed: () => Toast("Elevated")
                        ),
                        new FilledButton(
                            child: new Text("Filled"),
                            onPressed: () => Toast("Filled")
                        ),
                        new OutlinedButton(
                            child: new Text("Outlined"),
                            onPressed: () => Toast("Outlined")
                        ),
                        new TextButton(child: new Text("Text"), onPressed: () => Toast("Text")),
                        new ElevatedButton(new Text("Disabled")),
                        new ElevatedButton(
                            child: new Row(
                                mainAxisSize: MainAxisSize.Min,
                                children: [
                                    new Icon(MaterialIcons.Add) { Size = 16 }, new SizedBox(6),
                                    new Text("With icon"),
                                ]
                            ),
                            onPressed: () => Toast("Icon + label")
                        ),
                    ]
                )
            ),
            Section(
                title: "Icon buttons & gestures",
                child: new AdaptiveBuilder((_, size) => new Row(
                        [
                            new IconButton(
                                icon: new Icon(MaterialIcons.Home),
                                onPressed: () => Toast("home")
                            ),
                            new IconButton(
                                icon: new Icon(MaterialIcons.Search),
                                onPressed: () => Toast("search")
                            ),
                            new IconButton(
                                icon: new Icon(MaterialIcons.Settings),
                                onPressed: () => Toast("settings")
                            ),
                            new SizedBox(16),
                            new InkWell(
                                onTap: () => Toast("InkWell tapped"),
                                child: new Container(
                                    // A bare Container gets no control metrics of its own, so its
                                    // padding is the only thing sizing the tap target — 44 px on touch.
                                    padding: size == WindowSizeClass.Compact
                                        ? EdgeInsets.Symmetric(horizontal: 16, vertical: 14)
                                        : EdgeInsets.Symmetric(horizontal: 12, vertical: 8),
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
