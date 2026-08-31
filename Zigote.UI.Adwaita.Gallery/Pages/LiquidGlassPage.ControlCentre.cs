using Zigote.UI.Semantics;

namespace AdwaitaGallery.Pages;

public sealed partial class LiquidGlassPage
{
    // Toggle state lives on the page, not in the Build — a theme or accent flip rebuilds the
    // whole page, and the radios the user switched off must not come back on.
    private readonly Signal<bool> _airplane = new(false);
    private readonly Signal<bool> _hotspot = new(true);
    private readonly Signal<bool> _wifi = new(true);
    private readonly Signal<bool> _bluetooth = new(true);

    // The faders, the focus mode and the transport: same rule, same reason.
    private readonly Signal<float> _brightness = new(0.40f);
    private readonly Signal<float> _volume = new(0.36f);
    private readonly Signal<int> _focus = new(0);
    private readonly Signal<bool> _playing = new(true);

    /// <summary>The focus pill's modes, in the order a press cycles them; 0 is off.</summary>
    private static readonly (string Icon, string Label)[] FocusModes = [
        (MaterialIcons.Nightlight, "Focus"),
        (MaterialIcons.DoNotDisturbOn, "Do Not Disturb"),
        (MaterialIcons.Work, "Work"),
        (MaterialIcons.Bedtime, "Sleep"),
    ];

    /// <summary>Card height for the two top tiles and the faders: three rows of tiles plus the
    ///     utilities is what fits a 640px phone with 14px gutters.</summary>
    private const float CardH = 150f;

    // ── Scene 3: control centre ───────────────────────────────────────────────

    /// <summary>The control-centre scene: every tile a pane in the phone's one group.</summary>
    private Widget ControlCentre()
    {
        return new Padding(
            padding: EdgeInsets.All(16f),
            child: new Column(
                spacing: 14f,
                mainAxisAlignment: MainAxisAlignment.Center,
                crossAxisAlignment: CrossAxisAlignment.Stretch
            ) {
                Children = {
                    // NOT cross-stretch: with the whole phone height as the cross max, Stretch
                    // pins the cross-min to it and the cards' 150px boxes clamp up to full
                    // height — the cards become floor-to-ceiling slabs.
                    new Row(spacing: 14f) {
                        Children = {
                            new Expanded(ConnectivityCard()),
                            new Expanded(PlayerCard()),
                        },
                    },
                    new Row(spacing: 14f, crossAxisAlignment: CrossAxisAlignment.Center) {
                        Children = {
                            new Expanded(
                                // Min, or the column takes the phone's full remaining height and
                                // shoves the utility rows off the bottom (Column defaults Max).
                                new Column(
                                    spacing: 14f,
                                    mainAxisSize: MainAxisSize.Min,
                                    crossAxisAlignment: CrossAxisAlignment.Stretch
                                ) {
                                    Children = {
                                        new Row(spacing: 14f) {
                                            Children = {
                                                GlassCircle(
                                                    icon: MaterialIcons.ScreenLockRotation,
                                                    label: "Rotation lock",
                                                    latch: "Rotation lock"
                                                ),
                                                GlassCircle(
                                                    icon: MaterialIcons.NotificationsNone,
                                                    label: "Notifications",
                                                    latch: "Notifications"
                                                ),
                                            },
                                        },
                                        FocusPill(),
                                    },
                                }
                            ),
                            Fader.Capsule(
                                icon: MaterialIcons.LightMode,
                                label: "Brightness",
                                value: _brightness
                            ),
                            Fader.Capsule(
                                icon: MaterialIcons.VolumeUp,
                                label: "Volume",
                                value: _volume
                            ),
                        },
                    },
                    UtilityRow(
                        (MaterialIcons.FlashlightOn, "Flashlight"),
                        (MaterialIcons.Timer, "Timer"),
                        (MaterialIcons.Calculate, "Calculator"),
                        (MaterialIcons.PhotoCamera, "Camera")
                    ),
                    UtilityRow(
                        (MaterialIcons.NightlightRound, "Dark mode"),
                        (MaterialIcons.MobileScreenShare, "Screen mirroring"),
                        (MaterialIcons.StickyNote2, "Notes"),
                        (MaterialIcons.ZoomIn, "Magnifier")
                    ),
                },
            }
        );
    }

