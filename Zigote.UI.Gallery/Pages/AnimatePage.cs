using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     The zigote_animate fluent API. Effects play once on mount, so "Replay" simply rebuilds the
///     page (fresh widget instances → fresh entrance animations).
/// </summary>
internal sealed class AnimatePage : ComposedWidget
{
    private const string FluentSample =
        "Text(\"Hello\").Animate().Fade(duration: 500.ms).Scale(delay: 500.ms)";

    // One call per line: as a single 66-character run the sample is ~400 px wide and word-wraps
    // mid-token inside a phone-width card, which is exactly what a code sample must not do.
    private const string FluentSampleStacked =
        "Text(\"Hello\").Animate()\n    .Fade(duration: 500.ms)\n    .Scale(delay: 500.ms)";

    protected override Widget Build(BuildContext context)
    {
        // Staggered entrance of a small list.
        var list = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);
        for (int i = 0; i < 5; i++)
        {
            list.Children.Add(
                new Padding(
                    padding: EdgeInsets.Only(bottom: 6),
                    child: new Card(
                            new Padding(
                                padding: EdgeInsets.All(10),
                                child: new Text($"Row {i + 1}")
                            )
                        ).Animate()
                        .FadeIn(delay: (i * 130).ms, duration: 450.ms)
                        .Move(begin: new Offset(x: 0, y: 16))
                )
            );
        }

        var replay = new FilledButton(
            child: new Text("Replay animations"),
            onPressed: () => MarkNeedsBuild()
        );
        var replayHint = new Text(
            data: "Effects play once on mount — rebuild to replay.",
            style: new TextStyle(fontSize: 12, color: Colors.Gray)
        );

        return Sections(
            Section(
                title: "Replay",
                // Beside the button the caption gets ~140 px and wraps to three lines at phone
                // width — stack them there, keep the single row everywhere wider.
                child: new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                    ? new Column(
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        children: [replay, new SizedBox(height: 8), replayHint]
                    )
                    : new Row(
                        mainAxisSize: MainAxisSize.Min,
                        children: [replay, new SizedBox(12), replayHint]
                    )
                )
            ),
            Section(
                title: "Fluent API — the flutter_animate way",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        // Only the sample re-builds with the size class; the animated text below
                        // stays put so a resize doesn't replay its entrance.
                        new AdaptiveBuilder((_, size) => new Text(
                                data: size == WindowSizeClass.Compact
                                    ? FluentSampleStacked
                                    : FluentSample,
                                style: new TextStyle(
                                    fontSize: 12,
                                    fontStyle: FontStyle.Italic,
                                    color: Colors.Gray
                                )
                            )
                        ),
                        new SizedBox(height: 12),
                        new Text(
                                data: "Hello, Zigote!",
                                style: new TextStyle(fontSize: 28, fontWeight: FontWeight.Bold)
                            )
                            .Animate()
                            .Fade(500.ms)
                            .Scale(delay: 500.ms),
                    ]
                )
            ),
            Section(
                title: "Effects",
                child: new Wrap(
                    spacing: 10,
                    runSpacing: 10,
                    children: [
                        new Chip("Fade").Animate().FadeIn(550.ms),
                        new Chip("Slide").Animate().Slide(550.ms),
                        new Chip("Scale").Animate().Scale(
                            duration: 550.ms,
                            curve: Curves.EaseOutBack
                        ),
                        new Chip("Move").Animate().Move(
                            duration: 550.ms,
                            begin: new Offset(x: -30, y: 0)
                        ),
                        new Chip("Fade + Slide").Animate()
                            .FadeIn(550.ms)
                            .Slide(begin: new Offset(x: 0.3f, y: 0)),
                        new Chip("Shake").Animate().Shake(delay: 550.ms, duration: 600.ms),
                    ]
                )
            ),
            Section(
                title: "Sequenced with .Then()",
                child: new Text(
                        data: "Fade, then rise",
                        style: new TextStyle(fontSize: 17, fontWeight: FontWeight.Medium)
                    )
                    .Animate()
                    .FadeIn(550.ms)
                    .Then(150.ms)
                    .Move(begin: new Offset(x: 0, y: 14), curve: Curves.Spring)
            ),
            Section(title: "Staggered list", child: list),
            Section(
                title: "State transitions",
                child: new Text(
                    data:
                    "Checkbox, Radio, Switch, Segmented control, Chips and Tabs animate their own " +
                    "state changes — see the Selection page. Dialogs, dropdowns and context menus " +
                    "animate on open — see the Overlays page.",
                    style: new TextStyle(fontSize: 13, color: Colors.Gray)
                )
            )
        );
    }
}
