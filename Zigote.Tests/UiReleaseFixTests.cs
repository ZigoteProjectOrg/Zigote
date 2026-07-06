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
    private static Offset CenterOf(Widget w)
    {
        return new Offset(w.Bounds.X + w.Bounds.Width / 2f, w.Bounds.Y + w.Bounds.Height / 2f);
    }

    // ── Keyed reconcile keeps config in sync (UpdateFrom) ──────────────────────

    [Fact]
    public void Radio_KeyedReconcile_UpdatesGroupValue()
    {
        var a = new Radio<string>("x", "x") { Key = new ValueKey<int>(1) }; // selected (x == x)
        var row = new Row([a]);
        Assert.True(a.IsSelected);

        // New instance, same key, group now "y" — reconciler reuses `a` and must carry the new config in.
        row.SetChildren([new Radio<string>("x", "y") { Key = new ValueKey<int>(1) }]);

        Assert.Same(a, row.Children[0]);
        Assert.False(a.IsSelected); // was stale-true before the UpdateFrom fix
    }

    [Fact]
    public void Switch_KeyedReconcile_UpdatesValue()
    {
        var a = new Switch(false) { Key = new ValueKey<int>(1) };
        var row = new Row([a]);

        row.SetChildren([new Switch(true) { Key = new ValueKey<int>(1) }]);

        Assert.Same(a, row.Children[0]);
        Assert.True(a.Value); // UpdateFrom previously dropped Value
    }

    [Fact]
    public void TabBar_MutatingTabs_RebuildsCells_AndDoesNotThrow()
    {
        var tabs = new TabBar([new Tab("Alpha"), new Tab("Beta"), new Tab("Gamma")]);
        tabs.Measure(Constraints.Loose(400, 40));

        tabs.Tabs = ["Solo"]; // shorter list — stale cells would index past Tabs.Count

        var ex = Record.Exception(() =>
            {
                tabs.Measure(Constraints.Loose(400, 40));
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
        row.Measure(Constraints.Loose(400, 40));

        row.SetChildren([new TabBar([new Tab("X")]) { Key = new ValueKey<int>(1) }]);

        Assert.Same(a, row.Children[0]);
        var ex = Record.Exception(() => row.Measure(Constraints.Loose(400, 40)));
        Assert.Null(ex);
    }

    [Fact]
    public void AnimatedSwitcher_SameKeyChild_UpdatesInPlace()
    {
        var a = new Probe { Key = new ValueKey<int>(1) };
        var sw = new AnimatedSwitcher(a);

        sw.Child = new Probe { Key = new ValueKey<int>(1) }; // same key + type, new instance

        Assert.Same(a, sw.Child); // retained (no cross-fade)
        Assert.Equal(1, a.Updates); // config forwarded via UpdateFrom (previously dropped)
    }

    // ── Center loosens child constraints ──────────────────────────────────────

    [Fact]
    public void Center_CentersChild_UnderTightConstraints()
    {
        var child = new SizedBox(40, 20);
        var center = new Center(child);

        center.Measure(Constraints.Tight(200, 200));
        center.Layout(Offset.Zero);

        Assert.Equal(
            80f,
            child.Bounds.X,
            2
        ); // (200-40)/2 — was 0 (child forced to fill) before the fix
        Assert.Equal(90f, child.Bounds.Y, 2); // (200-20)/2
        Assert.Equal(40f, child.Bounds.Width, 2);
    }

    // ── GlassGlow forwards input to its child ─────────────────────────────────

    [Fact]
    public void GlassGlow_ForwardsHitAndClick_ToChild()
    {
        var clicks = 0;
        var glow = new GlassGlow(new Button("Go", () => clicks++));
        glow.Measure(Constraints.Loose(200, 100));
        glow.Layout(Offset.Zero);

        var hit = glow.HitTest(CenterOf(glow));
        Assert.IsType<Pressable>(hit); // was the GlassGlow itself before the fix (input swallowed)

        hit!.OnPointerDown(CenterOf(glow));
        hit.OnPointerUp(CenterOf(glow));
        Assert.Equal(1, clicks);
    }

    // ── InheritedWidget weak-dependent notify still rebuilds live dependents ───

    [Fact]
    public void InheritedWidget_NotifyDependents_RebuildsLiveDependent()
    {
        // Validates the HashSet→ConditionalWeakTable change still enumerates + notifies a live
        // dependent (the weak keying is what stops detached dependents leaking under a static theme).
        var reader = new Reader();
        var marker = new Marker { Child = reader };

        marker.Measure(Constraints.Tight(10, 10)); // Reader.Build runs → registers via DependOn
        Assert.False(reader.NeedsBuild);

        marker.Fire();
        Assert.True(reader.NeedsBuild);
    }

    private sealed class Marker : InheritedWidget
    {
        public override bool UpdateShouldNotify(InheritedWidget oldWidget)
        {
            return true;
        }

        public void Fire()
        {
            NotifyDependents();
        }
    }

    private sealed class Reader : StatelessWidget
    {
        protected override Widget Build(BuildContext ctx)
        {
            ctx.DependOn<Marker>();
            return new SizedBox(1, 1);
        }
    }

    /// <summary>A minimal leaf that records <see cref="UpdateFrom" /> calls.</summary>
    private sealed class Probe : Widget
    {
        public int Updates;

        public override Size Measure(Constraints c)
        {
            return c.Constrain(Size.Zero);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                0f,
                0f
            );
        }

        public override void Paint(PaintList paint)
        {
        }

        public override void UpdateFrom(Widget newWidget)
        {
            Updates++;
        }
    }
}