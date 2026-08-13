using Zigote.Core.Animation;
using Zigote.UI.Host;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwDialog — the Adwaita floating-sheet presenter: a centered card on
///     <see cref="AdwColors.DialogBg" /> at <see cref="AdwMetrics.WindowRadius" /> with a
///     <see cref="Elevation.Z3" /> shadow, over a <see cref="ThemeData.OverlayBackground" /> scrim.
///     Same presentation mechanism as the base <see cref="Zigote.UI.Widgets.Controls.Dialog" />:
///     pushed on the App overlay stack via <see cref="Show()" />, removed via <see cref="Close" />;
///     the scrim click and Escape close it while <see cref="CanClose" /> is true.
/// </summary>
public class AdwDialog : Widget, IDismissableOverlay
{
    private readonly AnimationController _anim;
    private readonly DecoratedBox _card;
    private readonly ClipRRect _clip;
    private bool _closing;
    private float? _contentHeight;
    private Size _contentSize;
    private float? _contentWidth;
    private App? _host;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public AdwDialog(Widget? child = null)
    {
        // `floating-sheet > sheet { border-radius: $dialog_radius }`; an alert overrides it to the
        // rounder $alert_radius, which is why this reads a virtual rather than the constant.
        _clip = new ClipRRect(radius: Radius, child: child);
        _card = new DecoratedBox {
            Radius = Radius,
            Elevation = Elevation.Z3,
            Child = _clip,
        };
        // Adwaita dialog presentation: ~150ms ease-out; scrim fades, sheet fades + rises 8px.
        // Reverse plays the same motion out; the overlay pops when the reverse completes.
        _anim = new AnimationController(durationSeconds: 0.15f, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _anim.OnTick += MarkNeedsPaint;
        _anim.OnDismissed += FinishClose;
    }


    /// <summary>The sheet's corner radius — <c>$dialog_radius</c> unless a subclass says otherwise.</summary>
    protected virtual float Radius => AdwMetrics.DialogRadius;

    /// <summary>The dialog content, clipped to the card's rounded corners.</summary>
    public Widget? Child
    {
        get => _clip.Child;
        set
        {
            _clip.Child = value;
            MarkNeedsLayout();
        }
    }

    /// <summary>When true (default), clicking the scrim or pressing Escape closes the dialog.</summary>
    public bool CanClose { get; set; } = true;

    /// <summary>
    ///     Fixed content width; null hugs the content under the base ~560px cap. Re-lays out on
    ///     change — both sizes are read in <see cref="Measure" />, and nothing else would schedule
    ///     the pass that picks a new one up (same reason <see cref="Child" /> does).
    /// </summary>
    public float? ContentWidth
    {
        get => _contentWidth;
        set => SetLayout(field: ref _contentWidth, value: value);
    }

    /// <summary>Fixed content height; null hugs the content under 85% of the window. Re-lays out.</summary>
    public float? ContentHeight
    {
        get => _contentHeight;
        set => SetLayout(field: ref _contentHeight, value: value);
    }

    /// <summary>Invoked whenever the dialog leaves the overlay stack, whatever closed it.</summary>
    public Action? OnClosed { get; set; }

    /// <summary>Escape: close when closable; always consume (modal barrier).</summary>
    public bool RequestDismiss()
    {
        if (CanClose) Close();
        return true;
    }

    // ── Ticker plumbing (Toast.cs pattern: rebind on every Attach) ─────────────


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount() => _anim.AttachTicker(this);

    /// <summary>Show this dialog as an overlay on the active App.</summary>
    public void Show()
    {
        if (_closing)
        {
            // Re-shown while the exit animation runs: still on the overlay stack — just play back in.
            _closing = false;
            _anim.Forward();
            return;
        }

        _host = App.Active ??
                throw new InvalidOperationException("No active App to present the dialog on.");
        _host.PushOverlay(this);
        _anim.Forward();
    }

    /// <summary>
    ///     Close the dialog: play the exit animation, then remove it from the overlay stack
    ///     (<see cref="OnClosed" /> fires when it actually leaves).
    /// </summary>
    public virtual void Close()
    {
        if (_closing) return;
        if (_host is null || _anim.Progress <= 0f)
        {
            // Never shown / not yet visible: nothing to animate out.
            FinishClose();
            return;
        }

        _closing = true;
        _anim.Reverse();
    }

    private void FinishClose()
    {
        _closing = false;
        _host?.PopOverlay(this);
        _host = null;
        OnClosed?.Invoke();
    }

    /// <summary>Construct-and-show convenience mirroring the base Dialog helpers.</summary>
    public static AdwDialog Show(Widget child, float? width = null, float? height = null)
    {
        var dlg = new AdwDialog(child) {
            ContentWidth = width,
            ContentHeight = height,
        };
        dlg.Show();
        return dlg;
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Dialog;
        config.AddFlag(SemanticsFlags.Modal);
        if (CanClose) config.Actions = SemanticsAction.Dismiss;
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _card.Fill = AdwPalette.For(_theme).DialogBg;

        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));

        // Edge margin the card can never eat into (also the phone-width fallback, like the base).
        float margin = Spacing.Xl * 2f;
        float maxW = ContentWidth ?? (_size.Width < WindowSize.CompactMax
            ? MathF.Max(x: 0f, y: _size.Width - margin)
            : MathF.Min(x: _size.Width * 0.8f, y: 560f));
        maxW = MathF.Min(x: maxW, y: MathF.Max(x: 0f, y: _size.Width - margin));
        float maxH = ContentHeight ?? _size.Height * 0.85f;
        maxH = MathF.Min(x: maxH, y: MathF.Max(x: 0f, y: _size.Height - margin));

        _contentSize = _card.Measure(
            new Constraints(
                minWidth: ContentWidth.HasValue ? maxW : 0f,
                maxWidth: maxW,
                minHeight: ContentHeight.HasValue ? maxH : 0f,
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
        _card.Layout(
            new Offset(
                x: origin.X + ((_size.Width - _contentSize.Width) / 2f),
                y: origin.Y + ((_size.Height - _contentSize.Height) / 2f)
            )
        );
    }

    public override void Paint(PaintList paint)
    {
        float t = _anim.Value;

        // Scrim fades with the transition.
        var scrim = _theme.OverlayBackground;
        paint.AddRect(bounds: Bounds, color: scrim.WithAlpha(scrim.A * t));

        // The sheet fades in while rising 8px into place (and back out on close).
        float rise = (1f - t) * 8f;
        bool fade = t < 0.999f;
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(dx: 0f, dy: rise);
        _card.Paint(paint);
        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();
    }

    // Modal barrier: consume everything inside the window; the card gets first pick.
    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _card.HitTest(point) ?? this;
    }

    public override void OnPointerDown(Offset point)
    {
        // Scrim click: inside the dialog bounds but outside the content card.
        if (CanClose && _card.HitTest(point) is null)
            Close();
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_card);
}
