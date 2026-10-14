using Zigote.UI.Semantics;

namespace AdwaitaGallery.Pages;

public sealed partial class LiquidGlassPage
{
    private readonly Signal<bool> _wifi = new(true);
    private readonly Signal<bool> _bluetooth = new(true);
    private readonly Signal<bool> _airdrop = new(false);
    private readonly Signal<float> _brightness = new(0.40f);
    private readonly Signal<float> _volume = new(0.36f);
    private readonly Signal<int> _focus = new(0);
    private readonly Signal<bool> _playing = new(true);

    private static readonly (string Icon, string Label)[] FocusModes = [
        (MaterialIcons.Nightlight, "Focus"),
        (MaterialIcons.DoNotDisturbOn, "Do Not Disturb"),
        (MaterialIcons.Work, "Work"),
        (MaterialIcons.Bedtime, "Sleep"),
    ];

    // The reference's hierarchy: connectivity capsules, a larger player, then two quiet
    // full-width sliders. Every pane shares one capture; badges and tracks are flat ink.
    private Widget ControlCentre() => new Padding(
        padding: EdgeInsets.All(16f),
        child: new Column(
            spacing: 12f,
            mainAxisAlignment: MainAxisAlignment.Center,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        ) {
            Children = {
                new Row(spacing: 12f) {
                    Children = {
                        new Expanded(
                            new Column(
                                spacing: 12f,
                                mainAxisSize: MainAxisSize.Min,
                                crossAxisAlignment: CrossAxisAlignment.Stretch
                            ) {
                                Children = {
                                    ConnectionPill(
                                        icon: MaterialIcons.Link,
                                        label: "Wi-Fi",
                                        activeSubtitle: "Connected",
                                        state: _wifi
                                    ),
                                    ConnectionPill(
                                        icon: MaterialIcons.Bluetooth,
                                        label: "Bluetooth",
                                        activeSubtitle: "On",
                                        state: _bluetooth
                                    ),
                                    ConnectionPill(
                                        icon: MaterialIcons.WifiTethering,
                                        label: "AirDrop",
                                        activeSubtitle: "Contacts",
                                        state: _airdrop
                                    ),
                                },
                            }
                        ),
                        new Expanded(
                            new Column(
                                spacing: 12f,
                                mainAxisSize: MainAxisSize.Min,
                                crossAxisAlignment: CrossAxisAlignment.Stretch
                            ) {
                                Children = {
                                    PlayerCard(),
                                    new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween) {
                                        Children = {
                                            GlassCircle(
                                                icon: MaterialIcons.ViewCompact,
                                                label: "Stage Manager",
                                                size: 63f,
                                                latch: "Stage Manager"
                                            ),
                                            GlassCircle(
                                                icon: MaterialIcons.Devices,
                                                label: "Screen mirroring",
                                                size: 63f,
                                                latch: "Screen mirroring"
                                            ),
                                        },
                                    },
                                },
                            }
                        ),
                    },
                },
                new Row(spacing: 12f, crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        GlassCircle(
                            icon: MaterialIcons.Contrast,
                            label: "Appearance",
                            size: 63f,
                            latch: "Appearance"
                        ),
                        GlassCircle(
                            icon: MaterialIcons.AccountBalanceWallet,
                            label: "Wallet",
                            size: 63f,
                            latch: "Wallet"
                        ),
                        new Expanded(FocusPill()),
                    },
                },
                new Fader(
                    label: "Display",
                    lowIcon: MaterialIcons.BrightnessLow,
                    highIcon: MaterialIcons.BrightnessHigh,
                    value: _brightness
                ),
                new Fader(
                    label: "Sound",
                    lowIcon: MaterialIcons.VolumeDown,
                    highIcon: MaterialIcons.VolumeUp,
                    value: _volume
                ),
            },
        }
    );

    private Widget ConnectionPill(string icon, string label, string activeSubtitle,
        Signal<bool> state) =>
        new Watch(() =>
            {
                bool on = state.Value;
                var pane = LiquidPane.Regular(
                    radius: AdwMetrics.Pill,
                    elevation: 2f,
                    child: new SizedBox(
                        height: 64f,
                        child: new Padding(
                            padding: EdgeInsets.Symmetric(horizontal: 12f, vertical: 0f),
                            child: new Row(
                                spacing: 8f,
                                crossAxisAlignment: CrossAxisAlignment.Center
                            ) {
                                Children = {
                                    ControlBadge(
                                        icon: icon,
                                        on: on,
                                        accent: Color.Rgb(r: 157, g: 65, b: 170)
                                    ),
                                    new Expanded(
                                        new Column(
                                            mainAxisSize: MainAxisSize.Min,
                                            crossAxisAlignment: CrossAxisAlignment.Start
                                        ) {
                                            Children = {
                                                new Label(
                                                    text: label,
                                                    style: new TextStyle(
                                                        Size: 13f,
                                                        Weight: FontWeight.SemiBold
                                                    ),
                                                    color: SceneInk
                                                ) {
                                                    MaxLines = 1,
                                                    Overflow = TextOverflow.Ellipsis,
                                                },
                                                new Label(
                                                    text: on ? activeSubtitle : "Off",
                                                    style: new TextStyle(Size: 12f),
                                                    color: SceneInkMuted
                                                ) {
                                                    MaxLines = 1,
                                                    Overflow = TextOverflow.Ellipsis,
                                                },
                                            },
                                        }
                                    ),
                                },
                            }
                        )
                    )
                );
                var button = LiquidPane.Interactive(
                    pane: pane,
                    onPressed: () => state.Value = !state.Value,
                    semantics: label
                );
                button.Checked = on;
                return button;
            }
        );

    private static Widget ControlBadge(string icon, bool on, Color accent) => new DecoratedBox {
        Fill = on ? Color.White : Color.White.WithAlpha(0.20f),
        Radius = AdwMetrics.Pill,
        Child = SizedBox.Square(
            size: 36f,
            child: new Center {
                Child = new IconGlyph(glyph: icon, size: 22f, color: on ? accent : SceneInk),
            }
        ),
    };

    private Widget FocusPill() => new Watch(() =>
        {
            int mode = _focus.Value;
            (string icon, string label) = FocusModes[mode];
            var pane = LiquidPane.Regular(
                radius: AdwMetrics.Pill,
                elevation: 2f,
                child: new SizedBox(
                    height: 63f,
                    child: new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 12f, vertical: 0f),
                        child: new Row(spacing: 8f, crossAxisAlignment: CrossAxisAlignment.Center) {
                            Children = {
                                ControlBadge(
                                    icon: icon,
                                    on: mode != 0,
                                    accent: Color.Rgb(r: 112, g: 105, b: 255)
                                ),
                                new Expanded(
                                    new Column(
                                        mainAxisSize: MainAxisSize.Min,
                                        crossAxisAlignment: CrossAxisAlignment.Start
                                    ) {
                                        Children = {
                                            new Label(
                                                text: label,
                                                style: new TextStyle(
                                                    Size: 13f,
                                                    Weight: FontWeight.SemiBold
                                                ),
                                                color: SceneInk
                                            ) {
                                                MaxLines = 1,
                                                Overflow = TextOverflow.Ellipsis,
                                            },
                                            new Label(
                                                text: mode == 0 ? "Off" : "On",
                                                style: new TextStyle(Size: 12f),
                                                color: SceneInkMuted
                                            ),
                                        },
                                    }
                                ),
                            },
                        }
                    )
                )
            );
            var button = LiquidPane.Interactive(
                pane: pane,
                onPressed: () => _focus.Value = (_focus.Peek() + 1) % FocusModes.Length,
                semantics: mode == 0 ? "Focus: off" : $"Focus: {label}"
            );
            button.Checked = mode != 0;
            return button;
        }
    );

    /// <summary>
    ///     Now playing. The transport is three flat Adwaita buttons — flat fills on glass, the
    ///     way a toolbar's buttons sit on a header bar — and only the middle one has state.
    /// </summary>
    private Widget PlayerCard()
    {
        var on = SceneInk;
        var muted = SceneInkMuted;
        return LiquidPane.Regular(
            child: new SizedBox(
                height: 140f,
                child: new Padding(
                    padding: EdgeInsets.All(12f),
                    child: new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                        Children = {
                            new Row(crossAxisAlignment: CrossAxisAlignment.Start) {
                                Children = {
                                    new DecoratedBox {
                                        Fill = Color.Rgba(
                                            r: 30,
                                            g: 34,
                                            b: 44,
                                            a: 0.85f
                                        ),
                                        Radius = 10f,
                                        Child = SizedBox.Square(
                                            size: 36f,
                                            child: new Center {
                                                Child = new IconGlyph(
                                                    glyph: MaterialIcons.MusicNote,
                                                    size: 18f,
                                                    color: Color.White
                                                ),
                                            }
                                        ),
                                    },
                                    new Spacer(),
                                    new IconGlyph(
                                        glyph: MaterialIcons.Smartphone,
                                        size: 16f,
                                        color: muted
                                    ),
                                },
                            },
                            new Spacer(),
                            new Label(
                                text: "Backseat Driver",
                                style: new TextStyle(Size: 14f, Weight: FontWeight.Bold),
                                color: on
                            ) {
                                MaxLines = 1,
                                Overflow = TextOverflow.Ellipsis,
                            },
                            new Label(
                                text: "Kane Brown",
                                style: AdwTypography.Caption,
                                color: muted
                            ),
                            new SizedBox(height: 6f),
                            ProgressLine(fraction: 0.35f),
                            new SizedBox(height: 4f),
                            new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween) {
                                Children = {
                                    TransportButton(
                                        icon: MaterialIcons.SkipPrevious,
                                        label: "Previous",
                                        onPressed: () => { }
                                    ),
                                    new Watch(() => TransportButton(
                                            icon: _playing.Value
                                                ? MaterialIcons.Pause
                                                : MaterialIcons.PlayArrow,
                                            label: _playing.Value ? "Pause" : "Play",
                                            onPressed: () => _playing.Value = !_playing.Peek()
                                        )
                                    ),
                                    TransportButton(
                                        icon: MaterialIcons.SkipNext,
                                        label: "Next",
                                        onPressed: () => { }
                                    ),
                                },
                            },
                        },
                    }
                )
            ),
            radius: PhoneRadius,
            elevation: 2f
        );
    }

    /// <summary>A flat, round, compact icon button — the transport's unit.</summary>
    private static Widget TransportButton(string icon, string label, Action onPressed)
    {
        return new AdwButton(label: label, onPressed: onPressed) {
            IconName = icon,
            Style = AdwButtonStyle.Flat,
            Circular = true,
            Compact = true,
        };
    }

    /// <summary>
    ///     A 3px flat progress line: a muted track with the played part in ink. Two boxes
    ///     rather than AdwProgressBar, whose accent fill and trough are tuned for a window, not
    ///     for white ink on glass.
    /// </summary>
    private static Widget ProgressLine(float fraction)
    {
        const float thickness = 3f;
        return new DecoratedBox {
            Fill = SceneInk.WithAlpha(0.28f),
            Radius = thickness / 2f,
            Child = new SizedBox(
                height: thickness,
                child: new FractionallySizedBox(
                    widthFactor: fraction,
                    child: new DecoratedBox {
                        Fill = SceneInk,
                        Radius = thickness / 2f,
                    }
                ) { Alignment = Alignment.CenterLeft }
            ),
        };
    }

    /// <summary>A horizontal fader with a full-pane hit target, relative dragging and keyboard input.</summary>
    private sealed class Fader : Widget
    {
        private readonly string _label;
        private readonly LiquidPane _pane;
        private readonly Signal<float> _value;
        private bool _dragging;
        private float _fill;
        private float _grabFill;
        private float _grabX;
        private ThemeData _theme = ThemeData.Dark;

        public Fader(string label, string lowIcon, string highIcon, Signal<float> value)
        {
            _label = label;
            _value = value;
            _fill = value.Peek();
            _pane = LiquidPane.Regular(
                radius: 28f,
                elevation: 2f,
                child: new SizedBox(
                    height: 64f,
                    child: new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 16f, vertical: 10f),
                        child: new
                            Column(spacing: 6f, crossAxisAlignment: CrossAxisAlignment.Stretch) {
                                Children = {
                                    new Label(
                                        text: label,
                                        style:
                                        new TextStyle(Size: 13f, Weight: FontWeight.SemiBold),
                                        color: SceneInk
                                    ),
                                    new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween) {
                                        Children = {
                                            new IconGlyph(
                                                glyph: lowIcon,
                                                size: 20f,
                                                color: SceneInk
                                            ),
                                            new IconGlyph(
                                                glyph: highIcon,
                                                size: 20f,
                                                color: SceneInk
                                            ),
                                        },
                                    },
                                },
                            }
                    )
                )
            );
        }

        private Rect Track => new(
            x: Bounds.X + 42f,
            y: Bounds.Y + 40f,
            width: MathF.Max(x: 1f, y: Bounds.Width - 84f),
            height: 4f
        );

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            return _pane.Measure(c);
        }

        public override void Layout(Offset origin)
        {
            _pane.Layout(origin);
            Bounds = _pane.Bounds;
        }

        public override void Paint(PaintList paint)
        {
            _pane.Paint(paint);
            var track = Track;
            paint.AddRect(bounds: track, color: SceneInk.WithAlpha(0.18f), radius: 2f);
            if (_fill > 0f)
                paint.AddRect(
                    bounds: new Rect(
                        x: track.X,
                        y: track.Y,
                        width: track.Width * _fill,
                        height: track.Height
                    ),
                    color: SceneInk,
                    radius: 2f
                );
            if (Focused) paint.AddFocusRing(bounds: Bounds, radius: 28f, theme: _theme);
        }

        public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_pane);

        public override Widget? HitTest(Offset point) =>
            Bounds.Contains(px: point.X, py: point.Y) ? this : null;

        public override MouseCursor? GetCursor(Offset point) => MouseCursor.Pointer;
        public override bool Focusable => true;
        public override void OnPointerEnter() => _pane.Hovered = true;
        public override void OnPointerExit() => _pane.Hovered = false;

        public override void OnPointerDown(Offset point)
        {
            _dragging = true;
            _pane.Pressed = true;
            _grabFill = _fill;
            _grabX = point.X;
        }

        public override void OnPointerMove(Offset point)
        {
            if (_dragging) Set(_grabFill + ((point.X - _grabX) / Track.Width));
        }

        public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
        {
            if (!down) return;
            switch ((KeyCode)scancode)
            {
                case KeyCode.Up or KeyCode.Right: Set(_fill + 0.05f); break;
                case KeyCode.Down or KeyCode.Left: Set(_fill - 0.05f); break;
                case KeyCode.Home: Set(0f); break;
                case KeyCode.End: Set(1f); break;
            }
        }

        public override bool CanTouchDrag(bool vertical) => _dragging && !vertical;
        public override void OnPointerUp(Offset point) => EndDrag();
        public override void OnPointerCancel() => EndDrag();

        private void EndDrag()
        {
            _dragging = false;
            _pane.Pressed = false;
        }

        private void Set(float fill)
        {
            _fill = Math.Clamp(value: fill, min: 0f, max: 1f);
            _value.Value = _fill;
            MarkNeedsPaint();
        }

        public override void DescribeSemantics(SemanticsConfiguration config)
        {
            config.Role = SemanticsRole.Slider;
            config.Label = _label;
            config.Value = $"{_fill * 100f:F0}%";
            config.IsLeaf = true;
        }

        public override int DebugStateHash() => HashCode.Combine(
            value1: base.DebugStateHash(),
            value2: _fill
        );
    }
}
