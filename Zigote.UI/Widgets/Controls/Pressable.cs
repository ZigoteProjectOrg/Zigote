using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     The interaction half of a composed control: a single-child wrapper that tracks hover, press and
///     focus, activates on Space/Enter, and draws the one shared keyboard focus ring. It owns no
///     appearance — the <see cref="Child" /> (typically a <see cref="Layout.DecoratedBox" />) is the
///     visual. The owning control reacts to <see cref="OnStateChanged" /> by recolouring its retained
///     child widgets, so hover/press feedback is a repaint, never a rebuild.
///     <para>
///         This is the sole focusable + key-handling node of a composed control: it captures pointer
///         events (HitTest returns itself, like <see cref="GestureDetector" />) so the App routes
///         input
///         here, and the App focuses it on click because it is the hit-test result.
///     </para>
/// </summary>
public sealed class Pressable : Widget, IPointerCapture
{
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public Widget? Child { get; set; }

    /// <summary>Fired on a completed tap (pointer up inside bounds) or Space/Enter release.</summary>
    public Action? OnPressed { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Raised whenever hover/press changes so the owner can recolour its retained children.</summary>
    public Action? OnStateChanged { get; set; }

    /// <summary>
    ///     Corner radius the focus ring follows — pass the child <see cref="Layout.DecoratedBox" />'s
    ///     radius.
    /// </summary>
    public float FocusRadius { get; set; }

    public bool Hovered { get; private set; }
    public bool Pressed { get; private set; }

    public override bool Focusable => Enabled;

    // ── Accessibility (the owning control configures these once) ────────────────

    /// <summary>The accessibility role this interaction node plays. Owners override for non-buttons.</summary>
    public SemanticsRole Role { get; set; } = SemanticsRole.Button;

    /// <summary>Accessible name announced for the control (the owner supplies its caption/label).</summary>
    public string? SemanticsLabel { get; set; }

    /// <summary>Checkbox/switch/radio checked state — <c>null</c> for a plain button (not checkable).</summary>
    public bool? Checked { get; set; }

    /// <summary>Tab/segment selected state — <c>null</c> for a control with no selection concept.</summary>
    public bool? SelectedState { get; set; }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = Role;
        config.Label = SemanticsLabel;
        config.IsLeaf = true; // the decorative child (box + label/glyph) is not its own node
        config.Actions = SemanticsAction.Tap | SemanticsAction.Focus;
        config.AddFlag(flag: SemanticsFlags.Focusable, on: Enabled);
        config.AddFlag(flag: SemanticsFlags.Focused, on: Focused);
        config.AddFlag(flag: SemanticsFlags.Disabled, on: !Enabled);
        if (Checked is { } c)
            config.AddFlag(SemanticsFlags.Checkable).AddFlag(flag: SemanticsFlags.Checked, on: c);
        if (SelectedState is { } s)
            config.AddFlag(SemanticsFlags.Selectable).AddFlag(flag: SemanticsFlags.Selected, on: s);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = Child?.Measure(c) ?? Size.Zero;
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
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Child?.Paint(paint);
        if (Focused && Enabled)
            paint.AddFocusRing(bounds: Bounds, radius: FocusRadius, theme: _theme);
    }

    // Capture all pointer events so the child's visuals are driven entirely by this wrapper — except
    // a nested capture (a button, a drag handle), which wins over the one wrapping it. A row that
    // is itself activatable routinely carries both; without this the row swallows their gestures.
    public override Widget? HitTest(Offset point)
    {
        if (!TouchTarget().Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) is IPointerCapture inner ? (Widget)inner : this;
    }

    /// <summary>
    ///     The hit rect. Under a finger, glyph-sized controls (a 16 px checkbox, a 22 px chip) grow
    ///     to the finger target on the axis that is too small — the rect only, so layout and paint
    ///     are untouched and the mouse keeps the exact bounds. Controls already on a row rhythm
    ///     (buttons, tiles, tabs) are left alone: inflating those would let a tap near a boundary
    ///     land on the neighbour, and their real fix is a taller measure at phone width.
    /// </summary>
    private Rect TouchTarget()
    {
        if (!App.PointerIsTouch) return Bounds;

        const float glyphSized = ControlMetrics.RowHeight;
        float gx = Bounds.Width < glyphSized
            ? (ControlMetrics.MinTouchTarget - Bounds.Width) / 2f
            : 0f;
        float gy = Bounds.Height < glyphSized
            ? (ControlMetrics.MinTouchTarget - Bounds.Height) / 2f
            : 0f;
        if (gx <= 0f && gy <= 0f) return Bounds;

        return new Rect(
            x: Bounds.X - gx,
            y: Bounds.Y - gy,
            width: Bounds.Width + (gx * 2f),
            height: Bounds.Height + (gy * 2f)
        );
    }

    public override void OnPointerEnter()
    {
        if (Hovered) return;
        Hovered = true;
        if (Enabled) UiFeedback.Hover?.Invoke();
        NotifyState();
    }

    public override void OnPointerExit()
    {
        if (!Hovered && !Pressed) return;
        Hovered = false;
        Pressed = false;
        NotifyState();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled || Pressed) return;
        Pressed = true;
        NotifyState();
    }

    public override void OnPointerUp(Offset point)
    {
        // Same rect the press was accepted through, so a tap that landed in the touch margin
        // still commits rather than silently doing nothing.
        if (Pressed && Enabled && TouchTarget().Contains(px: point.X, py: point.Y))
        {
            UiFeedback.Click?.Invoke();
            OnPressed?.Invoke();
        }

        if (Pressed)
        {
            Pressed = false;
            NotifyState();
        }
    }

    public override void OnPointerCancel()
    {
        // Pointer claimed elsewhere (touch scroll took the drag, OS cancelled the touch):
        // release the pressed visual and fire nothing.
        if (!Pressed) return;
        Pressed = false;
        NotifyState();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (scancode is 44 or 40) // Space or Enter
        {
            Pressed = down;
            NotifyState();
            if (!down && Enabled)
            {
                UiFeedback.Click?.Invoke();
                OnPressed?.Invoke();
            }
        }
    }

    protected override void OnFocusChanged(bool focused) => MarkNeedsPaint();

    private void NotifyState()
    {
        OnStateChanged?.Invoke();
        MarkNeedsPaint();
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: Hovered,
            value2: Pressed,
            value3: Focused,
            value4: Enabled,
            value5: Child?.DebugStateHash() ?? 0
        );
    }
}
