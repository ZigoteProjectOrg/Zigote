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
internal sealed class LocalizationPage : ComposedWidget
{
    private static readonly string[] Genders = ["male", "female", "other"];
    private float _count = 2;
    private int _gender;

    protected override Widget Build(BuildContext context)
    {
        var l = GalleryL10n.Of(context);
        var direction = context.TextDirectionOf();
        int count = (int)_count;

        return Sections(
            Section(
                title: l.SectionLocale,
                // One chip per string, each single-script: the engine shapes one run per string
                // (direction guessed from its first strong character), so mixing scripts in one
                // string would render the embedded run in the wrong order.
                // Wrap, not Row: a single run on desktop, but the Arabic labels are wider than a
                // phone card and a Row would paint the overflow outside it.
                child: new Wrap(
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
                title: l.SectionMessages,
                // A parameterized message is a generated method; the name argument is itself a
                // translated message, so it matches the script.
                child: new Text(data: l.Greeting(l.GreetingName), style: new TextStyle(17))
            ),
            Section(
                title: l.SectionPlural,
                child: CounterRow(label: l.FilesLabel, message: l.Files(count))
            ),
            Section(
                title: l.SectionOrdinal,
                child: CounterRow(label: l.RankLabel, message: l.Rank(count))
            ),
            Section(
                title: l.SectionSelect,
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new SegmentedControl(
                            segments: [l.GenderMale, l.GenderFemale, l.GenderOther],
                            selected: _gender,
                            onChanged: i =>
                            {
                                _gender = i;
                                MarkNeedsBuild();
                            }
                        ),
                        new SizedBox(height: 12),
                        new Text(data: l.Invite(Genders[_gender]), style: new TextStyle(15)),
                    ]
                )
            ),
            Section(
                title: l.SectionFormatting,
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        FormatRow(label: l.FmtNumber, value: context.FormatNumber(1234567.89)),
                        FormatRow(
                            label: l.FmtCurrency,
                            value:
                            $"{context.FormatCurrency(value: 1234.56m, currencyCode: "USD")}   ·   " +
                            $"{context.FormatCurrency(value: 1234.56m, currencyCode: "EUR")}   ·   " +
                            $"{context.FormatCurrency(value: 1234.56m, currencyCode: "JPY")}"
                        ),
                        FormatRow(
                            label: l.FmtPercent,
                            value: context.FormatPercent(value: 0.734, decimals: 1)
                        ),
                        FormatRow(
                            label: l.FmtDate,
                            value: context.FormatDate(
                                value: new DateTime(year: 2026, month: 7, day: 5),
                                style: DateStyle.Full
                            )
                        ),
                        FormatRow(
                            label: l.FmtTime,
                            value: context.FormatTime(
                                new DateTime(
                                    year: 2026,
                                    month: 7,
                                    day: 5,
                                    hour: 16,
                                    minute: 45,
                                    second: 0
                                )
                            )
                        ),
                    ]
                )
            ),
            Section(
                title: l.SectionDirection,
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new Text(
                            data: l.DirectionNote,
                            style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
                        ),
                        new SizedBox(height: 12),
                        DirectionStrip(
                            context: context,
                            l: l,
                            label: l.StripAmbient,
                            force: null
                        ),
                        new SizedBox(height: 8),
                        // An explicit Directionality overrides the ambient direction for a subtree —
                        // the same mechanism the LocalizationsScope installs app-wide from the locale.
                        DirectionStrip(
                            context: context,
                            l: l,
                            label: l.StripRtl,
                            force: TextDirection.Rtl
                        ),
                    ]
                )
            )
        );
    }

    private Widget CounterRow(string label, string message)
    {
        var stepper = new Stepper(
            value: _count,
            step: 1,
            min: 0,
            max: 111,
            onChanged: v =>
            {
                _count = v;
                MarkNeedsBuild();
            }
        );
        var counter = new Text(
            data: $"{label}: {(int)_count}",
            style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
        );
        var result = new Text(
            data: message,
            style: new TextStyle(fontSize: 15, fontWeight: FontWeight.Medium)
        );

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
        var labelText = new Text(
            data: label,
            style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
        );
        var valueText = new Text(data: value, style: new TextStyle(14));

        // The 120px label column costs 40% of a phone card's width, wrapping the long values (a
        // full date, three currencies) over several lines — stack them there instead.
        return new Padding(
            padding: EdgeInsets.Only(bottom: 6),
            child: new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                ? new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [labelText, valueText]
                )
                : new Row([new SizedBox(width: 120, child: labelText), valueText])
            )
        );
    }

    private static Widget DirectionStrip(BuildContext context, GalleryL10n l, string label,
        TextDirection? force)
    {
        var onSurface = ThemeProvider.Of(context).OnSurface;
        var chips = new List<Widget>();
        for (int i = 1; i <= 4; i++)
            chips.Add(StepChip(label: l.ChipStep(i), accent: i == 1, onSurface: onSurface));

        // Wrap mirrors under Directionality exactly like the Row did, but reflows the chips
        // instead of painting them past the card once the translated labels grow.
        Widget strip = new Wrap(children: chips, spacing: 8, runSpacing: 8);
        if (force is { } dir) strip = new Directionality(direction: dir, child: strip);

        var labelText = new Text(
            data: label,
            style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
        );

        // Label column (140) plus four chips (272) needs more width than a phone card has, so the
        // label takes its own line there.
        return new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
            ? new Column(
                crossAxisAlignment: CrossAxisAlignment.Start,
                children: [labelText, new SizedBox(height: 6), strip]
            )
            : new Row([new SizedBox(width: 140, child: labelText), strip])
        );
    }

    private static Widget StepChip(string label, bool accent, Color onSurface)
    {
        return new DecoratedBox {
            Fill = accent
                ? Color.Blue.WithAlpha(0.85f)
                : new Color(
                    r: 0.5f,
                    g: 0.5f,
                    b: 0.5f,
                    a: 0.18f
                ),
            Radius = Radii.Capsule,
            BorderWidth = 0f,
            Child = new Padding(
                padding: EdgeInsets.Symmetric(horizontal: Spacing.Md, vertical: Spacing.Xs),
                child: new Label(text: label, fontSize: 12, color: accent ? Color.White : onSurface)
            ),
        };
    }
}
