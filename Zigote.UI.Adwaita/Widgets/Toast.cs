using Zigote.Core.Animation;
using Zigote.Core.State;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     One toast notification for <see cref="AdwToastOverlay.AddToast" />: a title, an optional
///     action button, and an auto-dismiss timeout.
/// </summary>
public sealed class AdwToast
{
    public AdwToast(string title) => Title = title;

    /// <summary>
    ///     Settable so the canonical GNOME undo counter works: re-adding the toast that is already
    ///     showing rewrites this text in place ("3 files deleted" over "2 files deleted") instead of
    ///     queueing a second pill. See <see cref="AdwToastOverlay.AddToast" />.
    /// </summary>
    public string Title { get; set; }

    /// <summary>Optional action button label (accent-colored, flat).</summary>
    public string? ButtonLabel { get; set; }

    public Action? OnButtonClicked { get; set; }

    /// <summary>Seconds before auto-dismiss. 0 or less keeps the toast until dismissed manually.</summary>
    public float Timeout { get; set; } = 5f;
}

/// <summary>
///     AdwToastOverlay — wraps a page and floats toasts over it as a dark pill at bottom-center.
///     Toasts queue FIFO, one visible at a time; each auto-dismisses after its
///     <see cref="AdwToast.Timeout" /> and can be dismissed early via its close button or by
///     activating its action.
/// </summary>
public sealed class AdwToastOverlay : ComposedWidget
{
    private const float EnterDuration = 0.2f;

    private const float ExitDuration = 0.15f;

    // The toast surface is always dark, whatever the appearance — fixed colors, not theme reads.
    // `$toast_bg_color: #505053` — an opaque mid-grey, not a black wash: a translucent toast picks
    // up whatever it happens to be floating over, which is exactly what a notification must not do.
    private static readonly Color Surface = Color.Rgb(r: 80, g: 80, b: 83);

    private static readonly Color Fg = Color.Rgb(r: 255, g: 255, b: 255);
    private readonly Signal<AdwToast?> _current = new(null);
    private readonly AnimationController _present;

    private readonly Queue<AdwToast> _queue = new();
    private readonly AnimationController _timer;
    private Widget _child;
    private ThemeData _theme = AdwTheme.Light;

    // The showing pill's title widget, retained so a replace-in-place update can rewrite it
    // without rebuilding (and therefore re-animating) the toast.
    private Label? _titleLabel;

    public AdwToastOverlay(Widget child)
    {
        _child = child;
        _timer = new AnimationController(durationSeconds: 5f, vsync: this) {
            Curve = Curves.Linear,
        };
        _timer.OnCompleted += BeginExit;
        // Presentation: slide up + fade in on show, fade out before advancing the queue.
        _present =
            new AnimationController(durationSeconds: EnterDuration, vsync: this) {
                Curve = Curves.EaseOut,
            };
        _present.OnTick += MarkNeedsLayout;
        _present.OnDismissed += ShowNext;
    }

    public Widget Child
    {
        get => _child;
        set => this.Set(field: ref _child, value: value);
    }

    /// <summary>
    ///     Queue a toast; it shows immediately when none is visible. Re-adding the toast instance
    ///     that is currently showing updates it in place — adw_toast_overlay_add_toast() does the
    ///     same, which is how the GNOME undo counter grows without the pill flickering.
    ///     <para>
    ///         The reads here are <see cref="Signal{T}.Peek" />, not <c>.Value</c>: a tracked read
    ///         would subscribe whatever reaction is running (a toast raised from inside an Effect)
    ///         to the signal <see cref="ShowNext" /> then writes, and the graph re-runs that
    ///         reaction on every dismissal — one phantom toast per cycle, forever.
    ///     </para>
    /// </summary>
    public void AddToast(AdwToast toast)
    {
        if (ReferenceEquals(objA: toast, objB: _current.Peek()))
        {
            if (_titleLabel is not null) _titleLabel.Text = toast.Title;
            RestartTimeout(toast);
            return;
        }

        _queue.Enqueue(toast);
        if (_current.Peek() is null) ShowNext();
    }

    private void ShowNext()
    {
        var next = _queue.Count > 0 ? _queue.Dequeue() : null;
        _current.Value = next;
        _timer.Dismiss();
        if (next is null)
        {
            _present.Dismiss();
            return;
        }

        _present.Duration = EnterDuration;
        _present.Dismiss();
        _present.Forward();
        RestartTimeout(next);
    }

    /// <summary>Run the auto-dismiss clock from zero. A timeout of 0 or less keeps the toast up.</summary>
    private void RestartTimeout(AdwToast toast)
    {
        _timer.Dismiss();
        if (toast.Timeout <= 0f) return;
        _timer.Duration = toast.Timeout;
        _timer.Forward();
    }

    /// <summary>Fade the current toast out; when it lands, the queue advances via OnDismissed.</summary>
    private void BeginExit()
    {
        _timer.Dismiss();
        if (_current.Peek() is null || _present.Status is AnimationStatus.Reverse) return;
        _present.Duration = ExitDuration;
        _present.Reverse();
    }

    // Two controllers, two tickers — Widget.CreateTicker owns both and drops them on unmount.
    protected override void OnMount()
    {
        _timer.AttachTicker(this);
        _present.AttachTicker(this);
    }

