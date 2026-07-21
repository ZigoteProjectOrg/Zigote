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

    private static List<PaintCommandKind> Kinds(PaintList p)
    {
        return p.DebugCommands.Select(c => (PaintCommandKind)c.Kind).ToList();
    }

    private static Offset Center(Widget w)
    {
        return new Offset(w.Bounds.X + w.Bounds.Width / 2f, w.Bounds.Y + w.Bounds.Height / 2f);
    }

    private static (float R, float G, float B, float A) FirstRectColor(PaintList p)
    {
        foreach (var c in p.DebugCommands)
            if ((PaintCommandKind)c.Kind == PaintCommandKind.Rect)
                return (c.ColorR, c.ColorG, c.ColorB, c.ColorA);
        throw new XunitException("no Rect command emitted");
    }

    // ── DecoratedBox ──────────────────────────────────────────────────────────

    [Fact]
    public void DecoratedBox_PaintsShadowThenFillThenBorder()
    {
        var box = new DecoratedBox {
            Elevation = Elevation.Z1,
            Fill = new Color(1f, 0f, 0f),
            BorderColor = new Color(0f, 1f, 0f),
            Radius = 4f,
        };
        var p = Paint(box, Constraints.Tight(50, 50));
        Assert.Equal(
            new[] {
                PaintCommandKind.Shadow,
                PaintCommandKind.Rect,
                PaintCommandKind.Border,
            },
            Kinds(p)
        );
    }

    [Fact]
    public void DecoratedBox_SkipsTransparentFillAndBorder()
    {
        var box = new DecoratedBox {
            Fill = Color.Transparent,
            BorderColor = Color.Transparent,
        };
        var p = Paint(box, Constraints.Tight(50, 50));
        Assert.Empty(p.DebugCommands);
    }

    [Fact]
    public void DecoratedBox_SizesToChild()
    {
        var box = new DecoratedBox { Child = new SizedBox(40, 24) };
        var size = box.Measure(Constraints.Loose(200, 200));
        Assert.Equal(40f, size.Width, 3);
        Assert.Equal(24f, size.Height, 3);
    }

    // ── Pressable ─────────────────────────────────────────────────────────────

    [Fact]
    public void Pressable_IsFocusableAndCapturesHit()
    {
        var pressable = new Pressable { Child = new SizedBox(30, 30) };
        pressable.Measure(Constraints.Tight(30, 30));
        pressable.Layout(Offset.Zero);
        Assert.True(pressable.Focusable);
        Assert.Same(pressable, pressable.HitTest(new Offset(15, 15)));
    }

    [Fact]
    public void Pressable_TapFiresOnPressed()
    {
        var fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(30, 30),
            OnPressed = () => fired++,
        };
        pressable.Measure(Constraints.Tight(30, 30));
        pressable.Layout(Offset.Zero);
        pressable.OnPointerDown(new Offset(15, 15));
        pressable.OnPointerUp(new Offset(15, 15));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Pressable_ReleaseOutsideBoundsDoesNotFire()
    {
        var fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(30, 30),
            OnPressed = () => fired++,
        };
        pressable.Measure(Constraints.Tight(30, 30));
        pressable.Layout(Offset.Zero);
        pressable.OnPointerDown(new Offset(15, 15));
        pressable.OnPointerUp(new Offset(100, 100));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Pressable_SpaceAndEnterFireOnPressed()
    {
        var fired = 0;
        var pressable = new Pressable {
            Child = new SizedBox(30, 30),
            OnPressed = () => fired++,
        };
        pressable.Measure(Constraints.Tight(30, 30));
        pressable.Layout(Offset.Zero);
        pressable.OnKey(
            ' ',
            44,
            true,
            default
        ); // Space
        pressable.OnKey(
            ' ',
            44,
            false,
            default
        );
        pressable.OnKey(
            '\n',
            40,
            true,
            default
        ); // Enter
        pressable.OnKey(
            '\n',
            40,
            false,
            default
        );
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Pressable_DisabledIsNotFocusable()
    {
        var pressable = new Pressable {
            Child = new SizedBox(30, 30),
            Enabled = false,
        };
        Assert.False(pressable.Focusable);
    }

    // ── Button ────────────────────────────────────────────────────────────────

    [Fact]
    public void Button_HitTargetIsPressable_AndClickFires()
    {
        var clicks = 0;
        var btn = new Button("Hi", () => clicks++);
        btn.Measure(Constraints.Loose(200, 100));
        btn.Layout(Offset.Zero);
        var hit = btn.HitTest(Center(btn));
        Assert.IsType<Pressable>(hit);
        hit!.OnPointerDown(Center(btn));
        hit.OnPointerUp(Center(btn));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Button_HugsControlHeight_DoesNotFillAvailable()
    {
        // Regression: the centring Align must hug height (HeightFactor=1), not fill maxHeight — otherwise
        // a button in a tall Column/loose context would stretch instead of staying control-height.
        var btn = new Button("Hi", null);
        var size = btn.Measure(Constraints.Loose(400, 400));
        Assert.True(
            size.Height >= ControlMetrics.RegularHeight - 0.01f,
            $"height {size.Height} below min"
        );
        Assert.True(size.Height < 100f, $"height {size.Height} filled the available space");
    }

    [Fact]
    public void Button_HoverChangesFillColor()
    {
        var btn = new Button("Hi", () => { });
        var fill1 = FirstRectColor(Paint(btn, Constraints.Loose(200, 100)));

        var hit = (Pressable)btn.HitTest(Center(btn))!;
        hit.OnPointerEnter();

        var fill2 = FirstRectColor(Paint(btn, Constraints.Loose(200, 100)));
        Assert.NotEqual(fill1, fill2);
    }

    [Fact]
    public void Button_FlatStyle_PaintsNoFillWhenIdle()
    {
        var btn = new Button("Hi", () => { }) { Style = ButtonStyle.Flat };
        var p = Paint(btn, Constraints.Loose(200, 100));
        // Flat is borderless and transparent until hover: only the label text, no background rect.
        Assert.DoesNotContain(PaintCommandKind.Rect, Kinds(p));
    }

    // ── Card ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Card_PaintsElevationThenFillThenBorder()
    {
        var card = new Card(new SizedBox(20, 20));
        var kinds = Kinds(Paint(card, Constraints.Loose(200, 200)));
        var shadow = kinds.IndexOf(PaintCommandKind.Shadow);
        var rect = kinds.IndexOf(PaintCommandKind.Rect);
        var border = kinds.IndexOf(PaintCommandKind.Border);
        Assert.True(
            shadow >= 0 && rect >= 0 && border >= 0,
            "card must emit shadow, fill and border"
        );
        Assert.True(
            shadow < rect && rect < border,
            "card paint order must be shadow → fill → border"
        );
    }

    [Fact]
    public void Card_WrapsChildWithPadding()
    {
        var child = new SizedBox(20, 20);
        var card = new Card(child);
        var size = card.Measure(Constraints.Loose(400, 400));
        // Child plus the theme's default padding on all sides — strictly larger than the child.
        Assert.True(size.Width > 20f && size.Height > 20f);
    }

    // ── Checkbox ──────────────────────────────────────────────────────────────

    [Fact]
    public void Checkbox_MeasuresToSquare()
    {
        var size = new Checkbox(false).Measure(Constraints.Loose(100, 100));
        Assert.Equal(ControlMetrics.CheckboxSize, size.Width, 3);
        Assert.Equal(ControlMetrics.CheckboxSize, size.Height, 3);
    }

    [Fact]
    public void Checkbox_TapTogglesAndNotifies()
    {
        var changed = new List<bool>();
        var cb = new Checkbox(false, changed.Add);
        cb.Measure(Constraints.Loose(100, 100));
        cb.Layout(Offset.Zero);
        var hit = cb.HitTest(Center(cb))!;
        hit.OnPointerDown(Center(cb));
        hit.OnPointerUp(Center(cb));
        Assert.True(cb.Checked);
        Assert.Single(changed);
        Assert.True(changed[0]);
    }

    // ── Radio ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Radio_SelectNotifiesWithItsValue()
    {
        var picked = new List<string>();
        var radio = new Radio<string>("a", "b", picked.Add); // not selected (a != b)
        radio.Measure(Constraints.Loose(100, 100));
        radio.Layout(Offset.Zero);
        var hit = radio.HitTest(Center(radio))!;
        hit.OnPointerDown(Center(radio));
        hit.OnPointerUp(Center(radio));
        Assert.Single(picked);
        Assert.Equal("a", picked[0]);
    }

    [Fact]
    public void Radio_AlreadySelected_DoesNotRenotify()
    {
        var picked = new List<string>();
        var radio = new Radio<string>("a", "a", picked.Add); // already selected
        radio.Measure(Constraints.Loose(100, 100));
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
        var taps = 0;
        var chip = new Chip("Tag", false, () => taps++);
        chip.Measure(Constraints.Loose(200, 100));
        chip.Layout(Offset.Zero);
        var hit = chip.HitTest(Center(chip))!;
        hit.OnPointerDown(Center(chip));
        hit.OnPointerUp(Center(chip));
        Assert.Equal(1, taps);
    }
}