    /// <summary>The 2×2 radio cluster. The dots are flat fills — glass never stacks on
    ///     glass.</summary>
    private Widget ConnectivityCard()
    {
        return LiquidPane.Regular(
            child: new SizedBox(
                height: CardH,
                child: new Center {
                    Child = new Column(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
                        Children = {
                            new Row(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    ToggleDot(
                                        icon: MaterialIcons.Flight,
                                        label: "Airplane mode",
                                        on: _airplane
                                    ),
                                    ToggleDot(
                                        icon: MaterialIcons.WifiTethering,
                                        label: "Hotspot",
                                        on: _hotspot
                                    ),
                                },
                            },
                            new Row(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    ToggleDot(icon: MaterialIcons.Wifi, label: "Wi-Fi", on: _wifi),
                                    ToggleDot(
                                        icon: MaterialIcons.Bluetooth,
                                        label: "Bluetooth",
                                        on: _bluetooth
                                    ),
                                },
                            },
                        },
                    },
                }
            ),
            radius: PhoneRadius,
            elevation: 4f
        );
    }

    /// <summary>One radio: a flat accent disc when on, a translucent one when off.</summary>
    private Widget ToggleDot(string icon, string label, Signal<bool> on)
    {
        var theme = _theme;
        return new Watch(() =>
            {
                bool active = on.Value;
                return new Pressable {
                    OnPressed = () => on.Value = !on.Value,
                    FocusRadius = 24f,
                    // State rides Checked, so a screen reader hears "Wi-Fi, on" and the name
                    // stays stable for focus tracking; a flipping label is neither.
                    Checked = active,
                    SemanticsLabel = label,
                    Child = new DecoratedBox {
                        Fill = active ? theme.Accent : Color.White.WithAlpha(0.24f),
                        Radius = 24f,
                        Child = SizedBox.Square(
                            size: 48f,
                            child: new Center {
                                Child = new IconGlyph(
                                    glyph: icon,
                                    size: 22f,
                                    color: Color.White
                                ),
                            }
                        ),
                    },
                };
            }
        );
    }

    /// <summary>
    ///     Now playing. The transport is three flat Adwaita buttons — flat fills on glass, the
    ///     way a toolbar's buttons sit on a header bar — and only the middle one has state.
    /// </summary>
    private Widget PlayerCard()
    {
        Color on = SceneInk;
        Color muted = SceneInkMuted;
        return LiquidPane.Regular(
            child: new SizedBox(
                height: CardH,
                child: new Padding(
                    padding: EdgeInsets.All(12f),
                    child: new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                        Children = {
                            new Row(crossAxisAlignment: CrossAxisAlignment.Start) {
                                Children = {
                                    new DecoratedBox {
                                        Fill = Color.Rgba(r: 30, g: 34, b: 44, a: 0.85f),
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
                            ) { MaxLines = 1, Overflow = TextOverflow.Ellipsis },
                            new Label(text: "Kane Brown", style: AdwTypography.Caption,
                                color: muted),
                            new SizedBox(height: 6f),
                            ProgressLine(fraction: 0.35f),
                            new SizedBox(height: 4f),
                            new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween) {
                                Children = {
                                    TransportButton(icon: MaterialIcons.SkipPrevious,
                                        label: "Previous", onPressed: () => { }),
                                    new Watch(() => TransportButton(
                                        icon: _playing.Value
                                            ? MaterialIcons.Pause
                                            : MaterialIcons.PlayArrow,
                                        label: _playing.Value ? "Pause" : "Play",
                                        onPressed: () => _playing.Value = !_playing.Peek()
                                    )),
                                    TransportButton(icon: MaterialIcons.SkipNext,
                                        label: "Next", onPressed: () => { }),
                                },
                            },
                        },
                    }
                )
            ),
            radius: PhoneRadius,
            elevation: 4f
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
                    child: new DecoratedBox { Fill = SceneInk, Radius = thickness / 2f }
                ) { Alignment = Alignment.CenterLeft }
            ),
        };
    }

    /// <summary>
    ///     The focus pill cycles through <see cref="FocusModes" /> on each press — no chevron: a
    ///     chevron promises a menu, and this control cycles. Any mode but off is the latched look
    ///     — solid white glass, dark ink — the same way a lit circle is, and it is rebuilt rather
    ///     than retinted so the glyph and label flip with the glass.
    /// </summary>
    private Widget FocusPill()
    {
        return new Watch(() =>
            {
                int mode = _focus.Value;
                bool on = mode != 0;
                Color ink = on ? LatchedInk : SceneInk;
                (string icon, string label) = FocusModes[mode];
                var pane = LiquidPane.Clear(
                    child: new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 14f, vertical: 13f),
                        child: new Row(spacing: 8f, crossAxisAlignment: CrossAxisAlignment.Center) {
                            Children = {
                                new IconGlyph(glyph: icon, size: 17f, color: ink),
                                // The pill is as wide as the column beside the faders leaves it,
                                // which is less than "Do Not Disturb" wants at this size: the
                                // label clips with an ellipsis rather than growing the pill.
                                new Expanded(
                                    new Label(
                                        text: label,
                                        style: new TextStyle(
                                            Size: 15f,
                                            Weight: FontWeight.SemiBold
                                        ),
                                        color: ink
                                    ) { MaxLines = 1, Overflow = TextOverflow.Ellipsis }
                                ),
                            },
                        }
                    )
                );
                return Latched(
                    pane: pane,
                    on: on,
                    onPressed: () => _focus.Value = (_focus.Peek() + 1) % FocusModes.Length,
                    semantics: on ? $"Focus: {label}" : "Focus: off"
                );
            }
        );
    }

    /// <summary>A row of latching utility circles, keyed by their own labels.</summary>
    private Widget UtilityRow(params (string Icon, string Label)[] buttons)
    {
        var row = new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween);
        foreach ((string icon, string label) in buttons)
            row.Children.Add(GlassCircle(icon: icon, label: label, size: 56f, latch: label));
        return row;
    }

    /// <summary>
    ///     A vertical fader: a clear pane with a flat white fill riding its bottom. The drag is
    ///     relative — the fill moves with the finger from wherever it was, as the system's own
    ///     faders do; a press that jumped to the touch point would slam a mostly-dark screen
    ///     bright. The fader paints the fill itself rather than laying out a box for it, so a
    ///     drag is a repaint and never a layout — and the pane is laid out through the fader, so
    ///     it joins the phone's group like every other tile. The glyph rides the glass in scene
    ///     ink and flips dark once the white fill has risen over it.
    /// </summary>
    private sealed class Fader : Widget
    {
        private const float W = 62f;
        private const float GlyphSize = 22f;

        /// <summary>The glyph's clearance from the bottom — the fill's first stop covers
        ///     it.</summary>
        private const float GlyphInset = 16f;

        private readonly IconGlyph _glyph;
        private readonly string _label;
        private readonly LiquidPane _pane;
        private readonly Signal<float> _value;
        private bool _dragging;
        private float _fill;
        private float _grabFill;
        private float _grabY;
        private ThemeData _theme = ThemeData.Dark;

        private Fader(string icon, string label, Signal<float> value)
        {
            _label = label;
            _value = value;
            _fill = value.Peek();
            _pane = LiquidPane.Clear(child: new SizedBox(width: W, height: CardH), radius: W / 2f);
            _glyph = new IconGlyph(glyph: icon, size: GlyphSize);
        }

        /// <summary>
        ///     The fader in its capsule clip: the clip is what rounds the flat fill into the
        ///     pane's own corners. The lens is already that shape, so the group drawing it
        ///     outside the clip changes nothing.
        /// </summary>
        public static Widget Capsule(string icon, string label, Signal<float> value) =>
            new ClipRRect(
                radius: W / 2f,
                child: new Fader(icon: icon, label: label, value: value)
            );

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            Size size = _pane.Measure(c);
            _glyph.Measure(Constraints.Loose(width: GlyphSize, height: GlyphSize));
            return size;
        }

        public override void Layout(Offset origin)
        {
            _pane.Layout(origin);
            Bounds = _pane.Bounds;
            _glyph.Layout(new Offset(
                x: Bounds.X + ((Bounds.Width - GlyphSize) / 2f),
                y: Bounds.Y + Bounds.Height - GlyphInset - GlyphSize
            ));
        }

        public override void Paint(PaintList paint)
        {
            // The group already laid the lens down; what is left of the pane is nothing, but
            // painting it keeps a pane outside a group honest too.
            _pane.Paint(paint);
            float band = Bounds.Height * _fill;
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: Bounds.Y + Bounds.Height - band,
                    width: Bounds.Width,
                    height: band
                ),
                color: Color.White,
                radius: 0f
            );
            _glyph.Color = band >= GlyphInset + (GlyphSize / 2f) ? LatchedInk : SceneInk;
            _glyph.Paint(paint);
            if (Focused) paint.AddFocusRing(bounds: Bounds, radius: W / 2f, theme: _theme);
        }

        public override IEnumerable<Widget> GetChildren()
        {
            yield return _pane;
            yield return _glyph;
        }

        public override Widget? HitTest(Offset point) =>
            Bounds.Contains(px: point.X, py: point.Y) ? this : null;

        public override MouseCursor? GetCursor(Offset point) => MouseCursor.Pointer;

        public override bool Focusable => true;

        public override void OnPointerDown(Offset point)
        {
            _dragging = true;
            _grabFill = _fill;
            _grabY = point.Y;
        }

        public override void OnPointerMove(Offset point)
        {
            if (_dragging) Drag(point);
        }

        public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
        {
            if (!down) return;
            switch ((KeyCode)scancode)
            {
                case KeyCode.Up or KeyCode.Right:
                    Set(_fill + 0.05f);
                    break;
                case KeyCode.Down or KeyCode.Left:
                    Set(_fill - 0.05f);
                    break;
            }
        }

        // A finger on the fader is adjusting it along the very axis the page scrolls on.
        public override bool CanTouchDrag(bool vertical) => _dragging;

        public override void OnPointerUp(Offset point) => _dragging = false;

        public override void OnPointerCancel() => _dragging = false;

        private void Drag(Offset point) =>
            Set(_grabFill + ((_grabY - point.Y) / Bounds.Height));

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

        public override int DebugStateHash() =>
            HashCode.Combine(value1: base.DebugStateHash(), value2: _fill);
    }
}
