using Zigote.Core.Animation;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Sliders &amp; Progress — the range controls and the two bars, with a real (ticking) transfer
///     so the progress bar shows what it looks like in motion rather than parked at 40%.
/// </summary>
public sealed class SlidersPage : StatelessWidget, ITickerProvider
{
    private readonly Signal<float> _volume = new(0.65f);
    private readonly Signal<double> _copies = new(2);
    private readonly Signal<float> _progress = new(0f);
    private readonly Signal<bool> _running = new(false);

    private readonly AdwProgressBar _bar = new();
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

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        CreateTicker(Advance);
        if (_running.Peek()) _ticker!.Start();
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose();
        _ticker = null;
    }

    protected override Widget Build(BuildContext context)
    {
        _bar.Value = _progress.Value;

        return new GalleryPage(
            "Sliders & Progress",
            "Pick a value in a range, step one exactly, or show how far along something is.",
            MaterialIcons.Tune
        ) {
            Children = {
                Demo.Titled(
                    "Slider",
                    "Drag it, or focus it and use the arrow keys.",
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new AdwSlider(
                                    _volume.Peek(),
                                    0f,
                                    1f,
                                    v => _volume.Value = v
                                ) {
                                    SemanticsLabel = "Volume",
                                },
                                new Watch(() => new Align(
                                        Alignment.Center,
                                        Demo.Value($"volume = {_volume.Value:P0}")
                                    ) { HeightFactor = 1f }
                                ),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    "Faders",
                    "Vertical = true: the same control bottom-to-top, for a mixer or an equalizer.",
                    Demo.Stage(
                        new Row(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new SizedBox(40f, 160f, new AdwSlider(0.8f) { Vertical = true }),
                                new SizedBox(40f, 160f, new AdwSlider(0.5f) { Vertical = true }),
                                new SizedBox(40f, 160f, new AdwSlider(0.2f) { Vertical = true }),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    "Spin Button",
                    "For a value that has to be exact, typed or stepped.",
                    Demo.Stage(
                        Demo.Bar(
                            new AdwSpinButton(
                                _copies.Peek(),
                                1,
                                99,
                                1,
                                v => _copies.Value = v
                            ),
                            new Watch(() => Demo.Value($"copies = {_copies.Value:0}"))
                        )
                    )
                ),
                Demo.Titled(
                    "Progress",
                    "Determinate while there is a total to divide by; indeterminate when there is not.",
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                _bar,
                                new Watch(() => new Align(
                                        Alignment.Center,
                                        Demo.Value(
                                            _running.Value
                                                ? $"copying… {_progress.Value:P0}"
                                                : $"idle at {_progress.Value:P0}"
                                        )
                                    ) { HeightFactor = 1f }
                                ),
                                Demo.Bar(
                                    new Watch(() => new AdwButton(
                                            _running.Value ? "Pause" : "Start",
                                            () => _running.Value = !_running.Value
                                        ) { Style = AdwButtonStyle.Suggested }
                                    ),
                                    new AdwButton(
                                        "Reset",
                                        () =>
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
                    "Level Bar",
                    "A reading rather than a task — battery, signal, disk.",
                    Demo.Stage(
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
                    "In Rows",
                    null,
                    new AdwSpinRow(
                        "Copies",
                        "How many to print",
                        2,
                        1,
                        99
                    ),
                    new AdwActionRow("Volume") {
                        Suffixes = { new SizedBox(180f, child: new AdwSlider(0.65f)) },
                    }
                ),
            },
        };
    }

    private void Advance(float dt)
    {
        if (!_running.Value) return;
        var next = _progress.Value + dt * 0.25f;
        if (next >= 1f)
        {
            next = 1f;
            _running.Value = false;
        }

        _progress.Value = next;
        _bar.Value = next;
    }
}