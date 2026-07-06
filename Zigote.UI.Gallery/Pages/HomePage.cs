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
internal sealed class HomePage : StatelessWidget
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
        foreach (var demo in DemoRegistry.All) cards.Add(DemoCard(l, demo));

        return new Scaffold(
            new AppBar(
                new Text(l.AppTitle),
                centerTitle: true,
                actions: [
                    new IconButton(
                        new Icon(MaterialIcons.Info),
                        AboutAction(l)
                    ),
                ]
            ),
            floatingActionButton: new FloatingActionButton(
                FabAction(l),
                new Icon(MaterialIcons.Add)
            ),
            body: new SingleChildScrollView {
                Child = new Padding(
                    EdgeInsets.All(24),
                    new Column(
                        crossAxisAlignment: CrossAxisAlignment.Stretch,
                        children: [
                            Header(context, l),
                            new SizedBox(height: 20),
                            GridView.Count(
                                3,
                                cards,
                                16,
                                16,
                                2.1
                            ),
                        ]
                    )
                ),
            }
        );
    }

    // Resolve the messages in Build (the l instance is only valid for the active locale), capture
    // the strings in the action.
    private static Action AboutAction(GalleryL10n l)
    {
        var title = l.HomeAboutTitle;
        var body = l.HomeAboutBody;
        return () => Dialog.Alert(title, body).Show();
    }

    private static Action FabAction(GalleryL10n l)
    {
        var message = l.HomeFab;
        return () => GalleryUi.Toast(message);
    }

    private Widget Header(BuildContext context, GalleryL10n l)
    {
        return new Row(
            crossAxisAlignment: CrossAxisAlignment.Center,
            children: [
                new Expanded(
                    new Column(
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        children: [
                            new Text(
                                l.HomeHeadline,
                                new TextStyle(24, fontWeight: FontWeight.Bold)
                            ),
                            new SizedBox(height: 4),
                            new Text(
                                l.HomeTagline,
                                new TextStyle(13, color: Colors.Grey[500])
                            ),
                        ]
                    )
                ),
                new SizedBox(24),
                new Text(l.HomeLanguage, new TextStyle(color: Colors.Grey[500])),
                new SizedBox(12),
                LanguageSwitcher(context, l),
                new SizedBox(24),
                new Text(l.HomeAppearance, new TextStyle(color: Colors.Grey[500])),
                new SizedBox(12),
                // Watch rebuilds just this control when the theme signal changes. Labels are captured
                // from the locale-resolved `l` (this whole page rebuilds on a locale switch anyway).
                new Watch(() => new SegmentedControl(
                    [l.HomeThemeLight, l.HomeThemeDark],
                    _theme.Mode.Value == ThemeMode.Dark ? 1 : 0,
                    i => _theme.Set(i == 1 ? ThemeMode.Dark : ThemeMode.Light)
                )),
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
            items.Add(
                new DropdownMenuItem<string>(
                    locale.ToBcp47(),
                    GalleryL10n.Load(locale).LocaleName
                )
            );

        // The dropdown fills whatever width it is given — box it to a control-sized slot. The
        // pan-Unicode family renders every native name even while the UI face is Latin-only Inter.
        return new SizedBox(
            150,
            child: new DropdownButton<string>(
                items,
                l.Locale.ToBcp47(),
                tag =>
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
                    EdgeInsets.All(16),
                    new Column(
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        children: [
                            new CircleAvatar(
                                new Icon(demo.Icon) { Color = Colors.White },
                                demo.Accent
                            ),
                            new SizedBox(height: 12),
                            new Text(
                                demo.Title(l),
                                new TextStyle(15, fontWeight: FontWeight.SemiBold)
                            ),
                            new SizedBox(height: 4),
                            new Text(
                                demo.Description(l),
                                new TextStyle(12, color: Colors.Grey[500])
                            ),
                        ]
                    )
                )
            )
        );
    }
}