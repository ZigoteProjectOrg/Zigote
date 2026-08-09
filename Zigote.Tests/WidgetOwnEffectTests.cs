using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.Core.State;
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
        w.Measure(Constraints.Tight(100, 100)); // mounts the widget (OnMount → OwnEffect)

        Assert.Equal(1, w.Seen);
        Assert.Equal(1, w.Runs);

        s.Value = 2;
        Assert.Equal(2, w.Seen);
        Assert.Equal(2, w.Runs);
    }

    [Fact]
    public void OwnEffect_IsDisposed_WhenTheWidgetDetaches()
    {
        var s = new Signal<int>(1);
        var w = new CounterWidget(s);
        w.Measure(Constraints.Tight(100, 100));

        w.Detach(); // → unmount → drains what OnMount owned

        var runsAtDetach = w.Runs;
        s.Value = 3;
        Assert.Equal(runsAtDetach, w.Runs); // disposed → no more runs
        Assert.Equal(1, w.Cleanups); // the Func<Action> overload ran its final cleanup
        Assert.False(w.Mounted);
    }

    [Fact]
    public void OwnEffect_ComesBack_WhenTheWidgetIsRemounted()
    {
        var s = new Signal<int>(1);
        var w = new CounterWidget(s);
        w.Measure(Constraints.Tight(100, 100));
        w.Detach();

        w.Measure(Constraints.Tight(100, 100)); // re-mount
        Assert.True(w.Mounted);

        var runsAtRemount = w.Runs;
        s.Value = 4;
        Assert.Equal(4, w.Seen);
        Assert.Equal(runsAtRemount + 1, w.Runs);
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

        leaf.Attach(app!, null);
        Assert.True(leaf.Mounted);
        Assert.Equal(1, leaf.Seen);

        s.Value = 2;
        Assert.Equal(2, leaf.Seen);

        leaf.Detach();
        Assert.False(leaf.Mounted);
        s.Value = 3;
        Assert.Equal(2, leaf.Seen); // the owned effect went with the unmount
    }

    private sealed class CounterLeaf(Signal<int> source) : LeafWidget
    {
        public int Seen;

        protected override void OnMount()
        {
            OwnEffect(() => Seen = source.Value);
        }

        public override Size Measure(Constraints constraints)
        {
            return Size.Zero;
        }

        public override void Layout(Offset origin)
        {
        }

        public override void Paint(PaintList paint)
        {
        }
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

        protected override Widget Build(BuildContext context)
        {
            return new SizedBox(10, 10);
        }
    }
}
