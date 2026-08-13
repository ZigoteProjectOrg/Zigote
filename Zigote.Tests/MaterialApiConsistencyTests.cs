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
    private static Offset Center(Widget w)
    {
        return new Offset(w.Bounds.X + w.Bounds.Width / 2f, w.Bounds.Y + w.Bounds.Height / 2f);
    }

    // ── TextEditingController binding ─────────────────────────────────────────

    [Fact]
    public void Controller_SeedsField_AndFollowsExternalWrites()
    {
        var controller = new TextEditingController("seed");
        var field = new TextField(controller);
        Assert.Equal("seed", field.Text);

        controller.Text = "external";
        Assert.Equal("external", field.Text);

        controller.Clear();
        Assert.Equal(string.Empty, field.Text);
    }

    [Fact]
    public void Controller_ReceivesTypedEdits_WithoutEcho()
    {
        var controller = new TextEditingController();
        var notifications = 0;
        controller.Changed += _ => notifications++;
        var field = new TextField(controller);

        field.OnTextInput("hi");

        Assert.Equal("hi", controller.Text);
        Assert.Equal(0, notifications); // write-back is silent — no feedback loop
    }

    [Fact]
    public void Controller_ClearWithCaretAtEnd_ThenTyping_DoesNotThrow()
    {
        var controller = new TextEditingController();
        var field = new TextField(controller);
        field.OnTextInput("abcdef"); // caret now sits past the cleared text's length

        controller.Clear();
        field.OnTextInput("x");

        Assert.Equal("x", field.Text);
        Assert.Equal("x", controller.Text);
    }

    [Fact]
    public void Controller_DetachedField_StopsFollowing()
    {
        var controller = new TextEditingController("a");
        var field = new TextField(controller);

        field.Detach();
        controller.Text = "b";

        Assert.Equal("a", field.Text);
    }

    [Fact]
    public void TextField_CtorWiresOnSubmitted()
    {
        string? submitted = null;
        var field = new TextField(onSubmitted: v => submitted = v);
        field.OnTextInput("go");
        field.OnKey(
            '\0',
            40,
            true,
            Modifiers.None
        ); // Return
        Assert.Equal("go", submitted);
    }

    // ── Text softWrap ─────────────────────────────────────────────────────────

    [Fact]
    public void Text_SoftWrapFalse_ForcesSingleLine()
    {
        // Heuristic measurer: width = chars × fontSize × 0.55, height = fontSize × 1.2.
        var c = new Constraints(maxWidth: 60f, maxHeight: 600f);
        var style = new TextStyle(10f, FontWeight.Normal, 1.2f);

        // Default wraps to "aaaa bbbb" / "cccc dddd" / "eeee" → 3 lines of 12 px.
        var wrapped = new Text("aaaa bbbb cccc dddd eeee", style);
        Assert.Equal(36f, wrapped.Measure(c).Height, 2);

        var single = new Text("aaaa bbbb cccc dddd eeee", style, softWrap: false);
        var size = single.Measure(c);
        Assert.Equal(12f, size.Height, 2);
        Assert.Equal(60f, size.Width, 2); // clipped to the box, not grown to fit
    }

    // ── ListView padding ──────────────────────────────────────────────────────

    [Fact]
    public void ListView_Padding_InsetsRows_AndNarrowsTheirWidth()
    {
        var rows = new List<Widget>();
        for (var i = 0; i < 3; i++) rows.Add(new RowBox());
        var list = new ListView(
            rows,
            20,
            EdgeInsets.All(10f)
        ) { Smooth = false };

        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);

        Assert.Equal(10f, rows[0].Bounds.X, 2);
        Assert.Equal(10f, rows[0].Bounds.Y, 2);
        Assert.Equal(30f, rows[1].Bounds.Y, 2);
        Assert.Equal(180f, ((RowBox)rows[0]).LastMaxWidth, 2);
    }

    [Fact]
    public void ListView_Padding_ExtendsTheScrollRange()
    {
        var rows = new List<Widget>();
        for (var i = 0; i < 10; i++) rows.Add(new RowBox());
        var list = new ListView(
            rows,
            20,
            EdgeInsets.All(10f)
        ) { Smooth = false };
        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);

        // Content = 10 × 20 + 20 padding = 220; viewport 100 → max scroll 120 (3 ticks × 40).
        list.OnScroll(0f, -3f);
        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);

        // At max scroll the last row's bottom leaves room for the 10 px bottom inset.
        Assert.Equal(70f, rows[9].Bounds.Y, 2);
    }

    // ── Float-standardized numeric controls ───────────────────────────────────

    [Fact]
    public void Slider_FloatCtor_StepsAndNotifiesInFloat()
    {
        var last = float.NaN;
        var slider = new Slider(
            0.25f,
            0f,
            1f,
            v => last = v
        );
        slider.OnKey(
            '\0',
            79,
            true,
            Modifiers.None
        ); // Right → +5 %
        Assert.Equal(0.3f, slider.Value, 3);
        Assert.Equal(0.3f, last, 3);
    }

    [Fact]
    public void Stepper_Float_ClampsToMax_AndNotifiesOnce()
    {
        var fired = new List<float>();
        var stepper = new Stepper(
            9.5f,
            1f,
            0f,
            10f,
            fired.Add
        );

        stepper.OnKey(
            '\0',
            82,
            true,
            Modifiers.None
        ); // Up press
        stepper.OnKey(
            '\0',
            82,
            false,
            Modifiers.None
        ); // release → bump

        Assert.Equal(10f, stepper.Value, 3);
        Assert.Equal(new[] { 10f }, fired);

        // Already at Max: another bump neither moves nor re-fires.
        stepper.OnKey(
            '\0',
            82,
            true,
            Modifiers.None
        );
        stepper.OnKey(
            '\0',
            82,
            false,
            Modifiers.None
        );
        Assert.Single(fired);
    }

    // ── Canonical selection vocabulary ────────────────────────────────────────

    [Fact]
    public void TabBar_OnChanged_FiresOnKeyboardSelection()
    {
        var got = -1;
        var tabs = new TabBar([new Tab("A"), new Tab("B")], onChanged: i => got = i);
        tabs.OnKey(
            '\0',
            79,
            true,
            Modifiers.None
        ); // Right
        Assert.Equal(1, got);
        Assert.Equal(1, tabs.SelectedIndex);
    }

    [Fact]
    public void Checkbox_ValueProperty_TogglesThroughTap()
    {
        var cb = new Checkbox(false);
        cb.Measure(Constraints.Loose(100, 100));
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
        var btn = new Button("Hi", null);
        btn.Measure(Constraints.Loose(200, 100));
        btn.Layout(Offset.Zero);
        var hit = Assert.IsType<Pressable>(btn.HitTest(Center(btn)));
        Assert.False(hit.Enabled);

        var clicks = 0;
        btn.OnPressed = () => clicks++;
        btn.MarkNeedsBuild();
        btn.Measure(Constraints.Loose(200, 100));
        btn.Layout(Offset.Zero);

        hit = Assert.IsType<Pressable>(btn.HitTest(Center(btn)));
        Assert.True(hit.Enabled);
        hit.OnPointerDown(Center(btn));
        hit.OnPointerUp(Center(btn));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void AliasButtons_NullOnPressedDisables_ExplicitEnabledFalseWins()
    {
        var inert = new OutlinedButton(new Text("Go"));
        inert.Measure(Constraints.Loose(200, 100));
        inert.Layout(Offset.Zero);
        Assert.False(Assert.IsType<Pressable>(inert.HitTest(Center(inert))).Enabled);

        var vetoed = new ElevatedButton(new Text("Go"), () => { }) { Enabled = false };
        vetoed.Measure(Constraints.Loose(200, 100));
        vetoed.Layout(Offset.Zero);
        Assert.False(Assert.IsType<Pressable>(vetoed.HitTest(Center(vetoed))).Enabled);

        var live = new TextButton(new Text("Go"), () => { });
        live.Measure(Constraints.Loose(200, 100));
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
            _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
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
        }

        public override void Paint(PaintList paint)
        {
        }
    }
}
