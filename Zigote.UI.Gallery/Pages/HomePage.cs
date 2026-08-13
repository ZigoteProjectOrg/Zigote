using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Localizations;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Gallery;

/// <summary>
///     The landing screen: a card per <see cref="DemoRegistry" /> entry. Tapping a card writes an
///     intent to the <see cref="NavigationStore" /> — the card knows nothing about routes or the
///     navigator. The appearance toggle is a <see cref="Watch" /> over the <see cref="ThemeStore" />'s
///     <c>Mode</c> signal; the language switcher talks to the framework's
///     <see cref="LocalizationsController" />. All strings come from the generated
///     <see cref="GalleryL10n" /> — <c>GalleryL10n.Of(context)</c> registers a dependency, so a
///     locale switch rebuilds this page (and mirrors it under RTL).
/// </summary>
internal sealed class HomePage : ComposedWidget
{
    private readonly NavigationStore _navigation;
    private readonly ThemeStore _theme;

    public HomePage(ThemeStore theme, NavigationStore navigation)
    {
        _theme = theme;
        _navigation = navigation;
    }

    protected override Widget Build(BuildContext context)
    {
        var l = GalleryL10n.Of(context);

        var cards = new List<Widget>();
        foreach (var demo in DemoRegistry.All) cards.Add(DemoCard(l: l, demo: demo));

        return new Scaffold(
            appBar: new AppBar(
                title: new Text(l.AppTitle),
                centerTitle: true,
                actions: [
                    new IconButton(
                        icon: new Icon(MaterialIcons.Info),
                        onPressed: AboutAction(l)
                    ),
                ]
            ),
            floatingActionButton: new FloatingActionButton(
                onPressed: FabAction(l),
                child: new Icon(MaterialIcons.Add)
            ),
            // One page, three form factors: the size class (from the width the body actually
            // gets — reacts to live resizes and rotation) picks the header arrangement and the
            // card-grid density. Compact stacks everything; wider classes keep the original
            // desktop composition.
            body: new AdaptiveBuilder((ctx, size) => new SingleChildScrollView {
                    Child = new Padding(
                        padding: EdgeInsets.All(size == WindowSizeClass.Compact ? 16 : 24),
                        child: new Column(
                            crossAxisAlignment: CrossAxisAlignment.Stretch,
                            children: [
                                Header(context: ctx, l: l, size: size),
                                new SizedBox(height: 20),
                                GridView.Count(
                                    crossAxisCount: size switch {
                                        WindowSizeClass.Compact => 1,
                                        WindowSizeClass.Medium => 2,
                                        _ => 3,
                                    },
                                    children: cards,
                                    mainAxisSpacing: 16,
                                    crossAxisSpacing: 16,
                                    childAspectRatio: size == WindowSizeClass.Compact ? 2.6 : 2.1
                                ),
                            ]
                        )
                    ),
                }
            )
        );
    }

    // Resolve the messages in Build (the l instance is only valid for the active locale), capture
    // the strings in the action.
    private static Action AboutAction(GalleryL10n l)
    {
        string title = l.HomeAboutTitle;
        string body = l.HomeAboutBody;
        return () => Dialog.Alert(title: title, message: body).Show();
    }

    private static Action FabAction(GalleryL10n l)
    {
        string message = l.HomeFab;
        return () => GalleryUi.Toast(message);
    }

