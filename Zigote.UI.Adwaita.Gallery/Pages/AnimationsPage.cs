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
    private bool _alternate;
    private bool _clamp;

    private int _cycles;

    // Timed parameters.
    private double _duration = 500;
    private Func<float, float> _easing = Curves.EaseInOut;
    private bool _flip;

    // Spring parameters that actually drive the approximation.
    private double _mass = 1;
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

    /// <summary>
    ///     ponytail: the spring is approximated by <see cref="Curves.Spring" /> over a period derived
    ///     from mass and stiffness — initial velocity, damping and epsilon are shown but not applied.
    /// </summary>
    private float DurationSeconds => IsSpring
        ? MathF.Tau * MathF.Sqrt((float)(_mass / Math.Max(val1: _stiffness, val2: 1)))
        : (float)(_duration / 1000);

    protected override Widget Build(BuildContext context)
    {
        _sample.Background = ThemeProvider.Of(context).Accent;

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
                    subtitle: "Not implemented",
                    min: -1000,
                    max: 1000
                ),
                new AdwSpinRow(
                    title: "Damping",
                    subtitle: "Not implemented",
                    value: 10,
                    min: 0,
                    max: 1000
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
                    subtitle: "Not implemented",
                    value: 0.001,
                    min: 0.0001,
                    max: 0.01,
                    step: 0.001
                ),
                new AdwSwitchRow(title: "Clamp", onChanged: v => _clamp = v),
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
        if (name.StartsWith(value: "Ease-in-out", comparisonType: StringComparison.Ordinal))
            return Curves.EaseInOut;
        if (name.StartsWith(value: "Ease-in", comparisonType: StringComparison.Ordinal))
            return Curves.EaseIn;
        if (name.StartsWith(value: "Ease-out", comparisonType: StringComparison.Ordinal))
            return Curves.EaseOut;
        return Curves.EaseInOut;
    }

    private void Tick(float dt)
    {
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
        float value = Math.Clamp(
            value: CurrentCurve()(Math.Clamp(value: _t, min: 0f, max: 1f)),
            min: 0f,
            max: 1f
        );
        if (!IsSpring && _reverse ^ _flip) value = 1f - value;

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
