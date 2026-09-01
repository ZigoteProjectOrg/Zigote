using Zigote.Core.Animation;
using Zigote.UI.Widgets.Transitions;
using AlignT = Zigote.UI.Widgets.Layout.Alignment;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Motion — the composable animation presets, the staggered list they build, and the one
///     difference that is hard to see in a still image: a spring keeps the speed it already had
///     when its target moves, and a duration does not.
/// </summary>
public sealed class MotionPage : ComposedWidget
{
    /// <summary>Every box that takes its colour from the accent, re-tinted on each build.</summary>
    private readonly List<Container> _boxes = [];

    private readonly AnimatedAlign _durationDot;
    private readonly List<Animate> _entrances = [];
    private readonly List<Animate> _exits = [];
    private readonly List<Animate> _attention = [];
    private readonly List<Animate> _rows = [];
    private readonly AnimatedAlign _springDot;
    private bool _atEnd;
    private Widget? _content;

    public MotionPage()
    {
        _durationDot = new AnimatedAlign(
            alignment: new AlignT(X: 0f, Y: 0.5f),
            child: Dot(),
            duration: 0.5f
        );
        _springDot = new AnimatedAlign(
            alignment: new AlignT(X: 0f, Y: 0.5f),
            child: Dot()
        ) {
            Spec = MotionSpec.Snappy,
            AllowOvershoot = true,
        };
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        // The specimens are built once and kept: replay drives the animations they already own,
        // rather than rebuilding a fresh tree that would have to be re-mounted to play.
        _content ??= BuildContent();
        foreach (var box in _boxes) box.Background = theme.Accent;
        return _content;
    }

