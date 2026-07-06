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
    private double _count = 2;
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
                new Row(
                    mainAxisSize: MainAxisSize.Min,
                    children: [
                        new Chip(l.Locale.ToBcp47()),
                        new SizedBox(8),
                        new Chip(l.LocaleName),
                        new SizedBox(8),
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
        return new Row(
            [
                new Stepper(
                    _count,
                    1,
                    0,
                    111,
                    v => SetStateRebuild(() => _count = v)
                ),
                new SizedBox(12),
                new Text($"{label}: {(int)_count}", new TextStyle(12, color: Colors.Grey[500])),
                new SizedBox(24),
                new Text(message, new TextStyle(15, fontWeight: FontWeight.Medium)),
            ]
        );
    }

    private static Widget FormatRow(string label, string value)
    {
        return new Padding(
            EdgeInsets.Only(bottom: 6),
            new Row(
                [
                    new SizedBox(
                        120,
                        child: new Text(label, new TextStyle(12, color: Colors.Grey[500]))
                    ),
                    new Text(value, new TextStyle(14)),
                ]
            )
        );
    }

    private static Widget DirectionStrip(BuildContext context, GalleryL10n l, string label,
        TextDirection? force)
    {
        var onSurface = ThemeProvider.Of(context).OnSurface;
        var chips = new List<Widget>();
        for (var i = 1; i <= 4; i++)
        {
            if (i > 1) chips.Add(new SizedBox(8));
            chips.Add(StepChip(l.ChipStep(i), i == 1, onSurface));
        }

        Widget row = new Row(mainAxisSize: MainAxisSize.Min, children: chips);
        if (force is { } dir) row = new Directionality(dir, row);

        return new Row(
            [
                new SizedBox(
                    140,
                    child: new Text(label, new TextStyle(12, color: Colors.Grey[500]))
                ),
                row,
            ]
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