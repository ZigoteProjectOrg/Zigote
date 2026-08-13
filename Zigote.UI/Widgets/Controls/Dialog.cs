using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     A flat, macOS-style modal sheet. Renders a translucent scrim over the whole window with a
///     centered, opaque <see cref="ThemeData.Surface" /> card floating above it on a soft Z3 shadow.
///     Push onto the overlay stack with <see cref="Show" />; remove with <see cref="Dismiss" />.
/// </summary>
public class Dialog : Widget, IDismissableOverlay
{
    private readonly App _app;
    private readonly Card _card;
    private readonly AnimationController _enter;
    private Size _contentSize;

    // Resolved at Show(): with secondary OS windows, the window presenting the dialog is the one
    // whose dispatch is running (App.Active), which may differ from the window active at construction.
    private App? _host;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public Dialog(Widget content, App? app = null)
    {
        _card = new Card(content) {
            Radius = Radii.Xl,
            Elevation = 12f, // maps to the Z3 elevation bucket
            Bordered = false,
        };
        _app = app ?? App.Active ??
            throw new InvalidOperationException("No active UiApp found.");
        _enter = new AnimationController(durationSeconds: Motion.Standard, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _enter.OnTick += MarkNeedsLayout;
    }

    /// <summary>When true (default), clicking the scrim dismisses the dialog.</summary>
    public bool Dismissible { get; set; } = true;

    /// <summary>Opacity of the dimming scrim painted behind the card. macOS sheets sit at ~0.4.</summary>
    public float Scrim { get; set; } = 0.4f;

    /// <summary>
    ///     When &gt; 0, the content is sized to this fraction of the window width instead of hugging its
    ///     content under the default ~560pt cap. Use for large editor surfaces (e.g. a node graph).
    /// </summary>
    public float WidthFraction { get; set; }

    /// <summary>
    ///     When &gt; 0, the content is sized to this fraction of the window height (see
    ///     <see cref="WidthFraction" />).
    /// </summary>
    public float HeightFraction { get; set; }

    /// <summary>
    ///     Invoked whenever the dialog leaves the overlay stack, whatever closed it (a button,
    ///     the scrim, Esc). Hosts that must settle async state (e.g. complete a Task) hook this
    ///     instead of duplicating the logic across every close path.
    /// </summary>
    public Action? OnClosed { get; set; }

    /// <summary>
    ///     Esc closes the sheet when it is dismissible (and consumes the key regardless, as a modal
    ///     barrier).
    /// </summary>
    public bool RequestDismiss()
    {
        if (Dismissible) Dismiss();
        return true;
    }

    // The ticker CreateTicker hands out is owned by the mount period, so a re-attach just rebinds.
    protected override void OnMount() => _enter.AttachTicker(this);

    /// <summary>Show this dialog as an overlay.</summary>
    public void Show()
    {
        _host = App.Active ?? _app;
        _host.PushOverlay(this);
        _enter.Dismiss();
        _enter.Forward();
    }

    /// <summary>Remove this dialog from the overlay stack.</summary>
    public void Dismiss()
    {
        (_host ?? _app).PopOverlay(this);
        OnClosed?.Invoke();
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Dialog;
        config.AddFlag(SemanticsFlags.Modal);
        if (Dismissible) config.Actions = SemanticsAction.Dismiss;
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);

        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        // Loose caps so the card sizes to its content (content columns are MainAxisSize.Min and
        // width is bounded by a ConstrainedBox), with an absolute width cap for large monitors.
        // A *Fraction (when set) instead sizes the card to a fixed fraction of the window — for large
        // editor surfaces that should fill most of the screen rather than hug their content.
        // A phone-width window has no room to give 20% away: the 0.8 fraction leaves 312 px of 390
        // for a card whose content minimum is 280. Below Compact the sheet takes the whole width
        // less a real edge margin instead.
        float maxW = WidthFraction > 0f
            ? _size.Width * WidthFraction
            : _size.Width < WindowSize.CompactMax
                ? MathF.Max(x: 0f, y: _size.Width - (Spacing.Xl * 2f))
                : MathF.Min(x: _size.Width * 0.8f, y: 560f);
        float maxH = HeightFraction > 0f ? _size.Height * HeightFraction : _size.Height * 0.85f;
        float minW = WidthFraction > 0f ? maxW : 0f;
        float minH = HeightFraction > 0f ? maxH : 0f;
        _contentSize = _card.Measure(
            new Constraints(
                minWidth: minW,
                maxWidth: maxW,
                minHeight: minH,
                maxHeight: maxH
            )
        );
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
        float cx = origin.X + ((_size.Width - _contentSize.Width) / 2f);
        float cy = origin.Y + ((_size.Height - _contentSize.Height) / 2f);
        _card.Layout(new Offset(x: cx, y: cy));
    }

