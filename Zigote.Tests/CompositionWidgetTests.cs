using Xunit;
using Xunit.Sdk;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Guards the composition refactor: controls (Button/Chip/Checkbox/Radio/Card) are now built from
///     the
///     <see cref="DecoratedBox" /> + <see cref="Pressable" /> primitives instead of hand-written
///     Measure/Layout/Paint. These headless tests assert the primitives' paint order, that Pressable
///     is the
///     sole focusable hit target and activates correctly, and that the composed controls keep their
///     size
///     contract and interaction behaviour.
/// </summary>
public class CompositionWidgetTests
{
    private static PaintList Paint(Widget w, Constraints c)
    {
        w.Measure(c);
        w.Layout(Offset.Zero);
        var p = new PaintList();
        w.Paint(p);
        return p;
    }

    private static List<PaintCommandKind> Kinds(PaintList p) =>
        p.DebugCommands.Select(c => (PaintCommandKind)c.Kind).ToList();

    private static Offset Center(Widget w) => new(
        x: w.Bounds.X + (w.Bounds.Width / 2f),
        y: w.Bounds.Y + (w.Bounds.Height / 2f)
    );

    private static (float R, float G, float B, float A) FirstRectColor(PaintList p)
    {
        foreach (var c in p.DebugCommands)
        {
            if ((PaintCommandKind)c.Kind == PaintCommandKind.Rect)
                return (c.ColorR, c.ColorG, c.ColorB, c.ColorA);
        }

        throw new XunitException("no Rect command emitted");
    }

    // ── DecoratedBox ──────────────────────────────────────────────────────────

    [Fact]
    public void DecoratedBox_PaintsShadowThenFillThenBorder()
    {
        var box = new DecoratedBox {
            Elevation = Elevation.Z1,
            Fill = new Color(r: 1f, g: 0f, b: 0f),
            BorderColor = new Color(r: 0f, g: 1f, b: 0f),
            Radius = 4f,
        };
        var p = Paint(w: box, c: Constraints.Tight(width: 50, height: 50));
        Assert.Equal(
            expected: new[] {
                PaintCommandKind.Shadow,
                PaintCommandKind.Rect,
                PaintCommandKind.Border,
            },
            actual: Kinds(p)
        );
    }

    [Fact]
    public void DecoratedBox_SkipsTransparentFillAndBorder()
    {
        var box = new DecoratedBox {
            Fill = Color.Transparent,
            BorderColor = Color.Transparent,
        };
        var p = Paint(w: box, c: Constraints.Tight(width: 50, height: 50));
        Assert.Empty(p.DebugCommands);
    }

    [Fact]
    public void DecoratedBox_SizesToChild()
    {
        var box = new DecoratedBox { Child = new SizedBox(width: 40, height: 24) };
        var size = box.Measure(Constraints.Loose(width: 200, height: 200));
        Assert.Equal(expected: 40f, actual: size.Width, precision: 3);
        Assert.Equal(expected: 24f, actual: size.Height, precision: 3);
    }

    // ── Pressable ─────────────────────────────────────────────────────────────

    [Fact]
    public void Pressable_IsFocusableAndCapturesHit()
    {
        var pressable = new Pressable { Child = new SizedBox(width: 30, height: 30) };
        pressable.Measure(Constraints.Tight(width: 30, height: 30));
        pressable.Layout(Offset.Zero);
        Assert.True(pressable.Focusable);
        Assert.Same(expected: pressable, actual: pressable.HitTest(new Offset(x: 15, y: 15)));
    }

