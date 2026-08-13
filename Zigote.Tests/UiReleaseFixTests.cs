using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.LiquidGlass;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.Tests;

/// <summary>
///     Regression coverage for the pre-release fix batch: the retained-model reconcile contract
///     (keyed <see cref="Widget.UpdateFrom" /> on Radio/Switch/TabBar/AnimatedSwitcher), the
///     <see cref="Center" /> constraint-loosening bug, and <see cref="GlassGlow" /> swallowing input
///     to
///     its children. All headless — build, lay out, reconcile, assert state.
/// </summary>
public class UiReleaseFixTests
{
    private static Offset CenterOf(Widget w) => new(
        x: w.Bounds.X + (w.Bounds.Width / 2f),
        y: w.Bounds.Y + (w.Bounds.Height / 2f)
    );

    // ── Keyed reconcile keeps config in sync (UpdateFrom) ──────────────────────

    [Fact]
    public void Radio_KeyedReconcile_UpdatesGroupValue()
    {
        var a = new Radio<string>(value: "x", groupValue: "x") {
            Key = new ValueKey<int>(1),
        }; // selected (x == x)
        var row = new Row([a]);
        Assert.True(a.IsSelected);

        // New instance, same key, group now "y" — reconciler reuses `a` and must carry the new config in.
        row.SetChildren(
            [new Radio<string>(value: "x", groupValue: "y") { Key = new ValueKey<int>(1) }]
        );

        Assert.Same(expected: a, actual: row.Children[0]);
        Assert.False(a.IsSelected); // was stale-true before the UpdateFrom fix
    }

    [Fact]
    public void Switch_KeyedReconcile_UpdatesValue()
    {
        var a = new Switch(false) { Key = new ValueKey<int>(1) };
        var row = new Row([a]);

        row.SetChildren([new Switch(true) { Key = new ValueKey<int>(1) }]);

        Assert.Same(expected: a, actual: row.Children[0]);
        Assert.True(a.Value); // UpdateFrom previously dropped Value
    }

    [Fact]
    public void TabBar_MutatingTabs_RebuildsCells_AndDoesNotThrow()
    {
        var tabs = new TabBar([new Tab("Alpha"), new Tab("Beta"), new Tab("Gamma")]);
        tabs.Measure(Constraints.Loose(width: 400, height: 40));

        tabs.Tabs = ["Solo"]; // shorter list — stale cells would index past Tabs.Count

        var ex = Record.Exception(() =>
            {
                tabs.Measure(Constraints.Loose(width: 400, height: 40));
                tabs.Layout(Offset.Zero);
            }
        );
        Assert.Null(ex);
    }

    [Fact]
    public void TabBar_KeyedReconcile_DoesNotThrow_OnShorterTabList()
    {
        var a = new TabBar([new Tab("A"), new Tab("B"), new Tab("C")]) {
            Key = new ValueKey<int>(1),
        };
        var row = new Row([a]);
        row.Measure(Constraints.Loose(width: 400, height: 40));

        row.SetChildren([new TabBar([new Tab("X")]) { Key = new ValueKey<int>(1) }]);

        Assert.Same(expected: a, actual: row.Children[0]);
        var ex = Record.Exception(() => row.Measure(Constraints.Loose(width: 400, height: 40)));
        Assert.Null(ex);
    }

    [Fact]
    public void AnimatedSwitcher_SameKeyChild_UpdatesInPlace()
    {
        var a = new Probe { Key = new ValueKey<int>(1) };
        var sw = new AnimatedSwitcher(a);

        sw.Child = new Probe { Key = new ValueKey<int>(1) }; // same key + type, new instance

        Assert.Same(expected: a, actual: sw.Child); // retained (no cross-fade)
        Assert.Equal(
            expected: 1,
            actual: a.Updates
        ); // config forwarded via UpdateFrom (previously dropped)
    }

    // ── Center loosens child constraints ──────────────────────────────────────

    [Fact]
    public void Center_CentersChild_UnderTightConstraints()
    {
        var child = new SizedBox(width: 40, height: 20);
        var center = new Center(child);

        center.Measure(Constraints.Tight(width: 200, height: 200));
        center.Layout(Offset.Zero);

        Assert.Equal(
            expected: 80f,
            actual: child.Bounds.X,
            precision: 2
        ); // (200-40)/2 — was 0 (child forced to fill) before the fix
        Assert.Equal(expected: 90f, actual: child.Bounds.Y, precision: 2); // (200-20)/2
        Assert.Equal(expected: 40f, actual: child.Bounds.Width, precision: 2);
    }

    // ── GlassGlow forwards input to its child ─────────────────────────────────

    [Fact]
    public void GlassGlow_ForwardsHitAndClick_ToChild()
    {
        int clicks = 0;
        var glow = new GlassGlow(new Button(label: "Go", onPressed: () => clicks++));
        glow.Measure(Constraints.Loose(width: 200, height: 100));
        glow.Layout(Offset.Zero);

        var hit = glow.HitTest(CenterOf(glow));
        Assert.IsType<Pressable>(hit); // was the GlassGlow itself before the fix (input swallowed)

        hit!.OnPointerDown(CenterOf(glow));
        hit.OnPointerUp(CenterOf(glow));
        Assert.Equal(expected: 1, actual: clicks);
    }

    // ── InheritedWidget weak-dependent notify still rebuilds live dependents ───

    [Fact]
    public void InheritedWidget_NotifyDependents_RebuildsLiveDependent()
    {
        // Validates the HashSet→ConditionalWeakTable change still enumerates + notifies a live
        // dependent (the weak keying is what stops detached dependents leaking under a static theme).
        var reader = new Reader();
        var marker = new Marker { Child = reader };

        marker.Measure(
            Constraints.Tight(width: 10, height: 10)
        ); // Reader.Build runs → registers via DependOn
        Assert.False(reader.NeedsBuild);

        marker.Fire();
        Assert.True(reader.NeedsBuild);
    }

    private sealed class Marker : InheritedWidget
    {
        public override bool UpdateShouldNotify(InheritedWidget oldWidget) => true;

        public void Fire() => NotifyDependents();
    }

    private sealed class Reader : ComposedWidget
    {
        protected override Widget Build(BuildContext ctx)
        {
            ctx.DependOn<Marker>();
            return new SizedBox(width: 1, height: 1);
        }
    }

    /// <summary>A minimal leaf that records <see cref="UpdateFrom" /> calls.</summary>
    private sealed class Probe : Widget
    {
        public int Updates;

        public override Size Measure(Constraints c) => c.Constrain(Size.Zero);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 0f,
                height: 0f
            );
        }

        public override void Paint(PaintList paint) { }

        public override void UpdateFrom(Widget newWidget) => Updates++;
    }
}
