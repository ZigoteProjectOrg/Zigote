using Xunit;
using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Guards the Material public-API consistency pass: the <see cref="TextEditingController" />
///     two-way binding, the implemented <c>softWrap</c>/<c>ListView padding</c> parameters, the
///     float-standardized numeric controls, and the uniform "null OnPressed = disabled" button rule
///     (derived live per Build). All headless — no window.
/// </summary>
public class MaterialApiConsistencyTests
{
    private static Offset Center(Widget w) => new(
        x: w.Bounds.X + (w.Bounds.Width / 2f),
        y: w.Bounds.Y + (w.Bounds.Height / 2f)
    );

    // ── TextEditingController binding ─────────────────────────────────────────

    [Fact]
    public void Controller_SeedsField_AndFollowsExternalWrites()
    {
        var controller = new TextEditingController("seed");
        var field = new TextField(controller);
        Assert.Equal(expected: "seed", actual: field.Text);

        controller.Text = "external";
        Assert.Equal(expected: "external", actual: field.Text);

        controller.Clear();
        Assert.Equal(expected: string.Empty, actual: field.Text);
    }

    [Fact]
    public void Controller_ReceivesTypedEdits_WithoutEcho()
    {
        var controller = new TextEditingController();
        int notifications = 0;
        controller.Changed += _ => notifications++;
        var field = new TextField(controller);

        field.OnTextInput("hi");

        Assert.Equal(expected: "hi", actual: controller.Text);
        Assert.Equal(expected: 0, actual: notifications); // write-back is silent — no feedback loop
    }

    [Fact]
    public void Controller_ClearWithCaretAtEnd_ThenTyping_DoesNotThrow()
    {
        var controller = new TextEditingController();
        var field = new TextField(controller);
        field.OnTextInput("abcdef"); // caret now sits past the cleared text's length

        controller.Clear();
        field.OnTextInput("x");

        Assert.Equal(expected: "x", actual: field.Text);
        Assert.Equal(expected: "x", actual: controller.Text);
    }

    [Fact]
    public void Controller_DetachedField_StopsFollowing()
    {
        var controller = new TextEditingController("a");
        var field = new TextField(controller);

        field.Detach();
        controller.Text = "b";

        Assert.Equal(expected: "a", actual: field.Text);
    }

    [Fact]
    public void TextField_CtorWiresOnSubmitted()
    {
        string? submitted = null;
        var field = new TextField(onSubmitted: v => submitted = v);
        field.OnTextInput("go");
        field.OnKey(
            keyChar: '\0',
            scancode: 40,
            down: true,
            mods: Modifiers.None
        ); // Return
        Assert.Equal(expected: "go", actual: submitted);
    }

    // ── Text softWrap ─────────────────────────────────────────────────────────

    [Fact]
    public void Text_SoftWrapFalse_ForcesSingleLine()
    {
        // Heuristic measurer: width = chars × fontSize × 0.55, height = fontSize × 1.2.
        var c = new Constraints(maxWidth: 60f, maxHeight: 600f);
        var style = new TextStyle(Size: 10f, Weight: FontWeight.Normal, LineHeight: 1.2f);

        // Default wraps to "aaaa bbbb" / "cccc dddd" / "eeee" → 3 lines of 12 px.
        var wrapped = new Text(data: "aaaa bbbb cccc dddd eeee", style: style);
        Assert.Equal(expected: 36f, actual: wrapped.Measure(c).Height, precision: 2);

        var single = new Text(data: "aaaa bbbb cccc dddd eeee", style: style, softWrap: false);
        var size = single.Measure(c);
        Assert.Equal(expected: 12f, actual: size.Height, precision: 2);
        Assert.Equal(
            expected: 60f,
            actual: size.Width,
            precision: 2
        ); // clipped to the box, not grown to fit
    }

    // ── ListView padding ──────────────────────────────────────────────────────

    [Fact]
    public void ListView_Padding_InsetsRows_AndNarrowsTheirWidth()
    {
        var rows = new List<Widget>();
        for (int i = 0; i < 3; i++) rows.Add(new RowBox());
        var list = new ListView(
            children: rows,
            itemExtent: 20,
            padding: EdgeInsets.All(10f)
        ) { Smooth = false };

        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);

