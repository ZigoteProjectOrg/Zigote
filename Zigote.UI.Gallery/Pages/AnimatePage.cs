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
internal sealed class AnimatePage : StatefulWidget
{
    protected override WidgetState CreateState()
    {
        return new AnimatePageState();
    }
}

internal sealed class AnimatePageState : WidgetState<AnimatePage>
{
    public override Widget Build(BuildContext context)
    {
        // Staggered entrance of a small list.
        var list = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);
        for (var i = 0; i < 5; i++)
            list.Children.Add(
                new Padding(
                    EdgeInsets.Only(bottom: 6),
                    new Card(
                            new Padding(
                                EdgeInsets.All(10),
                                new Text($"Row {i + 1}")
                            )
                        ).Animate()
                        .FadeIn(delay: (i * 130).ms, duration: 450.ms)
                        .Move(begin: new Offset(0, 16))
                )
            );

        return Sections(
            Section(
                "Replay",
                new Row(
                    mainAxisSize: MainAxisSize.Min,
                    children: [
                        new FilledButton(
                            new Text("Replay animations"),
                            () => SetStateRebuild(() => { })
                        ),
                        new SizedBox(12),
                        new Text(
                            "Effects play once on mount — rebuild to replay.",
                            new TextStyle(12, color: Colors.Gray)
                        ),
                    ]
                )
            ),
            Section(
                "Fluent API — the flutter_animate way",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new Text(
                            "Text(\"Hello\").Animate().Fade(duration: 500.ms).Scale(delay: 500.ms)",
                            new TextStyle(12, fontStyle: FontStyle.Italic, color: Colors.Gray)
                        ),
                        new SizedBox(height: 12),
                        new Text("Hello, Zigote!", new TextStyle(28, fontWeight: FontWeight.Bold))
                            .Animate()
                            .Fade(500.ms)
                            .Scale(delay: 500.ms),
                    ]
                )
            ),
            Section(
                "Effects",
                new Wrap(
                    spacing: 10,
                    runSpacing: 10,
                    children: [
                        new Chip("Fade").Animate().FadeIn(550.ms),
                        new Chip("Slide").Animate().Slide(550.ms),
                        new Chip("Scale").Animate().Scale(550.ms, curve: Curves.EaseOutBack),
                        new Chip("Move").Animate().Move(550.ms, begin: new Offset(-30, 0)),
                        new Chip("Fade + Slide").Animate()
                            .FadeIn(550.ms)
                            .Slide(begin: new Offset(0.3f, 0)),
                        new Chip("Shake").Animate().Shake(delay: 550.ms, duration: 600.ms),
                    ]
                )
            ),
            Section(
                "Sequenced with .Then()",
                new Text("Fade, then rise", new TextStyle(17, fontWeight: FontWeight.Medium))
                    .Animate()
                    .FadeIn(550.ms)
                    .Then(150.ms)
                    .Move(begin: new Offset(0, 14), curve: Curves.Spring)
            ),
            Section("Staggered list", list),
            Section(
                "State transitions",
                new Text(
                    "Checkbox, Radio, Switch, Segmented control, Chips and Tabs animate their own " +
                    "state changes — see the Selection page. Dialogs, dropdowns and context menus " +
                    "animate on open — see the Overlays page.",
                    new TextStyle(13, color: Colors.Gray)
                )
            )
        );
    }
}