    public override void Paint(PaintList paint)
    {
        float t = _enter.Value;

        // Dimming scrim across the whole window; the card carries its own Z3 elevation shadow.
        paint.AddRect(
            bounds: Bounds,
            color: new Color(
                r: 0f,
                g: 0f,
                b: 0f,
                a: Scrim * t
            )
        );

        // The card fades in and rises a few points into place.
        float rise = (1f - t) * 12f;
        bool fade = t < 0.999f;
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(dx: 0f, dy: rise);
        _card.Paint(paint);
        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _card.HitTest(point) ?? this;
    }

    public override void OnPointerDown(Offset point)
    {
        // Scrim click: pointer is within dialog bounds but outside the content card
        if (Dismissible && _card.HitTest(point) is null)
            Dismiss();
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_card);

    // ── Static helpers ────────────────────────────────────────────────────────

    // ── Convenience overloads (use UiApp.Active — valid during Frame()) ──────────

    /// <summary>Alert dialog using the active UiApp.</summary>
    public static Dialog Alert(string title, string message,
        string buttonLabel = "OK")
    {
        return Alert(
            app: App.Active!,
            title: title,
            message: message,
            buttonLabel: buttonLabel
        );
    }

    /// <summary>Confirm dialog using the active UiApp.</summary>
    public static Dialog Confirm(string title, string message,
        Action onConfirm, Action? onCancel = null,
        string confirmLabel = "OK", string cancelLabel = "Cancel")
    {
        return Confirm(
            app: App.Active!,
            title: title,
            message: message,
            onConfirm: onConfirm,
            onCancel: onCancel,
            confirmLabel: confirmLabel,
            cancelLabel: cancelLabel
        );
    }

    /// <summary>Alert dialog with title, message, and an OK button.</summary>
    public static Dialog Alert(App app,
        string title, string message, string buttonLabel = "OK")
    {
        Dialog? dlg = null;
        var content = new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize =
                MainAxisSize.Min, // shrink to content height (was Max → stretched to ~80% screen)
            Children = {
                new Label(title) {
                    FontWeight = FontWeight.Bold,
                    Style = Label.LabelStyle.Title,
                },
                new SizedBox(height: Spacing.Md),
                new Label(message) { Style = Label.LabelStyle.Body },
                new SizedBox(height: Spacing.Xl),
                new Button(label: buttonLabel, onPressed: () => dlg?.Dismiss()),
            },
        };
        dlg = new Dialog(
            content: new ConstrainedBox(
                constraints: new Constraints(minWidth: 280f, maxWidth: 420f),
                child: content
            ),
            app: app
        );
        return dlg;
    }

    /// <summary>Confirm dialog with title, message, OK and Cancel buttons.</summary>
    public static Dialog Confirm(App app,
        string title, string message,
        Action onConfirm, Action? onCancel = null,
        string confirmLabel = "OK", string cancelLabel = "Cancel")
    {
        Dialog? dlg = null;
        var content = new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize =
                MainAxisSize.Min, // shrink to content height (was Max → stretched to ~80% screen)
            Children = {
                new Label(title) {
                    FontWeight = FontWeight.Bold,
                    Style = Label.LabelStyle.Title,
                },
                new SizedBox(height: Spacing.Md),
                new Label(message) { Style = Label.LabelStyle.Body },
                new SizedBox(height: Spacing.Xl),
                new Row {
                    MainAxisAlignment = MainAxisAlignment.Start,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        new Button(
                            label: confirmLabel,
                            onPressed: () =>
                            {
                                onConfirm();
                                dlg?.Dismiss();
                            }
                        ),
                        new SizedBox(Spacing.Sm),
                        new Button(
                            label: cancelLabel,
                            onPressed: () =>
                            {
                                onCancel?.Invoke();
                                dlg?.Dismiss();
                            }
                        ) {
                            Style = ButtonStyle.Flat,
                        },
                    },
                },
            },
        };
        dlg = new Dialog(
            content: new ConstrainedBox(
                constraints: new Constraints(minWidth: 280f, maxWidth: 420f),
                child: content
            ),
            app: app
        );
        return dlg;
    }
}
