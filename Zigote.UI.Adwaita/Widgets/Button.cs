// AdwButton exposes a `Label` string property that shadows the Label widget type inside the
// state class, so reference the widget type through an alias.

using Zigote.Core.Animation;
using LabelWidget = Zigote.UI.Widgets.Controls.Label;

namespace Zigote.UI.Adwaita;

/// <summary>
///     Fades a control's background colour to a new target over ~100ms ease-out — the Adwaita
///     hover/press transition — instead of snapping. Retargeting mid-flight starts from the
///     current interpolated colour; the first-ever call snaps so initial builds don't fade in
///     from transparent. Self-ticking: the ticker runs only while a fade is in flight and stops
///     itself on completion, so no Attach/Detach plumbing is needed.
///     ponytail: a widget disposed mid-fade lets the ticker run out its ~100ms against the
///     detached box, then stop — harmless; wire owner Detach through Snap if it ever matters.
/// </summary>
internal sealed class FillTransition
{
    private const float Duration = 0.1f;

    private readonly AnimationController _anim;
    private readonly Action<Color> _apply;
    private readonly Ticker _ticker;
    private readonly ColorTween _tween = new(Color.Transparent, Color.Transparent);
    private bool _started;

    /// <param name="apply">Writes the colour to the retained widget and marks it for repaint.</param>
    public FillTransition(Action<Color> apply)
    {
        _apply = apply;
        _anim = new AnimationController(Duration) { Curve = Curves.EaseOut };
        _anim.OnTick += () => _apply(_tween.Evaluate(_anim.Value));
        _ticker = new Ticker(Step);
    }

    private void Step(float dt)
    {
        _anim.Tick(dt);
        if (_anim.Status is AnimationStatus.Completed or AnimationStatus.Dismissed)
            _ticker.Stop();
    }

    /// <summary>Fade to <paramref name="target" />. The first call (or an unchanged colour) snaps.</summary>
    public void Target(Color target)
    {
        var current = _started ? _tween.Evaluate(_anim.Value) : target;
        if (current == target)
        {
            Snap(target);
            return;
        }

        _started = true;
        _tween.Begin = current;
        _tween.End = target;
        _anim.Dismiss();
        _anim.Forward();
        _ticker.Start();
    }

    /// <summary>Set the colour instantly (initial build, theme swap).</summary>
    public void Snap(Color target)
    {
        _started = true;
        _tween.Begin = target;
        _tween.End = target;
        _anim.Dismiss();
        _ticker.Stop();
        _apply(target);
    }
}

/// <summary>
///     The Adwaita push button: neutral translucent fill by default, with
///     <see cref="AdwButtonStyle" /> selecting .suggested-action / .destructive-action / .flat.
///     Composed from a <see cref="Pressable" /> over a <see cref="DecoratedBox" /> — hover/press
///     feedback is a recolour, never a rebuild. Disabled = whole-control 50% opacity, as Adwaita
///     does.
/// </summary>
public class AdwButton : StatefulWidget
{
    private bool _circular;
    private bool _compact;
    private Widget? _content;
    private bool _enabled = true;
    private string? _iconName;
    private string _label;
    private bool _pill;
    private AdwButtonStyle _style = AdwButtonStyle.Regular;

