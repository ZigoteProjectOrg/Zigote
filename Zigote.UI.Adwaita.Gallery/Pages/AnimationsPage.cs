using Zigote.Core.Animation;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Animations — a square that slides left to right as the animation value goes 0 → 1, with the
///     demo's transport controls and the Timed / Spring parameter groups. The Timed group runs the
///     real easing behind each of the 35 names; the Spring group runs a real
///     <see cref="Motion" />, so mass, stiffness, damping, initial velocity and epsilon all do what
///     they say.
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

    /// <summary>Live read-out, so the spring parameters can be seen doing something.</summary>
    private readonly Label _readout = new(text: "0.000", fontSize: 12f);
    private bool _alternate;
    private bool _clamp;

    private int _cycles;

    // Timed parameters.
    private double _duration = 500;
    private Func<float, float> _easing = Curves.EaseInOut;
    private bool _flip;

    // Spring parameters — every one of them drives the real Motion below.
    private double _damping = 10;
    private double _epsilon = 0.001;
    private double _initialVelocity;
    private double _mass = 1;
    private Motion _spring;
    private Phase _phase = Phase.Idle;
    private double _repeatCount = 1;
    private bool _reverse;
    private double _stiffness = 100;
    private float _t;
    private Ticker? _ticker;

    public AnimationsPage()
    {
        _slot = new Align(alignment: new Alignment(X: 0f, Y: 0.5f), child: _sample);

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
            new AdwViewStackPage(name: "Timed", title: "Timed", child: TimedGroup()),
            new AdwViewStackPage(name: "Spring", title: "Spring", child: SpringGroup())
        ) {
            OnVisibleChanged = _ => Reset(),
        };
    }

    private bool IsSpring => _stack.VisibleName == "Spring";

    private float DurationSeconds => (float)(_duration / 1000);

    /// <summary>
    ///     The spin rows in the Spring group, in the physicist's units (mass, stiffness, damping
    ///     coefficient), translated into the response/damping-ratio pair a <see cref="MotionSpec" />
    ///     takes: ω = √(k/m), response = 2π/ω, ζ = c / 2√(k·m).
    /// </summary>
    private MotionSpec SpringSpec()
    {
        float m = MathF.Max(x: (float)_mass, y: 0.001f);
        float k = MathF.Max(x: (float)_stiffness, y: 0.001f);
        float response = MathF.Tau * MathF.Sqrt(m / k);
        float zeta = (float)_damping / (2f * MathF.Sqrt(k * m));
        return MotionSpec.Spring(response: response, damping: zeta) with {
            Epsilon = MathF.Max(x: (float)_epsilon, y: 1e-5f),
        };
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        _sample.Background = theme.Accent;
        _readout.Color = theme.Hint;

        return new GalleryPage(
            title: "Animations",
            description:
            "A square driven from 0 to 1 by a timed animation or a spring, with the parameters that shape it.",
            iconName: MaterialIcons.Animation
        ) {
            Children = {
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    mainAxisSize: MainAxisSize.Min
                ) {
                    Children = {
                        new AdwClamp(
                            child: new SizedBox(height: 32f, child: _slot),
                            maximumSize: 400f
                        ),
                        new SizedBox(height: 8f),
                        new Center { Child = _readout },
                        new SizedBox(height: 22f),
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
                                width: 250f,
                                child: new AdwInlineViewSwitcher(_stack)
                            ),
                        },
                        new SizedBox(height: 32f),
                        new AdwClamp(child: _stack, maximumSize: 400f),
                    },
                },
            },
        };
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner: owner, parent: parent);
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
                    title: "Easing",
                    items: EasingNames,
                    selectedIndex: 6,
                    onSelected: i => _easing = CurveFor(EasingNames[i])
                ),
                new AdwSpinRow(
                    title: "Duration",
                    value: 500,
                    min: 100,
                    max: 4000,
                    step: 50,
                    onChanged: v => _duration = v
                ),
                new AdwSpinRow(
                    title: "Repeat Count",
                    value: 1,
                    min: 0,
                    max: 10,
                    onChanged: v => _repeatCount = v
                ),
                new AdwSwitchRow(title: "Reverse", onChanged: v => _reverse = v),
                new AdwSwitchRow(title: "Alternate", onChanged: v => _alternate = v),
            },
        };
    }

    private Widget SpringGroup()
    {
        return new AdwPreferencesGroup {
            Rows = {
                new AdwSpinRow(
                    title: "Initial Velocity",
                    subtitle: "Value units per second, at release",
                    min: -100,
                    max: 100,
                    onChanged: v => _initialVelocity = v
                ),
                new AdwSpinRow(
                    title: "Damping",
                    subtitle: "Below critical (2√(k·m)) it overshoots",
                    value: 10,
                    min: 0,
                    max: 1000,
                    onChanged: v => _damping = v
                ),
                new AdwSpinRow(
                    title: "Mass",
                    value: 1,
                    min: 0,
                    max: 100,
                    onChanged: v => _mass = v
                ),
                new AdwSpinRow(
                    title: "Stiffness",
                    value: 100,
                    min: 0,
                    max: 1000,
                    onChanged: v => _stiffness = v
                ),
                new AdwSpinRow(
                    title: "Epsilon",
                    subtitle: "How close counts as arrived",
                    value: 0.001,
                    min: 0.0001,
                    max: 0.01,
                    step: 0.001,
                    onChanged: v => _epsilon = v
                ),
                new AdwSwitchRow(title: "Clamp", onChanged: v => _clamp = v),
            },
        };
    }

    /// <summary>
    ///     Every AdwEasing name mapped to the curve that actually bears it — the quadratic family
    ///     under the bare names, and the three CSS keywords as the cubic Béziers they are defined as.
    /// </summary>
    private static Func<float, float> CurveFor(string name)
    {
        return name switch {
            "Linear" => Curves.Linear,
            "Ease-in (Quadratic)" => Curves.EaseIn,
            "Ease-out (Quadratic)" => Curves.EaseOut,
            "Ease-in-out (Quadratic)" => Curves.EaseInOut,
            "Ease-in (Cubic)" => Curves.EaseInCubic,
            "Ease-out (Cubic)" => Curves.EaseOutCubic,
            "Ease-in-out (Cubic)" => Curves.EaseInOutCubic,
            "Ease-in (Quartic)" => Curves.EaseInQuart,
            "Ease-out (Quartic)" => Curves.EaseOutQuart,
            "Ease-in-out (Quartic)" => Curves.EaseInOutQuart,
            "Ease-in (Quintic)" => Curves.EaseInQuint,
            "Ease-out (Quintic)" => Curves.EaseOutQuint,
            "Ease-in-out (Quintic)" => Curves.EaseInOutQuint,
            "Ease-in (Sine)" => Curves.EaseInSine,
            "Ease-out (Sine)" => Curves.EaseOutSine,
            "Ease-in-out (Sine)" => Curves.EaseInOutSine,
            "Ease-in (Exponential)" => Curves.EaseInExpo,
            "Ease-out (Exponential)" => Curves.EaseOutExpo,
            "Ease-in-out (Exponential)" => Curves.EaseInOutExpo,
            "Ease-in (Circular)" => Curves.EaseInCirc,
            "Ease-out (Circular)" => Curves.EaseOutCirc,
            "Ease-in-out (Circular)" => Curves.EaseInOutCirc,
            "Ease-in (Elastic)" => Curves.EaseInElastic,
            "Ease-out (Elastic)" => Curves.ElasticOut,
            "Ease-in-out (Elastic)" => Curves.EaseInOutElastic,
            "Ease-in (Back)" => Curves.EaseInBack,
            "Ease-out (Back)" => Curves.EaseOutBack,
            "Ease-in-out (Back)" => Curves.EaseInOutBack,
            "Ease-in (Bounce)" => Curves.EaseInBounce,
            "Ease-out (Bounce)" => Curves.BounceOut,
            "Ease-in-out (Bounce)" => Curves.EaseInOutBounce,
            "Ease" => Curves.Ease,
            "Ease-in" => CssEaseIn,
            "Ease-out" => CssEaseOut,
            "Ease-in-out" => CssEaseInOut,
            _ => Curves.EaseInOut,
        };
    }

    // The CSS keyword curves, which are Béziers rather than polynomials.
    private static readonly Func<float, float> CssEaseIn = Curves.Bezier(
        x1: 0.42f,
        y1: 0f,
        x2: 1f,
        y2: 1f
    );

    private static readonly Func<float, float> CssEaseOut = Curves.Bezier(
        x1: 0f,
        y1: 0f,
        x2: 0.58f,
        y2: 1f
    );

    private static readonly Func<float, float> CssEaseInOut = Curves.Bezier(
        x1: 0.42f,
        y1: 0f,
        x2: 0.58f,
        y2: 1f
    );

    private void Tick(float dt)
    {
        if (IsSpring)
        {
            // The spring owns its own clock: it stops itself when it has arrived and is no longer
            // moving, which is also what lets the frame loop go back to sleep.
            if (!_spring.Tick(dt))
            {
                _phase = Phase.Finished;
                _ticker?.Stop();
            }

            _t = _spring.Value;
            Apply();
            return;
        }

        _t += dt / MathF.Max(x: 0.001f, y: DurationSeconds);
        while (_t >= 1f)
        {
            _cycles++;
            int repeat = (int)_repeatCount;
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
                if (IsSpring)
                {
                    _spring = new Motion(
                        spec: SpringSpec(),
                        value: 0f,
                        target: 1f,
                        velocity: (float)_initialVelocity
                    );
                }

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
        _spring.SnapTo(0f);
        _phase = Phase.Idle;
        _ticker?.Stop();
        Apply();
    }

    private void Skip()
    {
        _t = 1f;
        _spring.SnapTo(1f);
        _phase = Phase.Finished;
        _ticker?.Stop();
        Apply();
    }

    private void Apply()
    {
        float value;
        if (IsSpring)
        {
            // Clamp means "no overshoot"; unclamped, the sample is allowed a little way past each
            // end of the track so a bouncy spring can actually be seen bouncing.
            value = _clamp
                ? Math.Clamp(value: _t, min: 0f, max: 1f)
                : Math.Clamp(value: _t, min: -0.08f, max: 1.08f);
            _readout.Text =
                $"value {_t:0.000}   ·   velocity {_spring.Velocity:0.00}/s";
        }
        else
        {
            // Elastic and back curves overshoot, so the timed sample is clamped to the track.
            value = Math.Clamp(
                value: _easing(Math.Clamp(value: _t, min: 0f, max: 1f)),
                min: 0f,
                max: 1f
            );
            if (_reverse ^ _flip) value = 1f - value;
            _readout.Text = $"value {value:0.000}";
        }

        _slot.Alignment = new Alignment(X: value, Y: 0.5f);
        _slot.MarkNeedsLayout();

        _playPause.IconName = _phase == Phase.Playing
            ? MaterialIcons.Pause
            : MaterialIcons.PlayArrow;
        _reset.Enabled = _phase != Phase.Idle;
        _skip.Enabled = _phase != Phase.Finished;
    }

    private enum Phase
    {
        Idle,
        Playing,
        Paused,
        Finished,
    }
}