    // ── Tree ───────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        // The toast layer builds from a Watch reaction, which runs outside this context — snapshot
        // the theme here (and rebuild on a theme change, since Of() registers the dependency).
        _theme = ThemeProvider.Of(context);
        return new Stack {
            Children = {
                Child,
                new Watch(BuildToastLayer),
            },
        };
    }

    private Widget BuildToastLayer()
    {
        var toast = _current.Value;
        return new Align(Alignment.BottomCenter) {
            Child = toast is null
                ? null
                : new PresentLayer(anim: _present, toast: toast, child: BuildToast(toast)),
        };
    }

    private Widget BuildToast(AdwToast toast)
    {
        var row = new Row(
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center,
            spacing: Spacing.Md
        ) {
            Children = {
                new Flexible(
                    _titleLabel = new Label(text: toast.Title, fontSize: 14f, color: Fg) {
                        MaxLines = 1,
                        Overflow = TextOverflow.Ellipsis,
                    }
                ),
            },
        };

        if (!string.IsNullOrEmpty(toast.ButtonLabel))
        {
            row.Children.Add(
                FlatButton(
                    content: new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 10f, vertical: 4f),
                        // The accent-background variant, not the standalone one: the pill is always
                        // dark, and libadwaita's .osd scope likewise falls back to accent_bg_color
                        // because the on-light standalone accent is unreadable on it.
                        child: new Label(
                            text: toast.ButtonLabel!,
                            fontSize: 14f,
                            color: _theme.Accent
                        ) {
                            FontWeight = FontWeight.Bold,
                            MaxLines = 1,
                        }
                    ),
                    radius:
                    13f, // half the ~26px button height — a pill that the focus ring can follow
                    label: toast.ButtonLabel!,
                    onPressed: () =>
                    {
                        toast.OnButtonClicked?.Invoke();
                        BeginExit();
                    }
                )
            );
        }

        row.Children.Add(
            FlatButton(
                content: SizedBox.Square(
                    size: 32f,
                    child: new Center {
                        Child = new IconGlyph(glyph: Icons.Close, size: 16f, color: Fg),
                    }
                ),
                radius: 16f,
                label: "Dismiss",
                onPressed: BeginExit
            )
        );

        var pill = new DecoratedBox {
            Fill = Surface,
            Radius = AdwMetrics.Pill,
            Elevation = Elevation.Z3,
            // `toast { padding: 6px; &:dir(ltr) { padding-left: 12px } }`.
            Child = new Padding(
                padding: EdgeInsets.Only(
                    left: AdwMetrics.RowPaddingX,
                    top: AdwMetrics.RowSpacing,
                    right: AdwMetrics.RowSpacing,
                    bottom: AdwMetrics.RowSpacing
                ),
                child: row
            ),
        };

        // `toast { margin: 12px; margin-bottom: 24px }`.
        return new Padding(
            padding: EdgeInsets.Only(bottom: Spacing.Xxl),
            child: new ConstrainedBox(
                constraints: new Constraints(
                    minWidth: 0f,
                    maxWidth: 450f,
                    minHeight: 0f,
                    maxHeight: float.PositiveInfinity
                ),
                child: pill
            )
        );
    }

    /// <summary>
    ///     A flat button on the dark toast surface: transparent, white wash on hover/press. Wired
    ///     against <see cref="AdwTheme.Dark" /> rather than the ambient theme — the pill is always
    ///     dark, so the light appearance's near-black wash would be invisible on it.
    /// </summary>
    private static Widget FlatButton(Widget content, float radius, string label, Action onPressed)
    {
        var box = new DecoratedBox {
            Radius = radius,
            Fill = Color.Transparent,
            Child = content,
        };
        var press = new Pressable {
            Child = box,
            FocusRadius = radius,
            OnPressed = onPressed,
            SemanticsLabel = label,
        };
        press.WireFill(box: box, theme: AdwTheme.Dark);
        return press;
    }

    /// <summary>
    ///     Presentation wrapper for the toast pill: rises ~12px while fading in (controller forward),
    ///     pure fade on the way out (controller reverse).
    ///     Hand-rolled rather than SlideTransition+FadeTransition: the slide is status-asymmetric
    ///     (rise in, fade-only out), which a transition driven by one controller value cannot
    ///     express — and this widget carries the toast's Alert semantics either way.
    /// </summary>
    private sealed class PresentLayer(AnimationController anim, AdwToast toast, Widget child)
        : Widget
    {
        private const float Rise = 12f;
        private Size _size;

        /// <summary>
        ///     A toast is the one surface that must reach a screen reader without ever taking focus —
        ///     GNOME announces AdwToast as an alert the moment it appears. Alert + LiveRegion is
        ///     exactly that contract, and the title is read live so a replace-in-place update (the
        ///     undo counter) re-announces the new text.
        /// </summary>
        public override void DescribeSemantics(SemanticsConfiguration config)
        {
            config.Role = SemanticsRole.Alert;
            config.Label = toast.Title;
            config.AddFlag(SemanticsFlags.LiveRegion);
        }

        public override Size Measure(Constraints c)
        {
            _size = child.Measure(c);
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );
            // Slide only on the way in; the exit is a pure fade.
            float dy = anim.Status is AnimationStatus.Reverse ? 0f : Rise * (1f - anim.Value);
            child.Layout(new Offset(x: origin.X, y: origin.Y + dy));
        }

        public override void Paint(PaintList paint)
        {
            float a = anim.Value;
            if (a <= 0.001f) return;
            if (a >= 0.999f)
            {
                child.Paint(paint);
                return;
            }

            paint.PushAlpha(a);
            child.Paint(paint);
            paint.PopAlpha();
        }

        public override Widget? HitTest(Offset point)
        {
            if (anim.Value <= 0.001f) return null;
            return Bounds.Contains(px: point.X, py: point.Y) ? child.HitTest(point) : null;
        }

        public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(child);
    }
}
