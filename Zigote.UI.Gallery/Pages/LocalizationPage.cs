using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Localizations;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     The i18n framework showcase: compile-safe messages via the generated <see cref="GalleryL10n" />
///     (ICU placeholders, CLDR plurals, ordinals, select — each a typed property/method), locale-aware
///     number/currency/date formatting, and RTL direction-aware layout. Everything re-resolves live
///     when the locale switches (<c>GalleryL10n.Of</c> registers a dependency on the ambient
///     <see cref="Localizations" /> provider).
/// </summary>
internal sealed class LocalizationPage : StatefulWidget
{
    protected override WidgetState CreateState()
    {
        return new LocalizationPageState();
    }
}

internal sealed class LocalizationPageState : WidgetState<LocalizationPage>
{
    private static readonly string[] Genders = ["male", "female", "other"];
    private float _count = 2;
    private int _gender;

    public override Widget Build(BuildContext context)
    {
        var l = GalleryL10n.Of(context);
        var direction = context.TextDirectionOf();
        var count = (int)_count;

        return Sections(
            Section(
                l.SectionLocale,
                // One chip per string, each single-script: the engine shapes one run per string
                // (direction guessed from its first strong character), so mixing scripts in one
                // string would render the embedded run in the wrong order.
                // Wrap, not Row: a single run on desktop, but the Arabic labels are wider than a
                // phone card and a Row would paint the overflow outside it.
                new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new Chip(l.Locale.ToBcp47()),
                        new Chip(l.LocaleName),
                        new Chip(direction == TextDirection.Rtl ? l.DirectionRtl : l.DirectionLtr),
                    ]
                )
            ),
            Section(
                l.SectionMessages,
                // A parameterized message is a generated method; the name argument is itself a
                // translated message, so it matches the script.
                new Text(l.Greeting(l.GreetingName), new TextStyle(17))
            ),
            Section(
                l.SectionPlural,
                CounterRow(l.FilesLabel, l.Files(count))
            ),
            Section(
                l.SectionOrdinal,
                CounterRow(l.RankLabel, l.Rank(count))
            ),
            Section(
                l.SectionSelect,
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new SegmentedControl(
                            [l.GenderMale, l.GenderFemale, l.GenderOther],
                            _gender,
                            i => SetStateRebuild(() => _gender = i)
                        ),
                        new SizedBox(height: 12),
                        new Text(l.Invite(Genders[_gender]), new TextStyle(15)),
                    ]
                )
            ),
            Section(
                l.SectionFormatting,
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        FormatRow(l.FmtNumber, context.FormatNumber(1234567.89)),
                        FormatRow(
                            l.FmtCurrency,
                            $"{context.FormatCurrency(1234.56m, "USD")}   ·   " +
                            $"{context.FormatCurrency(1234.56m, "EUR")}   ·   " +
                            $"{context.FormatCurrency(1234.56m, "JPY")}"
                        ),
                        FormatRow(l.FmtPercent, context.FormatPercent(0.734, 1)),
                        FormatRow(
                            l.FmtDate,
                            context.FormatDate(new DateTime(2026, 7, 5), DateStyle.Full)
                        ),
                        FormatRow(
                            l.FmtTime,
                            context.FormatTime(
                                new DateTime(
                                    2026,
                                    7,
                                    5,
                                    16,
                                    45,
                                    0
                                )
                            )
                        ),
                    ]
                )
            ),
            Section(
                l.SectionDirection,
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new Text(
                            l.DirectionNote,
                            new TextStyle(12, color: Colors.Grey[500])
                        ),
                        new SizedBox(height: 12),
                        DirectionStrip(
                            context,
                            l,
                            l.StripAmbient,
                            null
                        ),
                        new SizedBox(height: 8),
                        // An explicit Directionality overrides the ambient direction for a subtree —
                        // the same mechanism the LocalizationsScope installs app-wide from the locale.
                        DirectionStrip(
                            context,
                            l,
                            l.StripRtl,
                            TextDirection.Rtl
                        ),
                    ]
                )
            )
        );
    }

    private Widget CounterRow(string label, string message)
    {
        var stepper = new Stepper(
            _count,
            1,
            0,
            111,
            v => SetStateRebuild(() => _count = v)
        );
        var counter = new Text(
            $"{label}: {(int)_count}",
            new TextStyle(12, color: Colors.Grey[500])
        );
        var result = new Text(message, new TextStyle(15, fontWeight: FontWeight.Medium));

        // On a phone the translated label and the plural message can each fill the card on their
        // own, so the message drops below the stepper instead of sharing its line.
        return new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
            ? new Column(
                crossAxisAlignment: CrossAxisAlignment.Start,
                children: [
                    new Row(
                        mainAxisSize: MainAxisSize.Min,
                        children: [stepper, new SizedBox(12), counter]
                    ),
                    new SizedBox(height: 8),
                    result,
                ]
            )
            : new Row([stepper, new SizedBox(12), counter, new SizedBox(24), result])
        );
    }

    private static Widget FormatRow(string label, string value)
    {
        var labelText = new Text(label, new TextStyle(12, color: Colors.Grey[500]));
        var valueText = new Text(value, new TextStyle(14));

        // The 120px label column costs 40% of a phone card's width, wrapping the long values (a
        // full date, three currencies) over several lines — stack them there instead.
        return new Padding(
            EdgeInsets.Only(bottom: 6),
            new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                ? new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [labelText, valueText]
                )
                : new Row([new SizedBox(120, child: labelText), valueText])
            )
        );
    }

    private static Widget DirectionStrip(BuildContext context, GalleryL10n l, string label,
        TextDirection? force)
    {
        var onSurface = ThemeProvider.Of(context).OnSurface;
        var chips = new List<Widget>();
        for (var i = 1; i <= 4; i++) chips.Add(StepChip(l.ChipStep(i), i == 1, onSurface));

        // Wrap mirrors under Directionality exactly like the Row did, but reflows the chips
        // instead of painting them past the card once the translated labels grow.
        Widget strip = new Wrap(chips, spacing: 8, runSpacing: 8);
        if (force is { } dir) strip = new Directionality(dir, strip);

        var labelText = new Text(label, new TextStyle(12, color: Colors.Grey[500]));

        // Label column (140) plus four chips (272) needs more width than a phone card has, so the
        // label takes its own line there.
        return new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
            ? new Column(
                crossAxisAlignment: CrossAxisAlignment.Start,
                children: [labelText, new SizedBox(height: 6), strip]
            )
            : new Row([new SizedBox(140, child: labelText), strip])
        );
    }

    private static Widget StepChip(string label, bool accent, Color onSurface)
    {
        return new DecoratedBox {
            Fill = accent
                ? Color.Blue.WithAlpha(0.85f)
                : new Color(
                    0.5f,
                    0.5f,
                    0.5f,
                    0.18f
                ),
            Radius = Radii.Capsule,
            BorderWidth = 0f,
            Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Md, Spacing.Xs),
                new Label(label, 12, accent ? Color.White : onSurface)
            ),
        };
    }
}