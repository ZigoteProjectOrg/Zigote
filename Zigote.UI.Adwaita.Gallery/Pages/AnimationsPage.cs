using Zigote.Core.Animation;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Animations — a square that slides left to right as the animation value goes 0 → 1, with the
///     demo's transport controls and the Timed / Spring parameter groups.
/// </summary>
public sealed class AnimationsPage : ComposedWidget
{
    /// <summary>The AdwEasing values, in enum order; the labels come from the demo verbatim.</summary>
    private static readonly string[] EasingNames = [
        "Linear",
        "Ease-in (Quadratic)",
        "Ease-out (Quadratic)",
        "Ease-in-out (Quadratic)",
        "Ease-in (Cubic)",
        "Ease-out (Cubic)",
        "Ease-in-out (Cubic)",
        "Ease-in (Quartic)",
        "Ease-out (Quartic)",
        "Ease-in-out (Quartic)",
        "Ease-in (Quintic)",
        "Ease-out (Quintic)",
        "Ease-in-out (Quintic)",
        "Ease-in (Sine)",
        "Ease-out (Sine)",
        "Ease-in-out (Sine)",
        "Ease-in (Exponential)",
        "Ease-out (Exponential)",
        "Ease-in-out (Exponential)",
        "Ease-in (Circular)",
        "Ease-out (Circular)",
        "Ease-in-out (Circular)",
        "Ease-in (Elastic)",
        "Ease-out (Elastic)",
        "Ease-in-out (Elastic)",
        "Ease-in (Back)",
        "Ease-out (Back)",
        "Ease-in-out (Back)",
        "Ease-in (Bounce)",
        "Ease-out (Bounce)",
        "Ease-in-out (Bounce)",
        "Ease",
        "Ease-in",
        "Ease-out",
        "Ease-in-out",
    ];

    private readonly AdwButton _playPause;
    private readonly AdwButton _reset;

    private readonly Container _sample = new() {
        Width = 32f,
        Height = 32f,
        CornerRadius = Radii.Md,
    };

    private readonly AdwButton _skip;
    private readonly Align _slot;
    private readonly AdwViewStack _stack;

    private int _cycles;
    private Func<float, float> _easing = Curves.EaseInOut;
    private bool _flip;
    private Phase _phase = Phase.Idle;
    private float _t;
    private Ticker? _ticker;

    // Timed parameters.
    private double _duration = 500;
    private double _repeatCount = 1;
    private bool _reverse;
    private bool _alternate;

    // Spring parameters that actually drive the approximation.
    private double _mass = 1;
    private double _stiffness = 100;
    private bool _clamp;

    public AnimationsPage()
    {
        _slot = new Align(new Alignment(0f, 0.5f), _sample);

        _reset = new AdwButton(onPressed: Reset) {
            IconName = MaterialIcons.SkipPrevious,
            Style = AdwButtonStyle.Flat,
            Circular = true,
            Enabled = false,
        };
        // ponytail: 34px circular button — the demo's is 48×48, which AdwButton fixes for .circular.
        _playPause = new AdwButton(onPressed: PlayPause) {
            IconName = MaterialIcons.PlayArrow,
            Style = AdwButtonStyle.Suggested,
            Circular = true,
        };
        _skip = new AdwButton(onPressed: Skip) {
            IconName = MaterialIcons.SkipNext,
            Style = AdwButtonStyle.Flat,
            Circular = true,
        };

        _stack = new AdwViewStack(
            new AdwViewStackPage("Timed", "Timed", TimedGroup()),
            new AdwViewStackPage("Spring", "Spring", SpringGroup())
        ) {
            OnVisibleChanged = _ => Reset(),
        };
    }

    private enum Phase
    {
        Idle,
        Playing,
        Paused,
        Finished,
    }

    private bool IsSpring => _stack.VisibleName == "Spring";

    /// <summary>
    ///     ponytail: the spring is approximated by <see cref="Curves.Spring" /> over a period derived
    ///     from mass and stiffness — initial velocity, damping and epsilon are shown but not applied.
    /// </summary>
    private float DurationSeconds => IsSpring
        ? MathF.Tau * MathF.Sqrt((float)(_mass / Math.Max(_stiffness, 1)))
        : (float)(_duration / 1000);

