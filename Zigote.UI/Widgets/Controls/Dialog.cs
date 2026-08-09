using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

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
    private Size _size;
    private Ticker? _ticker;
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
        _enter = new AnimationController(Motion.Standard, this) { Curve = Curves.EaseOut };
        _enter.OnTick += MarkNeedsLayout;
    }

    // The ticker CreateTicker hands out is owned by the mount period, so a re-attach just rebinds.
    protected override void OnMount()
    {
        _enter.AttachTicker(this);
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
    ///     Esc closes the sheet when it is dismissible (and consumes the key regardless, as a modal
    ///     barrier).
    /// </summary>
    public bool RequestDismiss()
    {
        if (Dismissible) Dismiss();
        return true;
    }

    // Resolved at Show(): with secondary OS windows, the window presenting the dialog is the one
    // whose dispatch is running (App.Active), which may differ from the window active at construction.
    private App? _host;

    /// <summary>Show this dialog as an overlay.</summary>
    public void Show()
    {
        _host = App.Active ?? _app;
        _host.PushOverlay(this);
        _enter.Dismiss();
        _enter.Forward();
    }

    /// <summary>
    ///     Invoked whenever the dialog leaves the overlay stack, whatever closed it (a button,
    ///     the scrim, Esc). Hosts that must settle async state (e.g. complete a Task) hook this
    ///     instead of duplicating the logic across every close path.
    /// </summary>
    public Action? OnClosed { get; set; }

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

        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        // Loose caps so the card sizes to its content (content columns are MainAxisSize.Min and
        // width is bounded by a ConstrainedBox), with an absolute width cap for large monitors.
        // A *Fraction (when set) instead sizes the card to a fixed fraction of the window — for large
        // editor surfaces that should fill most of the screen rather than hug their content.
        // A phone-width window has no room to give 20% away: the 0.8 fraction leaves 312 px of 390
        // for a card whose content minimum is 280. Below Compact the sheet takes the whole width
        // less a real edge margin instead.
        var maxW = WidthFraction > 0f
            ? _size.Width * WidthFraction
            : _size.Width < WindowSize.CompactMax
                ? MathF.Max(0f, _size.Width - Spacing.Xl * 2f)
                : MathF.Min(_size.Width * 0.8f, 560f);
        var maxH = HeightFraction > 0f ? _size.Height * HeightFraction : _size.Height * 0.85f;
        var minW = WidthFraction > 0f ? maxW : 0f;
        var minH = HeightFraction > 0f ? maxH : 0f;
        _contentSize = _card.Measure(
            new Constraints(
                minW,
                maxW,
                minH,
                maxH
            )
        );
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        var cx = origin.X + (_size.Width - _contentSize.Width) / 2f;
        var cy = origin.Y + (_size.Height - _contentSize.Height) / 2f;
        _card.Layout(new Offset(cx, cy));
    }

    public override void Paint(PaintList paint)
    {
        var t = _enter.Value;

        // Dimming scrim across the whole window; the card carries its own Z3 elevation shadow.
        paint.AddRect(
            Bounds,
            new Color(
                0f,
                0f,
                0f,
                Scrim * t
            )
        );

        // The card fades in and rises a few points into place.
        var rise = (1f - t) * 12f;
        var fade = t < 0.999f;
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(0f, rise);
        _card.Paint(paint);
        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _card.HitTest(point) ?? this;
    }

    public override void OnPointerDown(Offset point)
    {
        // Scrim click: pointer is within dialog bounds but outside the content card
        if (Dismissible && _card.HitTest(point) is null)
            Dismiss();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_card);
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    // ── Convenience overloads (use UiApp.Active — valid during Frame()) ──────────

    /// <summary>Alert dialog using the active UiApp.</summary>
    public static Dialog Alert(string title, string message,
        string buttonLabel = "OK")
    {
        return Alert(
            App.Active!,
            title,
            message,
            buttonLabel
        );
    }

    /// <summary>Confirm dialog using the active UiApp.</summary>
    public static Dialog Confirm(string title, string message,
        Action onConfirm, Action? onCancel = null,
        string confirmLabel = "OK", string cancelLabel = "Cancel")
    {
        return Confirm(
            App.Active!,
            title,
            message,
            onConfirm,
            onCancel,
            confirmLabel,
            cancelLabel
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
                new Button(buttonLabel, () => dlg?.Dismiss()),
            },
        };
        dlg = new Dialog(new ConstrainedBox(new Constraints(280f, 420f), content), app);
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
                            confirmLabel,
                            () =>
                            {
                                onConfirm();
                                dlg?.Dismiss();
                            }
                        ),
                        new SizedBox(Spacing.Sm),
                        new Button(
                            cancelLabel,
                            () =>
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
        dlg = new Dialog(new ConstrainedBox(new Constraints(280f, 420f), content), app);
        return dlg;
    }
}