    [Fact]
    public void Pressable_TapFiresOnPressed()
    {
        int fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(width: 30, height: 30),
            OnPressed = () => fired++,
        };
        pressable.Measure(Constraints.Tight(width: 30, height: 30));
        pressable.Layout(Offset.Zero);
        pressable.OnPointerDown(new Offset(x: 15, y: 15));
        pressable.OnPointerUp(new Offset(x: 15, y: 15));
        Assert.Equal(expected: 1, actual: fired);
    }

    [Fact]
    public void Pressable_ReleaseOutsideBoundsDoesNotFire()
    {
        int fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(width: 30, height: 30),
            OnPressed = () => fired++,
        };
        pressable.Measure(Constraints.Tight(width: 30, height: 30));
        pressable.Layout(Offset.Zero);
        pressable.OnPointerDown(new Offset(x: 15, y: 15));
        pressable.OnPointerUp(new Offset(x: 100, y: 100));
        Assert.Equal(expected: 0, actual: fired);
    }

    [Fact]
    public void Pressable_SpaceAndEnterFireOnPressed()
    {
        int fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(width: 30, height: 30),
            OnPressed = () => fired++,
        };
        pressable.Measure(Constraints.Tight(width: 30, height: 30));
        pressable.Layout(Offset.Zero);
        pressable.OnKey(
            keyChar: ' ',
            scancode: 44,
            down: true,
            mods: default
        ); // Space
        pressable.OnKey(
            keyChar: ' ',
            scancode: 44,
            down: false,
            mods: default
        );
        pressable.OnKey(
            keyChar: '\n',
            scancode: 40,
            down: true,
            mods: default
        ); // Enter
        pressable.OnKey(
            keyChar: '\n',
            scancode: 40,
            down: false,
            mods: default
        );
        Assert.Equal(expected: 2, actual: fired);
    }

    [Fact]
    public void Pressable_DisabledIsNotFocusable()
    {
        var pressable = new Pressable {
            Child = new SizedBox(width: 30, height: 30),
            Enabled = false,
        };
        Assert.False(pressable.Focusable);
    }

    // ── Button ────────────────────────────────────────────────────────────────

    [Fact]
    public void Button_HitTargetIsPressable_AndClickFires()
    {
        int clicks = 0;
        var btn = new Button(label: "Hi", onPressed: () => clicks++);
        btn.Measure(Constraints.Loose(width: 200, height: 100));
        btn.Layout(Offset.Zero);
        var hit = btn.HitTest(Center(btn));
        Assert.IsType<Pressable>(hit);
        hit!.OnPointerDown(Center(btn));
        hit.OnPointerUp(Center(btn));
        Assert.Equal(expected: 1, actual: clicks);
    }

    [Fact]
    public void Button_HugsControlHeight_DoesNotFillAvailable()
    {
        // Regression: the centring Align must hug height (HeightFactor=1), not fill maxHeight — otherwise
        // a button in a tall Column/loose context would stretch instead of staying control-height.
        var btn = new Button(label: "Hi", onPressed: null);
        var size = btn.Measure(Constraints.Loose(width: 400, height: 400));
        Assert.True(
            condition: size.Height >= ControlMetrics.RegularHeight - 0.01f,
            userMessage: $"height {size.Height} below min"
        );
        Assert.True(
            condition: size.Height < 100f,
            userMessage: $"height {size.Height} filled the available space"
        );
    }

    [Fact]
    public void Button_HoverChangesFillColor()
    {
        var btn = new Button(label: "Hi", onPressed: () => { });
        var fill1 = FirstRectColor(Paint(w: btn, c: Constraints.Loose(width: 200, height: 100)));

        var hit = (Pressable)btn.HitTest(Center(btn))!;
        hit.OnPointerEnter();

        var fill2 = FirstRectColor(Paint(w: btn, c: Constraints.Loose(width: 200, height: 100)));
        Assert.NotEqual(expected: fill1, actual: fill2);
    }

    [Fact]
    public void Button_FlatStyle_PaintsNoFillWhenIdle()
    {
        var btn = new Button(label: "Hi", onPressed: () => { }) { Style = ButtonStyle.Flat };
        var p = Paint(w: btn, c: Constraints.Loose(width: 200, height: 100));
        // Flat is borderless and transparent until hover: only the label text, no background rect.
        Assert.DoesNotContain(expected: PaintCommandKind.Rect, collection: Kinds(p));
    }

    // ── Card ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Card_PaintsElevationThenFillThenBorder()
    {
        var card = new Card(new SizedBox(width: 20, height: 20));
        var kinds = Kinds(Paint(w: card, c: Constraints.Loose(width: 200, height: 200)));
        int shadow = kinds.IndexOf(PaintCommandKind.Shadow);
        int rect = kinds.IndexOf(PaintCommandKind.Rect);
        int border = kinds.IndexOf(PaintCommandKind.Border);
        Assert.True(
            condition: shadow >= 0 && rect >= 0 && border >= 0,
            userMessage: "card must emit shadow, fill and border"
        );
        Assert.True(
            condition: shadow < rect && rect < border,
            userMessage: "card paint order must be shadow → fill → border"
        );
    }

    [Fact]
    public void Card_WrapsChildWithPadding()
    {
        var child = new SizedBox(width: 20, height: 20);
        var card = new Card(child);
        var size = card.Measure(Constraints.Loose(width: 400, height: 400));
        // Child plus the theme's default padding on all sides — strictly larger than the child.
        Assert.True(size.Width > 20f && size.Height > 20f);
    }

    // ── Checkbox ──────────────────────────────────────────────────────────────

    [Fact]
    public void Checkbox_MeasuresToSquare()
    {
        var size = new Checkbox(false).Measure(Constraints.Loose(width: 100, height: 100));
        Assert.Equal(expected: ControlMetrics.CheckboxSize, actual: size.Width, precision: 3);
        Assert.Equal(expected: ControlMetrics.CheckboxSize, actual: size.Height, precision: 3);
    }

    [Fact]
    public void Checkbox_TapTogglesAndNotifies()
    {
        var changed = new List<bool>();
        var cb = new Checkbox(value: false, onChanged: changed.Add);
        cb.Measure(Constraints.Loose(width: 100, height: 100));
        cb.Layout(Offset.Zero);
        var hit = cb.HitTest(Center(cb))!;
        hit.OnPointerDown(Center(cb));
        hit.OnPointerUp(Center(cb));
        Assert.True(cb.Value);
        Assert.Single(changed);
        Assert.True(changed[0]);
    }

    // ── Radio ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Radio_SelectNotifiesWithItsValue()
    {
        var picked = new List<string>();
        var radio = new Radio<string>(
            value: "a",
            groupValue: "b",
            onChanged: picked.Add
        ); // not selected (a != b)
        radio.Measure(Constraints.Loose(width: 100, height: 100));
        radio.Layout(Offset.Zero);
        var hit = radio.HitTest(Center(radio))!;
        hit.OnPointerDown(Center(radio));
        hit.OnPointerUp(Center(radio));
        Assert.Single(picked);
        Assert.Equal(expected: "a", actual: picked[0]);
    }

    [Fact]
    public void Radio_AlreadySelected_DoesNotRenotify()
    {
        var picked = new List<string>();
        var radio = new Radio<string>(
            value: "a",
            groupValue: "a",
            onChanged: picked.Add
        ); // already selected
        radio.Measure(Constraints.Loose(width: 100, height: 100));
        radio.Layout(Offset.Zero);
        var hit = radio.HitTest(Center(radio))!;
        hit.OnPointerDown(Center(radio));
        hit.OnPointerUp(Center(radio));
        Assert.Empty(picked);
    }

    // ── Chip ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Chip_TapFiresOnTap()
    {
        int taps = 0;
        var chip = new Chip(label: "Tag", selected: false, onPressed: () => taps++);
        chip.Measure(Constraints.Loose(width: 200, height: 100));
        chip.Layout(Offset.Zero);
        var hit = chip.HitTest(Center(chip))!;
        hit.OnPointerDown(Center(chip));
        hit.OnPointerUp(Center(chip));
        Assert.Equal(expected: 1, actual: taps);
    }
}