    protected override Widget Build(BuildContext context)
    {
        _sample.Background = ThemeProvider.Of(context).Accent;

        return new GalleryPage(
            "Animations",
            "A square driven from 0 to 1 by a timed animation or a spring, with the parameters that shape it.",
            MaterialIcons.Animation
        ) {
            Children = {
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    mainAxisSize: MainAxisSize.Min
                ) {
                    Children = {
                        new AdwClamp(new SizedBox(height: 32f, child: _slot), 400f),
                        new SizedBox(height: 30f),
                        new Center {
                            Child = new Row(spacing: Spacing.Xl, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    _reset,
                                    _playPause,
                                    _skip,
                                },
                            },
                        },
                        new SizedBox(height: 30f),
                        new Center {
                            Child = new SizedBox(
                                250f,
                                child: new AdwInlineViewSwitcher(_stack)
                            ),
                        },
                        new SizedBox(height: 32f),
                        new AdwClamp(_stack, 400f),
                    },
                },
            },
        };
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _ticker ??= new Ticker(Tick);
        if (_phase == Phase.Playing) _ticker.Start();
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose();
        _ticker = null;
    }

    private Widget TimedGroup()
    {
        return new AdwPreferencesGroup {
            Rows = {
                new AdwComboRow(
                    "Easing",
                    EasingNames,
                    6,
                    i => _easing = CurveFor(EasingNames[i])
                ),
                new AdwSpinRow(
                    "Duration",
                    value: 500,
                    min: 100,
                    max: 4000,
                    step: 50,
                    onChanged: v => _duration = v
                ),
                new AdwSpinRow(
                    "Repeat Count",
                    value: 1,
                    min: 0,
                    max: 10,
                    onChanged: v => _repeatCount = v
                ),
                new AdwSwitchRow("Reverse", onChanged: v => _reverse = v),
                new AdwSwitchRow("Alternate", onChanged: v => _alternate = v),
            },
        };
    }

    private Widget SpringGroup()
    {
        return new AdwPreferencesGroup {
            Rows = {
                new AdwSpinRow(
                    "Initial Velocity",
                    "Not implemented",
                    min: -1000,
                    max: 1000
                ),
                new AdwSpinRow(
                    "Damping",
                    "Not implemented",
                    10,
                    0,
                    1000
                ),
                new AdwSpinRow(
                    "Mass",
                    value: 1,
                    min: 0,
                    max: 100,
                    onChanged: v => _mass = v
                ),
                new AdwSpinRow(
                    "Stiffness",
                    value: 100,
                    min: 0,
                    max: 1000,
                    onChanged: v => _stiffness = v
                ),
                new AdwSpinRow(
                    "Epsilon",
                    "Not implemented",
                    0.001,
                    0.0001,
                    0.01,
                    0.001
                ),
                new AdwSwitchRow("Clamp", onChanged: v => _clamp = v),
            },
        };
    }

    /// <summary>
    ///     ponytail: the toolkit ships 8 curves, so the 35 AdwEasing names map onto the nearest one
    ///     by family — a full version would need the easing formulas themselves.
    /// </summary>
    private static Func<float, float> CurveFor(string name)
    {
        if (name.Contains("Bounce")) return Curves.BounceOut;
        if (name.Contains("Elastic")) return Curves.ElasticOut;
        if (name.Contains("Back")) return Curves.EaseOutBack;
        if (name == "Linear") return Curves.Linear;
        if (name.StartsWith("Ease-in-out", StringComparison.Ordinal)) return Curves.EaseInOut;
        if (name.StartsWith("Ease-in", StringComparison.Ordinal)) return Curves.EaseIn;
        if (name.StartsWith("Ease-out", StringComparison.Ordinal)) return Curves.EaseOut;
        return Curves.EaseInOut;
    }

    private void Tick(float dt)
    {
        _t += dt / MathF.Max(0.001f, DurationSeconds);
        while (_t >= 1f)
        {
            _cycles++;
            var repeat = (int)_repeatCount;
            // Repeat count 0 loops forever, as AdwTimedAnimation does; the spring never repeats.
            if (IsSpring || (repeat != 0 && _cycles >= repeat))
            {
                _t = 1f;
                _phase = Phase.Finished;
                _ticker?.Stop();
                break;
            }

            _t -= 1f;
            if (_alternate) _flip = !_flip;
        }

        Apply();
    }

    private void PlayPause()
    {
        switch (_phase)
        {
            case Phase.Idle:
            case Phase.Finished:
                _t = 0f;
                _cycles = 0;
                _flip = false;
                _phase = Phase.Playing;
                _ticker?.Start();
                break;
            case Phase.Paused:
                _phase = Phase.Playing;
                _ticker?.Start();
                break;
            default:
                _phase = Phase.Paused;
                _ticker?.Stop();
                break;
        }

        Apply();
    }

    private void Reset()
    {
        _t = 0f;
        _cycles = 0;
        _flip = false;
        _phase = Phase.Idle;
        _ticker?.Stop();
        Apply();
    }

    private void Skip()
    {
        _t = 1f;
        _phase = Phase.Finished;
        _ticker?.Stop();
        Apply();
    }

    private Func<float, float> CurrentCurve()
    {
        if (!IsSpring) return _easing;
        // Clamping a spring means no overshoot, so a plain ease-out stands in for it.
        if (_clamp) return Curves.EaseOut;
        return Curves.Spring;
    }

    private void Apply()
    {
        // Elastic, back and spring curves overshoot, so the sample is clamped to the track.
        var value = Math.Clamp(CurrentCurve()(Math.Clamp(_t, 0f, 1f)), 0f, 1f);
        if (!IsSpring && _reverse ^ _flip) value = 1f - value;

        _slot.Alignment = new Alignment(value, 0.5f);
        _slot.MarkNeedsLayout();

        _playPause.IconName = _phase == Phase.Playing
            ? MaterialIcons.Pause
            : MaterialIcons.PlayArrow;
        _reset.Enabled = _phase != Phase.Idle;
        _skip.Enabled = _phase != Phase.Finished;
    }
}