    private Widget BuildContent()
    {
        return new GalleryPage(
            title: "Motion",
            description:
            "Named entrances, exits and loops built out of one timeline — and a spring that keeps " +
            "its speed when you change your mind halfway.",
            iconName: MaterialIcons.Animation
        ) {
            Children = {
                Demo.Titled(
                    title: "Interruptible",
                    description:
                    "Both dots chase the same target. Press Toggle twice in quick succession: the " +
                    "timed one stops dead and starts again, the spring bends around and carries on.",
                    child: Demo.Stage(
                        new Column(spacing: Spacing.Lg, mainAxisSize: MainAxisSize.Min) {
                            Children = {
                                Track(label: "Duration · 500 ms ease-out", track: _durationDot),
                                Track(label: "Spring · snappy", track: _springDot),
                                Demo.Bar(
                                    new AdwButton(label: "Toggle", onPressed: Toggle) {
                                        Style = AdwButtonStyle.Suggested,
                                    }
                                ),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "Entrances",
                    description: "One call each. They compose — chain another onto the same widget " +
                                 "and it joins the same timeline.",
                    child: new Column(spacing: Spacing.Lg, mainAxisSize: MainAxisSize.Min) {
                        Children = {
                            Demo.Stage(
                                Grid(
                                    Tile(name: "FadeInUp", preset: w => w.FadeInUp(), into: _entrances),
                                    Tile(name: "FadeInDown", preset: w => w.FadeInDown(), into: _entrances),
                                    Tile(name: "FadeInLeft", preset: w => w.FadeInLeft(), into: _entrances),
                                    Tile(name: "FadeInRight", preset: w => w.FadeInRight(), into: _entrances),
                                    Tile(name: "PopIn", preset: w => w.PopIn(), into: _entrances),
                                    Tile(name: "ZoomIn", preset: w => w.ZoomIn(), into: _entrances),
                                    Tile(name: "SpinIn", preset: w => w.SpinIn(), into: _entrances)
                                )
                            ),
                            Demo.Bar(new AdwButton(label: "Replay", onPressed: () => Replay(_entrances))),
                        },
                    }
                ),
                Demo.Titled(
                    title: "Exits",
                    description: "The same motions in reverse, for anything on its way out.",
                    child: new Column(spacing: Spacing.Lg, mainAxisSize: MainAxisSize.Min) {
                        Children = {
                            Demo.Stage(
                                Grid(
                                    Tile(name: "PopOut", preset: w => w.PopOut(), into: _exits),
                                    Tile(name: "FadeOutDown", preset: w => w.FadeOutDown(), into: _exits)
                                )
                            ),
                            Demo.Bar(new AdwButton(label: "Replay", onPressed: () => Replay(_exits))),
                        },
                    }
                ),
                Demo.Titled(
                    title: "Attention",
                    description: "For a field that was rejected — enough to notice, not enough to annoy.",
                    child: new Column(spacing: Spacing.Lg, mainAxisSize: MainAxisSize.Min) {
                        Children = {
                            Demo.Stage(
                                Grid(
                                    Tile(name: "Attention", preset: w => w.Attention(), into: _attention),
                                    Tile(name: "Wobble", preset: w => w.Wobble(), into: _attention)
                                )
                            ),
                            Demo.Bar(new AdwButton(label: "Again", onPressed: () => Replay(_attention))),
                        },
                    }
                ),
                Demo.Titled(
                    title: "Ambient",
                    description: "Loops with no end: they cost one ticker each and stop the moment " +
                                 "the page unmounts.",
                    child: Demo.Stage(
                        Grid(
                            Tile(name: "Pulse", preset: w => w.Pulse(), into: null),
                            Tile(name: "Breathe", preset: w => w.Breathe(), into: null),
                            Tile(name: "Spin", preset: w => w.Spin(), into: null)
                        )
                    )
                ),
                Demo.Titled(
                    title: "Staggered",
                    description: "A list that arrives as a wave — one Interval curve per index, so " +
                                 "the whole run is still a single timeline per row.",
                    child: new Column(spacing: Spacing.Lg, mainAxisSize: MainAxisSize.Min) {
                        Children = {
                            Demo.Stage(
                                StaggeredRows()
                            ),
                            Demo.Bar(new AdwButton(label: "Replay", onPressed: () => Replay(_rows))),
                        },
                    }
                ),
            },
        };
    }

    // ── Specimens ─────────────────────────────────────────────────────────────

    private Container Dot()
    {
        var dot = new Container {
            Width = 28f,
            Height = 28f,
            CornerRadius = Radii.Capsule,
        };
        _boxes.Add(dot);
        return dot;
    }

    private static Widget Track(string label, Widget track)
    {
        return new Column(
            spacing: Spacing.Xs,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        ) {
            Children = {
                Demo.Caption(label),
                new SizedBox(height: 34f, child: track),
            },
        };
    }

    /// <summary>A specimen cell: the animated box over its name, in a slot that does not move.</summary>
    private Widget Tile(string name, Func<Widget, Animate> preset, List<Animate>? into)
    {
        var box = new Container {
            Width = 44f,
            Height = 44f,
            CornerRadius = Radii.Md,
        };
        _boxes.Add(box);

        var animated = preset(box);
        into?.Add(animated);

        return new Column(
            spacing: Spacing.Sm,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        ) {
            Children = {
                new SizedBox(
                    width: 72f,
                    height: 72f,
                    child: new Center { Child = animated }
                ),
                Demo.Caption(name),
            },
        };
    }

    private static Widget Grid(params Widget[] tiles)
    {
        var wrap = new Wrap(spacing: Spacing.Lg, runSpacing: Spacing.Lg);
        foreach (var tile in tiles) wrap.Children.Add(tile);
        return wrap;
    }

    private Widget StaggeredRows()
    {
        List<Widget> bars = [];
        foreach (float width in (float[])[220f, 180f, 240f, 160f, 200f])
        {
            var bar = new Container {
                Width = width,
                Height = 20f,
                CornerRadius = Radii.Sm,
            };
            _boxes.Add(bar);
            bars.Add(bar);
        }

        var column = new Column(
            spacing: Spacing.Sm,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );
        foreach (var row in bars.Stagger(step: 80.ms))
        {
            _rows.Add((Animate)row);
            column.Children.Add(row);
        }

        return column;
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    private void Toggle()
    {
        _atEnd = !_atEnd;
        var target = new AlignT(X: _atEnd ? 1f : 0f, Y: 0.5f);
        _durationDot.Alignment = target;
        _springDot.Alignment = target;
    }

    private static void Replay(List<Animate> animations)
    {
        foreach (var animation in animations) animation.Restart();
    }
}