        Assert.Equal(expected: 10f, actual: rows[0].Bounds.X, precision: 2);
        Assert.Equal(expected: 10f, actual: rows[0].Bounds.Y, precision: 2);
        Assert.Equal(expected: 30f, actual: rows[1].Bounds.Y, precision: 2);
        Assert.Equal(expected: 180f, actual: ((RowBox)rows[0]).LastMaxWidth, precision: 2);
    }

    [Fact]
    public void ListView_Padding_ExtendsTheScrollRange()
    {
        var rows = new List<Widget>();
        for (int i = 0; i < 10; i++) rows.Add(new RowBox());
        var list = new ListView(
            children: rows,
            itemExtent: 20,
            padding: EdgeInsets.All(10f)
        ) { Smooth = false };
        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);

        // Content = 10 × 20 + 20 padding = 220; viewport 100 → max scroll 120 (3 ticks × 40).
        list.OnScroll(dx: 0f, dy: -3f);
        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);

        // At max scroll the last row's bottom leaves room for the 10 px bottom inset.
        Assert.Equal(expected: 70f, actual: rows[9].Bounds.Y, precision: 2);
    }

    // ── Float-standardized numeric controls ───────────────────────────────────

    [Fact]
    public void Slider_FloatCtor_StepsAndNotifiesInFloat()
    {
        float last = float.NaN;
        var slider = new Slider(
            value: 0.25f,
            min: 0f,
            max: 1f,
            onChanged: v => last = v
        );
        slider.OnKey(
            keyChar: '\0',
            scancode: 79,
            down: true,
            mods: Modifiers.None
        ); // Right → +5 %
        Assert.Equal(expected: 0.3f, actual: slider.Value, precision: 3);
        Assert.Equal(expected: 0.3f, actual: last, precision: 3);
    }

    [Fact]
    public void Stepper_Float_ClampsToMax_AndNotifiesOnce()
    {
        var fired = new List<float>();
        var stepper = new Stepper(
            value: 9.5f,
            step: 1f,
            min: 0f,
            max: 10f,
            onChanged: fired.Add
        );

        stepper.OnKey(
            keyChar: '\0',
            scancode: 82,
            down: true,
            mods: Modifiers.None
        ); // Up press
        stepper.OnKey(
            keyChar: '\0',
            scancode: 82,
            down: false,
            mods: Modifiers.None
        ); // release → bump

        Assert.Equal(expected: 10f, actual: stepper.Value, precision: 3);
        Assert.Equal(expected: new[] { 10f }, actual: fired);

        // Already at Max: another bump neither moves nor re-fires.
        stepper.OnKey(
            keyChar: '\0',
            scancode: 82,
            down: true,
            mods: Modifiers.None
        );
        stepper.OnKey(
            keyChar: '\0',
            scancode: 82,
            down: false,
            mods: Modifiers.None
        );
        Assert.Single(fired);
    }

    // ── Canonical selection vocabulary ────────────────────────────────────────

    [Fact]
    public void TabBar_OnChanged_FiresOnKeyboardSelection()
    {
        int got = -1;
        var tabs = new TabBar(tabs: [new Tab("A"), new Tab("B")], onChanged: i => got = i);
        tabs.OnKey(
            keyChar: '\0',
            scancode: 79,
            down: true,
            mods: Modifiers.None
        ); // Right
        Assert.Equal(expected: 1, actual: got);
        Assert.Equal(expected: 1, actual: tabs.SelectedIndex);
    }

    [Fact]
    public void Checkbox_ValueProperty_TogglesThroughTap()
    {
        var cb = new Checkbox(false);
        cb.Measure(Constraints.Loose(width: 100, height: 100));
        cb.Layout(Offset.Zero);
        var hit = cb.HitTest(Center(cb))!;
        hit.OnPointerDown(Center(cb));
        hit.OnPointerUp(Center(cb));
        Assert.True(cb.Value);
    }

    // ── Uniform button enablement ─────────────────────────────────────────────

    [Fact]
    public void Button_NullOnPressed_IsDisabled_AndEnablesOnRebuild()
    {
        var btn = new Button(label: "Hi", onPressed: null);
        btn.Measure(Constraints.Loose(width: 200, height: 100));
        btn.Layout(Offset.Zero);
        var hit = Assert.IsType<Pressable>(btn.HitTest(Center(btn)));
        Assert.False(hit.Enabled);

        int clicks = 0;
        btn.OnPressed = () => clicks++;
        btn.MarkNeedsBuild();
        btn.Measure(Constraints.Loose(width: 200, height: 100));
        btn.Layout(Offset.Zero);

        hit = Assert.IsType<Pressable>(btn.HitTest(Center(btn)));
        Assert.True(hit.Enabled);
        hit.OnPointerDown(Center(btn));
        hit.OnPointerUp(Center(btn));
        Assert.Equal(expected: 1, actual: clicks);
    }

    [Fact]
    public void AliasButtons_NullOnPressedDisables_ExplicitEnabledFalseWins()
    {
        var inert = new OutlinedButton(new Text("Go"));
        inert.Measure(Constraints.Loose(width: 200, height: 100));
        inert.Layout(Offset.Zero);
        Assert.False(Assert.IsType<Pressable>(inert.HitTest(Center(inert))).Enabled);

        var vetoed =
            new ElevatedButton(child: new Text("Go"), onPressed: () => { }) { Enabled = false };
        vetoed.Measure(Constraints.Loose(width: 200, height: 100));
        vetoed.Layout(Offset.Zero);
        Assert.False(Assert.IsType<Pressable>(vetoed.HitTest(Center(vetoed))).Enabled);

        var live = new TextButton(child: new Text("Go"), onPressed: () => { });
        live.Measure(Constraints.Loose(width: 200, height: 100));
        live.Layout(Offset.Zero);
        Assert.True(Assert.IsType<Pressable>(live.HitTest(Center(live))).Enabled);
    }

    private sealed class RowBox : Widget
    {
        private Size _size;

        public float LastMaxWidth { get; private set; }

        public override Size Measure(Constraints c)
        {
            LastMaxWidth = c.MaxWidth;
            _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
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
        }

        public override void Paint(PaintList paint) { }
    }
}
