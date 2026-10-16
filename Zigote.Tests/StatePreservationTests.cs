using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Transient state (scroll offsets, typed text, caret, focus, expander/pane positions) survives
///     the ways a retained tree replaces widgets behind the developer's back: a Watch recompute, a
///     page flipped away and back, a virtualized row scrolled out and in, a re-attach.
/// </summary>
public class StatePreservationTests
{
    private static readonly Constraints Box = Constraints.Tight(width: 200, height: 100);

    private static void Frame(Widget w)
    {
        w.Measure(Box);
        w.Layout(Offset.Zero);
    }

    private static ScrollView Scroller() => new(new SizedBox(width: 200, height: 1000));

    [Fact]
    public void Watch_FlippingToAnotherPageAndBack_RestoresTheFirstPagesScroll()
    {
        var page = new Signal<int>(0);
        ScrollView? a = null;
        var watch = new Watch(() => page.Value == 0 ? a = Scroller() : new SizedBox(width: 1, height: 1));
        Frame(watch);
        a!.OffsetY = 250f;

        page.Value = 1;
        Frame(watch);
        page.Value = 0;
        Frame(watch);

        Assert.Equal(expected: 250f, actual: a.OffsetY);
    }

    [Fact]
    public void Watch_Rebuild_KeepsUncontrolledTextAndCaret()
    {
        var tick = new Signal<int>(0);
        AdwTextField? field = null;
        var watch = new Watch(() =>
            {
                _ = tick.Value;
                return field = new AdwTextField();
            }
        );
        Frame(watch);
        field!.OnTextInput("ab");

        tick.Value++;
        Frame(watch);
        field.OnTextInput("c"); // caret carried to the end, not reset to 0

        Assert.Equal(expected: "abc", actual: field.Text);
    }

    [Fact]
    public void Watch_Rebuild_LeavesControlledTextAlone()
    {
        var draft = new Signal<string>("hello");
        AdwTextField? field = null;
        var watch = new Watch(() => field = new AdwTextField { Text = draft.Value });
        Frame(watch);
        field!.OnTextInput("!");

        draft.Value = ""; // the app cleared its draft
        Frame(watch);

        Assert.Equal(expected: "", actual: field.Text);
    }

    [Fact]
    public void ScrollController_CarriesOffsetToAnUnrelatedNewScroller()
    {
        var ctl = new ScrollController();
        var first = Scroller();
        first.Controller = ctl;
        first.Attach(owner: null!, parent: null);
        Frame(first);
        first.OffsetY = 300f;
        first.Detach();

        var second = Scroller();
        second.Controller = ctl;
        second.Attach(owner: null!, parent: null);
        Frame(second);

        Assert.Equal(expected: 300f, actual: second.OffsetY);
        ctl.JumpTo(100f);
        Assert.Equal(expected: 100f, actual: second.OffsetY);
    }

    [Fact]
    public void ScrollController_JumpBeforeMount_IsAppliedOnMount()
    {
        var ctl = new ScrollController();
        ctl.JumpTo(400f);
        var s = Scroller();
        s.Controller = ctl;
        s.Attach(owner: null!, parent: null);
        Frame(s);

        Assert.Equal(expected: 400f, actual: s.OffsetY);
    }

    [Fact]
    public void ListViewBuilder_RowScrolledOutAndBack_KeepsItsTypedText()
    {
        var fields = new Dictionary<int, AdwTextField>();
        var list = ListView.Builder(
            itemCount: 200,
            itemBuilder: i => fields[i] = new AdwTextField(),
            itemExtent: 20
        );
        Frame(list);
        fields[0].OnTextInput("kept");

        list.OffsetY = 2000f; // row 0 evicted
        Frame(list);
        list.OffsetY = 0f; // rebuilt
        Frame(list);

        Assert.Equal(expected: "kept", actual: fields[0].Text);
    }

    [Fact]
    public void KeyedChildren_PairByKey_WhenARebuildReordersThem()
    {
        var flip = new Signal<bool>(false);
        ScrollView? a = null;
        var watch = new Watch(() =>
            {
                var sa = a = new ScrollView(new SizedBox(width: 200, height: 1000))
                    { Key = new ValueKey<string>("a") };
                var sb = new ScrollView(new SizedBox(width: 200, height: 1000))
                    { Key = new ValueKey<string>("b") };
                return new Column { Children = { flip.Value ? sb : sa, flip.Value ? sa : sb } };
            }
        );
        Frame(watch);
        a!.OffsetY = 120f;

        flip.Value = true;
        Frame(watch);

        Assert.Equal(expected: 120f, actual: a.OffsetY);
    }

    [Fact]
    public void Focus_FollowsTheRebuiltTwin()
    {
        var app = FakeApp();
        var tick = new Signal<int>(0);
        FocusProbe? btn = null;
        var watch = new Watch(() =>
            {
                _ = tick.Value;
                return btn = new FocusProbe();
            }
        );
        watch.Attach(owner: app, parent: null);
        Frame(watch);
        app.RequestFocus(btn);
        var before = btn;

        tick.Value++;
        Frame(watch);

        Assert.NotSame(expected: before, actual: btn);
        Assert.Same(expected: btn, actual: app.FocusedWidget);
    }

    [Fact]
    public void TextFieldController_StillFollowedAfterReattach()
    {
        var ctl = new AdwTextEditingController("one");
        var field = new AdwTextField(controller: ctl);
        field.Attach(owner: null!, parent: null);
        field.Detach();
        ctl.Text = "two"; // while detached
        field.Attach(owner: null!, parent: null);
        Assert.Equal(expected: "two", actual: field.Text);

        ctl.Text = "three";
        Assert.Equal(expected: "three", actual: field.Text);
    }

    [Fact]
    public void ExpanderRow_Rebuild_KeepsUserExpansion()
    {
        var tick = new Signal<int>(0);
        AdwExpanderRow? row = null;
        var watch = new Watch(() =>
            {
                _ = tick.Value;
                return row = new AdwExpanderRow("Row");
            }
        );
        Frame(watch);
        row!.Expanded = true; // stands in for the user's click on the instance

        var clicked = row;
        tick.Value++;
        Frame(watch);

        Assert.NotSame(expected: clicked, actual: row);
        Assert.True(row.Expanded);
    }

    // Focusable without repainting on focus, which the bare fake App cannot service.
    private sealed class FocusProbe : LeafWidget
    {
        public override bool Focusable => true;
        public override Size Measure(Constraints c) => MeasuredSize = c.Constrain(Size.Zero);
        public override void Layout(Offset origin) { }
        public override void Paint(Zigote.Core.Paint.PaintList paint) { }
    }

    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App)
            .GetField(name: "_repaint", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(obj: app, value: new RepaintTracker());
        var overlays = typeof(App).GetField(
            name: "_overlays",
            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        overlays.SetValue(obj: app, value: Activator.CreateInstance(overlays.FieldType));
        return app;
    }
}