    public AdwButton(string label = "", Action? onPressed = null)
    {
        _label = label;
        OnPressed = onPressed;
    }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            MarkNeedsBuild();
        }
    }

    /// <summary>Optional icon glyph (an <see cref="Icons" /> constant) drawn before the label.</summary>
    public string? IconName
    {
        get => _iconName;
        set
        {
            if (_iconName == value) return;
            _iconName = value;
            MarkNeedsBuild();
        }
    }

    /// <summary>Optional widget child shown instead of the label + icon.</summary>
    public Widget? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value)) return;
            _content = value;
            MarkNeedsBuild();
        }
    }

    public AdwButtonStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            MarkNeedsBuild();
        }
    }

    /// <summary>.pill — fully-rounded capsule shape.</summary>
    public bool Pill
    {
        get => _pill;
        set
        {
            if (_pill == value) return;
            _pill = value;
            MarkNeedsBuild();
        }
    }

    /// <summary>.circular — a fixed 34×34 round icon button (icon only).</summary>
    public bool Circular
    {
        get => _circular;
        set
        {
            if (_circular == value) return;
            _circular = value;
            MarkNeedsBuild();
        }
    }

    /// <summary>Toolbar-density height (28px instead of 34px).</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            MarkNeedsBuild();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            MarkNeedsBuild();
        }
    }

    // Read live by the Pressable on each press; a null callback renders as disabled.
    public Action? OnPressed { get; set; }

    protected override WidgetState CreateState()
    {
        return new AdwButtonState();
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is AdwButton b)
        {
            Label = b.Label;
            IconName = b.IconName;
            OnPressed = b.OnPressed;
            Style = b.Style;
            Enabled = b.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Label,
            IconName,
            Style,
            Enabled,
            base.DebugStateHash()
        );
    }
}

internal sealed class AdwButtonState : WidgetState<AdwButton>
{
    private readonly DecoratedBox _box = new();
    private readonly Opacity _fade = new(1.0);
    private AdwButtonContent? _content;
    private FillTransition _fill = null!;
    private Pressable _root = null!;
    private ThemeData _theme = ThemeData.Dark;

    public override void InitState()
    {
        _fill = new FillTransition(c =>
            {
                _box.Fill = c;
                _box.MarkNeedsPaint();
            }
        );
        _root = new Pressable {
            Child = _box,
            OnStateChanged = ApplyColors,
            OnPressed = () => Widget.OnPressed?.Invoke(),
        };
        _fade.Child = _root;
    }

    public override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        var w = Widget;

        var radius = w.Circular ? 17f : w.Pill ? AdwMetrics.Pill : AdwMetrics.ControlRadius;
        var height = w.Compact ? AdwMetrics.CompactButtonHeight : AdwMetrics.ButtonHeight;
        var enabled = w.Enabled && w.OnPressed is not null;

        // .circular is icon-only in Adwaita, so the label is dropped from the content (it survives
        // as the accessible name) rather than allowed to size the square.
        _content = w.Content is null
            ? new AdwButtonContent(w.IconName, w.Circular ? "" : w.Label)
            : null;
        var content = w.Content ?? _content!;

        if (w.Circular || (w.Content is null && w.Label.Length == 0))
            // Icon-only: a fixed square, capsule-round when Circular.
            _box.Child = SizedBox.Square(w.Circular ? 34f : height, new Center(content));
        else
            _box.Child = AdwStyle.ButtonBody(content, height);

        _box.Radius = radius;
        _root.Enabled = enabled;
        _root.FocusRadius = radius;
        _root.SemanticsLabel = w.Label.Length > 0 ? w.Label : null;
        _fade.Value = enabled ? 1f : AdwStyle.DisabledOpacity;

        ApplyColors();
        return _fade;
    }

    private void ApplyColors()
    {
        var w = Widget;
        var enabled = w.Enabled && w.OnPressed is not null;
        _fill.Target(
            AdwStyle.ButtonFill(
                _theme,
                w.Style,
                _root.Hovered,
                _root.Pressed,
                enabled
            )
        );

        var fg = AdwStyle.ButtonForeground(_theme, w.Style);
        if (_content is not null) _content.Color = fg;
        if (w.Content is not null) TintForeground(w.Content, fg);
        _box.MarkNeedsPaint();
    }

    /// <summary>
    ///     Propagate the foreground colour into uncoloured labels/icons of a widget
    ///     <see cref="AdwButton.Content" /> so they stay readable on solid accent fills.
    ///     Explicitly-coloured children are left alone.
    /// </summary>
    internal static void TintForeground(Widget w, Color fg)
    {
        switch (w)
        {
            case LabelWidget { Color: null } l:
                l.Color = fg;
                break;
            case IconGlyph { Color: null } i:
                i.Color = fg;
                break;
        }

        foreach (var child in w.GetChildren()) TintForeground(child, fg);
    }
}