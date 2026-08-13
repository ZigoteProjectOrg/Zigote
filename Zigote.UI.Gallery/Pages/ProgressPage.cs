using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>Progress indicators, with a slider driving the determinate ones.</summary>
internal sealed class ProgressPage : ComposedWidget
{
    private float _progress = 0.65f;

    // Retained: a rebuild mid-drag would recreate the slider and drop the drag.
    private Slider? _slider;

    protected override Widget Build(BuildContext context)
    {
        _slider ??= new Slider(
            value: _progress,
            min: 0,
            max: 1,
            onChanged: v =>
            {
                _progress = v;
                MarkNeedsBuild();
            }
        );

        return Sections(
            Section(
                title: "Drive the value",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        _slider,
                        new Text($"Progress: {_progress:P0}"),
                    ]
                )
            ),
            Section(
                title: "Linear (determinate + indeterminate)",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new LinearProgressIndicator(_progress),
                        new SizedBox(height: 12),
                        new LinearProgressIndicator(),
                    ]
                )
            ),
            Section(
                title: "Circular & spinner",
                // The three indicators plus their gaps almost exactly fill a phone-width card, so
                // a narrower screen (or a larger text scale) would squeeze the 160-px bar to
                // nothing. Wrap reflows them into runs; wider windows keep the single row.
                child: new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                    ? new Wrap(
                        spacing: 24,
                        runSpacing: 12,
                        children: [
                            new CircularProgressIndicator(),
                            new Spinner(28),
                            new SizedBox(width: 160, child: new ProgressBar(_progress)),
                        ]
                    )
                    : new Row(
                        [
                            new CircularProgressIndicator(),
                            new SizedBox(24),
                            new Spinner(28),
                            new SizedBox(24),
                            new SizedBox(width: 160, child: new ProgressBar(_progress)),
                        ]
                    )
                )
            )
        );
    }
}