    private Widget Header(BuildContext context, GalleryL10n l, WindowSizeClass size)
    {
        var headline = new Column(
            crossAxisAlignment: CrossAxisAlignment.Start,
            children: [
                new Text(
                    data: l.HomeHeadline,
                    style: new TextStyle(fontSize: 24, fontWeight: FontWeight.Bold)
                ),
                new SizedBox(height: 4),
                new Text(
                    data: l.HomeTagline,
                    style: new TextStyle(fontSize: 13, color: Colors.Grey[500])
                ),
            ]
        );

        // Watch rebuilds just this control when the theme signal changes. Labels are captured
        // from the locale-resolved `l` (this whole page rebuilds on a locale switch anyway).
        var appearance = new Watch(() => new SegmentedControl(
                segments: [l.HomeThemeLight, l.HomeThemeDark],
                selected: _theme.Mode.Value == ThemeMode.Dark ? 1 : 0,
                onChanged: i => _theme.Set(i == 1 ? ThemeMode.Dark : ThemeMode.Light)
            )
        );

        // Phone widths can't fit headline + both controls on one line — stack them, with each
        // control on its own labelled row. Wider classes keep the single-row composition.
        if (size == WindowSizeClass.Compact)
        {
            return new Column(
                crossAxisAlignment: CrossAxisAlignment.Start,
                children: [
                    headline,
                    new SizedBox(height: 16),
                    new Row(
                        crossAxisAlignment: CrossAxisAlignment.Center,
                        children: [
                            new Text(
                                data: l.HomeLanguage,
                                style: new TextStyle(color: Colors.Grey[500])
                            ),
                            new SizedBox(12),
                            LanguageSwitcher(context: context, l: l),
                        ]
                    ),
                    new SizedBox(height: 12),
                    new Row(
                        crossAxisAlignment: CrossAxisAlignment.Center,
                        children: [
                            new Text(
                                data: l.HomeAppearance,
                                style: new TextStyle(color: Colors.Grey[500])
                            ),
                            new SizedBox(12),
                            appearance,
                        ]
                    ),
                ]
            );
        }

        return new Row(
            crossAxisAlignment: CrossAxisAlignment.Center,
            children: [
                new Expanded(headline),
                new SizedBox(24),
                new Text(data: l.HomeLanguage, style: new TextStyle(color: Colors.Grey[500])),
                new SizedBox(12),
                LanguageSwitcher(context: context, l: l),
                new SizedBox(24),
                new Text(data: l.HomeAppearance, style: new TextStyle(color: Colors.Grey[500])),
                new SizedBox(12),
                appearance,
            ]
        );
    }

    private static Widget LanguageSwitcher(BuildContext context, GalleryL10n l)
    {
        // Capture the controller in Build — BuildContext is not valid inside the change callback.
        var controller = Localizations.ControllerOf(context);

        // Each option shows its own native name (the localeName message of that locale's catalog).
        var items = new List<DropdownMenuItem<string>>();
        foreach (var locale in GalleryL10n.SupportedLocales)
        {
            items.Add(
                new DropdownMenuItem<string>(
                    value: locale.ToBcp47(),
                    child: GalleryL10n.Load(locale).LocaleName
                )
            );
        }

        // The dropdown fills whatever width it is given — box it to a control-sized slot. The
        // pan-Unicode family renders every native name even while the UI face is Latin-only Inter.
        return new SizedBox(
            width: 150,
            child: new DropdownButton<string>(
                items: items,
                value: l.Locale.ToBcp47(),
                onChanged: tag =>
                {
                    if (tag is not null) controller?.SetLocale(Locale.Parse(tag));
                }
            ) { FontFamily = GalleryFonts.PanUnicodeFamily }
        );
    }

    private Widget DemoCard(GalleryL10n l, DemoInfo demo)
    {
        return new Card(
            new InkWell(
                onTap: () => _navigation.OpenDemo(demo.Id),
                child: new Padding(
                    padding: EdgeInsets.All(16),
                    child: new Column(
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        children: [
                            new CircleAvatar(
                                child: new Icon(demo.Icon) { Color = Colors.White },
                                backgroundColor: demo.Accent
                            ),
                            new SizedBox(height: 12),
                            new Text(
                                data: demo.Title(l),
                                style: new TextStyle(fontSize: 15, fontWeight: FontWeight.SemiBold)
                            ),
                            new SizedBox(height: 4),
                            new Text(
                                data: demo.Description(l),
                                style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
                            ),
                        ]
                    )
                )
            )
        );
    }
}
