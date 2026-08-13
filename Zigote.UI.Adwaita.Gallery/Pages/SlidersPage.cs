using Zigote.Core.Animation;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Sliders &amp; Progress — the range controls and the two bars, with a real (ticking) transfer
///     so the progress bar shows what it looks like in motion rather than parked at 40%.
/// </summary>
public sealed class SlidersPage : ComposedWidget
{
    private readonly AdwProgressBar _bar = new();
    private readonly Signal<double> _copies = new(2);
    private readonly Signal<float> _progress = new(0f);
    private readonly Signal<bool> _running = new(false);
    private readonly Signal<float> _volume = new(0.65f);
    private Ticker? _ticker;

    public SlidersPage()
    {
        // The ticker runs only while the transfer does — a ticker left running keeps the whole
        // frame loop awake, which is not what an idle page should cost.
        _running.Changed += on =>
        {
            if (on) _ticker?.Start();
            else _ticker?.Stop();
        };
    }

    protected override void OnMount()
    {
        // Owned by the mount period; the toggle above starts/stops it.
        _ticker = CreateTicker(Advance);
        if (_running.Peek()) _ticker.Start();
    }

    protected override void OnUnmount() => _ticker = null; // the Ticker itself is disposed for us

    protected override Widget Build(BuildContext context)
    {
        _bar.Value = _progress.Value;

        return new GalleryPage(
            title: "Sliders & Progress",
            description:
            "Pick a value in a range, step one exactly, or show how far along something is.",
            iconName: MaterialIcons.Tune
        ) {
            Children = {
                Demo.Titled(
                    title: "Slider",
                    description: "Drag it, or focus it and use the arrow keys.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new AdwSlider(
                                    value: _volume.Peek(),
                                    min: 0f,
                                    max: 1f,
                                    onChanged: v => _volume.Value = v
                                ) {
                                    SemanticsLabel = "Volume",
                                },
                                new Watch(() => new Align(
                                        alignment: Alignment.Center,
                                        child: Demo.Value($"volume = {_volume.Value:P0}")
                                    ) { HeightFactor = 1f }
                                ),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "Faders",
                    description:
                    "Vertical = true: the same control bottom-to-top, for a mixer or an equalizer.",
                    child: Demo.Stage(
                        new Row(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new SizedBox(
                                    width: 40f,
                                    height: 160f,
                                    child: new AdwSlider(0.8f) { Vertical = true }
                                ),
                                new SizedBox(
                                    width: 40f,
                                    height: 160f,
                                    child: new AdwSlider(0.5f) { Vertical = true }
                                ),
                                new SizedBox(
                                    width: 40f,
                                    height: 160f,
                                    child: new AdwSlider(0.2f) { Vertical = true }
                                ),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "Spin Button",
                    description: "For a value that has to be exact, typed or stepped.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new AdwSpinButton(
                                value: _copies.Peek(),
                                min: 1,
                                max: 99,
                                step: 1,
                                onChanged: v => _copies.Value = v
                            ),
                            new Watch(() => Demo.Value($"copies = {_copies.Value:0}"))
                        )
                    )
                ),
                Demo.Titled(
                    title: "Progress",
                    description:
                    "Determinate while there is a total to divide by; indeterminate when there is not.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                _bar,
                                new Watch(() => new Align(
                                        alignment: Alignment.Center,
                                        child: Demo.Value(
                                            _running.Value
                                                ? $"copying… {_progress.Value:P0}"
                                                : $"idle at {_progress.Value:P0}"
                                        )
                                    ) { HeightFactor = 1f }
                                ),
                                Demo.Bar(
                                    new Watch(() => new AdwButton(
                                            label: _running.Value ? "Pause" : "Start",
                                            onPressed: () => _running.Value = !_running.Value
                                        ) { Style = AdwButtonStyle.Suggested }
                                    ),
                                    new AdwButton(
                                        label: "Reset",
                                        onPressed: () =>
                                        {
                                            _running.Value = false;
                                            _progress.Value = 0f;
                                        }
                                    )
                                ),
                                new AdwProgressBar { Indeterminate = true },
                                Demo.Caption("Indeterminate: no total, so it just keeps moving."),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "Level Bar",
                    description: "A reading rather than a task — battery, signal, disk.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Md,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new AdwLevelBar(0.85f),
                                new AdwLevelBar(0.45f),
                                new AdwLevelBar(0.15f),
                            },
                        }
                    )
                ),
                Demo.Group(
                    title: "In Rows",
                    description: null,
                    new AdwSpinRow(
                        title: "Copies",
                        subtitle: "How many to print",
                        value: 2,
                        min: 1,
                        max: 99
                    ),
                    new AdwActionRow("Volume") {
                        Suffixes = { new SizedBox(width: 180f, child: new AdwSlider(0.65f)) },
                    }
                ),
            },
        };
    }

    private void Advance(float dt)
    {
        if (!_running.Value) return;
        float next = _progress.Value + (dt * 0.25f);
        if (next >= 1f)
        {
            next = 1f;
            _running.Value = false;
        }

        _progress.Value = next;
        _bar.Value = next;
    }
}
