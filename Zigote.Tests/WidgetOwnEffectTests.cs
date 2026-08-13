using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="Widget.OwnEffect(Action)" /> ties an <see cref="Effect" />'s lifetime to the
///     widget's mount period: it tracks signals like a bare Effect but is disposed when the widget
///     leaves the tree — signals hold their observers strongly, so an unowned effect would keep firing
///     against the detached subtree forever. Re-attaching runs <see cref="Widget.OnMount" /> again, so
///     the binding comes back with it.
/// </summary>
[Collection("Reactive-serial")] // signals/effects share process-static reactive state
public class WidgetOwnEffectTests
{
    [Fact]
    public void OwnEffect_RunsImmediately_AndTracksSignals()
    {
        var s = new Signal<int>(1);
        var w = new CounterWidget(s);
        w.Measure(
            Constraints.Tight(width: 100, height: 100)
        ); // mounts the widget (OnMount → OwnEffect)

        Assert.Equal(expected: 1, actual: w.Seen);
        Assert.Equal(expected: 1, actual: w.Runs);

        s.Value = 2;
        Assert.Equal(expected: 2, actual: w.Seen);
        Assert.Equal(expected: 2, actual: w.Runs);
    }

    [Fact]
    public void OwnEffect_IsDisposed_WhenTheWidgetDetaches()
    {
        var s = new Signal<int>(1);
        var w = new CounterWidget(s);
        w.Measure(Constraints.Tight(width: 100, height: 100));

        w.Detach(); // → unmount → drains what OnMount owned

        int runsAtDetach = w.Runs;
        s.Value = 3;
        Assert.Equal(expected: runsAtDetach, actual: w.Runs); // disposed → no more runs
        Assert.Equal(
            expected: 1,
            actual: w.Cleanups
        ); // the Func<Action> overload ran its final cleanup
        Assert.False(w.Mounted);
    }

    [Fact]
    public void OwnEffect_ComesBack_WhenTheWidgetIsRemounted()
    {
        var s = new Signal<int>(1);
        var w = new CounterWidget(s);
        w.Measure(Constraints.Tight(width: 100, height: 100));
        w.Detach();

        w.Measure(Constraints.Tight(width: 100, height: 100)); // re-mount
        Assert.True(w.Mounted);

        int runsAtRemount = w.Runs;
        s.Value = 4;
        Assert.Equal(expected: 4, actual: w.Seen);
        Assert.Equal(expected: runsAtRemount + 1, actual: w.Runs);
    }

    // Regression: LeafWidget used to override Attach/Detach without calling base, which skipped the
    // mount lifecycle entirely — every leaf that binds a ticker in OnMount (CheckGlyph, RadioDotGlyph)
    // silently stopped animating.
    [Fact]
    public void LeafWidget_Mounts_AndUnmounts_LikeAnyOtherWidget()
    {
        var s = new Signal<int>(1);
        var leaf = new CounterLeaf(s);
        var app = App.Active;
        Assert.False(leaf.Mounted);

        leaf.Attach(owner: app!, parent: null);
        Assert.True(leaf.Mounted);
        Assert.Equal(expected: 1, actual: leaf.Seen);

        s.Value = 2;
        Assert.Equal(expected: 2, actual: leaf.Seen);

        leaf.Detach();
        Assert.False(leaf.Mounted);
        s.Value = 3;
        Assert.Equal(expected: 2, actual: leaf.Seen); // the owned effect went with the unmount
    }

    private sealed class CounterLeaf(Signal<int> source) : LeafWidget
    {
        public int Seen;

        protected override void OnMount() => OwnEffect(() => Seen = source.Value);

        public override Size Measure(Constraints constraints) => Size.Zero;

        public override void Layout(Offset origin) { }

        public override void Paint(PaintList paint) { }
    }

    private sealed class CounterWidget(Signal<int> source) : ComposedWidget
    {
        public int Cleanups;
        public int Runs;
        public int Seen;

        protected override void OnMount()
        {
            OwnEffect(() =>
                {
                    Runs++;
                    Seen = source.Value;
                    return () => Cleanups++;
                }
            );
        }

        protected override Widget Build(BuildContext context) =>
            new SizedBox(width: 10, height: 10);
    }
}
